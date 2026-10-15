using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

// Only a transform's own copied inputs are closed. No site, file or asset producer is executed.
internal sealed class StaticAssetInputAnalysis
{
    internal static readonly DiagnosticDescriptor Rule = new("LSA1203", "Asset is absent from the transform inputs",
        "Every known asset instance is absent from this transform's declared inputs", "LithoSharp", DiagnosticSeverity.Error, true,
        helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/src/LithoSharp.Analyzers/README.md#lsa1203");
    private readonly INamedTypeSymbol transform;
    private readonly INamedTypeSymbol asset;
    private readonly INamedTypeSymbol context;
    private StaticAssetInputAnalysis(INamedTypeSymbol transform, INamedTypeSymbol asset, INamedTypeSymbol context)
    { this.transform = transform; this.asset = asset; this.context = context; }

    internal static StaticAssetInputAnalysis? Create(Compilation compilation)
    {
        var transform = compilation.GetTypeByMetadataName("LithoSharp.SiteAssetTransform");
        var asset = compilation.GetTypeByMetadataName("LithoSharp.SiteAsset");
        var context = compilation.GetTypeByMetadataName("LithoSharp.SiteAssetTransformContext");
        return Core(transform) && Core(asset) && Core(context) ? new(transform!, asset!, context!) : null;
    }
    private static bool Core(INamedTypeSymbol? type) => type is { IsReferenceType: true }
        && type.ContainingAssembly.Identity.Name == "LithoSharp" && !type.Locations.Any(l => l.IsInSource);

    internal void Analyze(OperationAnalysisContext analysis)
    {
        analysis.CancellationToken.ThrowIfCancellationRequested();
        if (analysis.Operation is not IObjectCreationOperation creation || !Same(creation.Type, transform)
            || creation.Constructor is not { DeclaredAccessibility: Accessibility.Public, Parameters.Length: 6 }
            || StaticValueFlow.HasBindingError(creation)) return;
        var inputs = creation.Arguments.SingleOrDefault(a => a.Parameter is { Ordinal: 2, Name: "inputs" });
        var handlerArgument = creation.Arguments.SingleOrDefault(a => a.Parameter is { Ordinal: 4, Name: "handler" });
        var handlerValue = handlerArgument is null ? null : Unwrap(handlerArgument.Value);
        if (handlerValue is IDelegateCreationOperation delegateCreation) handlerValue = Unwrap(delegateCreation.Target);
        if (inputs is null || handlerValue is not IAnonymousFunctionOperation handler || handler.Symbol.Parameters.Length != 2
            || !Same(handler.Symbol.Parameters[0].Type, context)) return;
        var root = creation.Syntax.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
        if (root is null) return;
        var nodes = root.DescendantNodes().Take(StaticValueFlow.MaxOperations + 1).ToArray();
        if (nodes.Length > StaticValueFlow.MaxOperations || nodes.Any(n => n is IfStatementSyntax or SwitchStatementSyntax
            or ForStatementSyntax or CommonForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
            or TryStatementSyntax or GotoStatementSyntax)) return;
        var model = analysis.Compilation.GetSemanticModel(creation.Syntax.SyntaxTree);
        var parameter = handler.Symbol.Parameters[0];
        if (!SafeContext()) return;

        // Do not follow local arrays or aliases: later constructor arguments can change them
        // before the constructor body copies inputs. Inline lists have no shared array identity.
        var value = Unwrap(inputs.Value);
        IOperation[] elements;
        if (value is IArrayCreationOperation { Initializer: { } initializer }) elements = initializer.ElementValues.ToArray();
        else if (value is IArrayCreationOperation { Initializer: null, DimensionSizes.Length: 1 } empty
            && empty.DimensionSizes[0].ConstantValue is { HasValue: true, Value: 0 }) elements = Array.Empty<IOperation>();
        else if (value is ICollectionExpressionOperation expression && value.Syntax is CollectionExpressionSyntax syntax
            && syntax.Elements.All(e => e is ExpressionElementSyntax)) elements = expression.Elements.ToArray();
        else return;
        if (elements.Length > StaticValueFlow.MaxCandidates) return;
        var declared = new HashSet<(SyntaxTree, int)>();
        foreach (var element in elements)
        {
            var identities = Identities(element, 0);
            if (identities is null || identities.Length != 1) return;
            declared.Add(identities[0]);
        }
        foreach (var syntax in handler.Syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            analysis.CancellationToken.ThrowIfCancellationRequested();
            if (!InHandler(syntax) || model.GetOperation(syntax, analysis.CancellationToken) is not IInvocationOperation call
                || !InputCall(call) || !OwnReceiver(call) || StaticValueFlow.HasBindingError(call)) continue;
            var argument = call.Arguments.SingleOrDefault(a => a.Parameter?.Ordinal == 0);
            if (argument is null || argument.IsImplicit) continue;
            var identities = Identities(argument.Value, 0);
            if (identities is null || identities.Length == 0 || identities.Any(declared.Contains)) continue;
            var properties = ImmutableDictionary<string, string?>.Empty.Add("contract", "explicit-transform-inputs/1")
                .Add("scope", "asset-transform-inputs").Add("scopeState", "Closed").Add("staticState", "Known")
                .Add("reason", "all-known-input-instances-absent").Add("origin", "original-argument-utf16-span")
                .Add("candidateCount", identities.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            analysis.ReportDiagnostic(Diagnostic.Create(Rule, argument.Value.Syntax.GetLocation(),
                new[] { inputs.Value.Syntax.GetLocation() }, properties));
        }

        bool InHandler(SyntaxNode node) => SameSyntax(node.Ancestors().FirstOrDefault(n => n is AnonymousFunctionExpressionSyntax
            or LocalFunctionStatementSyntax), handler.Syntax);
        bool OwnReceiver(IInvocationOperation call) => call.Instance is not null
            && Unwrap(call.Instance) is IParameterReferenceOperation receiver && Same(receiver.Parameter, parameter);
        bool InputCall(IInvocationOperation call) => Same(call.TargetMethod.ContainingType, context)
            && !call.TargetMethod.IsStatic && call.TargetMethod.Parameters.Length == 1
            && call.TargetMethod.Parameters[0].RefKind == RefKind.None && Same(call.TargetMethod.Parameters[0].Type, asset)
            && call.TargetMethod.Name is "GetUrl" or "OpenRead";
        bool SafeContext()
        {
            foreach (var reference in handler.Syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                analysis.CancellationToken.ThrowIfCancellationRequested();
                if (!Same(model.GetSymbolInfo(reference, analysis.CancellationToken).Symbol, parameter)) continue;
                if (!InHandler(reference) || reference.Parent is not MemberAccessExpressionSyntax access
                    || access.Expression != reference || access.Parent is not InvocationExpressionSyntax invoke
                    || model.GetOperation(invoke, analysis.CancellationToken) is not IInvocationOperation call
                    || !OwnReceiver(call) || !Same(call.TargetMethod.ContainingType, context)
                    || call.TargetMethod.Name is not ("GetUrl" or "OpenRead" or "WriteAsync")) return false;
            }
            return true;
        }
        (SyntaxTree, int)[]? Identities(IOperation operation, int depth)
        {
            analysis.CancellationToken.ThrowIfCancellationRequested();
            if (depth >= StaticValueFlow.MaxDepth || StaticValueFlow.HasBindingError(operation)) return null;
            operation = Unwrap(operation);
            if (operation is IObjectCreationOperation instance && Same(instance.Type, asset))
                return new[] { (instance.Syntax.SyntaxTree, instance.Syntax.SpanStart) };
            if (operation is IConditionalOperation { WhenFalse: { } other } condition)
            {
                var left = Identities(condition.WhenTrue, depth + 1); var right = Identities(other, depth + 1);
                if (left is null || right is null) return null;
                var all = left.Concat(right).Distinct().Take(StaticValueFlow.MaxCandidates + 1).ToArray();
                return all.Length <= StaticValueFlow.MaxCandidates ? all : null;
            }
            if (operation is not ILocalReferenceOperation local || local.Local.RefKind != RefKind.None
                || local.Local.DeclaringSyntaxReferences.Length != 1
                || local.Local.DeclaringSyntaxReferences[0].GetSyntax(analysis.CancellationToken) is not VariableDeclaratorSyntax { Initializer: { } init }
                || !root.Span.Contains(init.Span) || init.SpanStart >= operation.Syntax.SpanStart) return null;
            foreach (var reference in nodes.OfType<IdentifierNameSyntax>())
            {
                analysis.CancellationToken.ThrowIfCancellationRequested();
                if (!Same(model.GetSymbolInfo(reference, analysis.CancellationToken).Symbol, local.Local)) continue;
                // Check the whole owner, including after construction and deferred/nested bodies.
                if (reference.Ancestors().Any(n => n is AssignmentExpressionSyntax assignment && assignment.Left.Span.Contains(reference.Span)
                    || n is RefExpressionSyntax || n is ArgumentSyntax a && a.RefKindKeyword.RawKind != 0)
                    || reference.Parent is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax) return null;
                var closure = reference.Ancestors().FirstOrDefault(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
                if (closure is not null && !SameSyntax(closure, handler.Syntax)) return null;
                var alias = reference.Ancestors().OfType<EqualsValueClauseSyntax>().FirstOrDefault();
                if (alias?.Parent is VariableDeclaratorSyntax declaration && IdentityExpression(alias.Value)
                    && model.GetDeclaredSymbol(declaration, analysis.CancellationToken) is ILocalSymbol) continue;
                var argument = reference.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault();
                if (argument is null || model.GetOperation(argument, analysis.CancellationToken) is not IArgumentOperation bound) return null;
                if (bound.Parent is IObjectCreationOperation owner && SameSyntax(owner.Syntax, creation.Syntax) && bound.Parameter?.Ordinal == 2) continue;
                if (bound.Parent is IInvocationOperation use && InputCall(use) && OwnReceiver(use) && InHandler(reference)) continue;
                return null;
            }
            var initial = model.GetOperation(init.Value, analysis.CancellationToken);
            return initial is null ? null : Identities(initial, depth + 1);
        }
    }
    private static bool IdentityExpression(ExpressionSyntax expression) => expression is IdentifierNameSyntax
        || expression is ParenthesizedExpressionSyntax parenthesized && IdentityExpression(parenthesized.Expression)
        || expression is ConditionalExpressionSyntax conditional && IdentityExpression(conditional.WhenTrue) && IdentityExpression(conditional.WhenFalse);
    private static bool Same(ISymbol? left, ISymbol? right) => SymbolEqualityComparer.Default.Equals(left, right);
    private static bool SameSyntax(SyntaxNode? left, SyntaxNode right) => left is not null && left.SyntaxTree == right.SyntaxTree && left.Span == right.Span;
    private static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation { OperatorMethod: null } conversion) operation = conversion.Operand;
        return operation;
    }
}
