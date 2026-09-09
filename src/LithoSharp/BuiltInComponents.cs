using LithoSharp.Content;
using System.Text;

namespace LithoSharp;

/// <summary>Renders the standard table of contents.</summary>
public sealed class TableOfContentsComponent : ISiteComponent<IReadOnlyList<SiteTemplateHeading>>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The headings, a heading, or the context is null.</exception>
    public IHtmlContent Render(IReadOnlyList<SiteTemplateHeading> props, ComponentRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(context);
        foreach (var heading in props)
            ArgumentNullException.ThrowIfNull(heading);
        return Html.UnsafeRaw(RenderCore(context.Configuration, props));
    }

    internal static string RenderCore(SiteGenerator.RenderContext configuration, IReadOnlyList<SiteTemplateHeading> headings)
    {
        var body = new StringBuilder();
        body.AppendLine("<aside class=\"post-toc\" aria-labelledby=\"post-toc-title\">");
        body.AppendLine($"<h2 id=\"post-toc-title\">{Html.Encode(configuration.Text.TableOfContentsHeading)}</h2>");
        if (headings.Count == 0)
            body.AppendLine($"<p>{Html.Encode(configuration.Text.TableOfContentsEmpty)}</p>");
        else
        {
            body.AppendLine($"<nav class=\"toc-nav\" aria-label=\"{Html.Encode(configuration.Text.TableOfContentsHeading)}\">");
            body.AppendLine("<div class=\"toc-track\" aria-hidden=\"true\"></div>");
            body.AppendLine("<div class=\"toc-indicator\" aria-hidden=\"true\"></div>");
            body.AppendLine("<ol class=\"toc-list\">");
            foreach (var heading in headings)
            {
                var depth = Math.Clamp(heading.Level - 1, 1, 3);
                body.AppendLine($"<li class=\"toc-depth-{depth}\" data-toc-item><a data-toc-link href=\"#{Html.Encode(heading.Id)}\">{Html.Encode(heading.Text)}</a></li>");
            }
            body.AppendLine("</ol>");
            body.AppendLine("</nav>");
        }
        body.AppendLine("</aside>");
        return body.ToString();
    }
}

/// <summary>Renders the standard header search form.</summary>
public sealed class SearchFormComponent : ISiteComponent<SiteUrl>
{
    /// <inheritdoc />
    public IHtmlContent Render(SiteUrl props, ComponentRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(context);
        return Html.UnsafeRaw(RenderCore(context.Configuration, props.Value));
    }

    internal static string RenderCore(SiteGenerator.RenderContext configuration, string actionUrl)
    {
        var searchPath = Html.Encode(actionUrl);
        var inputLabel = Html.Encode(configuration.Text.SearchInputLabel);
        var placeholder = Html.Encode(configuration.Text.HeaderSearchPlaceholder);
        var buttonLabel = Html.Encode(configuration.Text.SearchButtonLabel);
        return $"""
                  <form class="site-header-search browser-search" role="search" action="{searchPath}" method="get">
                    <label class="visually-hidden" for="header-search-input">{inputLabel}</label>
                    <input id="header-search-input" name="q" type="search" autocomplete="off" autocapitalize="off" spellcheck="false" enterkeyhint="search" placeholder="{placeholder}" aria-label="{inputLabel}">
                    <button type="submit" aria-label="{buttonLabel}">
                      {BuildSearchIconSvg()}
                      <span class="visually-hidden">{buttonLabel}</span>
                    </button>
                  </form>
                """;
    }

    private static string BuildSearchIconSvg() => """
        <svg aria-hidden="true" viewBox="0 0 24 24">
          <circle cx="11" cy="11" r="5.5" />
          <path d="M15.25 15.25L19 19" />
        </svg>
        """;

}

/// <summary>A navigation link.</summary>
public sealed record NavigationLink(string Label, SiteUrl Url, string? CssClass = null, bool IsCurrent = false);

