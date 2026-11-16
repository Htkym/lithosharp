namespace LithoSharp;

/// <summary>Separate rendered documentation regions for layouts that place navigation outside the article.</summary>
public sealed class DocumentationPageChrome
{
    /// <summary>Creates documentation regions with the rendered article and its page metadata.</summary>
    /// <param name="article">Article content, including the title, notices, messages, edit link and last-update information.</param>
    public DocumentationPageChrome(IHtmlContent article)
    {
        ArgumentNullException.ThrowIfNull(article);
        Article = article;
    }

    /// <summary>Article content without breadcrumbs, search, variant links or adjacent-document navigation.</summary>
    public IHtmlContent Article { get; }
    /// <summary>Optional breadcrumb navigation, including its navigation element.</summary>
    public IHtmlContent? Breadcrumbs { get; init; }
    /// <summary>Optional documentation search, including its section element.</summary>
    public IHtmlContent? Search { get; init; }
    /// <summary>Optional version and language navigation, including its navigation element.</summary>
    public IHtmlContent? Variants { get; init; }
    /// <summary>Optional previous and next document navigation, including its navigation element.</summary>
    public IHtmlContent? Adjacent { get; init; }
    /// <summary>Visible table-of-contents headings; empty when the document hides its table of contents.</summary>
    public IReadOnlyList<SiteTemplateHeading> Headings { get; init; } = [];
}
