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
    private ContentDependency(ContentDependencyKind kind, string key, string? value)
    {
        Kind = kind;
        Key = key;
        Value = value;
    }

    public ContentDependencyKind Kind { get; }

    public string Key { get; }

    public string? Value { get; }

    /// <summary>Path is relative to the collection input root.</summary>
    public static ContentDependency FromFile(string relativePath)
    {
        var input = BuildInput.FromFile(relativePath);
        return new ContentDependency(ContentDependencyKind.File, input.Key, value: null);
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

        return Array.AsReadOnly(values
            .Distinct()
            .OrderBy(static dependency => dependency.Kind)
            .ThenBy(static dependency => dependency.Key, StringComparer.Ordinal)
            .ThenBy(static dependency => dependency.Value, StringComparer.Ordinal)
            .ToArray());
    }
}