/// <summary>Previous and next links.</summary>
public sealed record PreviousNextComponentProps(NavigationLink? Previous, NavigationLink? Next);

/// <summary>Footer data.</summary>
public sealed record FooterComponentProps(string SiteTitle, bool Docs = false);

/// <summary>Renders previous and next links.</summary>
public sealed class PreviousNextComponent : ISiteComponent<PreviousNextComponentProps>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The props, context, or a supplied link URL is null.</exception>
    public IHtmlContent Render(PreviousNextComponentProps props, ComponentRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(context);
        if (props.Previous is not null)
            ArgumentNullException.ThrowIfNull(props.Previous.Url);
        if (props.Next is not null)
            ArgumentNullException.ThrowIfNull(props.Next.Url);
        return Html.UnsafeRaw(RenderCore(props.Previous?.Label, props.Previous?.Url.Value, props.Next?.Label, props.Next?.Url.Value));
    }

    internal static string RenderCore(string? previousLabel, string? previousUrl, string? nextLabel, string? nextUrl)
    {
        if (previousLabel is null && nextLabel is null) return string.Empty;
        var body = new StringBuilder("<nav class=\"docs-pagination\" aria-label=\"Document navigation\">\n");
        if (previousLabel is not null)
            body.AppendLine($"<a class=\"docs-pagination-previous\" rel=\"prev\" href=\"{Html.Encode(previousUrl)}\"><small>Previous</small><span>{Html.Encode(previousLabel)}</span></a>");
        else body.AppendLine("<span></span>");
        if (nextLabel is not null)
            body.AppendLine($"<a class=\"docs-pagination-next\" rel=\"next\" href=\"{Html.Encode(nextUrl)}\"><small>Next</small><span>{Html.Encode(nextLabel)}</span></a>");
        body.AppendLine("</nav>");
        return body.ToString();
    }
}

/// <summary>Renders flat navigation links.</summary>
public sealed class NavigationComponent : ISiteComponent<IReadOnlyList<NavigationLink>>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The links, a link, a link URL, or the context is null.</exception>
    public IHtmlContent Render(IReadOnlyList<NavigationLink> props, ComponentRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(context);
        foreach (var link in props)
        {
            ArgumentNullException.ThrowIfNull(link);
            ArgumentNullException.ThrowIfNull(link.Url);
        }
        return Html.UnsafeRaw(RenderCore(props.Select(link => (link.Label, link.Url.Value, link.CssClass, link.IsCurrent))));
    }

    internal static string RenderCore(IEnumerable<(string Label, string Url, string? CssClass, bool IsCurrent)> links) =>
        string.Join("\n        ", links.Select(link =>
            $"<a{(string.IsNullOrEmpty(link.CssClass) ? string.Empty : $" class=\"{Html.Encode(link.CssClass)}\"")} href=\"{Html.Encode(link.Url)}\"{(link.IsCurrent ? " aria-current=\"page\"" : string.Empty)}>{Html.Encode(link.Label)}</a>"));
}

/// <summary>Renders the standard site footer.</summary>
public sealed class FooterComponent : ISiteComponent<FooterComponentProps>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The props, site title, or context is null.</exception>
    public IHtmlContent Render(FooterComponentProps props, ComponentRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(props.SiteTitle);
        ArgumentNullException.ThrowIfNull(context);
        return Html.UnsafeRaw(RenderCore(props.SiteTitle, props.Docs));
    }

    internal static string RenderCore(string title, bool docs) => docs
        ? $"<footer class=\"docs-footer\"><p>Generated by {Html.Encode(title)}.</p></footer>"
        : $"<footer class=\"site-footer\">\n    <p>Generated by {Html.Encode(title)}.</p>\n  </footer>";
}

