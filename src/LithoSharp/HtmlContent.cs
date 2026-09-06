namespace LithoSharp;

/// <summary>Represents HTML that has already been assigned an output context.</summary>
public interface IHtmlContent
{
    /// <summary>Gets the HTML representation.</summary>
    string ToHtmlString();
}

/// <summary>HTML text encoded for element content.</summary>
public sealed class HtmlText : IHtmlContent
{
    /// <summary>Creates encoded text.</summary>
    public HtmlText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>The original text.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public string ToHtmlString() => Html.Encode(Value);

    /// <inheritdoc />
    public override string ToString() => ToHtmlString();
}

/// <summary>HTML text encoded for a quoted attribute value.</summary>
public sealed class HtmlAttributeValue : IHtmlContent
{
    /// <summary>Creates an encoded quoted attribute value.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value contains a control character.</exception>
    public HtmlAttributeValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("An HTML attribute value must not contain control characters.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The original attribute value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public string ToHtmlString() => Html.Encode(Value);

    /// <inheritdoc />
    public override string ToString() => ToHtmlString();
}

internal sealed class RawHtmlContent : IHtmlContent
{
    public RawHtmlContent(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        Value = html;
    }

    private string Value { get; }

    public string ToHtmlString() => Value;
}
