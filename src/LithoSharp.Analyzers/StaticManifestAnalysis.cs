using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using LithoSharp.Internal;
using LithoSharp.Routing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

internal sealed class StaticManifestAnalysis
{
    internal static readonly DiagnosticDescriptor Missing = new("LSA1201", "Route absent from a fresh closed manifest",
        "Every known path is absent from the selected fresh Closed route lookup manifest '{0}/{1}'", "LithoSharp", DiagnosticSeverity.Error, true,
        helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/src/LithoSharp.Analyzers/README.md#lsa1201");
    internal static readonly DiagnosticDescriptor Stale = new("LSA1205", "Static manifest provenance is stale",
        "The selected static manifest differs from current compiler inputs; route absence is Deferred", "LithoSharp", DiagnosticSeverity.Info, true,
        helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/src/LithoSharp.Analyzers/README.md#lsa1205");
    internal static readonly DiagnosticDescriptor TypedReference = new("LSA1401", "Use a generated page reference",
        "This literal route has one equivalent generated page reference", "LithoSharp", DiagnosticSeverity.Info, true,
        helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/src/LithoSharp.Analyzers/README.md#lsa1401");
    internal static ImmutableArray<DiagnosticDescriptor> Rules => ImmutableArray.Create(Missing, Stale, TypedReference);
    private readonly Compilation compilation;
    private readonly IMethodSymbol lookup;
    private readonly Lazy<string?> fingerprint;
    private readonly ImmutableArray<AdditionalText> files;
    private readonly AnalyzerConfigOptionsProvider options;
    private readonly ConcurrentDictionary<IPropertySymbol, Facts> facts = new(SymbolEqualityComparer.Default);
    private StaticManifestAnalysis(Compilation compilation, AnalyzerOptions analyzerOptions, CancellationToken cancellation, IMethodSymbol lookup)
    {
        this.compilation = compilation;
        this.lookup = lookup;
        files = analyzerOptions.AdditionalFiles;
        options = analyzerOptions.AnalyzerConfigOptionsProvider;
        fingerprint = new(() => StaticManifestFingerprint.Create(compilation, files, options, cancellation));
    }
    internal static StaticManifestAnalysis? Create(Compilation compilation, AnalyzerOptions options, CancellationToken cancellation)
    {
        var type = compilation.GetTypeByMetadataName("LithoSharp.Content.StaticSiteManifest");
        if (!Core(type)) return null;
        var method = type!.GetMembers("GetUrl").OfType<IMethodSymbol>().SingleOrDefault(m => !m.IsStatic && m.Arity == 0
            && m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_String);
        return method is null ? null : new(compilation, options, cancellation, method);
    }
    internal void Analyze(OperationBlockAnalysisContext context, IOperation operation, Func<IOperation, StaticValue> evaluate)
    {
        if (operation is not IInvocationOperation call || !SymbolEqualityComparer.Default.Equals(call.TargetMethod.OriginalDefinition, lookup)
            || StaticValueFlow.HasBindingError(call)) return;
        var property = Unwrap(call.Instance) as IPropertyReferenceOperation;
        // CFG captures the receiver before evaluating a conditional argument. Recover only the
        // original direct property expression; Read still verifies an immutable generated getter.
        if (property is null && call.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access })
            property = compilation.GetSemanticModel(access.SyntaxTree).GetOperation(access.Expression, context.CancellationToken) as IPropertyReferenceOperation;
        if (property is null || !property.Property.IsStatic) return;
        var scope = facts.GetOrAdd(property.Property, p => Read(p, context.CancellationToken));
        if (scope.State == "Stale")
        {
            context.ReportDiagnostic(Diagnostic.Create(Stale, property.Syntax.GetLocation(), Properties(scope)));
            return;
        }
        if (scope.State != "Closed") return;
        var argument = call.Arguments.SingleOrDefault(a => a.Parameter?.Ordinal == 0);
        if (argument is null || argument.IsImplicit) return;
        if (argument.Value.Syntax is LiteralExpressionSyntax literal && TryGetTypedReference(literal, context.CancellationToken) is not null)
            context.ReportDiagnostic(Diagnostic.Create(TypedReference, literal.GetLocation(), Properties(scope)));
        var values = evaluate(argument.Value);
        if (values.Values is null || values.Values.Length == 0 || !values.Values.All(v => v is string path && !scope.Paths.Contains(path))) return;
        context.ReportDiagnostic(Diagnostic.Create(Missing, argument.Value.Syntax.GetLocation(), Properties(scope)
            .Add("candidateCount", values.Values.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)), scope.Site, scope.Variant));
    }
    // Shared by the diagnostic and IDE action. Every use revalidates current compiler
    // inputs and the complete Pages initializer; diagnostic properties are not proof.
    internal string? TryGetTypedReference(LiteralExpressionSyntax literal, CancellationToken cancellation)
    {
        var model = compilation.GetSemanticModel(literal.SyntaxTree);
        if (literal.Parent is not ArgumentSyntax argument || argument.Parent?.Parent is not InvocationExpressionSyntax invocation
            || model.GetOperation(invocation, cancellation) is not IInvocationOperation call
            || !SymbolEqualityComparer.Default.Equals(call.TargetMethod.OriginalDefinition, lookup)
            || StaticValueFlow.HasBindingError(call) || Unwrap(call.Instance) is not IPropertyReferenceOperation receiver
            || !receiver.Property.IsStatic || literal.Token.Value is not string path) return null;
        var scope = facts.GetOrAdd(receiver.Property, p => Read(p, cancellation));
        if (scope.State != "Closed" || scope.Catalog is null || scope.Entries is null || !scope.Paths.Contains(path)) return null;
        // A user initializer can reenter a lookup while Catalog is still null. The
        // added Pages initializer must not become permanently faulted in that case.
        foreach (var member in receiver.Property.ContainingType.GetMembers().Where(m => m.IsStatic && !m.IsImplicitlyDeclared))
        {
            if (member is IFieldSymbol { IsConst: false } field && field.DeclaringSyntaxReferences.Any(r =>
                r.GetSyntax(cancellation) is VariableDeclaratorSyntax { Initializer: not null })) return null;
            if (member is not IPropertySymbol rootProperty || SymbolEqualityComparer.Default.Equals(rootProperty, receiver.Property)
                || SymbolEqualityComparer.Default.Equals(rootProperty, scope.Catalog)) continue;
            foreach (var syntax in rootProperty.DeclaringSyntaxReferences)
            {
                if (syntax.GetSyntax(cancellation) is not PropertyDeclarationSyntax { Initializer: not null } rootNode) continue;
                var rootModel = compilation.GetSemanticModel(rootNode.SyntaxTree);
                if (rootProperty.Name != "Binder" || !StaticManifestFingerprint.OwnGenerated(rootNode.SyntaxTree)
                    || Unwrap(rootModel.GetOperation(rootNode.Initializer!.Value, cancellation)) is not IObjectCreationOperation binder
                    || binder.Constructor is not { IsImplicitlyDeclared: true } || binder.Initializer is not null
                    || binder.Type is not INamedTypeSymbol binderType || binderType.BaseType?.SpecialType != SpecialType.System_Object
                    || binderType.StaticConstructors.Length != 0
                    || binderType.GetMembers().OfType<IFieldSymbol>().Any(f => !f.IsImplicitlyDeclared && !f.IsConst)
                    || binderType.GetMembers().OfType<IPropertySymbol>().Any(p => p.DeclaringSyntaxReferences.Any(r =>
                        r.GetSyntax(cancellation) is PropertyDeclarationSyntax { Initializer: not null }))) return null;
            }
        }
        var pages = receiver.Property.ContainingType.GetTypeMembers("Pages");
        if (pages.Length != 1 || !pages[0].IsStatic || pages[0].StaticConstructors.Any(c => !c.IsImplicitlyDeclared)) return null;
        var candidates = new List<IPropertySymbol>();
        foreach (var member in pages[0].GetMembers().Where(m => !m.IsImplicitlyDeclared && m is not IMethodSymbol { MethodKind: MethodKind.PropertyGet }))
        {
            cancellation.ThrowIfCancellationRequested();
            if (member is not IPropertySymbol page || !page.IsStatic || page.SetMethod is not null
                || page.DeclaredAccessibility != Accessibility.Public || page.DeclaringSyntaxReferences.Length != 1
                || page.Type is not INamedTypeSymbol pageType || !Core(pageType)
                || pageType.OriginalDefinition.ToDisplayString() != "LithoSharp.Pages.PageRef<TPage>"
                || page.DeclaringSyntaxReferences[0].GetSyntax(cancellation) is not PropertyDeclarationSyntax node
                || !StaticManifestFingerprint.OwnGenerated(node.SyntaxTree) || node.Initializer is null
                || node.AccessorList?.Accessors.Count != 1 || node.AccessorList.Accessors[0].Body is not null
                || node.AccessorList.Accessors[0].ExpressionBody is not null) return null;
            var pageModel = compilation.GetSemanticModel(node.SyntaxTree);
            if (Unwrap(pageModel.GetOperation(node.Initializer.Value, cancellation)) is not IObjectCreationOperation create
                || create.Arguments.Length != 2 || create.Initializer is not null
                || Unwrap(create.Arguments[0].Value) is not IObjectCreationOperation id
                || !Named(id.Type, "LithoSharp.Pages.PageId") || id.Arguments.Length != 1 || id.Initializer is not null
                || Unwrap(create.Arguments[1].Value) is not IPropertyReferenceOperation { Property.Name: "Route" } route
                || !Named(route.Property.ContainingType, "LithoSharp.Content.StaticContentCatalogEntry")
                || Unwrap(route.Instance) is not IInvocationOperation get || get.TargetMethod.Name != "GetEntry"
                || !Named(get.TargetMethod.ContainingType, "LithoSharp.Content.StaticContentCatalog") || get.Arguments.Length != 1
                || Unwrap(get.Instance) is not IPropertyReferenceOperation catalog
                || !SymbolEqualityComparer.Default.Equals(catalog.Property, scope.Catalog)
                || Unwrap(get.Arguments[0].Value) is not IObjectCreationOperation entryId
                || !Named(entryId.Type, "LithoSharp.Content.ContentEntryId") || entryId.Arguments.Length != 1 || entryId.Initializer is not null
                || entryId.Arguments[0].Value.ConstantValue.Value is not string key
                || !scope.Entries.TryGetValue(key, out var entryPath)
                || id.Arguments[0].Value.ConstantValue.Value as string != $"page:collection:{scope.CollectionId.Length}:{scope.CollectionId}:{key.Length}:{key}") return null;
            if (entryPath == path) candidates.Add(page);
        }
        if (candidates.Count != 1) return null;
        var candidate = candidates[0];
        var name = SyntaxFacts.GetKeywordKind(candidate.Name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(candidate.Name) != SyntaxKind.None
            ? "@" + candidate.Name : candidate.Name;
        return candidate.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "." + name + ".Route.PublicPath";
    }
    private static ImmutableDictionary<string, string?> Properties(Facts scope) => ImmutableDictionary<string, string?>.Empty
        .Add("coverage", scope.State).Add("contract", StaticManifestFingerprint.Contract).Add("origin", "original-reference-utf16-span")
        .Add("site", scope.Site).Add("variant", scope.Variant).Add("expectedFingerprint", scope.Expected).Add("currentFingerprint", scope.Current)
        .Add("stage", "compiler").Add("ruleOwner", "Analyzer");
    private Facts Read(IPropertySymbol property, CancellationToken cancellation)
    {
        var unavailable = new Facts("Unavailable", "", "", "", "", new HashSet<string>(StringComparer.Ordinal));
        // A get-only static auto-property may be reassigned by an explicit static constructor,
        // including one in another partial declaration. This applies to both Manifest and Catalog.
        // Do not execute or attempt to prove arbitrary constructor bodies safe.
        if (property.ContainingType.StaticConstructors.Any(constructor => !constructor.IsImplicitlyDeclared)) return unavailable;
        if (property.DeclaringSyntaxReferences.Length != 1) return unavailable;
        var node = property.DeclaringSyntaxReferences[0].GetSyntax(cancellation) as PropertyDeclarationSyntax;
        if (node?.Initializer is null || !StaticManifestFingerprint.OwnGenerated(node.SyntaxTree) || property.SetMethod is not null) return unavailable;
        var sources = property.GetAttributes().Where(a => Named(a.AttributeClass, "LithoSharp.Content.StaticSiteManifestSourceAttribute")).ToArray();
        var declarations = property.ContainingType.GetAttributes().Where(a => Named(a.AttributeClass, "LithoSharp.Content.StaticContentCollectionAttribute")).ToArray();
        if (sources.Length != 1 || declarations.Length != 1) return unavailable;
        var source = sources[0];
        var declaration = declarations[0];
        if (source is null || declaration is null || source.ConstructorArguments.Length != 6 || declaration.ConstructorArguments.Length != 3
            || !declaration.NamedArguments.Any(a => a.Key == "EmitStaticSiteManifest" && a.Value.Value is true)) return unavailable;
        var args = source.ConstructorArguments;
        if (args[0].Value as string != StaticManifestFingerprint.Contract || args[1].Value is not string expected
            || args[2].Value is not string state || args[3].Value is not string site || args[4].Value is not string variant
            || args[5].Kind != TypedConstantKind.Array || args[5].IsNull || args[5].Values.Any(v => v.Value is not string)) return unavailable;
        string Scope(string name) => declaration.NamedArguments.FirstOrDefault(a => a.Key == name).Value.Value as string ?? "";
        if (site != Scope("Site") || variant != Scope("Variant")) return unavailable;
        if (state != "Closed") return new("Open", site, variant, expected, "", unavailable.Paths);
        var current = fingerprint.Value;
        if (current is null) return unavailable;
        if (expected != current) return new("Stale", site, variant, expected, current, unavailable.Paths);
        var paths = new HashSet<string>(args[5].Values.Select(v => (string)v.Value!), StringComparer.Ordinal);
        if (paths.Count != args[5].Values.Length) return unavailable;
        var declaredPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            string Get(string key) => options.GetOptions(file).TryGetValue("build_metadata.AdditionalFiles." + key, out var value) ? value : "";
            if (Get("LithoSharpCollection") != declaration.ConstructorArguments[2].Value as string
                || Get("LithoSharpSite") != site || Get("LithoSharpVariant") != variant) continue;
            var routeText = Get("LithoSharpRoute");
            try
            {
                var route = routeText.EndsWith("/", StringComparison.Ordinal) ? SiteRoute.ForDirectoryIndex(routeText) : SiteRoute.ForFile(routeText);
                if (!declaredPaths.Add(route.PublicPath)) return unavailable;
            }
            catch (Exception e) when (e is ArgumentException or UriFormatException) { return unavailable; }
        }
        if (!declaredPaths.SetEquals(paths)) return unavailable;
        // Verify that the generated runtime initializer actually uses the same catalog and paths.
        var model = compilation.GetSemanticModel(node.SyntaxTree);
        if (Unwrap(model.GetOperation(node.Initializer.Value, cancellation)) is not IInvocationOperation create
            || !Named(create.TargetMethod.ContainingType, "LithoSharp.Content.StaticSiteManifest") || create.TargetMethod.Name != "Create"
            || create.Arguments.Length != 2 || create.Arguments[1].Value.ConstantValue.Value as string != expected
            || Unwrap(create.Arguments[0].Value) is not IPropertyReferenceOperation catalog || !catalog.Property.IsStatic
            || !SymbolEqualityComparer.Default.Equals(catalog.Property.ContainingType, property.ContainingType)) return unavailable;
        if (catalog.Property.DeclaringSyntaxReferences.Length != 1) return unavailable;
        var catalogNode = catalog.Property.DeclaringSyntaxReferences[0].GetSyntax(cancellation) as PropertyDeclarationSyntax;
        if (catalogNode?.Initializer is null || !StaticManifestFingerprint.OwnGenerated(catalogNode.SyntaxTree) || catalog.Property.SetMethod is not null) return unavailable;
        var catalogModel = compilation.GetSemanticModel(catalogNode.SyntaxTree);
        if (Unwrap(catalogModel.GetOperation(catalogNode.Initializer.Value, cancellation)) is not IObjectCreationOperation catalogCreate
            || !Named(catalogCreate.Type, "LithoSharp.Content.StaticContentCatalog") || catalogCreate.Arguments.Length != 4 || catalogCreate.Initializer is not null
            || Unwrap(catalogCreate.Arguments[0].Value) is not IObjectCreationOperation id
            || !Named(id.Type, "LithoSharp.Content.ContentCollectionId") || id.Arguments.Length != 1
            || !Equals(id.Arguments[0].Value.ConstantValue.Value, declaration.ConstructorArguments[2].Value)
            || !Equals(catalogCreate.Arguments[2].Value.ConstantValue.Value, site) || !Equals(catalogCreate.Arguments[3].Value.ConstantValue.Value, variant)
            || Unwrap(catalogCreate.Arguments[1].Value) is not IArrayCreationOperation entries || entries.Initializer is null) return unavailable;
        var actualPaths = new HashSet<string>(StringComparer.Ordinal);
        var actualEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in entries.Initializer.ElementValues)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Unwrap(value) is not IObjectCreationOperation entry || !Named(entry.Type, "LithoSharp.Content.StaticContentCatalogEntry")
                || entry.Arguments.Length != 3 || entry.Initializer is not null || entry.Arguments[1].Value.ConstantValue.Value is not string
                || Unwrap(entry.Arguments[2].Value) is not IInvocationOperation route
                || !Named(route.TargetMethod.ContainingType, "LithoSharp.Routing.SiteRoute")
                || route.Arguments[0].Value.ConstantValue.Value is not string path
                || route.Arguments.Any(a => a.Parameter?.Ordinal == 1 && a.Value.ConstantValue.Value is not null)) return unavailable;
            if (Unwrap(entry.Arguments[0].Value) is not IObjectCreationOperation entryId || !Named(entryId.Type, "LithoSharp.Content.ContentEntryId")
                || entryId.Arguments.Length != 1 || entryId.Arguments[0].Value.ConstantValue.Value is not string key) return unavailable;
            try
            {
                string publicPath;
                if (route.TargetMethod.Name == "ForFile") publicPath = SiteRoute.ForFile(path).PublicPath;
                else if (route.TargetMethod.Name == "ForDirectoryIndex") publicPath = SiteRoute.ForDirectoryIndex(path).PublicPath;
                else return unavailable;
                if (!actualPaths.Add(publicPath) || actualEntries.ContainsKey(key)) return unavailable;
                actualEntries.Add(key, publicPath);
            }
            catch (Exception e) when (e is ArgumentException or UriFormatException) { return unavailable; }
        }
        return actualPaths.SetEquals(paths) ? new("Closed", site, variant, expected, current, paths,
            catalog.Property, actualEntries, (string)declaration.ConstructorArguments[2].Value!) : unavailable;
    }
    private static IOperation? Unwrap(IOperation? operation)
    { while (operation is IConversionOperation { OperatorMethod: null } conversion) operation = conversion.Operand; return operation; }
    private static bool Core(INamedTypeSymbol? symbol) => symbol?.ContainingAssembly.Identity.Name == "LithoSharp" && !symbol.Locations.Any(l => l.IsInSource);
    private static bool Named(ITypeSymbol? symbol, string name) => symbol is INamedTypeSymbol named && Core(named) && named.ToDisplayString() == name;
    private sealed class Facts
    {
        internal Facts(string state, string site, string variant, string expected, string current, HashSet<string> paths,
            IPropertySymbol? catalog = null, Dictionary<string, string>? entries = null, string collectionId = "")
        { State = state; Site = site; Variant = variant; Expected = expected; Current = current; Paths = paths; Catalog = catalog; Entries = entries; CollectionId = collectionId; }
        internal IPropertySymbol? Catalog { get; }
        internal Dictionary<string, string>? Entries { get; }
        internal string CollectionId { get; }
        internal string State { get; }
        internal string Site { get; }
        internal string Variant { get; }
        internal string Expected { get; }
        internal string Current { get; }
        internal HashSet<string> Paths { get; }
    }
}
