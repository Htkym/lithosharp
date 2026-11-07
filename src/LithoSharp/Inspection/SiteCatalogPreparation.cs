using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;

namespace LithoSharp.Inspection;

// The optional packages are friends of Core. This operation-local handoff is not
// a public certificate that a later generation may reuse.
internal interface ISiteCatalogPreparationExtension
{
    Task<SiteCatalogPreparation> InspectForGenerationAsync(SiteBuildContext context, CancellationToken cancellationToken);
}

internal sealed class SiteCatalogPreparation(
    SiteCatalogPreflightContribution contribution,
    IReadOnlyList<SiteContentCollection> contentCollections,
    Func<SiteBuildContext, CancellationToken, Task<SiteBuildContribution>> prepare,
    Func<CancellationToken, Task> validate)
{
    private int consumed;
    internal SiteCatalogPreflightContribution Contribution { get; } = contribution;
    internal IReadOnlyList<SiteContentCollection> ContentCollections { get; } = contentCollections;
    internal IReadOnlyList<string> DeferredReasons { get; init; } = [];
    // Preserve the build extension's existing source-error exception contract;
    // report-only catalog inspection still returns the diagnostics unchanged.
    internal IReadOnlyList<SiteDiagnostic> SourceErrorDiagnostics { get; init; } = [];
    internal void ThrowIfSourceFailed()
    {
        if (SourceErrorDiagnostics.Count != 0)
            throw new SiteBuildExtensionException(SourceErrorDiagnostics);
    }
    internal Task ValidateInputsAsync(CancellationToken cancellationToken) => validate(cancellationToken);

    internal async Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref consumed, 1) != 0)
            throw new InvalidOperationException("A catalog snapshot may only be prepared once.");
        ThrowIfSourceFailed();
        new SitePreflightReport("trusted-catalog", "catalog", Contribution.Diagnostics, []).ThrowIfFailed();
        var routes = new SiteRouteTable();
        foreach (var claim in Contribution.Routes)
            routes.Register(claim.Route, claim.OwnerId, claim.Location);
        routes.ValidateOrThrow();
        await validate(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await prepare(context, cancellationToken).ConfigureAwait(false);
    }

    internal static SiteCatalogPreparation FromCollections(
        IReadOnlyList<SiteContentCollection> collections, IEnumerable<SiteDiagnostic> diagnostics,
        SiteBuildContext context, Func<SiteBuildContext, CancellationToken, Task<SiteBuildContribution>> prepare,
        Func<CancellationToken, Task> validate, CancellationToken cancellationToken = default,
        IEnumerable<string>? deferredReasons = null, IEnumerable<SiteDiagnostic>? sourceErrorDiagnostics = null) =>
        FromContribution(new() { ContentCollections = collections, Diagnostics = diagnostics.ToArray() }, context, prepare, validate, cancellationToken, deferredReasons, sourceErrorDiagnostics);

    internal static SiteCatalogPreparation FromContribution(
        SiteBuildContribution catalog, SiteBuildContext context,
        Func<SiteBuildContext, CancellationToken, Task<SiteBuildContribution>> prepare,
        Func<CancellationToken, Task> validate, CancellationToken cancellationToken = default,
        IEnumerable<string>? deferredReasons = null, IEnumerable<SiteDiagnostic>? sourceErrorDiagnostics = null)
    {
        var collections = catalog.ContentCollections.ToArray();
        var table = new SiteRouteTable();
        var unpublished = new List<string>();
        var routes = collections.SelectMany(collection => collection.CreatePages(
                context.Site.BaseUrl, context.BuildTimestamp, context.Options.EnvironmentName,
                table, unpublished, cancellationToken))
            .Select(page => new SitePreflightRoute(page.Route, page.OwnerId, page.SourceLocation))
            .Concat(catalog.Assets.Select(asset => new SitePreflightRoute(
                SiteRoute.ForFile(asset.RelativeOutputPath, context.Site.BaseUrl), "asset:" + asset.Id)))
            .ToArray();
        return new(new(routes, catalog.Diagnostics.Concat(table.Validate().Diagnostics)), collections, prepare, validate)
        {
            DeferredReasons = Array.AsReadOnly((deferredReasons ?? []).ToArray()),
            SourceErrorDiagnostics = Array.AsReadOnly((sourceErrorDiagnostics ?? []).ToArray()),
        };
    }
}
