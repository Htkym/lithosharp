namespace LithoSharp;

/// <summary>
/// Built-in template for documentation with directory navigation.
/// </summary>
public sealed class DocsSiteTemplate : ISiteTemplate
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

    internal bool SearchEnabled => EnableSearch || EnableHeaderSearch;

    /// <inheritdoc />
    public Task<SiteTemplateResult> RenderAsync(
        SiteTemplateContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(context.RenderDocsTemplate(this));
    }
}
