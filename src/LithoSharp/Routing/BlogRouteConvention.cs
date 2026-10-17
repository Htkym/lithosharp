namespace LithoSharp.Routing;

internal static class BlogRouteConvention
{
    /// <summary>Preserves the legacy .html file URL.</summary>
    public static SiteRoute ForMarkdownPost(string relativeOutputPath, string? baseUrl = null) =>
        SiteRoute.ForFile(relativeOutputPath, baseUrl);

    /// <summary>Preserves the legacy .html file URL.</summary>
    public static SiteRoute ForExtraPage(string relativeOutputPath, string? baseUrl = null) =>
        SiteRoute.ForFile(relativeOutputPath, baseUrl);
}
