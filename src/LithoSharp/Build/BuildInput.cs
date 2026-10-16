using System.Collections.ObjectModel;
using System.Text;

namespace LithoSharp.Build;

public enum BuildInputKind
{
    Value,

    Collection,

    Configuration,

    File,
}

public sealed class BuildInput
{
    private BuildInput(BuildInputKind kind, string key, string? value)
    {
        Kind = kind;
        Key = key;
        Value = value;
    }

    public BuildInputKind Kind { get; }

    public string Key { get; }

    public string? Value { get; }

    public static BuildInput FromValue(string key, string value) =>
        new(BuildInputKind.Value, NormalizeKey(key), PreserveValue(value));

    /// <summary>Requires a nonempty deterministic fingerprint for the entire collection.</summary>
    public static BuildInput FromCollection(string collectionId) =>
        new(BuildInputKind.Collection, NormalizeKey(collectionId), value: null);

    /// <summary>Requires a nonempty deterministic fingerprint for the entire collection.</summary>
    public static BuildInput FromCollection(string collectionId, string fingerprint) =>
        new(
            BuildInputKind.Collection,
            NormalizeKey(collectionId),
            PreserveFingerprint(fingerprint));

    public static BuildInput FromConfiguration(string key, string value) =>
        new(BuildInputKind.Configuration, NormalizeKey(key), PreserveValue(value));

    /// <summary>Uses a safe relative file path independent of the output root.</summary>
    public static BuildInput FromFile(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var normalizedPath = Routing.SiteRoute.NormalizeRelativeOutputPath(relativePath);
        return new BuildInput(BuildInputKind.File, normalizedPath, value: null);
    }

    /// <summary>Requires a safe relative file path and a nonempty deterministic content fingerprint.</summary>
    public static BuildInput FromFile(string relativePath, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var normalizedPath = Routing.SiteRoute.NormalizeRelativeOutputPath(relativePath);
        return new BuildInput(
            BuildInputKind.File,
            normalizedPath,
            PreserveFingerprint(fingerprint));
    }

    private static string NormalizeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0 || string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A build input key must not be empty.", nameof(key));
        }

        if (!string.Equals(key, key.Trim(), StringComparison.Ordinal)
            || key.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A build input key must not have surrounding whitespace or control characters.",
                nameof(key));
        }

        return key.Normalize(NormalizationForm.FormC);
    }

    private static string PreserveValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value;
    }

    private static string PreserveFingerprint(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (fingerprint.Length == 0)
        {
            throw new ArgumentException(
                "A build input fingerprint must not be empty.",
                nameof(fingerprint));
        }

        return fingerprint;
    }

    internal static ReadOnlyCollection<BuildInput> Snapshot(IEnumerable<BuildInput>? inputs)
    {
        var values = inputs?.ToArray() ?? [];
        if (values.Any(input => input is null))
        {
            throw new ArgumentException("Build inputs must not contain null.", nameof(inputs));
        }

        return Array.AsReadOnly(values
            .OrderBy(input => input.Kind)
            .ThenBy(input => input.Key, StringComparer.Ordinal)
            .ThenBy(input => input.Value, StringComparer.Ordinal)
            .ToArray());
    }
}
