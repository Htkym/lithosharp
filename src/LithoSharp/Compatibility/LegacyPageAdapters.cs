using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Publishing;
using LithoSharp.Routing;

namespace LithoSharp.Compatibility;

/// <summary>
/// 既存の Markdown ページと追加ページを共通ページモデルへ変換します。
/// </summary>
internal static class LegacyPageAdapters
{
    /// <summary>既存の Markdown 投稿を共通ページへ変換します。</summary>
    /// <param name="post">変換する投稿。</param>
    /// <param name="baseUrl">サイトの絶対 HTTP(S) ベース URL。</param>
    /// <returns>投稿を保持した共通ページ。</returns>
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

    /// <summary>既存の追加ページを共通ページへ変換します。</summary>
    /// <param name="page">変換する追加ページ。</param>
    /// <param name="baseUrl">サイトの絶対 HTTP(S) ベース URL。</param>
    /// <returns>追加ページを保持した共通ページ。</returns>
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

    /// <summary>明示したビルド時刻と環境で公開対象のページだけを返します。</summary>
    /// <typeparam name="TContent">ページコンテンツの型。</typeparam>
    /// <param name="pages">評価するページ。</param>
    /// <param name="buildTimestamp">評価に使用するビルド時刻。</param>
    /// <param name="environmentName">評価する環境名。</param>
    /// <returns>公開条件を満たしたページの配列。</returns>
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