/// <summary>Renders the blog header.</summary>
public sealed record BlogHeaderComponentProps(
    string Brand,
    SiteUrl HomeUrl,
    IReadOnlyList<NavigationLink> Links,
    SiteUrl? SearchUrl = null,
    bool IncludeNavigation = true);

/// <summary>Renders the blog header.</summary>
public sealed class BlogHeaderComponent : ISiteComponent<BlogHeaderComponentProps>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The props, required header data, a link URL, or the context is null.</exception>
    public IHtmlContent Render(BlogHeaderComponentProps props, ComponentRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(props.Brand);
        ArgumentNullException.ThrowIfNull(props.HomeUrl);
        ArgumentNullException.ThrowIfNull(props.Links);
        ArgumentNullException.ThrowIfNull(context);
        foreach (var link in props.Links)
        {
            ArgumentNullException.ThrowIfNull(link);
            ArgumentNullException.ThrowIfNull(link.Url);
        }
        var nav = NavigationComponent.RenderCore(props.Links.Select(link => (link.Label, link.Url.Value, link.CssClass, link.IsCurrent)));
        var search = props.SearchUrl is null ? string.Empty : SearchFormComponent.RenderCore(context.Configuration, props.SearchUrl.Value);
        return Html.UnsafeRaw(RenderCore(props.Brand, props.HomeUrl.Value, nav, search, props.IncludeNavigation, context.Text, context.Theme.EnableThemeSwitching));
    }

    internal static string RenderCore(string brand, string homeUrl, string navHtml, string searchHtml, bool includeNavigation, SiteText text, bool enableThemeSwitching)
    {
        if (!includeNavigation)
            return $"<header class=\"site-header\"><a class=\"brand\" href=\"{Html.Encode(homeUrl)}\">{Html.Encode(brand)}</a></header>";
        var menuLabel = Html.Encode(text.MenuLabel);
        var navigationLabel = Html.Encode(text.SiteNavigationLabel);
        return $"""
              <header class="site-header">
                <a class="brand" href="{Html.Encode(homeUrl)}">{Html.Encode(brand)}</a>
                <div class="site-nav-shell">
                  <a class="site-home-link site-icon-button" href="{Html.Encode(homeUrl)}" aria-label="Home">
                    {BuildHomeIconSvg()}
                    <span class="visually-hidden">Home</span>
                  </a>
                  {searchHtml}
                  {(enableThemeSwitching ? SiteGenerator.BuildThemeToggle() : string.Empty)}
                  <button class="site-menu-toggle site-icon-button" type="button" aria-expanded="false" aria-controls="site-menu" aria-label="{menuLabel}" data-site-menu-toggle>
                    {BuildMenuIconSvg()}
                    <span class="visually-hidden">{menuLabel}</span>
                  </button>
                  <nav id="site-menu" class="site-nav" data-site-nav aria-label="{navigationLabel}">
                    {navHtml}
                  </nav>
                </div>
              </header>
            """;
    }

    private static string BuildHomeIconSvg() => "<svg aria-hidden=\"true\" viewBox=\"0 0 24 24\">\n  <path d=\"M4.5 10.5L12 4.5l7.5 6v8.25a.75.75 0 0 1-.75.75h-4.5a.75.75 0 0 1-.75-.75V15a1.5 1.5 0 0 0-3 0v3.75a.75.75 0 0 1-.75.75h-4.5a.75.75 0 0 1-.75-.75z\" />\n</svg>";

    private static string BuildMenuIconSvg() => "<svg aria-hidden=\"true\" viewBox=\"0 0 24 24\">\n  <path d=\"M4.5 7.5h15M4.5 12h15M4.5 16.5h15\" />\n</svg>";
}

