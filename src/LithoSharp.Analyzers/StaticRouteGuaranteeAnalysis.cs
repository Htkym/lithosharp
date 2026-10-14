using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using LithoSharp.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

// Explicit producer selection is separate from catalog lookup membership and scalar Unknown.
internal sealed class StaticRouteGuaranteeAnalysis
{
    internal static readonly DiagnosticDescriptor Rule = new("LSA1403", "Selected producer routes remain deferred",
        "The selected producer route scope has {0} contribution item(s) that cannot be enumerated statically",
        "LithoSharp", DiagnosticSeverity.Info, true,
        helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/src/LithoSharp.Analyzers/README.md#lsa1403");
    internal const int MaxProducers = 64;
    private readonly Compilation compilation;
    private readonly INamedTypeSymbol options;
    private readonly INamedTypeSymbol definition;
    private readonly INamedTypeSymbol extension;
    private readonly INamedTypeSymbol contribution;
    private readonly IPropertySymbol registered;
    private readonly IPropertySymbol selected;
    private readonly IPropertySymbol definitionOptions;
    private readonly Lazy<string?> fingerprint;

    private StaticRouteGuaranteeAnalysis(Compilation compilation, AnalyzerOptions inputs, CancellationToken token,
        INamedTypeSymbol options, INamedTypeSymbol definition, INamedTypeSymbol extension, INamedTypeSymbol contribution,
        IPropertySymbol registered, IPropertySymbol selected, IPropertySymbol definitionOptions)
    {
        this.compilation = compilation; this.options = options; this.definition = definition;
        this.extension = extension; this.contribution = contribution;
        this.registered = registered; this.selected = selected; this.definitionOptions = definitionOptions;
        fingerprint = new(() => StaticManifestFingerprint.Create(compilation, inputs.AdditionalFiles, inputs.AnalyzerConfigOptionsProvider, token));
    }
    internal static StaticRouteGuaranteeAnalysis? Create(Compilation compilation, AnalyzerOptions inputs, CancellationToken token)
    {
        var options = compilation.GetTypeByMetadataName("LithoSharp.SiteGenerationOptions");
        var definition = compilation.GetTypeByMetadataName("LithoSharp.SiteDefinition");
        var extension = compilation.GetTypeByMetadataName("LithoSharp.Build.ISiteBuildExtension");
        var contribution = compilation.GetTypeByMetadataName("LithoSharp.Build.SiteBuildContribution");
        if (!Core(options) || !Core(definition) || !Core(extension) || !Core(contribution)) return null;
        var registered = options!.GetMembers("Extensions").OfType<IPropertySymbol>().SingleOrDefault();
        var selected = options.GetMembers("StaticRouteGuaranteeProducers").OfType<IPropertySymbol>().SingleOrDefault();
        var owner = definition!.GetMembers("Options").OfType<IPropertySymbol>().SingleOrDefault();
        if (registered is null || selected is null || owner is null || selected.IsStatic
            || !SymbolEqualityComparer.Default.Equals(registered.Type, selected.Type)
            || !SymbolEqualityComparer.Default.Equals(owner.Type, options)) return null;
        return new(compilation, inputs, token, options, definition, extension!, contribution!, registered, selected, owner);
    }
    internal void Analyze(OperationBlockAnalysisContext context, IOperation operation)
    {
        if (operation is not IObjectCreationOperation cfg || !SymbolEqualityComparer.Default.Equals(cfg.Type, options)) return;
        var model = compilation.GetSemanticModel(cfg.Syntax.SyntaxTree);
        if (model.GetOperation(cfg.Syntax, context.CancellationToken) is not IObjectCreationOperation original
            || original.Initializer is null || original.Constructor?.Parameters.Length != 0 || StaticValueFlow.HasBindingError(original)) return;
        var selection = Assignment(original, selected);
        if (selection is null) return;
        var ownerSyntax = cfg.Syntax.Ancestors().OfType<BaseObjectCreationExpressionSyntax>().FirstOrDefault(node =>
            model.GetOperation(node, context.CancellationToken) is IObjectCreationOperation creation
            && SymbolEqualityComparer.Default.Equals(creation.Type, definition));
        if (ownerSyntax is null || model.GetOperation(ownerSyntax, context.CancellationToken) is not IObjectCreationOperation owner
            || Assignment(owner, definitionOptions)?.Value.Syntax.Span != original.Syntax.Span) return;
        var block = ownerSyntax.Ancestors().OfType<BlockSyntax>().FirstOrDefault();
        if (block is null || block.DescendantNodes().Take(StaticValueFlow.MaxOperations + 1).Count() > StaticValueFlow.MaxOperations
            || block.DescendantNodes().Any(node => node is IfStatementSyntax or SwitchStatementSyntax or ForStatementSyntax
                or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or TryStatementSyntax or GotoStatementSyntax)) return;
        var registration = Assignment(original, registered);
        if (registration is null || !Elements(registration.Value, out var registrations) || !Elements(selection.Value, out var selections)
            || selections.Length == 0 || registrations.Length > MaxProducers || selections.Length > MaxProducers) return;
        var registeredInstances = new Dictionary<(SyntaxTree Tree, int Start), IObjectCreationOperation>();
        foreach (var item in registrations)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var identity = Instance(item, model, block, context.CancellationToken, 0);
            if (identity is null) continue; // Unselected opaque registrations do not erase a proved selected membership.
            registeredInstances[(identity.Syntax.SyntaxTree, identity.Syntax.SpanStart)] = identity;
        }
        var unique = new HashSet<(SyntaxTree Tree, int Start)>();
        var deferred = new List<Location>();
        foreach (var item in selections)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var identity = Instance(item, model, block, context.CancellationToken, 0);
            if (identity is null) return;
            var key = (identity.Syntax.SyntaxTree, identity.Syntax.SpanStart);
            if (!registeredInstances.ContainsKey(key)) return; // A nonregistered selection never closes this scope.
            if (!unique.Add(key)) continue;
            if (KnownEmpty(identity, context.CancellationToken)) continue;
            if (!DocumentationRegistration(identity, model, block, ownerSyntax.SpanStart, context.CancellationToken)) return;
            deferred.Add(identity.Syntax.GetLocation());
        }
        var current = fingerprint.Value;
        if (deferred.Count == 0 || current is null) return;
        var properties = ImmutableDictionary<string, string?>.Empty
            .Add("contract", "explicit-producer-route-scope/1").Add("scope", "selected-producer-route-contributions")
            .Add("scopeState", "Deferred").Add("deferredProducerCount", deferred.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("reason", "registered-documentation-routes-not-statically-enumerable")
            .Add("siteDefinitionStart", ownerSyntax.SpanStart.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("currentFingerprint", current).Add("origin", "original-selection-and-producer-utf16-spans")
            .Add("stage", "compiler").Add("ruleOwner", "Analyzer");
        context.ReportDiagnostic(Diagnostic.Create(Rule, selection.Value.Syntax.GetLocation(), deferred, properties, deferred.Count));
    }
    private static ISimpleAssignmentOperation? Assignment(IObjectCreationOperation creation, IPropertySymbol property) =>
        creation.Initializer?.Initializers.OfType<ISimpleAssignmentOperation>().SingleOrDefault(assignment =>
            assignment.Target is IPropertyReferenceOperation target && SymbolEqualityComparer.Default.Equals(target.Property, property));

    private static bool Elements(IOperation value, out IOperation[] elements)
    {
        value = Unwrap(value);
        if (value is IArrayCreationOperation { Initializer: { } initializer }) { elements = initializer.ElementValues.ToArray(); return true; }
        if (value is ICollectionExpressionOperation expression && value.Syntax is CollectionExpressionSyntax collection
            && collection.Elements.All(element => element is ExpressionElementSyntax))
        {
            // Read semantic elements of the original explicit list; spreads/getters/factories are unsupported.
            elements = expression.Elements.Select(Unwrap).ToArray();
            return elements.Length == collection.Elements.Count;
        }
        elements = Array.Empty<IOperation>(); return false;
    }
    private IObjectCreationOperation? Instance(IOperation value, SemanticModel model, BlockSyntax block, CancellationToken token, int depth)
    {
        value = Unwrap(value);
        if (depth >= StaticValueFlow.MaxDepth || StaticValueFlow.HasBindingError(value)) return null;
        if (value is IObjectCreationOperation instance && instance.Type is INamedTypeSymbol type
            && type.IsReferenceType && type.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, extension))) return instance;
        if (value is not ILocalReferenceOperation local || local.Local.RefKind != RefKind.None
            || local.Local.DeclaringSyntaxReferences.Length != 1
            || local.Local.DeclaringSyntaxReferences[0].GetSyntax(token) is not VariableDeclaratorSyntax { Initializer: { } initializer }
            || initializer.SpanStart >= value.Syntax.SpanStart || !block.Span.Contains(initializer.Span)) return null;
        // Track only unchanged local identity. Captured/ref locals and writes are not inferred safe.
        foreach (var reference in block.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            token.ThrowIfCancellationRequested();
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(reference, token).Symbol, local.Local)) continue;
            if (reference.Ancestors().Any(node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                || reference.Ancestors().Any(node => node is AssignmentExpressionSyntax assignment && assignment.Left.Span.Contains(reference.Span)
                    || node is RefExpressionSyntax
                    || node is ArgumentSyntax argument && argument.RefKindKeyword.RawKind != 0)
                || reference.Parent is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax) return null;
        }
        var initial = model.GetOperation(initializer.Value, token);
        return initial is null ? null : Instance(initial, model, block, token, depth + 1);
    }
    private bool KnownEmpty(IObjectCreationOperation instance, CancellationToken token)
    {
        var prepare = extension.GetMembers("PrepareAsync").OfType<IMethodSymbol>().Single();
        if (instance.Type is not INamedTypeSymbol type || type.FindImplementationForInterfaceMember(prepare) is not IMethodSymbol method
            || method.DeclaringSyntaxReferences.Length != 1 || method.DeclaringSyntaxReferences[0].GetSyntax(token) is not MethodDeclarationSyntax syntax
            || syntax.DescendantNodes().Take(StaticValueFlow.MaxOperations + 1).Count() > StaticValueFlow.MaxOperations) return false;
        ExpressionSyntax? expression = syntax.ExpressionBody?.Expression;
        if (expression is null && syntax.Body is { } body)
        {
            var returns = body.DescendantNodes().OfType<ReturnStatementSyntax>().ToArray();
            if (returns.Length != 1 || returns[0].Parent != body) return false;
            expression = returns[0].Expression;
        }
        if (expression is null) return false;
        var model = compilation.GetSemanticModel(expression.SyntaxTree);
        if (model.GetOperation(expression, token) is not IInvocationOperation call || call.TargetMethod.Name != "FromResult"
            || call.TargetMethod.ContainingType.ToDisplayString() != "System.Threading.Tasks.Task"
            || call.TargetMethod.ContainingType.Locations.Any(location => location.IsInSource) || call.Arguments.Length != 1
            || call.TargetMethod.Arity != 1 || !SymbolEqualityComparer.Default.Equals(call.TargetMethod.TypeArguments[0], contribution)
            || Unwrap(call.Arguments[0].Value) is not IObjectCreationOperation result || result.Constructor?.Parameters.Length != 0
            || !SymbolEqualityComparer.Default.Equals(result.Type, contribution) || StaticValueFlow.HasBindingError(result)
            || result.Initializer?.Initializers.Any(item => item is not ISimpleAssignmentOperation) is true) return false;
        var collection = contribution.GetMembers("ContentCollections").OfType<IPropertySymbol>().Single();
        var assigned = Assignment(result, collection);
        return assigned is null || Elements(assigned.Value, out var entries) && entries.Length == 0;
    }
    private bool DocumentationRegistration(IObjectCreationOperation instance, SemanticModel model, BlockSyntax block, int ownerStart, CancellationToken token)
    {
        if (instance.Type is not INamedTypeSymbol type || type.ToDisplayString() != "LithoSharp.Mdx.DocumentationSite"
            || type.ContainingAssembly.Identity.Name != "LithoSharp.Mdx" || type.Locations.Any(location => location.IsInSource)) return false;
        foreach (var statement in block.Statements.OfType<ExpressionStatementSyntax>().Where(statement => statement.SpanStart < ownerStart))
        {
            token.ThrowIfCancellationRequested();
            if (model.GetOperation(statement.Expression, token) is not IInvocationOperation call
                || !SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, type) || call.TargetMethod.Name != "AddCollection"
                || call.TargetMethod.Parameters.Length != 1 || call.Arguments.Length != 1 || call.Instance is null
                || Unwrap(call.Arguments[0].Value) is not IObjectCreationOperation collection
                || collection.Type is not INamedTypeSymbol collectionType || collectionType.ToDisplayString() != "LithoSharp.Mdx.DocumentationCollection"
                || collectionType.ContainingAssembly.Identity.Name != "LithoSharp.Mdx" || collectionType.Locations.Any(location => location.IsInSource)) continue;
            var receiver = Instance(call.Instance, model, block, token, 0);
            if (receiver?.Syntax.Span == instance.Syntax.Span && receiver.Syntax.SyntaxTree == instance.Syntax.SyntaxTree) return true;
        }
        return false;
    }
    private static IOperation Unwrap(IOperation operation)
    {
        while (operation is IConversionOperation { OperatorMethod: null } conversion) operation = conversion.Operand;
        return operation;
    }
    private static bool Core(INamedTypeSymbol? symbol) => symbol?.ContainingAssembly.Identity.Name == "LithoSharp"
        && !symbol.Locations.Any(location => location.IsInSource);
}
