using LithoSharp.Content;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    internal static string RenderHead(
        RenderContext configuration,
        string fullTitle,
        string description,
        string canonicalUrl,
        string openGraphType,
        string? socialImageUrl,
        DateTimeOffset? publishedAt,
        bool docs,
        bool includeBlogNavigation,
        Pages.PageMetadata? metadata = null, IHtmlContent? head = null, bool includeDefaultScript = true)
    {
        var seo = SeoComponent.RenderCore(configuration.Site.Title, fullTitle, description,
            canonicalUrl, openGraphType, socialImageUrl, $"{configuration.Site.Title} social preview", publishedAt);
        if (metadata?.NoIndex == true) seo += "  <meta name=\"robots\" content=\"noindex\">\n";
        if (metadata is not null)
            seo += string.Concat(metadata.Alternates.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                $"  <link rel=\"alternate\" hreflang=\"{Html.Encode(pair.Key)}\" href=\"{pair.Value.ToAttributeValue()}\">\n"));
        return HeadComponent.RenderCore(configuration.Site.Title, configuration.Theme.ThemeColor, seo + head?.ToHtmlString(),
            configuration.Routes.PublicPath(configuration.Routes.SiteCss), BuildFaviconLinks(configuration),
            BuildGoogleAnalyticsSnippet(configuration),
            !docs && includeBlogNavigation ? configuration.Routes.PublicPath(configuration.Routes.Feed) : null,
            includeDefaultScript && (docs || includeBlogNavigation) ? configuration.Routes.PublicPath(configuration.Routes.SiteScript) : null,
            docs);
    }
    internal static string RenderTemplateTableOfContents(
        RenderContext configuration,
        IReadOnlyList<SiteTemplateHeading> headings)
    {
        ArgumentNullException.ThrowIfNull(headings);
        return TableOfContentsComponent.RenderCore(configuration, headings);
    }

    private static string RenderDocsPagination(
        RenderContext configuration,
        IReadOnlyList<MarkdownPost> orderedPosts,
        int currentIndex)
    {
        if (currentIndex < 0)
        {
            return string.Empty;
        }

        var previous = currentIndex > 0 ? orderedPosts[currentIndex - 1] : null;
        var next = currentIndex + 1 < orderedPosts.Count ? orderedPosts[currentIndex + 1] : null;
        if (previous is null && next is null)
        {
            return string.Empty;
        }

        return PreviousNextComponent.RenderCore(
            previous is null ? null : GetDocsLabel(previous),
            previous is null ? null : configuration.Routes.PublicPath(configuration.Routes.Post(previous)),
            next is null ? null : GetDocsLabel(next),
            next is null ? null : configuration.Routes.PublicPath(configuration.Routes.Post(next)));
    }

    private static string DocsLayout(
        RenderContext configuration,
        SiteTemplateNavigationNode root,
        string title,
        string body,
        string relativePath,
        string? currentPagePath,
        string? tableOfContents,
        string? description = null,
        string openGraphType = "website",
        DateTimeOffset? publishedAt = null,
        string? socialImageRelativePath = null,
        bool includeBlogNavigation = true,
        Pages.PageMetadata? metadata = null)
    {
        var socialImageUrl = socialImageRelativePath is not null
            ? configuration.Routes.AbsoluteUrl(configuration.Routes.File(socialImageRelativePath)) : null;
        return DocsPageLayout.RenderCore(configuration, title, body, relativePath, description,
            openGraphType, publishedAt, socialImageUrl, RenderDocsSidebar(configuration, root, currentPagePath), tableOfContents, metadata);
    }

    private static string Layout(
        RenderContext configuration,
        string title,
        string body,
        string relativePath,
        string? description = null,
        string openGraphType = "website",
        DateTimeOffset? publishedAt = null,
        string? socialImageRelativePath = null,
        bool includeBlogNavigation = true,
        Pages.PageMetadata? metadata = null)
    {
        var socialImageUrl = socialImageRelativePath is not null
            ? configuration.Routes.AbsoluteUrl(configuration.Routes.File(socialImageRelativePath)) : null;
        return BlogPageLayout.RenderCore(configuration, title, body, relativePath, description,
            openGraphType, publishedAt, socialImageUrl, includeBlogNavigation, metadata);
    }

    internal static string BuildSiteHeader(RenderContext configuration, bool includeNavigation = true)
    {
        var homePath = configuration.Routes.PublicPath(configuration.Routes.Home);
        if (!includeNavigation)
        {
            return BlogHeaderComponent.RenderCore(configuration.Site.Title, homePath,
                string.Empty, string.Empty, false, configuration.Text);
        }
        var archivesPath = configuration.Routes.PublicPath(configuration.Routes.Archives);
        var tagsPath = configuration.Routes.PublicPath(configuration.Routes.Tags);
        var searchPath = configuration.Routes.PublicPath(configuration.Routes.SearchPage);
        var feedPath = configuration.Routes.PublicPath(configuration.Routes.Feed);
        var navLinks = new List<(string Label, string Url, string? CssClass, bool IsCurrent)>
        {
            ("Home", homePath, "site-nav-home", false),
            ("Archives", archivesPath, null, false),
            ("Tags", tagsPath, null, false),
        };
        foreach (var extraPage in configuration.ExtraPages)
        {
            if (string.IsNullOrEmpty(extraPage.NavLabel))
            {
                continue;
            }

            var extraPath = configuration.Routes.PublicPath(configuration.Routes.ExtraPage(extraPage));
            navLinks.Add((extraPage.NavLabel, extraPath, string.IsNullOrEmpty(extraPage.NavCssClass) ? null : extraPage.NavCssClass, false));
        }

        foreach (var page in configuration.ContentPages
                     .Where(page => page.IsIncludedIn(GeneratedPageDerivedSurfaces.Navigation)
                         && !string.IsNullOrWhiteSpace(page.Metadata.Title)))
        {
            navLinks.Add((page.Metadata.Title!, configuration.Routes.PublicPath(page.Route), null, false));
        }

        navLinks.Add(("Search", searchPath, null, false));
        navLinks.Add(("RSS", feedPath, "rss-nav-link", false));
        var navHtml = NavigationComponent.RenderCore(navLinks);
        return BlogHeaderComponent.RenderCore(
            configuration.Site.Title,
            homePath,
            navHtml,
            SearchFormComponent.RenderCore(configuration, searchPath),
            includeNavigation,
            configuration.Text);
    }

}
