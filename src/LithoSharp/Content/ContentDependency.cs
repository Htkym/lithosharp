using System.Collections.ObjectModel;
using LithoSharp.Build;

namespace LithoSharp.Content;

public enum ContentDependencyKind
{
    Value,

    File,

    /// <summary>A registered asset build node, including generated shared chunks.</summary>
    Asset,
}

public sealed class ContentDependency : IEquatable<ContentDependency>
{
    private ContentDependency(ContentDependencyKind kind, string key, string? value, string? physicalFilePath = null, string? capturedHash = null)
    {
        Kind = kind;
        Key = key;
        Value = value;
        PhysicalFilePath = physicalFilePath ?? key;
        CapturedHash = capturedHash;
    }

    public ContentDependencyKind Kind { get; }

    public string Key { get; }

    public string? Value { get; }

    internal string PhysicalFilePath { get; }
    internal string? CapturedHash { get; }

    /// <summary>Path is relative to the collection input root.</summary>
    public static ContentDependency FromFile(string relativePath)
    {
        var input = BuildInput.FromFile(relativePath);
        var physicalPath = string.Join('/', relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        return new ContentDependency(ContentDependencyKind.File, input.Key, value: null, physicalFilePath: physicalPath);
    }

    internal static ContentDependency FromCapturedFile(string relativePath, string sha256)
    {
        var file = FromFile(relativePath);
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A captured file dependency requires its exact SHA-256 hash.", nameof(sha256));
        return new ContentDependency(ContentDependencyKind.File, file.Key, null, file.PhysicalFilePath, sha256.ToLowerInvariant());
    }

    /// <summary>References a registered asset by its identifier.</summary>
    public static ContentDependency FromAsset(string assetId) =>
        new(ContentDependencyKind.Asset, new BuildNodeId(assetId).Value, null);

    public static ContentDependency FromValue(string key, string value)
    {
        var normalizedKey = ContentIdentity.Normalize(key, nameof(key));
        ArgumentNullException.ThrowIfNull(value);
        return new ContentDependency(
            ContentDependencyKind.Value,
            normalizedKey,
            value);
    }

    public bool Equals(ContentDependency? other) =>
        other is not null
        && Kind == other.Kind
        && StringComparer.Ordinal.Equals(Key, other.Key)
        && StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is ContentDependency other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(Key), Value);

    internal static ReadOnlyCollection<ContentDependency> Snapshot(
        IEnumerable<ContentDependency>? dependencies)
    {
        var values = dependencies?.ToArray() ?? [];
        if (values.Any(static dependency => dependency is null))
        {
            throw new ArgumentException("Content dependencies must not contain null.", nameof(dependencies));
        }

        var collision = values.Where(static dependency => dependency.Kind == ContentDependencyKind.File)
            .GroupBy(static dependency => dependency.Key, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Select(static dependency => dependency.PhysicalFilePath).Distinct(StringComparer.Ordinal).Skip(1).Any());
        if (collision is not null)
            throw new ArgumentException($"File dependencies collide at normalized path '{collision.Key}' with different physical paths.", nameof(dependencies));

        var conflictingCapture = values.Where(static dependency => dependency.Kind == ContentDependencyKind.File)
            .GroupBy(static dependency => dependency.Key, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Select(static dependency => dependency.CapturedHash).Where(static hash => hash is not null)
                .Distinct(StringComparer.Ordinal).Skip(1).Any());
        if (conflictingCapture is not null)
            throw new ArgumentException($"File dependencies contain conflicting captured hashes for '{conflictingCapture.Key}'.", nameof(dependencies));

        return Array.AsReadOnly(values
            .OrderByDescending(static dependency => dependency.CapturedHash is not null)
            .Distinct()
            .OrderBy(static dependency => dependency.Kind)
            .ThenBy(static dependency => dependency.Key, StringComparer.Ordinal)
            .ThenBy(static dependency => dependency.Value, StringComparer.Ordinal)
            .ToArray());
    }
}
