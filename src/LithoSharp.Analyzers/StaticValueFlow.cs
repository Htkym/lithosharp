using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

// Unknown absorbs joins. Never evaluate user getters, calls, ToString or initializers.
internal sealed class StaticValue
{
    private StaticValue(object?[]? values, string? reason) { Values = values; Reason = reason; }
    internal object?[]? Values { get; }
    internal string? Reason { get; }
    internal static StaticValue Unknown(string reason) => new(null, reason);
    internal static StaticValue Known(object? value) => value is string s && s.Length > StaticValueFlow.MaxStringLength
        ? Unknown("string-budget") : new(new[] { value }, null);
    internal StaticValue Join(StaticValue other)
    {
        if (Values is null) return this;
        if (other.Values is null) return other;
        var values = Values.Concat(other.Values).Distinct().Take(StaticValueFlow.MaxCandidates + 1).ToArray();
        return values.Length > StaticValueFlow.MaxCandidates ? Unknown("candidate-budget") : new(values, null);
    }
}

internal static class StaticValueFlow
{
    internal const int MaxCandidates = 8;
    internal const int MaxBlocks = 256;
    internal const int MaxOperations = 4096;
    internal const int MaxDepth = 64;
    internal const int MaxStringLength = 65536;
    internal const int MaxNestedGraphs = 64;

    internal static bool HasBindingError(IOperation operation) => Walk(operation).Any(o => o.Kind == OperationKind.Invalid
        || o.Type?.TypeKind == TypeKind.Error || o is IConversionOperation c && (!c.Conversion.Exists || c.OperatorMethod is not null));

