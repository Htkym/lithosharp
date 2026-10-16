using System.Collections.ObjectModel;

namespace LithoSharp.Build;

public sealed class BuildArtifact
{
    /// <summary>Path validity is checked by the build plan so diagnostics can be reported together.</summary>
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

    public BuildArtifactId Id { get; }

    public BuildNodeId OwnerNodeId { get; }

    public string RelativeOutputPath { get; }
}

public sealed class BuildNode
{
    /// <summary>Copies input sequences into an immutable declaration snapshot.</summary>
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

    public BuildNodeId Id { get; }

    public IReadOnlyList<BuildInput> Inputs { get; }

    public IReadOnlyList<BuildNodeId> Dependencies { get; }

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
