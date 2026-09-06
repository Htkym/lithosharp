using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;

namespace LithoSharp;

/// <summary>
/// Renders HTML pages and text assets for a static site.
/// </summary>
public interface ISiteTemplate
{
    /// <summary>Renders the template output.</summary>
    Task<SiteTemplateResult> RenderAsync(
        SiteTemplateContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides template data and rendering helpers.
/// </summary>
public sealed class SiteTemplateContext
{
    private readonly SiteGenerator _generator;

    internal SiteTemplateContext(
        SiteGenerator generator,
        SiteSettings site,
        IReadOnlyList<MarkdownPost> posts,
        SiteText text,
        SiteThemeOptions theme,
        IReadOnlyList<SiteExtraPage> extraPages,
        IReadOnlyList<RenderedPage> contentPages,
        IReadOnlyList<SiteTemplatePage> pages,
        SiteTemplateNavigationNode navigation,
        SiteGenerator.RenderContext configuration,
        AssetRegistry assets)
    {
        _generator = generator;
        Site = site;
        Posts = posts;
        Text = text;
        Theme = theme;
        ExtraPages = extraPages;
        ContentPages = contentPages;
        Pages = pages;
        Navigation = navigation;
        Configuration = configuration;
        Assets = assets;
    }

    /// <summary>Site settings.</summary>
    public SiteSettings Site { get; }

    /// <summary>Read Markdown content.</summary>
    public IReadOnlyList<MarkdownPost> Posts { get; }

    /// <summary>UI text.</summary>
    public SiteText Text { get; }

    /// <summary>Theme settings.</summary>
    public SiteThemeOptions Theme { get; }

    /// <summary>Additional pages.</summary>
    public IReadOnlyList<SiteExtraPage> ExtraPages { get; }

    /// <summary>型付きコンテンツコレクションから描画され、公開条件を満たしたページです。</summary>
    public IReadOnlyList<RenderedPage> ContentPages { get; }

    /// <summary>Documentation pages in navigation order.</summary>
    public IReadOnlyList<SiteTemplatePage> Pages { get; }

    /// <summary>Directory-based documentation navigation.</summary>
    public SiteTemplateNavigationNode Navigation { get; }

    internal SiteGenerator.RenderContext Configuration { get; }

    /// <summary>このビルドで登録された資産を取得します。</summary>
    public AssetRegistry Assets { get; }

    internal SiteTemplateResult RenderDocsTemplate() => _generator.RenderDocsTemplate(this);

    internal SiteTemplateResult RenderBlogTemplate() => _generator.RenderBlogTemplate(this);

    /// <summary>Renders Markdown with LithoSharp's safe configuration.</summary>
    public string RenderMarkdown(string markdown) => _generator.RenderMarkdown(markdown);

    /// <summary>Builds a root-relative URL for the configured base URL.</summary>
    public string GetSitePath(string relativePath) => _generator.GetSitePath(Configuration, relativePath);

    /// <summary>Renders a complete document with the standard metadata, header, and footer.</summary>
    public string RenderDocument(SiteTemplateDocument document) =>
        _generator.RenderTemplateDocument(Configuration, document);

    /// <summary>Renders a table of contents for the supplied headings.</summary>
    public string RenderTableOfContents(IReadOnlyList<SiteTemplateHeading> headings) =>
        SiteGenerator.RenderTemplateTableOfContents(Configuration, headings);
}

/// <summary>
/// A text file emitted by a template.
/// </summary>
public sealed record SiteTemplateFile
{
    /// <summary>Path relative to the output root.</summary>
    public required string RelativePath { get; init; }

    /// <summary>UTF-8 text content.</summary>
    public required string Content { get; init; }
}

/// <summary>
/// Template rendering output.
/// </summary>
public sealed record SiteTemplateResult
{
    /// <summary>Creates the result.</summary>
    public SiteTemplateResult(IReadOnlyList<SiteTemplateFile> files)
    {
        Files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <summary>Generated files.</summary>
    public IReadOnlyList<SiteTemplateFile> Files { get; }
}

/// <summary>
/// A rendered Markdown page available to a template.
/// </summary>
public sealed record SiteTemplatePage
{
    /// <summary>Source Markdown post.</summary>
    public required MarkdownPost Post { get; init; }

    /// <summary>Root-relative URL for the page.</summary>
    public required string Url { get; init; }

    /// <summary>HTML rendered from the Markdown body.</summary>
    public required string ContentHtml { get; init; }

    /// <summary>Headings in the rendered content.</summary>
    public required IReadOnlyList<SiteTemplateHeading> Headings { get; init; }

    /// <summary>Previous page in documentation order.</summary>
    public SiteTemplatePageLink? Previous { get; init; }

    /// <summary>Next page in documentation order.</summary>
    public SiteTemplatePageLink? Next { get; init; }
}

/// <summary>
/// A link to an adjacent documentation page.
/// </summary>
public sealed record SiteTemplatePageLink(string Title, string Url);

/// <summary>
/// A heading in rendered Markdown content.
/// </summary>
public sealed record SiteTemplateHeading(int Level, string Id, string Text);

/// <summary>
/// A node in the directory-based documentation navigation tree.
/// </summary>
public sealed record SiteTemplateNavigationNode
{
    /// <summary>Node label.</summary>
    public required string Label { get; init; }

    /// <summary>Page associated with the node, when present.</summary>
    public SiteTemplatePage? Page { get; init; }

    /// <summary>Child nodes in display order.</summary>
    public required IReadOnlyList<SiteTemplateNavigationNode> Children { get; init; }
}

/// <summary>
/// Data for rendering a standard HTML document.
/// </summary>
public sealed record SiteTemplateDocument
{
    /// <summary>Page title.</summary>
    public required string Title { get; init; }

    /// <summary>Output path relative to the site root.</summary>
    public required string RelativePath { get; init; }

    /// <summary>HTML inside the main content element.</summary>
    public required string BodyHtml { get; init; }

    /// <summary>Optional page description.</summary>
    public string? Description { get; init; }

    /// <summary>Open Graph type.</summary>
    public string OpenGraphType { get; init; } = "website";

    /// <summary>Optional publication date.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>Optional social image path.</summary>
    public string? SocialImageRelativePath { get; init; }
}
