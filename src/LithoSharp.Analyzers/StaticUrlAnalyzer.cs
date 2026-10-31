using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using LithoSharp.Internal;
using LithoSharp.Routing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

/// <summary>Reports proven misuse of registered LithoSharp APIs without executing user code.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StaticUrlAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Url = Rule("LSA1001", "Invalid absolute site URL");
    private static readonly DiagnosticDescriptor Route = Rule("LSA1002", "Invalid site route");
    private static readonly DiagnosticDescriptor Path = Rule("LSA1003", "Invalid relative output path");
    private static readonly DiagnosticDescriptor Option = Rule("LSA1004", "Invalid site quality option");

    private static DiagnosticDescriptor Rule(string id, string title) => new(id, title,
        "Every known value of '{0}' violates the {1} runtime contract", "LithoSharp",
        DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/src/LithoSharp.Analyzers/README.md#" + id.ToLowerInvariant());

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Url, Route, Path, Option, GenerationOptionsAnalysis.Rule, TestingDomMigrationAnalysis.Rule).AddRange(HtmlTemplateAnalysis.Rules).AddRange(StaticManifestAnalysis.Rules).Add(StaticRouteGuaranteeAnalysis.Rule).Add(StaticCoveragePolicy.Rule).Add(StaticAssetInputAnalysis.Rule).Add(FixedHtmlAnchorAnalysis.Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            // Legacy Testing consumers may have no registered Core URL/HTML/manifest sinks.
            TestingDomMigrationAnalysis.Register(start);
            start.RegisterCompilationEndAction(FixedHtmlAnchorAnalysis.Analyze);
            var assetInputs = StaticAssetInputAnalysis.Create(start.Compilation);
            if (assetInputs is not null)
                start.RegisterOperationAction(assetInputs.Analyze, OperationKind.ObjectCreation);
            var sinks = RegisterSinks(start.Compilation);
            var generationOptions = GenerationOptionsAnalysis.Create(start.Compilation);
            var html = HtmlTemplateAnalysis.Create(start.Compilation);
            var htmlUrls = new HtmlFlowUrlAnalysis(start.Compilation);
            var manifest = StaticManifestAnalysis.Create(start.Compilation, start.Options, start.CancellationToken);
            var guarantee = StaticRouteGuaranteeAnalysis.Create(start.Compilation, start.Options, start.CancellationToken);
            if (sinks.Count == 0 && html is null && manifest is null && guarantee is null && generationOptions is null)
                return;
            var coverage = new StaticCoveragePolicy(start.Compilation);
            start.RegisterOperationAction(operation =>
            {
                operation.CancellationToken.ThrowIfCancellationRequested();
                manifest?.DiscoverCoverage(operation.Operation, coverage);
                guarantee?.DiscoverCoverage(operation.Operation, coverage);
            }, OperationKind.Invocation, OperationKind.SimpleAssignment);
            start.RegisterCompilationEndAction(coverage.Report);
            if (html is not null)
            {
                start.RegisterCompilationEndAction(htmlUrls.Report);
                start.RegisterCompilationEndAction(html.Report);
            }
            start.RegisterOperationBlockAction(block => StaticValueFlow.Analyze(block, (operation, evaluate) =>
            {
                block.CancellationToken.ThrowIfCancellationRequested();
                generationOptions?.Analyze(block, operation, evaluate);
                html?.Analyze(block, operation, evaluate, htmlUrls);
                manifest?.Analyze(block, operation, evaluate, coverage);
                guarantee?.Analyze(block, operation, coverage);
                var method = operation is IInvocationOperation call ? call.TargetMethod
                    : (operation as IObjectCreationOperation)?.Constructor;
                if (method is null || !sinks.TryGetValue(method.OriginalDefinition, out var rules))
                    return;
                var arguments = operation is IInvocationOperation invocation ? invocation.Arguments
                    : ((IObjectCreationOperation)operation).Arguments;
                if (StaticValueFlow.HasBindingError(operation))
                    return;
                foreach (var rule in rules)
                {
                    var argument = arguments.FirstOrDefault(a => a.Parameter?.Ordinal == rule.Ordinal);
                    // Null-forgiving syntax can make an explicit CFG argument IsImplicit.
                    if (argument is null || argument.ArgumentKind != ArgumentKind.Explicit)
                        continue;
                    var values = evaluate(argument.Value);
                    if (values.Values is null || values.Values.Length == 0 || !values.Values.All(rule.Invalid))
                        continue;
                    var properties = ImmutableDictionary<string, string?>.Empty
                        .Add("staticState", "Known").Add("candidateCount", values.Values.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .Add("origin", "original-argument-utf16-span");
                    var sourceArgument = argument.Syntax.FirstAncestorOrSelf<ArgumentSyntax>();
                    var sourceOwner = sourceArgument?.Parent?.Parent;
                    var expression = sourceOwner is not null && sourceOwner.SyntaxTree == operation.Syntax.SyntaxTree && sourceOwner.Span == operation.Syntax.Span
                        ? sourceArgument!.Expression : argument.Value.Syntax;
                    block.ReportDiagnostic(Diagnostic.Create(rule.Descriptor, expression.GetLocation(),
                        properties, argument.Parameter!.Name, method.ContainingType.Name + "." + method.Name));
                }
            }, html, coverage.Interrupt));
        });
    }

    private static Dictionary<IMethodSymbol, List<Sink>> RegisterSinks(Compilation compilation)
    {
        var result = new Dictionary<IMethodSymbol, List<Sink>>(SymbolEqualityComparer.Default);
        void Register(string metadataName, string member, string[] parameterTypes, params Sink[] rules)
        {
            var type = compilation.GetTypeByMetadataName(metadataName);
            if (type?.ContainingAssembly.Identity.Name != "LithoSharp" || type.Locations.Any(l => l.IsInSource))
                return;
            foreach (var method in type.GetMembers(member).OfType<IMethodSymbol>().Where(m => m.Arity == 0
                && m.DeclaredAccessibility == Accessibility.Public && m.Parameters.Length == parameterTypes.Length
                && m.Parameters.All(p => p.RefKind == RefKind.None)))
            {
                if (!method.Parameters.Select(p => p.Type.ToDisplayString()).SequenceEqual(parameterTypes))
                    continue;
                if (member != ".ctor" && (!method.IsStatic || !SymbolEqualityComparer.Default.Equals(method.ReturnType, type)))
                    continue;
                result.Add(method.OriginalDefinition, rules.ToList());
            }
        }
        var strings = new[] { "string", "string?" };
        Register("LithoSharp.SiteUrl", "FromAbsolute", new[] { "string" }, new Sink(0, Url, v => Reject(() => StaticApiGuards.ValidateAbsoluteUrl((string)v!))));
        foreach (var type in new[] { "LithoSharp.SiteUrl", "LithoSharp.Routing.SiteRoute" })
        {
            Register(type, "ForFile", strings, new Sink(0, Route, v => Reject(() => SiteRoute.ForFile((string)v!))),
                new Sink(1, Route, v => Reject(() => SiteRoute.ForFile("valid.html", (string?)v))));
            Register(type, type.EndsWith("SiteUrl", StringComparison.Ordinal) ? "ForDirectory" : "ForDirectoryIndex", strings,
                new Sink(0, Route, v => Reject(() => SiteRoute.ForDirectoryIndex((string)v!))),
                new Sink(1, Route, v => Reject(() => SiteRoute.ForDirectoryIndex("", (string?)v))));
        }
        // This instance wrapper returns SiteUrl, not its own generic containing type.
        // Keep the static/self-return registration contract above unchanged.
        var contentRef = compilation.GetTypeByMetadataName("LithoSharp.Content.ContentRef`1")?.OriginalDefinition;
        var siteUrl = compilation.GetTypeByMetadataName("LithoSharp.SiteUrl")?.OriginalDefinition;
        var siteRoute = compilation.GetTypeByMetadataName("LithoSharp.Routing.SiteRoute")?.OriginalDefinition;
        var manifest = compilation.GetTypeByMetadataName("LithoSharp.Content.StaticSiteManifest");
        if (manifest is { Arity: 0, IsSealed: true } && siteUrl is { Arity: 0, IsSealed: true }
            && manifest.ContainingAssembly.Identity.Name == "LithoSharp"
            && SymbolEqualityComparer.Default.Equals(manifest.ContainingAssembly, siteUrl.ContainingAssembly)
            && !manifest.Locations.Any(l => l.IsInSource) && !siteUrl.Locations.Any(l => l.IsInSource))
        {
            foreach (var method in manifest.GetMembers("GetUrl").OfType<IMethodSymbol>().Where(m =>
                m.MethodKind == MethodKind.Ordinary && !m.IsStatic && m.Arity == 0 && m.DeclaredAccessibility == Accessibility.Public
                && !m.ReturnsByRef && !m.ReturnsByRefReadonly && SymbolEqualityComparer.Default.Equals(m.ReturnType, siteUrl)
                && m.Parameters.Length == 1 && m.Parameters[0].RefKind == RefKind.None && !m.Parameters[0].IsOptional
                && m.Parameters[0].Type.SpecialType == SpecialType.System_String))
                // GetUrl rejects null before membership or an open resolver is consulted.
                result.Add(method.OriginalDefinition, new List<Sink> { new Sink(0, Route, v => v is null) });
        }
        if (siteUrl is { Arity: 0, IsSealed: true } && siteRoute is { Arity: 0, IsSealed: true }
            && siteUrl.ContainingAssembly.Identity.Name == "LithoSharp"
            && SymbolEqualityComparer.Default.Equals(siteUrl.ContainingAssembly, siteRoute.ContainingAssembly)
            && !siteUrl.Locations.Any(l => l.IsInSource) && !siteRoute.Locations.Any(l => l.IsInSource))
        {
            foreach (var method in siteUrl.GetMembers("FromRoute").OfType<IMethodSymbol>().Where(m =>
                m.MethodKind == MethodKind.Ordinary && m.IsStatic && m.Arity == 0 && m.DeclaredAccessibility == Accessibility.Public
                && !m.ReturnsByRef && !m.ReturnsByRefReadonly && SymbolEqualityComparer.Default.Equals(m.ReturnType, siteUrl)
                && m.Parameters.Length == 1 && m.Parameters[0].RefKind == RefKind.None
                && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, siteRoute)))
                // Mirrors FromRoute's initial ThrowIfNull without evaluating a route or its PublicPath.
                result.Add(method.OriginalDefinition, new List<Sink> { new Sink(0, Route, v => v is null) });
        }
        if (contentRef is { Arity: 1, IsSealed: true } && siteUrl is not null
            && contentRef.ContainingAssembly.Identity.Name == "LithoSharp"
            && SymbolEqualityComparer.Default.Equals(contentRef.ContainingAssembly, siteUrl.ContainingAssembly)
            && !contentRef.Locations.Any(l => l.IsInSource) && !siteUrl.Locations.Any(l => l.IsInSource))
        {
            foreach (var method in contentRef.GetMembers("GetUrl").OfType<IMethodSymbol>().Where(m =>
                m.MethodKind == MethodKind.Ordinary && !m.IsStatic && m.Arity == 0 && m.DeclaredAccessibility == Accessibility.Public
                && !m.ReturnsByRef && !m.ReturnsByRefReadonly && SymbolEqualityComparer.Default.Equals(m.ReturnType, siteUrl)
                && m.Parameters.Length == 1 && m.Parameters[0].RefKind == RefKind.None
                && m.Parameters[0].Type.SpecialType == SpecialType.System_String && m.Parameters[0].IsOptional
                && m.Parameters[0].HasExplicitDefaultValue && m.Parameters[0].ExplicitDefaultValue is null))
                result.Add(method.OriginalDefinition, new List<Sink>
                { new Sink(0, Route, v => Reject(() => SiteRoute.ForFile("valid.html", (string?)v))) });
        }
        if (contentRef is { Arity: 1, IsSealed: true }
            && contentRef.ContainingAssembly.Identity.Name == "LithoSharp"
            && !contentRef.Locations.Any(l => l.IsInSource))
        {
            var parameterTypes = new[] { compilation.GetTypeByMetadataName("LithoSharp.Content.ContentCollectionId"),
                compilation.GetTypeByMetadataName("LithoSharp.Content.ContentEntryId"), siteRoute };
            if (parameterTypes.All(type => type is { Arity: 0, IsSealed: true }
                && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, contentRef.ContainingAssembly)
                && !type.Locations.Any(l => l.IsInSource)))
            {
                foreach (var constructor in contentRef.InstanceConstructors.Where(m =>
                    m.MethodKind == MethodKind.Constructor && !m.IsStatic && m.Arity == 0
                    && m.DeclaredAccessibility == Accessibility.Public && m.Parameters.Length == 3
                    && m.Parameters.All(p => p.RefKind == RefKind.None && !p.IsOptional)
                    && m.Parameters.Select((p, i) => SymbolEqualityComparer.Default.Equals(p.Type, parameterTypes[i])).All(same => same)))
                    // Each sink reads the route's evaluation snapshot, even before later argument effects.
                    result.Add(constructor.OriginalDefinition, new List<Sink> { new Sink(2, Route, v => v is null) });
            }
        }
        Register("LithoSharp.SiteAssetOutput", ".ctor", new[] { "string", "string" },
            new Sink(1, Path, v => Reject(() => SiteRoute.NormalizeRelativeOutputPath((string)v!))));
        Register("LithoSharp.SiteAsset", ".ctor", new[] { "string", "string", "string", "string" },
            new Sink(2, Path, v => Reject(() => SiteRoute.NormalizeRelativeOutputPath((string)v!))),
            new Sink(3, Path, v => Reject(() => SiteRoute.NormalizeRelativeOutputPath((string)v!))));
        Register("LithoSharp.Quality.SiteQualityOptions", ".ctor",
            new[] { "LithoSharp.Diagnostics.SiteDiagnosticSeverity", "bool", "LithoSharp.Quality.ExternalLinkCheckOptions?" },
            new Sink(0, Option, v => v is int severity && Reject(() => StaticApiGuards.ValidateFailureThreshold(severity))));
        Register("LithoSharp.Quality.ExternalLinkCheckOptions", ".ctor",
            new[] { "string", "System.TimeSpan?", "System.TimeSpan?", "System.TimeSpan?" },
            new Sink(0, Option, v => v is null || v is string && Reject(() => StaticApiGuards.ValidateExternalCacheFilePath((string)v))),
            new Sink(1, Option, v => v is StaticDuration duration && Reject(() => StaticApiGuards.ValidateExternalCacheDuration(duration.Ticks))),
            new Sink(2, Option, v => v is StaticDuration duration && Reject(() => StaticApiGuards.ValidateExternalTimerDuration(duration.Ticks, "minimumRequestInterval", true))),
            new Sink(3, Option, v => v is StaticDuration duration && Reject(() => StaticApiGuards.ValidateExternalRequestTimeout(duration.Ticks))));
        return result;
    }

    private static bool Reject(Action guard)
    {
        try
        {
            guard();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (UriFormatException)
        {
            return true;
        }
    }

    private sealed class Sink
    {
        internal Sink(int ordinal, DiagnosticDescriptor descriptor, Func<object?, bool> invalid)
        {
            Ordinal = ordinal;
            Descriptor = descriptor;
            Invalid = invalid;
        }
        internal int Ordinal { get; }
        internal DiagnosticDescriptor Descriptor { get; }
        internal Func<object?, bool> Invalid { get; }
    }
}
