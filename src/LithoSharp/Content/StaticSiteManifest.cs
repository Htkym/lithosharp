using LithoSharp.Routing;

namespace LithoSharp.Content;

/// <summary>The completeness of one explicitly scoped reference universe.</summary>
public enum StaticCoverageState
{
    /// <summary>A complete immutable universe, subject to compiler provenance freshness.</summary>
    Closed,
    /// <summary>Other producers or a resolver may supply additional targets.</summary>
    Open,
    /// <summary>The stored compiler provenance differs from current inputs.</summary>
    Stale,
    /// <summary>No usable evidence is available.</summary>
    Unavailable
}

/// <summary>An explicit immutable route lookup universe shared by generated references and runtime.</summary>
/// <remarks>This describes lookup membership, not an entire site's publication or generated HTML anchors/assets.</remarks>
public sealed class StaticSiteManifest
{
    private readonly IReadOnlyDictionary<string, SiteRoute> routes;
    private readonly Func<string, SiteUrl>? resolver;
    private StaticSiteManifest(StaticContentCatalog catalog, StaticCoverageState state, string fingerprint, Func<string, SiteUrl>? resolver)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Site = catalog.Site;
        Variant = catalog.Variant;
        CollectionId = catalog.CollectionId;
        RouteCoverage = state;
        SourceFingerprint = fingerprint;
        this.resolver = resolver;
        routes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, SiteRoute>(
            catalog.Entries.ToDictionary(entry => entry.Route.PublicPath, entry => entry.Route, StringComparer.Ordinal));
    }
    /// <summary>The selected site identity.</summary>
    public string Site { get; }
    /// <summary>The selected variant identity.</summary>
    public string Variant { get; }
    /// <summary>The source collection identity.</summary>
    public ContentCollectionId CollectionId { get; }
    /// <summary>The membership coverage of this explicit route lookup.</summary>
    public StaticCoverageState RouteCoverage { get; }
    /// <summary>HTML rendering may add anchors; this manifest does not close that universe.</summary>
    public StaticCoverageState AnchorCoverage => StaticCoverageState.Open;
    /// <summary>Asset producers are outside this route lookup universe.</summary>
    public StaticCoverageState AssetCoverage => StaticCoverageState.Open;
    /// <summary>The generated compiler input fingerprint. It is not a publication certificate.</summary>
    public string SourceFingerprint { get; }
    /// <summary>Creates an explicit closed lookup from a snapshot of the canonical catalog.</summary>
    /// <remarks>Compiler diagnostics additionally require a verified generated origin and matching current fingerprint.</remarks>
    public static StaticSiteManifest Create(StaticContentCatalog catalog, string sourceFingerprint)
    {
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        if (sourceFingerprint.Length != 64 || sourceFingerprint.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A manifest fingerprint must be lowercase SHA256.", nameof(sourceFingerprint));
        return new(catalog, StaticCoverageState.Closed, sourceFingerprint, null);
    }
    /// <summary>Creates an open lookup; a resolver can supply targets outside the catalog.</summary>
    public static StaticSiteManifest Open(StaticContentCatalog catalog, Func<string, SiteUrl>? resolver = null) =>
        new(catalog, StaticCoverageState.Open, "", resolver);
    /// <summary>Looks up an exact public path in this manifest, optionally using its open resolver.</summary>
    /// <exception cref="KeyNotFoundException">The selected manifest and resolver do not provide this path.</exception>
    public SiteUrl GetUrl(string publicPath)
    {
        ArgumentNullException.ThrowIfNull(publicPath);
        if (routes.TryGetValue(publicPath, out var route)) return SiteUrl.FromRoute(route);
        return resolver?.Invoke(publicPath) ?? throw new KeyNotFoundException($"Route '{publicPath}' is not present in manifest '{Site}/{Variant}/{CollectionId.Value}'.");
    }
}

/// <summary>Provenance emitted on an opted-in generated manifest property.</summary>
/// <remarks>A hand-written attribute alone is not compiler evidence of complete or fresh coverage.</remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class StaticSiteManifestSourceAttribute(string contract, string fingerprint, string coverage, string site, string variant, params string[] publicPaths) : Attribute
{
    /// <summary>The versioned lookup contract.</summary>
    public string Contract { get; } = contract;
    /// <summary>The original compiler input fingerprint.</summary>
    public string Fingerprint { get; } = fingerprint;
    /// <summary>The generated completeness state.</summary>
    public string Coverage { get; } = coverage;
    /// <summary>The selected site identity.</summary>
    public string Site { get; } = site;
    /// <summary>The selected variant identity.</summary>
    public string Variant { get; } = variant;
    /// <summary>The exact public paths used by the same generated runtime manifest.</summary>
    public IReadOnlyList<string> PublicPaths { get; } = Array.AsReadOnly(publicPaths.ToArray());
}
