using System.Text;

namespace LithoSharp.Pages;

/// <summary>Opaque NFC-normalized identity with ordinal, case-sensitive equality, independent of OS path rules. Empty values, surrounding whitespace and control characters are rejected.</summary>
public sealed class PageId : IEquatable<PageId>
{
    public PageId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0 || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A page identifier must not be empty.", nameof(value));
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A page identifier must not have leading or trailing whitespace.", nameof(value));
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("A page identifier must not contain control characters.", nameof(value));
        }

        Value = value.Normalize(NormalizationForm.FormC);
    }

    public string Value { get; }

    public bool Equals(PageId? other) =>
        other is not null && StringComparer.Ordinal.Equals(Value, other.Value);

    public override bool Equals(object? obj) => obj is PageId other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
