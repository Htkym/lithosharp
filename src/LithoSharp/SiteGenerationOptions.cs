namespace LithoSharp;

using LithoSharp.Content;
using LithoSharp.Quality;
using LithoSharp.Routing;

public sealed record SiteGenerationOptions
{
    /// <summary>Optional output path prefixes for a subset build. Other owned outputs and shared chunks are retained.</summary>
    /// <remarks>A subset requires an existing full output and clean=false. Shared aggregate files outside the scope are retained; include context-specific search partitions in the scope.</remarks>
    public IReadOnlyList<string> OutputScope { get; init; } = [];
    /// <summary>Opt-in trusted build preparation. Ordinary sites do not run external processors.</summary>
    public IReadOnlyList<LithoSharp.Build.ISiteBuildExtension> Extensions { get; init; } = [];

    /// <summary>Selects registered producer instances whose route contributions should be inspected statically.</summary>
    /// <remarks>This selection does not register or prepare producers. Missing diagnostics do not certify complete coverage.</remarks>
    public IReadOnlyList<LithoSharp.Build.ISiteBuildExtension> StaticRouteGuaranteeProducers { get; init; } = [];

    /// <summary>Prepared assets whose paths are preserved, for example bundled JavaScript chunks.</summary>
    public IReadOnlyList<SiteGeneratedAsset> GeneratedAssets { get; init; } = [];

    /// <summary>Maximum simultaneous renders. The default is one; extensions remain serial unless they declare thread safety.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;

    /// <summary>Build cache root outside output and public input. Null uses a sibling .lithosharp directory, partitioned by output identity.</summary>
    public string? BuildCacheDirectory { get; init; }

    /// <summary>Normalized to UTC; falls back to SOURCE_DATE_EPOCH, then current UTC time.</summary>
    public DateTimeOffset? BuildTimestamp { get; init; }

    public string EnvironmentName { get; init; } = "Production";

    public IReadOnlyList<SiteContentCollection> ContentCollections { get; init; } = [];

    public LithoSharp.Build.SiteBuildPlan? PreviousBuildPlan { get; init; }

    public IReadOnlyList<SiteAsset> Assets { get; init; } = [];

    /// <summary>Transforms with declared source assets and output files.</summary>
    public IReadOnlyList<SiteAssetTransform> AssetTransforms { get; init; } = [];

    /// <summary>Optional directory whose regular files are copied using their original relative paths. It must not overlap the output directory or contain caches.</summary>
    public string? PublicDirectory { get; init; }

    /// <summary>Optional transform cache directory outside the output directory. Null disables the disk cache.</summary>
    public string? AssetCacheDirectory { get; init; }

    public IReadOnlyList<SiteRedirect> Redirects { get; init; } = [];

    /// <summary>When omitted, no quality checks or network requests run.</summary>
    public SiteQualityOptions? Quality { get; init; }

    /// <summary>Reuses bounded output-specific local facts; target and rule checks still run each time. Used only when quality checks are enabled.</summary>
    public bool HtmlFactsCacheEnabled { get; init; } = true;

    // Internal consumer plan, frozen when the owner is created; no public API or late mutation.
    internal LithoSharp.Build.HtmlRawFactsDemand RawHtmlFactsDemand { get; init; }

    /// <summary>Opt-in timing collection; Timings is null when disabled.</summary>
    public bool CollectTimings { get; init; }
}
