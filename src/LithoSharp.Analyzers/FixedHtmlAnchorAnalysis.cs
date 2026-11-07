using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using LithoSharp.HtmlParsing;
using LithoSharp.Internal;
using LithoSharp.Routing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

internal static class FixedHtmlAnchorAnalysis
{
    internal static readonly DiagnosticDescriptor Rule = new("LSA1202", "Anchor absent from a fresh closed HTML page",
        "Anchor '{0}' is absent from the selected fixed HTML page '{1}'", "LithoSharp", DiagnosticSeverity.Error, true,
        helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/src/LithoSharp.Analyzers/README.md#lsa1202",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

    internal static void Analyze(CompilationAnalysisContext context)
    {
        if (!FixedHtmlPlanModel.Core(context.Compilation.GetTypeByMetadataName("LithoSharp.Content.FixedHtmlSiteAttribute"), "LithoSharp.Content.FixedHtmlSiteAttribute"))
            return;
        var fingerprint = FixedHtmlPlanModel.Fingerprint(context.Compilation, context.Options.AdditionalFiles, context.Options.AnalyzerConfigOptionsProvider, context.CancellationToken);
        if (fingerprint is null)
            return;
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (StaticManifestFingerprint.OwnGenerated(tree))
                continue;
            var semantic = context.Compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot(context.CancellationToken).DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (semantic.GetDeclaredSymbol(declaration, context.CancellationToken) is not INamedTypeSymbol symbol || !seen.Add(symbol))
                    continue;
                var model = FixedHtmlPlanModel.Read(symbol, context.CancellationToken);
                if (model is null || model.Pages.Count > 64 || symbol.StaticConstructors.Any(c => !c.IsImplicitlyDeclared))
                    continue;
                var members = symbol.GetMembers("FixedHtmlPlan");
                if (members.Length != 1 || members[0] is not IPropertySymbol property
                    || property.SetMethod is not null || !FixedHtmlPlanModel.Core(property.Type, "LithoSharp.Content.FixedHtmlSitePlan")
                    || property.DeclaringSyntaxReferences.Length != 1)
                    continue;
                var ownerTree = property.DeclaringSyntaxReferences[0].SyntaxTree;
                // A generated-looking path or attribute alone is not evidence. Reconstruct the exact owner
                // from current original buffers and bind it to the actual immutable Core entry point.
                if (!StaticManifestFingerprint.OwnGenerated(ownerTree) || ownerTree.GetText(context.CancellationToken).ToString() != model.Emit(fingerprint))
                    continue;
                var ownerSyntax = property.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken) as PropertyDeclarationSyntax;
                if (ownerSyntax?.ExpressionBody is null || context.Compilation.GetSemanticModel(ownerTree)
                    .GetOperation(ownerSyntax.ExpressionBody.Expression, context.CancellationToken) is not IObjectCreationOperation owner
                    || owner.Constructor is not { } constructor || !FixedHtmlPlanModel.Core(constructor.ContainingType, "LithoSharp.Content.FixedHtmlSitePlan")
                    || constructor.Parameters.Length != 3 || StaticValueFlow.HasBindingError(owner)
                    || constructor.Parameters[0].Type.SpecialType != SpecialType.System_String
                    || constructor.Parameters.Skip(1).Any(parameter => parameter.Type is not IArrayTypeSymbol
                    { Rank: 1, ElementType.SpecialType: SpecialType.System_String }))
                    continue;
                AnalyzePlan(context, model, fingerprint);
            }
        }
    }

    private sealed record Page(FixedHtmlPageModel Model, HtmlOriginAlternative Origin, HtmlLiteralFacts Facts);

    private static Page? ReadPage(CompilationAnalysisContext context, FixedHtmlPageModel page)
    {
        if (page.Method.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken) is not MethodDeclarationSyntax syntax)
            return null;
        var expression = syntax.ExpressionBody?.Expression
            ?? (syntax.Body?.Statements.Count == 1 && syntax.Body.Statements[0] is ReturnStatementSyntax statement ? statement.Expression : null);
        if (expression is null)
            return null;
        var semantic = context.Compilation.GetSemanticModel(syntax.SyntaxTree);
        if (semantic.GetOperation(expression, context.CancellationToken) is not IInvocationOperation call
            || !FixedHtmlPlanModel.Core(call.TargetMethod.ContainingType, "LithoSharp.Html")
            || call.TargetMethod.Name != "UnsafeRaw" || !call.TargetMethod.IsStatic || call.TargetMethod.Arity != 0
            || call.Arguments.Length != 1 || call.TargetMethod.Parameters[0].Type.SpecialType != SpecialType.System_String
            || StaticValueFlow.HasBindingError(call))
            return null;
        var origins = HtmlLiteralAnalysis.LiteralOrigins(call.Arguments[0].Value, context.CancellationToken);
        if (origins?.Alternatives.Count != 1)
            return null;
        var origin = origins.Alternatives[0];
        var facts = HtmlLiteralAnalysis.Parse(origin.Value, context.CancellationToken);
        return facts.AllowsClosedAnchors ? new(page, origin, facts) : null;
    }

    private static void AnalyzePlan(CompilationAnalysisContext context, FixedHtmlPlanModel model, string fingerprint)
    {
        var pages = model.Pages.ToDictionary(page => page.OutputPath, page => ReadPage(context, page), StringComparer.Ordinal);
        var root = SiteRoute.ForDirectoryIndex("", model.BaseUrl).PublicPath;
        var site = new Uri(model.BaseUrl);
        var origin = site.GetLeftPart(UriPartial.Authority);
        foreach (var source in pages.Values)
        {
            if (source is null)
                continue;
            var sourceUrl = new Uri(site, source.Model.PublicPath);
            foreach (var reference in source.Facts.Urls)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (reference.ElementNamespace != HtmlNamespaces.Html || reference.AttributeNamespace is not null
                    || reference.ElementName is not ("a" or "area") || reference.AttributeName != "href")
                    continue;
                var value = reference.Value.Value.Trim();
                if (!value.StartsWith("/", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal)
                    || !Uri.TryCreate(sourceUrl, value, out var address) || address.Query.Length != 0 || address.Fragment.Length <= 1
                    || !SiteQualityUrlRules.TryInternalOutputPath(address, origin, root, out var path)
                    || !pages.TryGetValue(path, out var target) || target is null)
                    continue;
                var fragment = SiteQualityUrlRules.DecodedFragment(address);
                if (target.Facts.Anchors.Contains(fragment, StringComparer.Ordinal))
                    continue;
                var location = source.Origin.Map(reference.Value.Source);
                if (location is null || location.SourceTree is null)
                    continue;
                var properties = ImmutableDictionary<string, string?>.Empty.Add("scope", "fixed-html-plan-page")
                    .Add("scopeState", "Closed").Add("freshness", "Fresh").Add("fingerprint", fingerprint)
                    .Add("sourcePage", source.Model.Method.Name).Add("targetPage", target.Model.Method.Name)
                    .Add("planIdentity", model.Symbol.ToDisplayString()).Add("siteBase", model.BaseUrl).Add("variant", "")
                    .Add("sourcePublicPath", source.Model.PublicPath).Add("sourceOutput", source.Model.OutputPath)
                    .Add("targetOutput", path).Add("origin", "original-literal-url-utf16-span");
                context.ReportDiagnostic(Diagnostic.Create(Rule, location, new[] { target.Model.Method.Locations.First(l => l.IsInSource) }, properties,
                    fragment, target.Model.PublicPath));
            }
        }
    }
}
