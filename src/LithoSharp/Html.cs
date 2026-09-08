using System.Net;

namespace LithoSharp;

/// <summary>
/// A small helper for HTML output.
/// </summary>
public static class Html
{
    /// <summary>Encodes the value as an HTML-safe string.</summary>
    /// <param name="value">The input string.</param>
    /// <returns>The encoded string.</returns>
    public static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>Creates a value containing explicitly trusted raw HTML.</summary>
    /// <param name="html">Trusted HTML.</param>
    /// <returns>The raw HTML value.</returns>
    /// <remarks>Callers must ensure that <paramref name="html"/> is safe for the output context.</remarks>
    public static IHtmlContent UnsafeRaw(string html) => new RawHtmlContent(html);
}
