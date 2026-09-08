using LithoSharp.Configuration;
using LithoSharp.Content;

namespace LithoSharp.Build;

/// <summary>Prepares declared content and assets before the common build plan is validated.</summary>
/// <remarks>Extensions are trusted build code. They must not write to the public output directory.</remarks>
public interface ISiteBuildExtension
{
    /// <summary>Discovers inputs and returns complete artifact declarations without publishing them.</summary>
    Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default);
}

/// <summary>Provides the fixed environment for build preparation.</summary>
public sealed class SiteBuildContext
{
    internal SiteBuildContext(SiteSettings site, SiteGenerationOptions options, string outputDirectory,
        string cacheDirectory, DateTimeOffset buildTimestamp)
    {
        Site = site;
        Options = options;
        OutputDirectory = outputDirectory;
        CacheDirectory = cacheDirectory;
        BuildTimestamp = buildTimestamp;
    }

    /// <summary>Site settings. Only explicitly selected fields may be exposed to browsers.</summary>
    public SiteSettings Site { get; }
    /// <summary>The current generation options.</summary>
    public SiteGenerationOptions Options { get; }
    /// <summary>The final output path, which preparation must not write.</summary>
    public string OutputDirectory { get; }
    /// <summary>The existing build cache root, outside the public output.</summary>
    public string CacheDirectory { get; }
    /// <summary>The fixed UTC timestamp used by publication and rendering.</summary>
    public DateTimeOffset BuildTimestamp { get; }
}

/// <summary>Additions to the existing content and asset registries.</summary>
public sealed record SiteBuildContribution
{
    /// <summary>Typed collections to include in the build.</summary>
    public IReadOnlyList<SiteContentCollection> ContentCollections { get; init; } = [];
    /// <summary>Prepared assets, each with one declared owner.</summary>
    public IReadOnlyList<SiteGeneratedAsset> Assets { get; init; } = [];
}
