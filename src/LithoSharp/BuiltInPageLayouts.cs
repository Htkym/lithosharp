using LithoSharp.Pages;

namespace LithoSharp;

/// <summary>Renders a page with the built-in Blog document shell.</summary>
public sealed class BlogPageLayout : IPageLayout<PageLayoutContent>
{
    /// <inheritdoc />
    public IHtmlContent Render(SitePage<PageLayoutContent> page, PageRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(page.Content);
        ArgumentNullException.ThrowIfNull(page.Content.Body);

        return Html.UnsafeRaw(RenderCore(
            context.Configuration,
            page.Metadata.Title ?? page.Route.PublicPath,
            page.Content.Body.ToHtmlString(),
            page.Route.RelativeOutputPath,
            page.Metadata.Description,
            page.Content.OpenGraphType,
            page.Metadata.PublishFrom,
            ResolveSocialImageUrl(context, page.Content.SocialImageUrl),
            page.Content.IncludeBlogNavigation));
    }

    internal static string RenderCore(
        SiteGenerator.RenderContext configuration,
        string title,
        string body,
        string relativePath,
        string? description,
        string openGraphType,
        DateTimeOffset? publishedAt,
        string? socialImageUrl,
        bool includeBlogNavigation)
    {
        var fullTitle = title == configuration.Site.Title ? title : $"{title} - {configuration.Site.Title}";
        var pageDescription = string.IsNullOrWhiteSpace(description) ? configuration.Site.Description : description;
        var canonicalUrl = configuration.Routes.AbsoluteUrl(configuration.Routes.File(relativePath));
        if (configuration.HasSocialImage) socialImageUrl ??= configuration.Routes.AbsoluteUrl(configuration.Routes.DefaultSocialImage);
        var header = SiteGenerator.BuildSiteHeader(configuration, includeBlogNavigation);
        return $"""
            <!doctype html>
            <html lang="{Html.Encode(configuration.Site.Language)}">
            {SiteGenerator.RenderHead(configuration, fullTitle, pageDescription, canonicalUrl, openGraphType, socialImageUrl, publishedAt, docs: false, includeBlogNavigation)}
            <body>
              {header}
              <main>
            {body}
              </main>
              {FooterComponent.RenderCore(configuration.Site.Title, docs: false)}
            </body>
            </html>
            """;
    }

    private static string? ResolveSocialImageUrl(PageRenderingContext context, SiteUrl? socialImageUrl) =>
        socialImageUrl is null
            ? null
            : new Uri(new Uri(context.Site.BaseUrl, UriKind.Absolute), socialImageUrl.Value).ToString();
}

/// <summary>Renders a page with the built-in Docs document shell.</summary>
public sealed class DocsPageLayout : IPageLayout<PageLayoutContent>
{
    /// <inheritdoc />
    public IHtmlContent Render(SitePage<PageLayoutContent> page, PageRenderingContext context)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(page.Content);
        ArgumentNullException.ThrowIfNull(page.Content.Body);

        return Html.UnsafeRaw(RenderCore(
            context.Configuration,
            page.Metadata.Title ?? page.Route.PublicPath,
            page.Content.Body.ToHtmlString(),
            page.Route.RelativeOutputPath,
            page.Metadata.Description,
            page.Content.OpenGraphType,
            page.Metadata.PublishFrom,
            ResolveSocialImageUrl(context, page.Content.SocialImageUrl),
            page.Content.Sidebar?.ToHtmlString(),
            page.Content.TableOfContents?.ToHtmlString()));
    }

    internal static string RenderCore(
        SiteGenerator.RenderContext configuration,
        string title,
        string body,
        string relativePath,
        string? description,
        string openGraphType,
        DateTimeOffset? publishedAt,
        string? socialImageUrl,
        string? sidebar,
        string? tableOfContents)
    {
        var fullTitle = title == configuration.Site.Title ? title : $"{title} - {configuration.Site.Title}";
        var pageDescription = string.IsNullOrWhiteSpace(description) ? configuration.Site.Description : description;
        var canonicalUrl = configuration.Routes.AbsoluteUrl(configuration.Routes.File(relativePath));
        if (configuration.HasSocialImage) socialImageUrl ??= configuration.Routes.AbsoluteUrl(configuration.Routes.DefaultSocialImage);
        return $"""
            <!doctype html>
            <html lang="{Html.Encode(configuration.Site.Language)}">
            {SiteGenerator.RenderHead(configuration, fullTitle, pageDescription, canonicalUrl, openGraphType, socialImageUrl, publishedAt, docs: true, includeBlogNavigation: true)}
            <body class="docs-body">
              {DocsHeaderComponent.RenderCore(configuration.Site.Title, configuration.Routes.PublicPath(configuration.Routes.Home))}
              <div class="docs-shell">
                {sidebar ?? string.Empty}
                <main class="docs-main">
                  <article class="docs-content">
            {body}
                  </article>
                </main>
                {tableOfContents ?? string.Empty}
              </div>
              {FooterComponent.RenderCore(configuration.Site.Title, docs: true)}
            </body>
            </html>
            """;
    }

    private static string? ResolveSocialImageUrl(PageRenderingContext context, SiteUrl? socialImageUrl) =>
        socialImageUrl is null
            ? null
            : new Uri(new Uri(context.Site.BaseUrl, UriKind.Absolute), socialImageUrl.Value).ToString();
}