/// <summary>Renders the documentation header.</summary>
public sealed class DocsHeaderComponent : ISiteComponent<NavigationLink>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The link, label, URL, or context is null.</exception>
    public IHtmlContent Render(NavigationLink props, ComponentRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(props.Label);
        ArgumentNullException.ThrowIfNull(props.Url);
        ArgumentNullException.ThrowIfNull(context);
        return Html.UnsafeRaw(RenderCore(props.Label, props.Url.Value, context.Theme.EnableThemeSwitching));
    }

    internal static string RenderCore(string title, string url, bool enableThemeSwitching) => $"""
            <header class="docs-header">
                <a class="docs-brand" href="{Html.Encode(url)}">{Html.Encode(title)}</a>
                <div class="docs-header-actions">
                {(enableThemeSwitching ? SiteGenerator.BuildThemeToggle() : string.Empty)}
                <button class="docs-menu-toggle" type="button" aria-expanded="false" aria-controls="docs-sidebar" data-docs-menu-toggle>Menu</button>
                </div>
              </header>
            """;
}

/// <summary>SEO metadata.</summary>
public sealed record SeoComponentProps(string FullTitle, string Description, SiteUrl CanonicalUrl, string OpenGraphType, SiteUrl SocialImageUrl, string SocialImageAlt, DateTimeOffset? PublishedAt = null);

/// <summary>HTML document head inputs.</summary>
public sealed record HeadComponentProps(SeoComponentProps Seo, SiteUrl StylesheetUrl, IHtmlContent FaviconLinks, IHtmlContent Analytics, SiteUrl? FeedUrl = null, SiteUrl? ScriptUrl = null, bool Docs = false);

/// <summary>Renders SEO metadata.</summary>
public sealed class SeoComponent : ISiteComponent<SeoComponentProps>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The props, required SEO data, or context is null.</exception>
    public IHtmlContent Render(SeoComponentProps props, ComponentRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(props.FullTitle);
        ArgumentNullException.ThrowIfNull(props.Description);
        ArgumentNullException.ThrowIfNull(props.CanonicalUrl);
        ArgumentNullException.ThrowIfNull(props.OpenGraphType);
        ArgumentNullException.ThrowIfNull(props.SocialImageUrl);
        ArgumentNullException.ThrowIfNull(props.SocialImageAlt);
        ArgumentNullException.ThrowIfNull(context);
        return Html.UnsafeRaw(RenderCore(props, context.Site.Title));
    }
    internal static string RenderCore(SeoComponentProps props, string siteTitle) =>
        RenderCore(siteTitle, props.FullTitle, props.Description, props.CanonicalUrl.Value,
            props.OpenGraphType, props.SocialImageUrl.Value, props.SocialImageAlt, props.PublishedAt);

    internal static string RenderCore(string siteTitle, string fullTitle, string description,
        string canonicalUrl, string openGraphType, string? socialImageUrl, string socialImageAlt, DateTimeOffset? publishedAt)
    {
        var published = publishedAt is null ? "  " : $"  <meta property=\"article:published_time\" content=\"{publishedAt.Value:O}\">";
        return $"  <title>{Html.Encode(fullTitle)}</title>\n"
            + $"  <meta name=\"description\" content=\"{Html.Encode(description)}\">\n"
            + $"  <link rel=\"canonical\" href=\"{Html.Encode(canonicalUrl)}\">\n"
            + $"  <meta property=\"og:site_name\" content=\"{Html.Encode(siteTitle)}\">\n"
            + $"  <meta property=\"og:type\" content=\"{Html.Encode(openGraphType)}\">\n"
            + $"  <meta property=\"og:title\" content=\"{Html.Encode(fullTitle)}\">\n"
            + $"  <meta property=\"og:description\" content=\"{Html.Encode(description)}\">\n"
            + $"  <meta property=\"og:url\" content=\"{Html.Encode(canonicalUrl)}\">\n"
            + (socialImageUrl is null ? string.Empty : $"  <meta property=\"og:image\" content=\"{Html.Encode(socialImageUrl)}\">\n"
                + $"  <meta property=\"og:image:alt\" content=\"{Html.Encode(socialImageAlt)}\">\n")
            + $"  <meta name=\"twitter:card\" content=\"{(socialImageUrl is null ? "summary" : "summary_large_image")}\">\n"
            + $"  <meta name=\"twitter:title\" content=\"{Html.Encode(fullTitle)}\">\n"
            + $"  <meta name=\"twitter:description\" content=\"{Html.Encode(description)}\">\n"
            + (socialImageUrl is null ? string.Empty : $"  <meta name=\"twitter:image\" content=\"{Html.Encode(socialImageUrl)}\">\n"
                + $"  <meta name=\"twitter:image:alt\" content=\"{Html.Encode(socialImageAlt)}\">\n")
            + published + "\n";
    }
}

