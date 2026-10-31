using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LithoSharp.HtmlParsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers;

internal sealed record HtmlOriginPiece(string Value, SyntaxTree? Tree, CSharpLiteralMap? Map, TextSpan TokenSpan);

internal sealed record HtmlOriginAlternative(string Value, IReadOnlyList<HtmlOriginPiece> Pieces)
{
    internal Location? Map(HtmlSpan span)
    {
        if (span.Start < 0 || span.Length < 0 || span.Start > Value.Length || span.Length > Value.Length - span.Start)
            return null;
        var offset = 0;
        if (span.Length == 0 && Pieces.Count > 1)
        {
            foreach (var piece in Pieces)
            {
                if (span.Start == offset || span.Start == offset + piece.Value.Length)
                    return null;
                offset += piece.Value.Length;
            }
        }
        offset = 0;
        foreach (var piece in Pieces)
        {
            if (span.Start >= offset && span.End <= offset + piece.Value.Length)
            {
                var mapped = piece.Map?.Map(span.Start - offset, span.Length);
                return mapped is { } range && piece.Tree is { } tree ? Location.Create(tree, range) : null;
            }
            offset += piece.Value.Length;
        }
        return null;
    }

    internal bool Same(HtmlOriginAlternative other) => Value == other.Value && Pieces.Count == other.Pieces.Count
        && Pieces.Zip(other.Pieces, (a, b) => a.Value == b.Value && ReferenceEquals(a.Tree, b.Tree)
            && a.TokenSpan == b.TokenSpan && (a.Map is null) == (b.Map is null)).All(equal => equal);
}

// A null container is missing proof, never a truncated list of successful alternatives.
internal sealed class HtmlStringOrigins
{
    internal const int MaxPieces = 64;
    internal const int MaxAlternatives = 64;
    internal IReadOnlyList<HtmlOriginAlternative> Alternatives { get; }
    private HtmlStringOrigins(IReadOnlyList<HtmlOriginAlternative> alternatives) => Alternatives = alternatives;

    internal static HtmlStringOrigins? Create(IEnumerable<HtmlOriginAlternative> alternatives)
    {
        var result = new List<HtmlOriginAlternative>();
        foreach (var alternative in alternatives)
        {
            if (alternative.Pieces.Count > MaxPieces || alternative.Value.Length > StaticValueFlow.MaxStringLength)
                return null;
            if (!result.Any(prior => prior.Same(alternative)))
                result.Add(alternative);
            if (result.Count > MaxAlternatives)
                return null;
        }
        return result.Count == 0 ? null : new(result.AsReadOnly());
    }

    internal static HtmlStringOrigins Unmapped(string value) => new(new[] {
        new HtmlOriginAlternative(value, new[] { new HtmlOriginPiece(value, null, null, default) }) });
    internal static HtmlStringOrigins? Join(HtmlStringOrigins? a, HtmlStringOrigins? b)
        => a is null || b is null ? null : Create(a.Alternatives.Concat(b.Alternatives));
    internal static HtmlStringOrigins? Concat(HtmlStringOrigins? a, HtmlStringOrigins? b)
    {
        if (a is null || b is null)
            return null;
        var result = new List<HtmlOriginAlternative>();
        foreach (var left in a.Alternatives)
            foreach (var right in b.Alternatives)
            {
                if (left.Pieces.Count + right.Pieces.Count > MaxPieces || right.Value.Length > StaticValueFlow.MaxStringLength - left.Value.Length)
                    return null;
                var next = new HtmlOriginAlternative(left.Value + right.Value, left.Pieces.Concat(right.Pieces).ToArray());
                if (!result.Any(prior => prior.Same(next)))
                    result.Add(next);
                if (result.Count > MaxAlternatives)
                    return null;
            }
        return Create(result);
    }
}

internal sealed class HtmlOriginProvider(Compilation compilation, CancellationToken cancellation)
{
    private readonly Dictionary<ISymbol, HtmlStringOrigins?> constants = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> resolving = new(SymbolEqualityComparer.Default);

