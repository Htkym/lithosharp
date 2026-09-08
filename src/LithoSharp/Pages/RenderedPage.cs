using LithoSharp.Routing;

namespace LithoSharp.Pages;

/// <summary>
/// ソースページの識別情報と、レンダラーが生成した文字列コンテンツを結び付けます。
/// </summary>
public sealed class RenderedPage
{
    /// <summary>レンダリング済みページを作成します。</summary>
    /// <param name="sourceId">ソースページの安定した識別子。</param>
    /// <param name="route">ソースページのサイト内ルート。</param>
    /// <param name="content">レンダラーが生成した文字列コンテンツ。</param>
    /// <param name="metadata">ソースページのメタデータ。</param>
    public RenderedPage(PageId sourceId, SiteRoute route, string content, PageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        SourceId = sourceId;
        Route = route;
        Content = content;
        Metadata = metadata;
    }

    /// <summary>ソースページの安定した識別子を取得します。</summary>
    public PageId SourceId { get; }

    /// <summary>ソースページのサイト内ルートを取得します。</summary>
    public SiteRoute Route { get; }

    /// <summary>レンダラーが生成した文字列コンテンツを取得します。</summary>
    public string Content { get; }

    /// <summary>ソースページのメタデータを取得します。</summary>
    public PageMetadata Metadata { get; }
}
