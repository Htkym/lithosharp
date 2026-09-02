using System.Collections.ObjectModel;

namespace LithoSharp.Build;

/// <summary>ビルドノードが所有する単一の出力成果物を表します。</summary>
public sealed class BuildArtifact
{
    /// <summary>成果物宣言を作成します。</summary>
    /// <param name="id">成果物の安定した識別子。</param>
    /// <param name="ownerNodeId">成果物を一意に所有するノードの識別子。</param>
    /// <param name="relativeOutputPath">出力ルートからの相対ファイルパス。</param>
    /// <exception cref="ArgumentNullException">いずれかの引数が <see langword="null"/> です。</exception>
    /// <remarks>出力パスの妥当性は、複数の問題をまとめて報告できるようにビルド計画で検証します。</remarks>
    public BuildArtifact(
        BuildArtifactId id,
        BuildNodeId ownerNodeId,
        string relativeOutputPath)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(ownerNodeId);
        ArgumentNullException.ThrowIfNull(relativeOutputPath);

        Id = id;
        OwnerNodeId = ownerNodeId;
        RelativeOutputPath = relativeOutputPath;
    }

    /// <summary>成果物の安定した識別子を取得します。</summary>
    public BuildArtifactId Id { get; }

    /// <summary>成果物を一意に所有するノードの識別子を取得します。</summary>
    public BuildNodeId OwnerNodeId { get; }

    /// <summary>出力ルートからの相対ファイルパスを取得します。</summary>
    public string RelativeOutputPath { get; }
}

/// <summary>入力、ノード依存関係、所有成果物を宣言する不変のビルド単位を表します。</summary>
public sealed class BuildNode
{
    /// <summary>ビルドノード宣言を作成し、列挙値を不変のスナップショットとして保持します。</summary>
    /// <param name="id">ノードの安定した識別子。</param>
    /// <param name="inputs">ノードが直接参照する入力。</param>
    /// <param name="dependencies">先に完了する必要があるノード。</param>
    /// <param name="artifacts">ノードが所有する成果物。</param>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException">列挙値に <see langword="null"/> が含まれます。</exception>
    public BuildNode(
        BuildNodeId id,
        IEnumerable<BuildInput>? inputs = null,
        IEnumerable<BuildNodeId>? dependencies = null,
        IEnumerable<BuildArtifact>? artifacts = null)
    {
        ArgumentNullException.ThrowIfNull(id);

        Id = id;
        Inputs = BuildInput.Snapshot(inputs);
        Dependencies = SnapshotDependencies(dependencies);
        Artifacts = SnapshotArtifacts(artifacts);
    }

    /// <summary>ノードの安定した識別子を取得します。</summary>
    public BuildNodeId Id { get; }

    /// <summary>種類、キー、値の順に並べられた宣言入力を取得します。</summary>
    public IReadOnlyList<BuildInput> Inputs { get; }

    /// <summary>識別子の序数順に並べられた依存ノードを取得します。</summary>
    public IReadOnlyList<BuildNodeId> Dependencies { get; }

    /// <summary>識別子と出力パスの序数順に並べられた所有成果物を取得します。</summary>
    public IReadOnlyList<BuildArtifact> Artifacts { get; }

    private static ReadOnlyCollection<BuildNodeId> SnapshotDependencies(
        IEnumerable<BuildNodeId>? dependencies)
    {
        var values = dependencies?.ToArray() ?? [];
        if (values.Any(dependency => dependency is null))
        {
            throw new ArgumentException("Build dependencies must not contain null.", nameof(dependencies));
        }

        return Array.AsReadOnly(values
            .Distinct()
            .OrderBy(dependency => dependency.Value, StringComparer.Ordinal)
            .ToArray());
    }

    private static ReadOnlyCollection<BuildArtifact> SnapshotArtifacts(
        IEnumerable<BuildArtifact>? artifacts)
    {
        var values = artifacts?.ToArray() ?? [];
        if (values.Any(artifact => artifact is null))
        {
            throw new ArgumentException("Build artifacts must not contain null.", nameof(artifacts));
        }

        return Array.AsReadOnly(values
            .OrderBy(artifact => artifact.Id.Value, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.RelativeOutputPath, StringComparer.Ordinal)
            .ThenBy(artifact => artifact.OwnerNodeId.Value, StringComparer.Ordinal)
            .ToArray());
    }
}
