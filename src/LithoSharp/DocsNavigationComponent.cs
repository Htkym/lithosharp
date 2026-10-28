using System.Text;

namespace LithoSharp;

/// <summary>Documentation navigation, in display order.</summary>
/// <param name="Root">The precomputed navigation tree.</param>
/// <param name="AdditionalLinks">Links displayed after the tree.</param>
/// <param name="CurrentUrl">The current page URL.</param>
public sealed record DocsNavigationComponentProps(
    SiteTemplateNavigationNode Root,
    IReadOnlyList<NavigationLink> AdditionalLinks,
    SiteUrl? CurrentUrl = null)
{
    /// <summary>Uses native disclosure controls for topics and initially opens the current page's ancestors.</summary>
    public bool CollapseTopics { get; init; }
}

/// <summary>Renders the documentation sidebar using the built-in CSS and script contract.</summary>
public sealed class DocsNavigationComponent : ISiteComponent<DocsNavigationComponentProps>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The props, root, root children, additional links, a link URL, or context is null.</exception>
    public IHtmlContent Render(DocsNavigationComponentProps props, ComponentRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(props.Root);
        ArgumentNullException.ThrowIfNull(props.Root.Children);
        ArgumentNullException.ThrowIfNull(props.AdditionalLinks);
        ArgumentNullException.ThrowIfNull(context);
        foreach (var link in props.AdditionalLinks)
        {
            ArgumentNullException.ThrowIfNull(link);
            ArgumentNullException.ThrowIfNull(link.Url);
        }
        return Html.UnsafeRaw(RenderCore(props.Root,
            props.AdditionalLinks.Select(link => (link.Label, link.Url.Value, link.IsCurrent)), props.CurrentUrl?.Value, props.CollapseTopics));
    }

    internal static string RenderCore(SiteTemplateNavigationNode root,
        IEnumerable<(string Label, string Url, bool IsCurrent)> additionalLinks, string? currentUrl, bool collapseTopics = false)
    {
        var body = new StringBuilder();
        body.AppendLine("<aside id=\"docs-sidebar\" class=\"docs-sidebar\" data-docs-sidebar>");
        body.AppendLine("<nav aria-label=\"Documentation navigation\">");
        body.AppendLine("<ul class=\"docs-nav-list\">");
        AppendNodes(body, root, currentUrl, collapseTopics);
        foreach (var link in additionalLinks)
        {
            body.Append("<li>");
            AppendLink(body, link.Label, link.Url, link.IsCurrent || IsCurrent(link.Url, currentUrl));
            body.AppendLine("</li>");
        }
        body.AppendLine("</ul>");
        body.AppendLine("</nav>");
        body.AppendLine("</aside>");
        return body.ToString();
    }

    private static void AppendNodes(StringBuilder body, SiteTemplateNavigationNode parent, string? currentUrl, bool collapseTopics)
    {
        foreach (var node in parent.Children)
        {
            var isCurrent = node.Page is not null && IsCurrent(node.Page.Url, currentUrl);
            if (node.Children.Count == 0 && node.Page is not null)
            {
                body.Append("<li>");
                AppendLink(body, node.Label, node.Page.Url, isCurrent);
                body.AppendLine("</li>");
                continue;
            }

            var containsCurrent = ContainsUrl(node, currentUrl);
            var folderClass = containsCurrent ? "docs-nav-folder is-ancestor" : "docs-nav-folder";
            body.AppendLine($"<li class=\"{folderClass}\">");
            var collapsible = collapseTopics && node.Children.Count > 0;
            if (collapsible)
            {
                body.AppendLine($"<details class=\"docs-nav-topic\"{(containsCurrent ? " open" : string.Empty)}>");
                body.AppendLine($"<summary>{Html.Encode(node.Label)}</summary>");
            }
            if (node.Page is not null)
            {
                AppendLink(body, node.Label, node.Page.Url, isCurrent);
                body.AppendLine();
            }
            else if (!collapsible)
            {
                body.AppendLine($"<span>{Html.Encode(node.Label)}</span>");
            }
            body.AppendLine("<ul>");
            AppendNodes(body, node, currentUrl, collapseTopics);
            if (collapsible)
            {
                body.AppendLine("</ul></details></li>");
            }
            else
            {
                body.AppendLine("</ul></li>");
            }
        }
    }

    private static void AppendLink(StringBuilder body, string label, string url, bool isCurrent)
    {
        var cssClass = isCurrent ? "docs-nav-link is-current" : "docs-nav-link";
        var current = isCurrent ? " aria-current=\"page\"" : string.Empty;
        body.Append($"<a class=\"{cssClass}\" href=\"{Html.Encode(url)}\"{current}>{Html.Encode(label)}</a>");
    }

    private static bool IsCurrent(string url, string? currentUrl) =>
        !string.IsNullOrWhiteSpace(currentUrl) && string.Equals(url, currentUrl, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsUrl(SiteTemplateNavigationNode node, string? currentUrl) =>
        (node.Page is not null && IsCurrent(node.Page.Url, currentUrl))
        || node.Children.Any(child => ContainsUrl(child, currentUrl));
}
