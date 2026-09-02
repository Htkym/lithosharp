using LithoSharp.Routing;

namespace LithoSharp.Pages;

/// <summary>
/// 安定した識別子、サイト内ルート、型付きコンテンツ、およびメタデータを持つページを表します。
/// </summary>
/// <typeparam name="TContent"><see langword="null"/> ではないページのソースコンテンツの型。</typeparam>
public sealed class SitePage<TContent>
    where TContent : notnull
{
    /// <summary>ページを作成します。</summary>
    /// <param name="id">安定したページ識別子。</param>
    /// <param name="route">ページのサイト内ルート。</param>
    /// <param name="content">型付きのソースコンテンツ。</param>
    /// <param name="metadata">ページのメタデータ。</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/>、<paramref name="route"/>、<paramref name="content"/>、
    /// または <paramref name="metadata"/> が <see langword="null"/> です。
    /// </exception>
    public SitePage(PageId id, SiteRoute route, TContent content, PageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        Id = id;
        Route = route;
        Content = content;
        Metadata = metadata;
    }

    /// <summary>安定したページ識別子を取得します。</summary>
    public PageId Id { get; }

    /// <summary>ページのサイト内ルートを取得します。</summary>
    public SiteRoute Route { get; }

    /// <summary>型付きのソースコンテンツを取得します。</summary>
    public TContent Content { get; }

    /// <summary>ページのメタデータを取得します。</summary>
    public PageMetadata Metadata { get; }
}
