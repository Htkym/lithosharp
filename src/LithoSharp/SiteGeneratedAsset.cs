using LithoSharp.Build;
using LithoSharp.Routing;

namespace LithoSharp;

/// <summary>An immutable prepared asset whose path has already been assigned by its producer.</summary>
/// <remarks>Use for bundled chunks with embedded relative references. The registry hashes the bytes,
/// preserves the declared path, and publishes through the same transaction as all other assets.</remarks>
public sealed class SiteGeneratedAsset
{
    private readonly byte[] bytes;

    /// <summary>Snapshots an asset, its inputs and references to other registered assets.</summary>
    /// <exception cref="ArgumentException">An identifier or output path is invalid.</exception>
    public SiteGeneratedAsset(string id, string relativeOutputPath, ReadOnlyMemory<byte> content,
        IEnumerable<BuildInput>? inputs = null, IEnumerable<string>? referencedAssetIds = null)
    {
        Id = new BuildNodeId(id).Value;
        RelativeOutputPath = SiteRoute.NormalizeRelativeOutputPath(relativeOutputPath);
        bytes = content.ToArray();
        Inputs = BuildInput.Snapshot(inputs);
        ReferencedAssetIds = Array.AsReadOnly((referencedAssetIds ?? [])
            .Select(value => new BuildNodeId(value).Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>The unique asset identifier; its build node is named <c>asset:{Id}</c>.</summary>
    public string Id { get; }
    /// <summary>The validated output path, without an additional fingerprint suffix.</summary>
    public string RelativeOutputPath { get; }
    /// <summary>The declared inputs used to prepare the asset.</summary>
    public IReadOnlyList<BuildInput> Inputs { get; }
    /// <summary>Other assets referenced by this asset, including shared chunks.</summary>
    public IReadOnlyList<string> ReferencedAssetIds { get; }
    /// <summary>Opens a read-only snapshot of the prepared content.</summary>
    public Stream OpenRead() => new MemoryStream(bytes, writable: false);
    internal byte[] Bytes => bytes;
}
