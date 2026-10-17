using System.Text;

namespace LithoSharp.Build;

/// <summary>NFC-normalized, case-sensitive identity independent of OS path rules. Empty values, surrounding whitespace and control characters are rejected.</summary>
public sealed class BuildNodeId : IEquatable<BuildNodeId>
{
    public BuildNodeId(string value)
    {
        Value = BuildIdentifier.Normalize(value, nameof(value), "node");
    }

    public string Value { get; }

    public bool Equals(BuildNodeId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is BuildNodeId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

/// <summary>NFC-normalized, case-sensitive identity independent of OS path rules. Empty values, surrounding whitespace and control characters are rejected.</summary>
public sealed class BuildArtifactId : IEquatable<BuildArtifactId>
{
    public BuildArtifactId(string value)
    {
        Value = BuildIdentifier.Normalize(value, nameof(value), "artifact");
    }

    public string Value { get; }

    public bool Equals(BuildArtifactId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is BuildArtifactId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

internal static class BuildIdentifier
{
    public static string Normalize(string value, string parameterName, string kind)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"A build {kind} identifier must not be empty.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A build {kind} identifier must not have leading or trailing whitespace.",
                parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A build {kind} identifier must not contain control characters.",
                parameterName);
        }

        return value.Normalize(NormalizationForm.FormC);
    }
}
