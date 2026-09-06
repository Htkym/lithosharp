namespace LithoSharp;

/// <summary>Rendered content and optional regions for a built-in page layout.</summary>
/// <param name="Body">The content inside the main article.</param>
public sealed record PageLayoutContent(IHtmlContent Body)
{
    /// <summary>Optional documentation sidebar, including its aside element.</summary>
    public IHtmlContent? Sidebar { get; init; }

    /// <summary>Optional table of contents, including its navigation element.</summary>
    public IHtmlContent? TableOfContents { get; init; }

    /// <summary>Open Graph content type. Defaults to website.</summary>
    public string OpenGraphType { get; init; } = "website";

    /// <summary>Optional social preview image URL. Defaults to the site's generated image.</summary>
    public SiteUrl? SocialImageUrl { get; init; }

    /// <summary>Whether Blog navigation, search, feed link, and script are included.</summary>
    public bool IncludeBlogNavigation { get; init; } = true;
}
