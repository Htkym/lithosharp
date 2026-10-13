using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.Inspection;
using LithoSharp.Routing;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    private static async Task<(IReadOnlyList<SiteCatalogPreparation?> Catalogs, SitePreflightReport Report)>
        InspectGenerationCatalogsAsync(SiteBuildContext context, SiteRouteTable routes, CancellationToken cancellationToken)
    {
        // Inspection owner IDs belong to the declared catalog. A third-party
        // inspector need not use Core's internal prepared-content identity.
        routes = routes.Copy();
        var catalogs = new List<SiteCatalogPreparation?>();
        var diagnostics = new List<SiteDiagnostic>();
        var deferred = new List<string> { "The catalog phase does not inspect final HTML, template-owned output or compiled assets." };
        var collections = context.Options.ContentCollections.ToList();
        foreach (var extension in context.Options.Extensions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(extension);
            SiteCatalogPreparation? snapshot = null;
            SiteCatalogPreflightContribution? contribution = null;
            if (extension is ISiteCatalogPreparationExtension participant)
            {
                snapshot = await participant.InspectForGenerationAsync(context, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A catalog inspector returned no snapshot.");
                contribution = snapshot.Contribution;
                deferred.AddRange(snapshot.DeferredReasons);
                collections.AddRange(snapshot.ContentCollections);
                ValidateContentCollectionIds(collections);
            }
            else if (extension is ISiteCatalogPreflightExtension inspector)
            {
                contribution = await inspector.InspectCatalogAsync(context, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A catalog inspector returned no contribution.");
                deferred.Add($"Extension '{extension.GetType().FullName}' has no catalog snapshot handoff; its preparation is checked again after acquisition.");
            }
            else deferred.Add($"Extension '{extension.GetType().FullName}' has no nonrendering catalog inspector; its preparation remains Deferred.");
            catalogs.Add(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            if (contribution is null) continue;
            snapshot?.ThrowIfSourceFailed();
            diagnostics.AddRange(contribution.Diagnostics);
            new SitePreflightReport("trusted-catalog", "catalog", diagnostics, deferred).ThrowIfFailed();
            foreach (var claim in contribution.Routes) routes.Register(claim.Route, claim.OwnerId, claim.Location);
            routes.ValidateOrThrow();
        }
        // Detect changes caused by a later inspector before *any* prepare hook.
        foreach (var snapshot in catalogs)
            if (snapshot is not null) await snapshot.ValidateInputsAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return (catalogs, new("trusted-catalog", "catalog", diagnostics, deferred));
    }

    /// <summary>Stops source Error before invoking the factory, then inspects its actual catalog without rendering.</summary>
    /// <remarks>The factory owns disposal of its definition's extensions. This method neither renders nor publishes.</remarks>
    public async Task<SitePreflightReport> PreflightFactoryAsync(
        Func<CancellationToken, Task<SiteDefinition>> createDefinition, SitePreflightReport sourceReport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createDefinition);
        ArgumentNullException.ThrowIfNull(sourceReport);
        if (sourceReport.Mode != "static-inputs") throw new ArgumentException("A source preflight snapshot is required.", nameof(sourceReport));
        cancellationToken.ThrowIfCancellationRequested();
        sourceReport.ThrowIfFailed();
        var definition = await createDefinition(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The site factory returned no definition.");
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = await PreflightCatalogAsync(definition, cancellationToken).ConfigureAwait(false);
        return new("trusted-catalog", "catalog", sourceReport.Diagnostics.Concat(catalog.Diagnostics), catalog.DeferredReasons);
    }

    /// <summary>Inspects actual route declarations after factory acquisition and before any renderer is invoked.</summary>
    /// <remarks>Unknown extension prepare hooks are not called; they remain Deferred. Final HTML and asset checks still run during generation.</remarks>
    public async Task<SitePreflightReport> PreflightCatalogAsync(SiteDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();
        var options = definition.Options ?? throw new ArgumentException("Generation options are required.", nameof(definition));
        var customization = definition.Customization ?? new SiteCustomization();
        var timestamp = ResolveBuildTimestamp(options.BuildTimestamp);
        var output = Path.GetFullPath(definition.OutputDirectory);
        var cache = Path.GetFullPath(options.BuildCacheDirectory ?? Path.Combine(Path.GetDirectoryName(output)!, DefaultBuildCacheDirectoryName));
        var context = new SiteBuildContext(definition.Site, options, output, cache, timestamp);
        var diagnostics = definition.Posts.SelectMany(p => p.CompilerDiagnostics ?? []).ToList();
        var deferred = new List<string> { "Final HTML, template-owned output and generated assets have not been inspected." };
        var routes = new SiteRouteTable();
        var unpublished = new List<string>();
        var markdown = AdaptPublishedMarkdownPages(definition.Posts, definition.Site.BaseUrl, timestamp, options.EnvironmentName, routes, unpublished);
        var extra = FilterPublished(AdaptExtraPages(customization.ExtraPages, definition.Site.BaseUrl, routes), timestamp, options.EnvironmentName);
        _ = AdaptContentCollections(options.ContentCollections, definition.Site.BaseUrl, timestamp, options.EnvironmentName, routes, unpublished, cancellationToken);
        var builtIn = customization.Template is DocsSiteTemplate or BlogSiteTemplate;
        RegisterPageRoutes(routes, markdown, builtIn);
        RegisterPageRoutes(routes, extra, builtIn);
        // Errors already known from the actual core catalog stop before extension analysis.
        diagnostics.AddRange(routes.Validate().Diagnostics);
        if (!diagnostics.Any(d => d.Severity == SiteDiagnosticSeverity.Error))
        {
            foreach (var extension in options.Extensions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SiteCatalogPreflightContribution contribution;
                if (extension is ISiteCatalogPreparationExtension participant)
                {
                    var snapshot = await participant.InspectForGenerationAsync(context, cancellationToken).ConfigureAwait(false);
                    contribution = snapshot.Contribution;
                    deferred.AddRange(snapshot.DeferredReasons);
                }
                else if (extension is ISiteCatalogPreflightExtension inspector)
                    contribution = await inspector.InspectCatalogAsync(context, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("A catalog inspector returned no contribution.");
                else
                {
                    deferred.Add($"Extension '{extension.GetType().FullName}' has no nonrendering catalog inspector.");
                    continue;
                }
                diagnostics.AddRange(contribution.Diagnostics);
                foreach (var claim in contribution.Routes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ArgumentNullException.ThrowIfNull(claim);
                    routes.Register(claim.Route, claim.OwnerId, claim.Location);
                }
                diagnostics.AddRange(routes.Validate().Diagnostics);
                if (diagnostics.Any(d => d.Severity == SiteDiagnosticSeverity.Error)) break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new("trusted-catalog", "catalog", diagnostics, deferred);
    }
}