    internal HtmlStringOrigins? Evaluate(IOperation operation, StaticValue scalar,
        Func<IOperation, StaticValue> snapshot, Func<ILocalSymbol, StaticValue> local, Func<CaptureId, StaticValue> capture)
    {
        cancellation.ThrowIfCancellationRequested();
        if (scalar.Values is null || scalar.Values.Any(value => value is not string))
            return null;
        var result = Build(operation, snapshot, local, capture, 0);
        if (result is null)
            return null;
        // Values are authoritative; provenance never changes scalar semantics.
        return scalar.Values.Cast<string>().All(value => result.Alternatives.Any(a => a.Value == value))
            && result.Alternatives.All(a => scalar.Values.Contains(a.Value)) ? result : null;
    }

    private HtmlStringOrigins? Build(IOperation operation, Func<IOperation, StaticValue> snapshot,
        Func<ILocalSymbol, StaticValue> local, Func<CaptureId, StaticValue> capture, int depth)
    {
        cancellation.ThrowIfCancellationRequested();
        if (depth >= StaticValueFlow.MaxDepth || operation.Syntax.Span.Length > HtmlLiteralAnalysis.MaxSourceLength)
            return null;
        var syntaxDepth = 0;
        var outerSyntax = operation.Syntax;
        while (outerSyntax.Parent is ParenthesizedExpressionSyntax parent)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++syntaxDepth >= HtmlLiteralAnalysis.MaxExpressionDepth || parent.Span.Length > HtmlLiteralAnalysis.MaxSourceLength)
                return null;
            outerSyntax = parent;
        }
        HtmlStringOrigins? Child(IOperation child) => snapshot(child).Origins;
        if (operation is ILiteralOperation && operation.Type?.SpecialType == SpecialType.System_String
            && operation.ConstantValue.HasValue && operation.ConstantValue.Value is string text)
        {
            var syntax = operation.Syntax;
            while (syntax is ParenthesizedExpressionSyntax p)
                syntax = p.Expression;
            CSharpLiteralMap? map = syntax is LiteralExpressionSyntax literal ? CSharpLiteralMap.Create(literal.Token)
                : syntax is InterpolatedStringTextSyntax interpolated ? CSharpLiteralMap.CreateInterpolated(interpolated, text) : null;
            var tokenSpan = syntax is LiteralExpressionSyntax token ? token.Token.Span
                : syntax is InterpolatedStringTextSyntax part ? part.TextToken.Span : syntax.Span;
            return HtmlStringOrigins.Create(new[] { new HtmlOriginAlternative(text,
                new[] { new HtmlOriginPiece(text, map is null ? null : syntax.SyntaxTree, map, tokenSpan) }) });
        }
        switch (operation)
        {
            case ILocalReferenceOperation reference when reference.Local.IsConst:
                return Constant(reference.Local, reference.ConstantValue);
            case IFieldReferenceOperation reference when reference.Field.IsConst:
                return Constant(reference.Field, reference.ConstantValue);
            case ILocalReferenceOperation reference when reference.Local.RefKind == RefKind.None:
                return local(reference.Local).Origins;
            case IFlowCaptureReferenceOperation reference:
                return capture(reference.Id).Origins;
            case IFlowCaptureOperation captured:
                return Child(captured.Value);
            case ISimpleAssignmentOperation assignment when !assignment.IsRef && assignment.Target is ILocalReferenceOperation target && target.Local.RefKind == RefKind.None:
                return Child(assignment.Value);
            case IParenthesizedOperation parenthesized:
                return Child(parenthesized.Operand);
            case IConversionOperation converted when converted.IsImplicit && converted.Conversion.IsIdentity && converted.OperatorMethod is null:
                return Child(converted.Operand);
            case IBinaryOperation binary when binary.OperatorKind == BinaryOperatorKind.Add && binary.OperatorMethod is null && !binary.IsLifted
                && binary.Type?.SpecialType == SpecialType.System_String
                && binary.LeftOperand.Type?.SpecialType == SpecialType.System_String && binary.RightOperand.Type?.SpecialType == SpecialType.System_String:
                return HtmlStringOrigins.Concat(Child(binary.LeftOperand), Child(binary.RightOperand));
            case IInterpolatedStringTextOperation part:
                return Child(part.Text);
            case IInterpolationOperation hole when hole.Alignment is null && hole.FormatString is null && hole.Expression.Type?.SpecialType == SpecialType.System_String:
                return Child(hole.Expression);
            case IInterpolatedStringOperation interpolation:
                HtmlStringOrigins? result = HtmlStringOrigins.Create(new[] { new HtmlOriginAlternative("", Array.Empty<HtmlOriginPiece>()) });
                foreach (var part in interpolation.Parts)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (part is IInterpolationOperation h && (h.Alignment is not null || h.FormatString is not null || h.Expression.Type?.SpecialType != SpecialType.System_String))
                        return null;
                    var partOrigins = part is IInterpolatedStringTextOperation literalPart ? Child(literalPart.Text)
                        : part is IInterpolationOperation holePart ? Child(holePart.Expression) : null;
                    result = HtmlStringOrigins.Concat(result, partOrigins);
                    if (result is null)
                        return null;
                }
                return result;
            case IConditionalOperation conditional when conditional.WhenFalse is not null:
                var condition = snapshot(conditional.Condition).Values;
                return condition is { Length: 1 } && condition[0] is bool choice
                    ? Child(choice ? conditional.WhenTrue : conditional.WhenFalse)
                    : HtmlStringOrigins.Join(Child(conditional.WhenTrue), Child(conditional.WhenFalse));
            default:
                return null;
        }
    }

    private HtmlStringOrigins? Constant(ISymbol symbol, Optional<object?> value)
    {
        if (!value.HasValue || value.Value is not string text)
            return null;
        if (constants.TryGetValue(symbol, out var found))
            return found;
        if (resolving.Count >= StaticValueFlow.MaxDepth || !resolving.Add(symbol))
            return null;
        try
        {
            var declarations = symbol.DeclaringSyntaxReferences;
            if (declarations.Length != 1)
                return constants[symbol] = HtmlStringOrigins.Unmapped(text);
            var syntax = declarations[0].GetSyntax(cancellation);
            if (syntax is not VariableDeclaratorSyntax { Initializer: { } initializer } || !compilation.SyntaxTrees.Contains(syntax.SyntaxTree))
                return constants[symbol] = HtmlStringOrigins.Unmapped(text);
            var model = compilation.GetSemanticModel(syntax.SyntaxTree);
            var operation = model.GetOperation(initializer.Value, cancellation);
            if (operation is null || StaticValueFlow.HasBindingError(operation) || !operation.ConstantValue.HasValue || !Equals(operation.ConstantValue.Value, text))
                return null;
            var readDepth = 0;
            StaticValue Read(IOperation child)
            {
                if (!child.ConstantValue.HasValue)
                    return StaticValue.Unknown("nonconstant-user-code");
                if (++readDepth + resolving.Count >= StaticValueFlow.MaxDepth)
                {
                    readDepth--;
                    return StaticValue.Known(child.ConstantValue.Value);
                }
                try
                {
                    return StaticValue.Known(child.ConstantValue.Value).WithOrigins(Build(child, Read,
                    _ => StaticValue.Unknown("nonconstant-user-code"), _ => StaticValue.Unknown("nonconstant-user-code"), readDepth + resolving.Count));
                }
                finally
                {
                    readDepth--;
                }
            }
            var origins = Build(operation, Read, _ => StaticValue.Unknown("nonconstant-user-code"), _ => StaticValue.Unknown("nonconstant-user-code"), resolving.Count);
            return constants[symbol] = origins;
        }
        finally
        {
            resolving.Remove(symbol);
        }
    }
}
