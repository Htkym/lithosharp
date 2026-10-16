using LithoSharp.Routing;

namespace LithoSharp.Pages;

public sealed class RenderedPage
{
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

    public PageId SourceId { get; }

    public SiteRoute Route { get; }

    public string Content { get; }

    public PageMetadata Metadata { get; }
}
