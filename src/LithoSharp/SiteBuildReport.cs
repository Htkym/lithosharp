using System.Collections.ObjectModel;
using LithoSharp.Diagnostics;

namespace LithoSharp;

/// <summary>Generation report with UTC time and artifact paths relative to the output root.</summary>
public sealed class SiteBuildReport : IEquatable<SiteBuildReport>
{
    public const int CurrentVersion = 1;

    public SiteBuildReport(
        DateTimeOffset buildTimestamp,
        string environmentName,
        string templateIdentity,
        IEnumerable<SiteBuildReportNode>? nodes = null,
        IEnumerable<SiteBuildReportInvalidation>? invalidations = null,
        IEnumerable<string>? generatedArtifacts = null,
        IEnumerable<string>? skippedArtifacts = null,
        IEnumerable<string>? unpublishedPages = null,
        IEnumerable<string>? staleRemovedArtifacts = null,
        IEnumerable<SiteDiagnostic>? diagnostics = null,
        string transactionOutcome = "Committed",
        bool retainedRecoveryState = false)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(templateIdentity);
        ArgumentNullException.ThrowIfNull(transactionOutcome);
        Version = CurrentVersion;
        BuildTimestamp = buildTimestamp.ToUniversalTime();
        EnvironmentName = environmentName;
        TemplateIdentity = templateIdentity;
        Nodes = ReadOnly((nodes ?? [])
            .Select(static node => new SiteBuildReportNode(node.NodeId, node.OwnedArtifacts)
            {
                CacheHit = node.CacheHit,
                CacheMissReason = node.CacheMissReason,
            })
            .Distinct()
            .OrderBy(static node => node.NodeId, StringComparer.Ordinal)
            .ThenBy(static node => node.OwnedArtifacts, OrdinalStringListComparer.Instance));
        Invalidations = ReadOnly((invalidations ?? [])
            .Select(static invalidation => new SiteBuildReportInvalidation(
                invalidation.NodeId,
                invalidation.Reasons))
            .Distinct()
            .OrderBy(static invalidation => invalidation.NodeId, StringComparer.Ordinal)
            .ThenBy(static invalidation => invalidation.Reasons, OrdinalStringListComparer.Instance));
        GeneratedArtifacts = SnapshotStrings(generatedArtifacts);
        SkippedArtifacts = SnapshotStrings(skippedArtifacts);
        UnpublishedPages = SnapshotStrings(unpublishedPages);
        StaleRemovedArtifacts = SnapshotStrings(staleRemovedArtifacts);
        Diagnostics = ReadOnly((diagnostics ?? [])
            .Select(static diagnostic => new SiteDiagnostic(
                diagnostic.Id,
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.Location is null ? null : CopyLocation(diagnostic.Location),
                diagnostic.Category,
                diagnostic.RelatedLocations.Select(static related => CopyLocation(related))))
            .OrderBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Severity)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Location?.FilePath, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Location?.Line)
            .ThenBy(diagnostic => diagnostic.Location?.Column)
            .ThenBy(diagnostic => diagnostic.Location?.EndLine)
            .ThenBy(diagnostic => diagnostic.Location?.EndColumn)
            .ThenBy(diagnostic => diagnostic.Category, StringComparer.Ordinal)
            .ThenBy(diagnostic => RelatedKey(diagnostic), StringComparer.Ordinal)
            .DistinctBy(static diagnostic => (
                diagnostic.Id,
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.Location?.FilePath,
                diagnostic.Location?.Line,
                diagnostic.Location?.Column,
                diagnostic.Location?.EndLine,
                diagnostic.Location?.EndColumn,
                diagnostic.Category,
                RelatedKey(diagnostic))));
        TransactionOutcome = transactionOutcome;
        RetainedRecoveryState = retainedRecoveryState;
    }

    public int Version { get; }
    public DateTimeOffset BuildTimestamp { get; }
    public string EnvironmentName { get; }
    public string TemplateIdentity { get; }
    public IReadOnlyList<SiteBuildReportNode> Nodes { get; }
    public int CacheHitCount => Nodes.Count(static node => node.CacheHit);
    public int CacheMissCount => Nodes.Count(static node => !node.CacheHit);
    public IReadOnlyList<SiteBuildReportInvalidation> Invalidations { get; }
    public IReadOnlyList<string> GeneratedArtifacts { get; }
    public IReadOnlyList<string> SkippedArtifacts { get; }
    public IReadOnlyList<string> UnpublishedPages { get; }
    public IReadOnlyList<string> StaleRemovedArtifacts { get; }
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
    public string TransactionOutcome { get; }
    public bool RetainedRecoveryState { get; }

    public bool Equals(SiteBuildReport? other) =>
        other is not null
        && Version == other.Version
        && BuildTimestamp == other.BuildTimestamp
        && EnvironmentName == other.EnvironmentName
        && TemplateIdentity == other.TemplateIdentity
        && TransactionOutcome == other.TransactionOutcome
        && RetainedRecoveryState == other.RetainedRecoveryState
        && Nodes.SequenceEqual(other.Nodes)
        && Invalidations.SequenceEqual(other.Invalidations)
        && GeneratedArtifacts.SequenceEqual(other.GeneratedArtifacts)
        && SkippedArtifacts.SequenceEqual(other.SkippedArtifacts)
        && UnpublishedPages.SequenceEqual(other.UnpublishedPages)
        && StaleRemovedArtifacts.SequenceEqual(other.StaleRemovedArtifacts)
        && Diagnostics.Count == other.Diagnostics.Count
        && Diagnostics.Zip(other.Diagnostics).All(pair =>
            pair.First.Id == pair.Second.Id
            && pair.First.Severity == pair.Second.Severity
            && pair.First.Message == pair.Second.Message
            && pair.First.Location?.FilePath == pair.Second.Location?.FilePath
            && pair.First.Location?.Line == pair.Second.Location?.Line
            && pair.First.Location?.Column == pair.Second.Location?.Column
            && pair.First.Location?.EndLine == pair.Second.Location?.EndLine
            && pair.First.Location?.EndColumn == pair.Second.Location?.EndColumn
            && pair.First.Category == pair.Second.Category
            && pair.First.RelatedLocations.Count == pair.Second.RelatedLocations.Count
            && pair.First.RelatedLocations.Zip(pair.Second.RelatedLocations).All(related =>
                related.First.FilePath == related.Second.FilePath
                && related.First.Line == related.Second.Line
                && related.First.Column == related.Second.Column
                && related.First.EndLine == related.Second.EndLine
                && related.First.EndColumn == related.Second.EndColumn));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SiteBuildReport);
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Version, BuildTimestamp, EnvironmentName, TemplateIdentity);

    private static SiteSourceLocation CopyLocation(SiteSourceLocation location) =>
        new(location.FilePath, location.Line, location.Column, location.EndLine, location.EndColumn);

    private static string RelatedKey(SiteDiagnostic diagnostic) =>
        string.Join(";", diagnostic.RelatedLocations.Select(static related => string.Concat(related.FilePath, ":", related.Line?.ToString(), ":", related.Column?.ToString(), "-", related.EndLine?.ToString(), ":", related.EndColumn?.ToString())));

    private static IReadOnlyList<string> SnapshotStrings(IEnumerable<string>? values) =>
        ReadOnly((values ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
        new ReadOnlyCollection<T>(values.ToArray());

    private sealed class OrdinalStringListComparer : IComparer<IReadOnlyList<string>>
    {
        public static OrdinalStringListComparer Instance { get; } = new();

        public int Compare(IReadOnlyList<string>? x, IReadOnlyList<string>? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            for (var index = 0; index < Math.Min(x.Count, y.Count); index++)
            {
                var comparison = StringComparer.Ordinal.Compare(x[index], y[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return x.Count.CompareTo(y.Count);
        }
    }
}

public sealed record SiteBuildReportNode(string NodeId, IReadOnlyList<string> OwnedArtifacts)
{
    private string nodeId = NodeId ?? throw new ArgumentNullException(nameof(NodeId));
    private IReadOnlyList<string> ownedArtifacts = Snapshot(OwnedArtifacts);

    public string NodeId
    {
        get => nodeId;
        init => nodeId = value ?? throw new ArgumentNullException(nameof(value));
    }

    public IReadOnlyList<string> OwnedArtifacts
    {
        get => ownedArtifacts;
        init => ownedArtifacts = Snapshot(value);
    }

    public bool CacheHit { get; init; }

    public string? CacheMissReason { get; init; }

    /// <inheritdoc />
    public bool Equals(SiteBuildReportNode? other) =>
        other is not null
        && NodeId == other.NodeId
        && OwnedArtifacts.SequenceEqual(other.OwnedArtifacts)
        && CacheHit == other.CacheHit
        && CacheMissReason == other.CacheMissReason;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(NodeId, OwnedArtifacts.Count, CacheHit, CacheMissReason);

    private static IReadOnlyList<string> Snapshot(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ReadOnlyCollection<string>(
            values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }
}

public sealed record SiteBuildReportInvalidation(string NodeId, IReadOnlyList<string> Reasons)
{
    private string nodeId = NodeId ?? throw new ArgumentNullException(nameof(NodeId));
    private IReadOnlyList<string> reasons = Snapshot(Reasons);

    public string NodeId
    {
        get => nodeId;
        init => nodeId = value ?? throw new ArgumentNullException(nameof(value));
    }

    public IReadOnlyList<string> Reasons
    {
        get => reasons;
        init => reasons = Snapshot(value);
    }

    /// <inheritdoc />
    public bool Equals(SiteBuildReportInvalidation? other) =>
        other is not null
        && NodeId == other.NodeId
        && Reasons.SequenceEqual(other.Reasons);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(NodeId, Reasons.Count);

    private static IReadOnlyList<string> Snapshot(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new ReadOnlyCollection<string>(
            values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }
}
