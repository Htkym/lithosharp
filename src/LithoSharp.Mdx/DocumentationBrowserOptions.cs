namespace LithoSharp.Mdx;

/// <summary>The document set searched by the documentation browser.</summary>
public enum DocumentationSearchScope
{
    /// <summary>Searches the current collection, version and language.</summary>
    Collection,
    /// <summary>Searches the site's shared index in the current language, including language-neutral pages.</summary>
    Site
}

/// <summary>Progressive browser enhancements for documentation; no external service is enabled implicitly.</summary>
public sealed record DocumentationBrowserOptions
{
    /// <summary>Uses HTML fetches and the common root lifecycle for internal navigation.</summary>
    public bool Navigation { get; init; } = true;
    /// <summary>Offers light, dark and operating-system color modes.</summary>
    public bool Theme { get; init; } = true;
    /// <summary>An optional dismissible announcement.</summary>
    public string? Announcement { get; init; }
    /// <summary>Additional navbar links.</summary>
    public IReadOnlyList<NavigationLink> Navbar { get; init; } = [];
    /// <summary>Additional footer links.</summary>
    public IReadOnlyList<NavigationLink> Footer { get; init; } = [];
    /// <summary>Prepares an offline cache for the explicitly published documentation and its chunks.</summary>
    public bool Offline { get; init; }
    /// <summary>Adds lazy search using the configured document scope.</summary>
    public bool Search { get; init; } = true;
    /// <summary>The search scope. Site requires the site's shared search index, for example DocsSiteTemplate.EnableSearch.</summary>
    public DocumentationSearchScope SearchScope { get; init; } = DocumentationSearchScope.Collection;
    /// <summary>An optional external search provider. Null uses the local index selected by SearchScope without external network services.</summary>
    public DocumentationAlgolia? Algolia { get; init; }
    /// <summary>Optional explicitly connected analytics. No request is sent until consent is granted.</summary>
    public DocumentationAnalytics? Analytics { get; init; }
}

/// <summary>Explicit public Algolia credentials. Never supply an administrative API key.</summary>
public sealed record DocumentationAlgolia(string ApplicationId, string SearchOnlyApiKey, string IndexName);

/// <summary>An explicit analytics endpoint and provider contract.</summary>
public sealed record DocumentationAnalytics(SiteUrl Endpoint, string Domain)
{
    /// <summary>Either plausible or json. Both require the lithosharp:consent event with granted=true.</summary>
    public string Provider { get; init; } = "plausible";
}
