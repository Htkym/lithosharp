namespace LithoSharp;

/// <summary>Rendered content and optional regions for a built-in page layout.</summary>
/// <param name="Body">The content inside the main article.</param>
public sealed record PageLayoutContent(IHtmlContent Body)
{
    /// <summary>Explicitly trusted additions to the document head.</summary>
    public IHtmlContent? Head { get; init; }
    /// <summary>Whether the built-in script is included. A custom client can own menu and navigation behavior instead.</summary>
    public bool IncludeDefaultScript { get; init; } = true;
    /// <summary>Optional documentation sidebar, including its aside element.</summary>
    public IHtmlContent? Sidebar { get; init; }

    /// <summary>Optional table of contents, including its navigation element.</summary>
    public IHtmlContent? TableOfContents { get; init; }

    /// <summary>Open Graph content type. Defaults to website.</summary>
    public string OpenGraphType { get; init; } = "website";

    /// <summary>Optional social preview image URL. Defaults to the site's generated image when one is available.</summary>
    public SiteUrl? SocialImageUrl { get; init; }

    /// <summary>Whether Blog navigation, search, feed link, and script are included.</summary>
    public bool IncludeBlogNavigation { get; init; } = true;
}
