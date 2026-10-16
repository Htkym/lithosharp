using LithoSharp.Build;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp;

/// <summary>GeneratedFiles includes reused artifacts; PostCount includes only published Markdown posts.</summary>
public sealed record SiteGenerationResult(
    string OutputDirectory,
    int PostCount,
    IReadOnlyList<string> GeneratedFiles)
{
    // Consumed by the existing Tool host and diagnostics report, without adding
    // a new public generation API or authorizing any later build.
    internal Inspection.SitePreflightReport? Preflight { get; init; }
    internal (int ParseCount, int ReuseCount, long RetainedBytes, int EntryCount) HtmlFactsSharing { get; init; }

    public SiteBuildPlan BuildPlan { get; init; } = SiteBuildPlan.Create([]);

    /// <summary>Gets the validated public route for each generated artifact, ordered by output path.</summary>
    public IReadOnlyList<SiteRoute> Routes { get; init; } = [];

    public SiteQualityReport QualityReport { get; init; } = new();

    public SiteBuildReport BuildReport { get; init; } = new(
        DateTimeOffset.UnixEpoch,
        "Production",
        "Unknown");

    public SiteBuildTimings? Timings { get; init; }

    public bool Equals(SiteGenerationResult? other) =>
        other is not null
        && StringComparer.Ordinal.Equals(OutputDirectory, other.OutputDirectory)
        && PostCount == other.PostCount
        && EqualityComparer<IReadOnlyList<string>>.Default.Equals(
            GeneratedFiles,
            other.GeneratedFiles);

    /// <summary>Uses only the legacy positional members to preserve record compatibility.</summary>
    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(OutputDirectory),
            PostCount,
            EqualityComparer<IReadOnlyList<string>>.Default.GetHashCode(GeneratedFiles));
}
