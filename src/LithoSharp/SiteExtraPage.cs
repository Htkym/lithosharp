namespace LithoSharp;

/// <summary>
/// A page the generator emits in addition to the standard pages. Use it to add
/// site-specific pages, such as release notes or a list of sources.
/// </summary>
public sealed record SiteExtraPage
{
    /// <summary>Relative output path (for example <c>releases.html</c>).</summary>
    public required string RelativePath { get; init; }

    /// <summary>Page title.</summary>
    public required string Title { get; init; }

    /// <summary>Body HTML placed inside the <c>main</c> element.</summary>
    public required string BodyHtml { get; init; }

    /// <summary>Navigation label. When <c>null</c>, the page is not shown in navigation.</summary>
    public string? NavLabel { get; init; }

    /// <summary>CSS class applied to the navigation link.</summary>
    public string? NavCssClass { get; init; }

    /// <summary>Whether to include the page in the sitemap.</summary>
    public bool IncludeInSitemap { get; init; } = true;
}
