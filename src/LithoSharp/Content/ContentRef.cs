using LithoSharp.Routing;

namespace LithoSharp.Content;

/// <summary>A typed reference to a static content entry and its page route.</summary>
public sealed class ContentRef<TEntry> where TEntry : notnull
{
    /// <summary>Creates a reference using stable identifiers and a validated route.</summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public ContentRef(ContentCollectionId collectionId, ContentEntryId id, SiteRoute route)
    {
        CollectionId = collectionId ?? throw new ArgumentNullException(nameof(collectionId));
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Route = route ?? throw new ArgumentNullException(nameof(route));
    }

    /// <summary>The stable collection ID.</summary>
    public ContentCollectionId CollectionId { get; }

    /// <summary>The stable content entry ID.</summary>
    public ContentEntryId Id { get; }

    /// <summary>The entry's validated public route and output path.</summary>
    public SiteRoute Route { get; }

    /// <summary>Gets the URL, optionally rebased to the site's base URL.</summary>
    /// <exception cref="UriFormatException">The supplied base URL is invalid.</exception>
    public SiteUrl GetUrl(string? baseUrl = null) =>
        SiteUrl.FromRoute(baseUrl is null ? Route : Route.WithBaseUrl(baseUrl));
}
