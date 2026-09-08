using System.Collections.ObjectModel;
using LithoSharp.Diagnostics;

namespace LithoSharp;

/// <summary>決定的なサイト生成の概要を表します。</summary>
public sealed class SiteBuildReport : IEquatable<SiteBuildReport>
{
    /// <summary>レポートのスキーマバージョン。</summary>
    public const int CurrentVersion = 1;

    /// <summary>レポートを作成します。</summary>
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
                diagnostic.Location is null
                    ? null
                    : new SiteSourceLocation(
                        diagnostic.Location.FilePath,
                        diagnostic.Location.Line,
                        diagnostic.Location.Column)))
            .OrderBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Severity)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Location?.FilePath, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Location?.Line)
            .ThenBy(diagnostic => diagnostic.Location?.Column)
            .DistinctBy(static diagnostic => (
                diagnostic.Id,
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.Location?.FilePath,
                diagnostic.Location?.Line,
                diagnostic.Location?.Column)));
        TransactionOutcome = transactionOutcome;
        RetainedRecoveryState = retainedRecoveryState;
    }

    /// <summary>レポートのスキーマバージョンを取得します。</summary>
    public int Version { get; }
    /// <summary>UTC に正規化したビルド時刻を取得します。</summary>
    public DateTimeOffset BuildTimestamp { get; }
    /// <summary>ビルド環境名を取得します。</summary>
    public string EnvironmentName { get; }
    /// <summary>テンプレートの安定した型名を取得します。</summary>
    public string TemplateIdentity { get; }
    /// <summary>ノードと所有成果物を取得します。</summary>
    public IReadOnlyList<SiteBuildReportNode> Nodes { get; }
    /// <summary>キャッシュから再利用したノード数を取得します。</summary>
    public int CacheHitCount => Nodes.Count(static node => node.CacheHit);
    /// <summary>キャッシュから再利用しなかったノード数を取得します。</summary>
    public int CacheMissCount => Nodes.Count(static node => !node.CacheHit);
    /// <summary>前回計画との差分による無効化を取得します。</summary>
    public IReadOnlyList<SiteBuildReportInvalidation> Invalidations { get; }
    /// <summary>生成した相対成果物パスを取得します。</summary>
    public IReadOnlyList<string> GeneratedArtifacts { get; }
    /// <summary>生成を省略した相対成果物パスを取得します。</summary>
    public IReadOnlyList<string> SkippedArtifacts { get; }
    /// <summary>公開しなかったページ識別子を取得します。</summary>
    public IReadOnlyList<string> UnpublishedPages { get; }
    /// <summary>所有情報に基づき削除した相対成果物パスを取得します。</summary>
    public IReadOnlyList<string> StaleRemovedArtifacts { get; }
    /// <summary>生成中に収集した診断を取得します。</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
    /// <summary>原子的な出力トランザクションの結果を取得します。</summary>
    public string TransactionOutcome { get; }
    /// <summary>回復用状態が保持されているかどうかを取得します。</summary>
    public bool RetainedRecoveryState { get; }

    /// <summary>レポートの内容が等しいかどうかを判定します。</summary>
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
            && pair.First.Location?.Column == pair.Second.Location?.Column);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SiteBuildReport);
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Version, BuildTimestamp, EnvironmentName, TemplateIdentity);

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

/// <summary>レポートに含めるビルドノードと所有成果物です。</summary>
public sealed record SiteBuildReportNode(string NodeId, IReadOnlyList<string> OwnedArtifacts)
{
    private string nodeId = NodeId ?? throw new ArgumentNullException(nameof(NodeId));
    private IReadOnlyList<string> ownedArtifacts = Snapshot(OwnedArtifacts);

    /// <summary>ビルドノード識別子を取得します。</summary>
    public string NodeId
    {
        get => nodeId;
        init => nodeId = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>ノードが所有する相対成果物パスを取得します。</summary>
    public IReadOnlyList<string> OwnedArtifacts
    {
        get => ownedArtifacts;
        init => ownedArtifacts = Snapshot(value);
    }

    /// <summary>ノードの成果物をキャッシュから再利用したかどうかを取得します。</summary>
    public bool CacheHit { get; init; }

    /// <summary>キャッシュを再利用しなかった理由を取得します。理由がない場合は <see langword="null"/> です。</summary>
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

/// <summary>レポートに含めるノード無効化理由です。</summary>
public sealed record SiteBuildReportInvalidation(string NodeId, IReadOnlyList<string> Reasons)
{
    private string nodeId = NodeId ?? throw new ArgumentNullException(nameof(NodeId));
    private IReadOnlyList<string> reasons = Snapshot(Reasons);

    /// <summary>ビルドノード識別子を取得します。</summary>
    public string NodeId
    {
        get => nodeId;
        init => nodeId = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>ノードが無効になった理由を取得します。</summary>
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
