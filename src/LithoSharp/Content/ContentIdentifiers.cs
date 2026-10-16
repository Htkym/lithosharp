using System.Text;

namespace LithoSharp.Content;

/// <summary>NFC-normalized identity with ordinal, case-sensitive equality. Empty values, surrounding whitespace and control characters are rejected.</summary>
public sealed class ContentCollectionId : IEquatable<ContentCollectionId>
{
    public ContentCollectionId(string value) => Value = ContentIdentity.Normalize(value, nameof(value));

    public string Value { get; }

    public bool Equals(ContentCollectionId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is ContentCollectionId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

/// <summary>NFC-normalized identity with ordinal, case-sensitive equality. Empty values, surrounding whitespace and control characters are rejected.</summary>
public sealed class ContentEntryId : IEquatable<ContentEntryId>
{
    public ContentEntryId(string value) => Value = ContentIdentity.Normalize(value, nameof(value));

    public string Value { get; }

    public bool Equals(ContentEntryId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is ContentEntryId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

/// <summary>NFC-normalized identity with ordinal, case-sensitive equality. Empty values, surrounding whitespace and control characters are rejected.</summary>
public sealed class ContentLayoutId : IEquatable<ContentLayoutId>
{
    public ContentLayoutId(string value) => Value = ContentIdentity.Normalize(value, nameof(value));

    public string Value { get; }

    public bool Equals(ContentLayoutId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is ContentLayoutId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

/// <summary>NFC-normalized identity with ordinal, case-sensitive equality. Empty values, surrounding whitespace and control characters are rejected.</summary>
public sealed class ContentTransformationId : IEquatable<ContentTransformationId>
{
    public ContentTransformationId(string value) => Value = ContentIdentity.Normalize(value, nameof(value));

    public string Value { get; }

    public bool Equals(ContentTransformationId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is ContentTransformationId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}

internal static class ContentIdentity
{
    public static string Normalize(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A content identifier must not be empty.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A content identifier must not have surrounding whitespace or control characters.",
                parameterName);
        }

        return value.Normalize(NormalizationForm.FormC);
    }
}