/// <summary>Renders the document head.</summary>
public sealed class HeadComponent : ISiteComponent<HeadComponentProps>
{
    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">The props, required head or SEO data, or context is null.</exception>
    public IHtmlContent Render(HeadComponentProps props, ComponentRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(props.Seo);
        ArgumentNullException.ThrowIfNull(props.Seo.FullTitle);
        ArgumentNullException.ThrowIfNull(props.Seo.Description);
        ArgumentNullException.ThrowIfNull(props.Seo.CanonicalUrl);
        ArgumentNullException.ThrowIfNull(props.Seo.OpenGraphType);
        ArgumentNullException.ThrowIfNull(props.Seo.SocialImageUrl);
        ArgumentNullException.ThrowIfNull(props.Seo.SocialImageAlt);
        ArgumentNullException.ThrowIfNull(props.StylesheetUrl);
        ArgumentNullException.ThrowIfNull(props.FaviconLinks);
        ArgumentNullException.ThrowIfNull(props.Analytics);
        ArgumentNullException.ThrowIfNull(context);
        return Html.UnsafeRaw(RenderCore(context.Site.Title, context.Theme.ThemeColor,
            SeoComponent.RenderCore(props.Seo, context.Site.Title), props.StylesheetUrl.Value,
            props.FaviconLinks.ToHtmlString(), props.Analytics.ToHtmlString(),
            props.FeedUrl?.Value, props.ScriptUrl?.Value, props.Docs, context.Theme.EnableThemeSwitching));
    }

    internal static string RenderCore(string siteTitle, string themeColor, string seo,
        string stylesheetUrl, string faviconLinks, string analytics, string? feedUrl, string? scriptUrl, bool docs, bool enableThemeSwitching)
    {
        var feed = feedUrl is null ? string.Empty : $"\n  <link rel=\"alternate\" type=\"application/rss+xml\" title=\"{Html.Encode(siteTitle)}\" href=\"{Html.Encode(feedUrl)}\">";
        var script = scriptUrl is null ? string.Empty : $"<script src=\"{Html.Encode(scriptUrl)}\" defer></script>";
        var ending = docs
            ? (feedUrl is null ? string.Empty : "  " + feed + "\n") + "  " + script + "\n"
            : "  " + feed + "\n  " + (scriptUrl is null ? string.Empty : "\n  " + script) + "\n";
        return "<head>\n"
            + "  <meta charset=\"utf-8\">\n"
            + "  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
            + $"  <meta name=\"color-scheme\" content=\"{(enableThemeSwitching ? "light dark" : "light")}\">\n"
            + (enableThemeSwitching && scriptUrl is not null ? "  <script>try{const t=localStorage.getItem('lithosharp-theme');if(t==='light'||t==='dark')document.documentElement.dataset.siteTheme=t;}catch{}</script>\n" : string.Empty)
            + $"  <meta name=\"theme-color\" content=\"{Html.Encode(themeColor)}\">\n"
            + seo
            + "  <link rel=\"stylesheet\" href=\"" + Html.Encode(stylesheetUrl) + "\">\n"
            + "  " + faviconLinks + "\n"
            + "  " + analytics + "\n"
            + ending
            + "</head>";
    }
}
