namespace LithoSharp;

/// <summary>
/// Built-in template for documentation with directory navigation.
/// </summary>
public class DocsSiteTemplate : ISiteTemplate
{
    /// <summary>Emits the shared local search and sitemap. False preserves the original Docs outputs.</summary>
    public bool EnableSearch { get; init; }
    /// <summary>Shows a GET search form in the header except on the search page. Also enables local search.</summary>
    public bool EnableHeaderSearch { get; init; }

    /// <summary>Collapses sidebar topics except those containing the current page.</summary>
    public bool CollapseTopics { get; init; }

    /// <summary>Shows home-page topic cards from the existing navigation and page summaries.</summary>
    public bool ShowTopicCards { get; init; }

    /// <summary>Shows the home-page start link. True preserves the default.</summary>
    public bool ShowStartLink { get; init; } = true;

    /// <summary>Uses a compact introduction on the home page.</summary>
    public bool CompactHome { get; init; }

    /// <summary>Optionally renders a shared sidebar for all template pages from the current URL and locale. Null preserves generated navigation.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string?, string?, IHtmlContent>? NavigationRenderer { get; init; }

    /// <summary>Adds per-page navigation and actions while retaining the built-in brand, search, theme, and menu controls. The URL is null for the not-found page.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string?, string?, DocsHeaderContent>? HeaderContentRenderer { get; init; }

    internal bool SearchEnabled => EnableSearch || EnableHeaderSearch;

    /// <inheritdoc />
    public virtual Task<SiteTemplateResult> RenderAsync(
        SiteTemplateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(context.RenderDocsTemplate(this));
    }
}
