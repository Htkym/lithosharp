using LithoSharp.Routing;

namespace LithoSharp.Pages;

public sealed class SitePage<TContent>
    where TContent : notnull
{
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

    public PageId Id { get; }

    public SiteRoute Route { get; }

    public TContent Content { get; }

    public PageMetadata Metadata { get; }
}
