using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Inspection;

/// <summary>A diagnostic snapshot for one explicitly selected preflight phase.</summary>
/// <remarks>This is not a certificate for future rendering or final HTML publication.</remarks>
public sealed class SitePreflightReport
{
    internal SitePreflightReport(string mode, string stage, IEnumerable<SiteDiagnostic> diagnostics, IEnumerable<string> deferred)
    {
        Mode = mode;
        Stage = stage;
        Diagnostics = new SiteQualityReport(diagnostics).Diagnostics;
        DeferredReasons = Array.AsReadOnly(deferred.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }
    /// <summary>Either static-inputs or trusted-catalog.</summary>
    public string Mode { get; }
    /// <summary>The source or catalog phase that produced this snapshot.</summary>
    public string Stage { get; }
    /// <summary>Diagnostics with their original rule IDs and severity.</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
    /// <summary>Checks outside this phase; absence of Error does not prove these checks passed.</summary>
    public IReadOnlyList<string> DeferredReasons { get; }
    /// <summary>Whether this snapshot contains no Error.</summary>
    public bool Succeeded => !Diagnostics.Any(d => d.Severity == SiteDiagnosticSeverity.Error);
    /// <summary>Stops the caller on Error without rewriting severity or suppression policy.</summary>
    public void ThrowIfFailed()
    {
        if (!Succeeded) throw new SiteQualityValidationException(new SiteQualityReport(Diagnostics));
    }
}

/// <summary>Nonexecuting inspection of explicit source buffers.</summary>
public static class SitePreflight
{
    /// <summary>Inspects Markdown without building a project or invoking a factory.</summary>
    public static SitePreflightReport InspectMarkdown(string sourcePath, string text, CancellationToken cancellationToken = default) =>
        FromDiagnostics(DocumentInspection.Inspect(sourcePath, text, cancellationToken: cancellationToken).Diagnostics);
    /// <summary>Combines source diagnostics, including diagnostics from the MDX analysis-only worker.</summary>
    public static SitePreflightReport FromDiagnostics(IEnumerable<SiteDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new("static-inputs", "source", diagnostics, ["Runtime catalog and final HTML have not been inspected."]);
    }
}

/// <summary>An extension's actual declared routes before rendering, bundling or publishing.</summary>
/// <remarks>Implementations run only in explicitly trusted catalog mode. They must not invoke render or prepare hooks.</remarks>
public interface ISiteCatalogPreflightExtension : ISiteBuildExtension
{
    /// <summary>Acquires a catalog and analyzes source without invoking SSR.</summary>
    Task<SiteCatalogPreflightContribution> InspectCatalogAsync(SiteBuildContext context, CancellationToken cancellationToken = default);
}

/// <summary>One trusted declared route and its existing owner identity.</summary>
public sealed class SitePreflightRoute
{
    /// <summary>Creates a route claim from a trusted catalog.</summary>
    public SitePreflightRoute(SiteRoute route, string ownerId, SiteSourceLocation? location = null)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        Route = route; OwnerId = ownerId; Location = location;
    }
    /// <summary>The existing validated route.</summary>
    public SiteRoute Route { get; }
    /// <summary>The existing owner identity.</summary>
    public string OwnerId { get; }
    /// <summary>The catalog source location, when available.</summary>
    public SiteSourceLocation? Location { get; }
}

/// <summary>A catalog snapshot used by the common route validator.</summary>
public sealed class SiteCatalogPreflightContribution
{
    /// <summary>Copies one nonrendering catalog result.</summary>
    public SiteCatalogPreflightContribution(IEnumerable<SitePreflightRoute> routes, IEnumerable<SiteDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var claims = routes.ToArray();
        if (claims.Any(claim => claim is null)) throw new ArgumentException("Routes must not contain null.", nameof(routes));
        Routes = Array.AsReadOnly(claims);
        Diagnostics = new SiteQualityReport(diagnostics).Diagnostics;
    }
    /// <summary>Actual routes acquired by this inspection operation.</summary>
    public IReadOnlyList<SitePreflightRoute> Routes { get; }
    /// <summary>Existing source and loader diagnostics.</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
}
