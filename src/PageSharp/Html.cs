using System.Net;

namespace PageSharp;

/// <summary>
/// A small helper for HTML output.
/// </summary>
public static class Html
{
    /// <summary>Encodes the value as an HTML-safe string.</summary>
    /// <param name="value">The input string.</param>
    /// <returns>The encoded string.</returns>
    public static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
