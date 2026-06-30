namespace LithoSharp.Configuration;

/// <summary>
/// Core information about the generated web site.
/// </summary>
public sealed record SiteSettings
{
    /// <summary>Site name.</summary>
    public string Title { get; init; } = "My Site";

    /// <summary>Site description.</summary>
    public string Description { get; init; } = "A static site built with LithoSharp.";

    /// <summary>Base URL the site is published at (for example on GitHub Pages).</summary>
    public string BaseUrl { get; init; } = "https://example.com/";

    /// <summary>URL of the GitHub repository that manages this web site.</summary>
    public string RepositoryUrl { get; init; } = string.Empty;

    /// <summary>Google Analytics 4 measurement ID.</summary>
    public string GoogleAnalyticsMeasurementId { get; init; } = string.Empty;

    /// <summary>Language code for the site.</summary>
    public string Language { get; init; } = "en";

    /// <summary>Author name for the site.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Default time zone used when rendering post dates.</summary>
    public string TimeZone { get; init; } = "UTC";
}
