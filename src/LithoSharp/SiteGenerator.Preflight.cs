using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.Inspection;
using LithoSharp.Routing;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
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
                if (extension is not ISiteCatalogPreflightExtension inspector)
                {
                    deferred.Add($"Extension '{extension.GetType().FullName}' has no nonrendering catalog inspector.");
                    continue;
                }
                var contribution = await inspector.InspectCatalogAsync(context, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("A catalog inspector returned no contribution.");
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
