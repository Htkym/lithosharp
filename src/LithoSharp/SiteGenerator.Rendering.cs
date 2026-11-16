using LithoSharp.Content;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    internal static RenderContext ForPageLanguage(RenderContext configuration, string? locale) =>
        SiteText.IsJapanese(locale) && ReferenceEquals(configuration.Text, SiteText.English)
            ? configuration with { Text = SiteText.Japanese } : configuration;

    internal static string BuildThemeToggle(string? locale = null) => $"""
        <button class="site-theme-toggle" type="button" aria-label="{SiteText.Label(locale, "Dark mode")}" aria-pressed="false" data-site-theme-toggle hidden>
          <svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.5"><circle cx="12" cy="12" r="8"/><path d="M12 4a8 8 0 0 1 0 16Z" fill="currentColor" stroke="none"/></svg>
        </button>
        """;

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
        Pages.PageMetadata? metadata = null, IHtmlContent? head = null, bool includeDefaultScript = true, ImageMetadata? socialImageMetadata = null)
    {
        var socialImageAlt = SiteText.IsJapanese(metadata?.Language) ? configuration.Site.Title + "のプレビュー画像"
            : configuration.Site.Title + " social preview";
        var seo = SeoComponent.RenderCore(configuration.Site.Title, fullTitle, description,
            canonicalUrl, openGraphType, socialImageUrl, socialImageAlt, publishedAt);
        if (socialImageUrl is not null && socialImageMetadata is not null)
        {
            seo += $"  <meta property=\"og:image:type\" content=\"{Html.Encode(socialImageMetadata.MediaType)}\">\n"
                + $"  <meta property=\"og:image:width\" content=\"{socialImageMetadata.Width.ToString(System.Globalization.CultureInfo.InvariantCulture)}\">\n"
                + $"  <meta property=\"og:image:height\" content=\"{socialImageMetadata.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)}\">\n";
        }
        if (metadata?.NoIndex == true)
            seo += "  <meta name=\"robots\" content=\"noindex\">\n";
        if (metadata is not null)
            seo += string.Concat(metadata.Alternates.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
                $"  <link rel=\"alternate\" hreflang=\"{Html.Encode(pair.Key)}\" href=\"{pair.Value.ToAttributeValue()}\">\n"));
        return HeadComponent.RenderCore(configuration.Site.Title, configuration.Theme.ThemeColor, seo + head?.ToHtmlString(),
            configuration.Routes.PublicPath(configuration.Routes.SiteCss), BuildFaviconLinks(configuration),
            BuildGoogleAnalyticsSnippet(configuration),
            !docs && includeBlogNavigation ? configuration.Routes.PublicPath(configuration.Routes.Feed) : null,
            includeDefaultScript && (docs || includeBlogNavigation) ? configuration.Routes.PublicPath(configuration.Routes.SiteScript) : null,
            docs, configuration.Theme.EnableThemeSwitching,
            HasExternalThemeRestoration(configuration) ? configuration.Routes.PublicPath(configuration.Routes.ThemeRestorationScript) : null);
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

    internal static string BuildSiteHeader(RenderContext configuration, bool includeNavigation = true, string? locale = null)
    {
        var homePath = configuration.Routes.PublicPath(configuration.Routes.Home);
        if (!includeNavigation)
        {
            return BlogHeaderComponent.RenderCore(configuration.Site.Title, homePath,
                string.Empty, string.Empty, false, configuration.Text, configuration.Theme.EnableThemeSwitching, locale);
        }
        var archivesPath = configuration.Routes.PublicPath(configuration.Routes.Archives);
        var tagsPath = configuration.Routes.PublicPath(configuration.Routes.Tags);
        var searchPath = configuration.Routes.PublicPath(configuration.Routes.SearchPage);
        var feedPath = configuration.Routes.PublicPath(configuration.Routes.Feed);
        var navLinks = new List<(string Label, string Url, string? CssClass, bool IsCurrent)>
        {
            (SiteText.Label(locale, "Home"), homePath, "site-nav-home", false),
            (SiteText.Label(locale, "Archives"), archivesPath, null, false),
            (SiteText.Label(locale, "Tags"), tagsPath, null, false),
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

        navLinks.Add((SiteText.Label(locale, "Search"), searchPath, null, false));
        navLinks.Add(("RSS", feedPath, "rss-nav-link", false));
        var navHtml = NavigationComponent.RenderCore(navLinks);
        return BlogHeaderComponent.RenderCore(
            configuration.Site.Title,
            homePath,
            navHtml,
            SearchFormComponent.RenderCore(configuration, searchPath),
            includeNavigation,
            configuration.Text, configuration.Theme.EnableThemeSwitching, locale);
    }

}