    internal static void Analyze(OperationBlockAnalysisContext context, Action<IOperation, Func<IOperation, StaticValue>> sink)
    {
        foreach (var root in context.OperationBlocks)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var model = context.Compilation.GetSemanticModel(root.Syntax.SyntaxTree);
            var outer = ControlFlowGraph.Create(root.Parent?.Syntax ?? root.Syntax, model, context.CancellationToken);
            if (outer is null) continue;
            foreach (var graph in NestedGraphs(outer, context.CancellationToken))
            {
                if (graph.Blocks.Length > MaxBlocks) continue;
                var operationCount = 0;
                foreach (var operation in graph.Blocks.SelectMany(b => b.Operations.Concat(b.BranchValue is null
                    ? Enumerable.Empty<IOperation>() : new[] { b.BranchValue })).SelectMany(Walk))
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (++operationCount > MaxOperations) break;
                }
                if (operationCount > MaxOperations) continue;
                // Exceptional edges/finally execution are not ordinary CFG predecessor joins.
                // Defer their local facts, as well as loops, rather than assume ordinal flow.
                var deferredFlow = graph.Blocks.Any(b => b.Predecessors.Any(p => p.Source.Ordinal >= b.Ordinal))
                    || Regions(graph.Root).Any(r => r.Kind != ControlFlowRegionKind.Root && r.Kind != ControlFlowRegionKind.LocalLifetime);
                var states = new Dictionary<int, State>();
                foreach (var block in graph.Blocks)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                    if (!block.IsReachable) continue;
                    var predecessors = block.Predecessors.Where(p => states.ContainsKey(p.Source.Ordinal)).ToArray();
                    var state = deferredFlow || predecessors.Length == 0 ? new State()
                        : State.Merge(predecessors.Select(p => states[p.Source.Ordinal]).ToArray());
                    var snapshots = new Dictionary<IOperation, StaticValue>();
                    var depths = new Dictionary<IOperation, int>();
                    void Apply(IOperation operation)
                    {
                        context.CancellationToken.ThrowIfCancellationRequested();
                        // Capture each expression when it is evaluated, before later siblings write
                        // locals. Invocation/constructor arguments must never be re-evaluated using
                        // the final call state; named arguments follow their source evaluation order.
                        var expressionDepth = 1 + operation.ChildOperations.Select(child => depths[child]).DefaultIfEmpty(0).Max();
                        depths[operation] = expressionDepth;
                        snapshots[operation] = expressionDepth > MaxDepth && !operation.ConstantValue.HasValue
                            ? StaticValue.Unknown("depth-budget") : Evaluate(operation, state, 0, snapshots);
                        sink(operation, value => snapshots.TryGetValue(value, out var snapshot)
                            ? snapshot : StaticValue.Unknown("missing-evaluation-snapshot"));
                        if (!deferredFlow && operation is ISimpleAssignmentOperation assignment && assignment.Target is ILocalReferenceOperation local)
                        {
                            if (assignment.IsRef || local.Local.RefKind != RefKind.None) state.Invalidate("ref-alias");
                            else state.Locals[local.Local] = Evaluate(assignment.Value, state, 0, snapshots);
                        }
                        else if (!deferredFlow && operation is IFlowCaptureOperation capture)
                            state.Captures[capture.Id] = Evaluate(capture.Value, state, 0, snapshots);
                        // Calls/getters can mutate captured locals even with no explicit argument.
                        if (operation is IInvocationOperation or IObjectCreationOperation or IPropertyReferenceOperation
                            or IDynamicInvocationOperation or IAwaitOperation or IAddressOfOperation)
                            state.Invalidate("call-or-getter-escape");
                        if (operation is ISimpleAssignmentOperation other && other.Target is not ILocalReferenceOperation)
                            state.Invalidate("alias-write");
                        if (operation is IIncrementOrDecrementOperation increment && increment.Target is ILocalReferenceOperation changed)
                            state.Locals[changed.Local] = StaticValue.Unknown("mutation");
                        if (operation is ICompoundAssignmentOperation compound && compound.Target is ILocalReferenceOperation target)
                            state.Locals[target.Local] = StaticValue.Unknown("mutation");
                    }
                    void Visit(IOperation operation)
                    {
                        // Only this root's expression snapshots are needed; persisted CFG facts
                        // live in State. Do not retain every intermediate value across statements.
                        snapshots.Clear();
                        depths.Clear();
                        var pending = new Stack<(IOperation Operation, bool Visited)>();
                        pending.Push((operation, false));
                        while (pending.Count != 0)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            var next = pending.Pop();
                            if (next.Visited) { Apply(next.Operation); continue; }
                            pending.Push((next.Operation, true));
                            foreach (var child in next.Operation.ChildOperations.Reverse()) pending.Push((child, false));
                        }
                    }
                    foreach (var operation in block.Operations) Visit(operation);
                    if (block.BranchValue is not null) Visit(block.BranchValue);
                    states[block.Ordinal] = state;
                }
            }
        }
    }

    private static IEnumerable<ControlFlowRegion> Regions(ControlFlowRegion root)
    {
        var pending = new Stack<ControlFlowRegion>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var region = pending.Pop();
            yield return region;
            foreach (var child in region.NestedRegions) pending.Push(child);
        }
    }

    private static IEnumerable<ControlFlowGraph> NestedGraphs(ControlFlowGraph outer, CancellationToken cancellation)
    {
        var pending = new Stack<ControlFlowGraph>();
        pending.Push(outer);
        var count = 0;
        while (pending.Count != 0 && count++ < MaxNestedGraphs)
        {
            cancellation.ThrowIfCancellationRequested();
            var graph = pending.Pop();
            yield return graph;
            foreach (var local in graph.LocalFunctions)
                pending.Push(graph.GetLocalFunctionControlFlowGraph(local, cancellation));
            foreach (var anonymous in graph.Blocks.SelectMany(b => b.Operations.Concat(b.BranchValue is null
                ? Enumerable.Empty<IOperation>() : new[] { b.BranchValue })).SelectMany(Walk).OfType<IFlowAnonymousFunctionOperation>())
                pending.Push(graph.GetAnonymousFunctionControlFlowGraph(anonymous, cancellation));
        }
    }

    private static StaticValue Evaluate(IOperation operation, State state, int depth,
        IReadOnlyDictionary<IOperation, StaticValue> snapshots)
    {
        if (snapshots.TryGetValue(operation, out var snapshot)) return snapshot;
        if (depth >= MaxDepth) return StaticValue.Unknown("depth-budget");
        if (operation.Kind == OperationKind.Invalid || operation.Type?.TypeKind == TypeKind.Error)
            return StaticValue.Unknown("binding-error");
        if (operation.ConstantValue.HasValue) return StaticValue.Known(operation.ConstantValue.Value);
        switch (operation)
        {
            case ILocalReferenceOperation local when local.Local.RefKind == RefKind.None:
                return state.Locals.TryGetValue(local.Local, out var value) ? value : StaticValue.Unknown("unassigned-or-loop");
            case IFlowCaptureReferenceOperation capture:
                return state.Captures.TryGetValue(capture.Id, out var captured) ? captured : StaticValue.Unknown("unassigned-or-loop");
            case ISimpleAssignmentOperation assignment when !assignment.IsRef
                && assignment.Target is ILocalReferenceOperation local && local.Local.RefKind == RefKind.None:
                return Evaluate(assignment.Value, state, depth + 1, snapshots);
            case IConversionOperation conversion when conversion.Conversion.Exists && conversion.OperatorMethod is null
                && (conversion.Conversion.IsIdentity || conversion.Conversion.IsReference):
                return Evaluate(conversion.Operand, state, depth + 1, snapshots);
            case IBinaryOperation binary when binary.OperatorKind == BinaryOperatorKind.Add
                && binary.Type?.SpecialType == SpecialType.System_String && binary.OperatorMethod is null:
                return Concat(Evaluate(binary.LeftOperand, state, depth + 1, snapshots), Evaluate(binary.RightOperand, state, depth + 1, snapshots));
            case IInterpolatedStringOperation interpolated:
                var text = StaticValue.Known("");
                foreach (var part in interpolated.Parts)
                {
                    if (part is IInterpolatedStringTextOperation literal)
                        text = Concat(text, Evaluate(literal.Text, state, depth + 1, snapshots));
                    else if (part is IInterpolationOperation hole && hole.Alignment is null && hole.FormatString is null
                        && hole.Expression.Type?.SpecialType == SpecialType.System_String)
                        text = Concat(text, Evaluate(hole.Expression, state, depth + 1, snapshots));
                    else return StaticValue.Unknown("interpolation-user-code");
                }
                return text;
            case IConditionalOperation conditional:
                var condition = Evaluate(conditional.Condition, state, depth + 1, snapshots);
                if (condition.Values is { Length: 1 } && condition.Values[0] is bool choice)
                    return conditional.WhenFalse is null ? StaticValue.Unknown("missing-branch")
                        : Evaluate(choice ? conditional.WhenTrue : conditional.WhenFalse, state, depth + 1, snapshots);
                return conditional.WhenFalse is null ? StaticValue.Unknown("missing-branch")
                    : Evaluate(conditional.WhenTrue, state, depth + 1, snapshots).Join(Evaluate(conditional.WhenFalse, state, depth + 1, snapshots));
            default: return StaticValue.Unknown("nonconstant-user-code");
        }
    }

    private static StaticValue Concat(StaticValue left, StaticValue right)
    {
        if (left.Values is null) return left;
        if (right.Values is null) return right;
        StaticValue? result = null;
        foreach (var a in left.Values)
            foreach (var b in right.Values)
            {
                if (a is not null && a is not string || b is not null && b is not string) return StaticValue.Unknown("nonstring-concat");
                var value = StaticValue.Known((string?)a + (string?)b);
                result = result is null ? value : result.Join(value);
                if (result.Values is null) return result;
            }
        return result ?? StaticValue.Unknown("empty-set");
    }

    private static IEnumerable<IOperation> Walk(IOperation root)
    {
        var pending = new Stack<IOperation>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            var next = pending.Pop();
            yield return next;
            foreach (var child in next.ChildOperations) pending.Push(child);
        }
    }

    private sealed class State
    {
        internal Dictionary<ILocalSymbol, StaticValue> Locals { get; } = new(SymbolEqualityComparer.Default);
        internal Dictionary<CaptureId, StaticValue> Captures { get; } = new();
        internal void Invalidate(string reason)
        {
            foreach (var key in Locals.Keys.ToArray()) Locals[key] = StaticValue.Unknown(reason);
            foreach (var key in Captures.Keys.ToArray()) Captures[key] = StaticValue.Unknown(reason);
        }
        internal static State Merge(State[] inputs)
        {
            var result = new State();
            foreach (var key in inputs.SelectMany(s => s.Locals.Keys).Distinct<ILocalSymbol>(SymbolEqualityComparer.Default))
                result.Locals[key] = inputs.Select(s => s.Locals.TryGetValue(key, out var v) ? v : StaticValue.Unknown("unknown-branch")).Aggregate((a, b) => a.Join(b));
            foreach (var key in inputs.SelectMany(s => s.Captures.Keys).Distinct())
                result.Captures[key] = inputs.Select(s => s.Captures.TryGetValue(key, out var v) ? v : StaticValue.Unknown("unknown-branch")).Aggregate((a, b) => a.Join(b));
            return result;
        }
    }
}
