using LithoSharp.Routing;

namespace LithoSharp;

/// <summary>A validated site URL.</summary>
public sealed class SiteUrl
{
    private SiteUrl(string value) => Value = value;

    /// <summary>The URL string.</summary>
    public string Value { get; }

    /// <summary>Creates a URL from an already validated route.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="route"/> is null.</exception>
    public static SiteUrl FromRoute(SiteRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new SiteUrl(route.PublicPath);
    }

    /// <summary>Creates a URL for a directory index route.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> is null.</exception>
    /// <exception cref="ArgumentException">The relative path is unsafe.</exception>
    /// <exception cref="UriFormatException">URL encoding or the base URL is invalid.</exception>
    public static SiteUrl ForDirectory(string relativePath, string? baseUrl = null) =>
        new(SiteRoute.ForDirectoryIndex(relativePath, baseUrl).PublicPath);

    /// <summary>Creates a URL for an output file route.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> is null.</exception>
    /// <exception cref="ArgumentException">The relative path is empty or unsafe.</exception>
    /// <exception cref="UriFormatException">URL encoding or the base URL is invalid.</exception>
    public static SiteUrl ForFile(string relativePath, string? baseUrl = null) =>
        FromRoute(SiteRoute.ForFile(relativePath, baseUrl));

    /// <summary>Creates a validated absolute HTTP or HTTPS URL.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="UriFormatException">The value is not a safe absolute HTTP or HTTPS URL.</exception>
    public static SiteUrl FromAbsolute(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(char.IsControl) || value.Any(char.IsWhiteSpace) || value.Contains('\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.IsWellFormedOriginalString())
        {
            throw new UriFormatException("The URL must be an absolute, well-formed HTTP or HTTPS URL.");
        }

        return new SiteUrl(value);
    }

    /// <summary>Converts this URL to a quoted HTML attribute value.</summary>
    public HtmlAttributeValue ToAttributeValue() => new(Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
