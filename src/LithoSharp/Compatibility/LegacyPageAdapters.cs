using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Publishing;
using LithoSharp.Routing;

namespace LithoSharp.Compatibility;

internal static class LegacyPageAdapters
{
    public static SitePage<MarkdownPost> ToSitePage(MarkdownPost post, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(post);
        return ToSitePage(post, ToPageMetadata(post), baseUrl);
    }

    internal static SitePage<MarkdownPost> ToSitePage(
        MarkdownPost post,
        PageMetadata metadata,
        string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(metadata);
        var route = DocsRouteConvention.ForMarkdownPost(post.RelativeOutputPath, baseUrl);
        return new SitePage<MarkdownPost>(
            CreateMarkdownPageId(post),
            route,
            post,
            metadata);
    }

    internal static PageId CreateMarkdownPageId(MarkdownPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return new PageId($"markdown:{post.RelativeOutputPath.Replace('\\', '/')}");
    }

    internal static PageMetadata ToPageMetadata(MarkdownPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return new PageMetadata(
            post.FrontMatter.Title,
            post.FrontMatter.Summary,
            post.FrontMatter.Draft,
            post.FrontMatter.PublishFrom,
            post.FrontMatter.PublishUntil,
            post.FrontMatter.Environments);
    }

    public static SitePage<SiteExtraPage> ToSitePage(SiteExtraPage page, string? baseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        var route = DocsRouteConvention.ForExtraPage(page.RelativePath, baseUrl);
        return new SitePage<SiteExtraPage>(
            new PageId($"extra:{route.RelativeOutputPath}"),
            route,
            page,
            new PageMetadata(title: page.Title));
    }

    public static IReadOnlyList<SitePage<TContent>> FilterPublished<TContent>(
        IEnumerable<SitePage<TContent>> pages,
        DateTimeOffset buildTimestamp,
        string environmentName)
        where TContent : notnull
    {
        ArgumentNullException.ThrowIfNull(pages);
        return pages
            .Where(page =>
                page is not null
                && PagePublicationPolicy.ShouldPublish(page.Metadata, buildTimestamp, environmentName))
            .ToArray();
    }
}
