using LithoSharp.Routing;

namespace LithoSharp.Pages;

/// <summary>A page reference whose generic type prevents accidental cross-page-type assignment.</summary>
public sealed class PageRef<TPage> where TPage : notnull
{
    /// <summary>Creates a typed reference to a validated route.</summary>
    /// <exception cref="ArgumentNullException">The ID or route is null.</exception>
    public PageRef(PageId id, SiteRoute route)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Route = route ?? throw new ArgumentNullException(nameof(route));
    }

    /// <summary>The stable page ID.</summary>
    public PageId Id { get; }

    /// <summary>The validated public route and output path.</summary>
    public SiteRoute Route { get; }

    /// <summary>Gets the URL, optionally rebased to the site's base URL.</summary>
    /// <exception cref="UriFormatException">The supplied base URL is invalid.</exception>
    public SiteUrl GetUrl(string? baseUrl = null) =>
        SiteUrl.FromRoute(baseUrl is null ? Route : Route.WithBaseUrl(baseUrl));
}
