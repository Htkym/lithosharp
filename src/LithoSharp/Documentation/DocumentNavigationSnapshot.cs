using LithoSharp.Routing;

namespace LithoSharp.Documentation;

public enum DocumentPublicationState
{
    Published = 0,
    Unlisted = 1,
}

public sealed class DocumentNavigationEntry
{
    internal DocumentNavigationEntry(
        DocumentKey key,
        string sourcePath,
        SiteRoute route,
        string label,
        DocumentPublicationState publication,
        IReadOnlyList<string> sidebars)
    {
        Key = key;
        SourcePath = sourcePath;
        Route = route;
        Label = label;
        Publication = publication;
        Sidebars = Array.AsReadOnly(sidebars.ToArray());
    }

    public DocumentKey Key { get; }

    public string SourcePath { get; }

    public SiteRoute Route { get; }

    public string Label { get; }

    public DocumentPublicationState Publication { get; }

    public IReadOnlyList<string> Sidebars { get; }
}

public sealed class DocumentVariantLink
{
    internal DocumentVariantLink(string version, string locale, bool exists, SiteRoute? route)
    {
        Version = version;
        Locale = locale;
        Exists = exists;
        Route = route;
    }

    public string Version { get; }

    public string Locale { get; }

    public bool Exists { get; }

    public SiteRoute? Route { get; }
}

/// <summary>Uses catalog route validation. Unknown routes/keys return empty results; collisions and unresolved sidebar references fail construction.</summary>
public sealed class DocumentNavigationSnapshot
{
    private readonly DocumentCatalog catalog;
    private readonly Dictionary<DocumentKey, DocumentPage> pages;
    private readonly Dictionary<DocumentKey, DocumentNavigationEntry> entries;
    private readonly Dictionary<string, DocumentNavigationEntry> bySource;
    private readonly Dictionary<string, DocumentNavigationEntry> byPublicPath;
    private readonly Dictionary<string, DocumentNavigationEntry> byOutputPath;
    private readonly string? defaultSidebar;

    private DocumentNavigationSnapshot(
        DocumentCatalog catalog,
        string collection,
        string version,
        string locale,
        IReadOnlyDictionary<string, IReadOnlyList<DocumentNavigationItem>> sidebars,
        IReadOnlyList<DocumentNavigationEntry> documents,
        string? defaultSidebar)
    {
        this.catalog = catalog;
        Collection = collection;
        Version = version;
        Locale = locale;
        Sidebars = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<DocumentNavigationItem>>(
            sidebars.ToDictionary(pair => pair.Key, pair => SnapshotItems(pair.Value), StringComparer.Ordinal));
        Documents = Array.AsReadOnly(documents.ToArray());
        this.defaultSidebar = defaultSidebar;
        pages = documents.Select(document => catalog.Resolve(document.Key))
            .ToDictionary(page => page.Key);
        entries = documents.ToDictionary(document => document.Key);
        bySource = new Dictionary<string, DocumentNavigationEntry>(StringComparer.Ordinal);
        byPublicPath = new Dictionary<string, DocumentNavigationEntry>(StringComparer.Ordinal);
        byOutputPath = new Dictionary<string, DocumentNavigationEntry>(StringComparer.Ordinal);
        foreach (var document in documents)
        {
            bySource.TryAdd(document.SourcePath, document);
            byPublicPath.TryAdd(document.Route.PublicPath, document);
            byOutputPath.TryAdd(document.Route.RelativeOutputPath, document);
        }
    }

    public string Collection { get; }

    public string Version { get; }

    public string Locale { get; }

    public IReadOnlyDictionary<string, IReadOnlyList<DocumentNavigationItem>> Sidebars { get; }

    public IReadOnlyList<DocumentNavigationEntry> Documents { get; }

    /// <summary>The default sidebar is used when supplied, otherwise the first sidebar; absent definitions use automatic navigation.</summary>
    public static DocumentNavigationSnapshot Create(
        DocumentCatalog catalog,
        string collection,
        string version,
        string locale,
        IReadOnlyDictionary<string, IReadOnlyList<SidebarItem>>? sidebars = null,
        IReadOnlyDictionary<string, DocumentCategory>? categories = null,
        string? defaultSidebar = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);

        var scope = catalog.Pages
            .Where(page => page.Key.Collection == collection && page.Key.Version == version && page.Key.Locale == locale)
            .OrderBy(page => page.Key.ToString(), StringComparer.Ordinal).ToArray();
        var resolved = sidebars is null || sidebars.Count == 0
            ? new Dictionary<string, IReadOnlyList<DocumentNavigationItem>>(StringComparer.Ordinal)
                { ["default"] = catalog.Sidebar(collection, version, locale, categories: categories) }
            : sidebars.ToDictionary(pair => pair.Key, pair => catalog.Sidebar(collection, version, locale, pair.Value, categories),
                StringComparer.Ordinal);
        if (defaultSidebar is not null && !resolved.ContainsKey(defaultSidebar))
        {
            throw new ArgumentException($"Unknown sidebar '{defaultSidebar}'.", nameof(defaultSidebar));
        }
        var membership = new Dictionary<DocumentKey, List<string>>();
        foreach (var pair in resolved)
        {
            foreach (var key in Flatten(pair.Value))
            {
                if (!membership.TryGetValue(key, out var names))
                {
                    names = [];
                    membership[key] = names;
                }
                if (!names.Contains(pair.Key))
                {
                    names.Add(pair.Key);
                }
            }
        }
        var documents = scope.Select(page => new DocumentNavigationEntry(
            page.Key,
            page.SourcePath,
            page.Route,
            page.Label,
            page.FrontMatter.Unlisted ? DocumentPublicationState.Unlisted : DocumentPublicationState.Published,
            membership.TryGetValue(page.Key, out var names)
                ? names.OrderBy(name => name, StringComparer.Ordinal).ToArray()
                : [])).ToArray();
        return new DocumentNavigationSnapshot(catalog, collection, version, locale, resolved, documents, defaultSidebar);
    }

    public bool TryGet(DocumentKey key, out DocumentNavigationEntry? document) =>
        entries.TryGetValue(key, out document);

    public bool TryGetBySourcePath(string sourcePath, out DocumentNavigationEntry? document) =>
        bySource.TryGetValue(sourcePath, out document);

    public bool TryGetByPublicPath(string publicPath, out DocumentNavigationEntry? document) =>
        byPublicPath.TryGetValue(publicPath, out document);

    public bool TryGetByOutputPath(string relativeOutputPath, out DocumentNavigationEntry? document) =>
        byOutputPath.TryGetValue(relativeOutputPath, out document);

    /// <summary>Selects the explicit, display, default, then first sidebar. Unknown documents return an empty result.</summary>
    public IReadOnlyList<DocumentNavigationItem> Breadcrumbs(DocumentKey key, string? sidebarName = null)
    {
        if (!pages.ContainsKey(key))
        {
            return [];
        }
        return DocumentCatalog.Breadcrumbs(key, SelectSidebar(key, sidebarName));
    }

    /// <summary>Uses the same canonical sidebar selection; missing neighbors or unknown documents return null.</summary>
    public (DocumentNavigationEntry? Previous, DocumentNavigationEntry? Next) Adjacent(DocumentKey key, string? sidebarName = null)
    {
        if (!pages.ContainsKey(key))
        {
            return (null, null);
        }
        var (previous, next) = catalog.Adjacent(key, SelectSidebar(key, sidebarName));
        return (previous is null ? null : entries[previous.Key], next is null ? null : entries[next.Key]);
    }

    /// <summary>Unknown IDs return an empty result; missing version/locale targets have Exists=false.</summary>
    public IReadOnlyList<DocumentVariantLink> Variants(string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        if (!catalog.Pages.Any(page => page.Key.Collection == Collection && page.Key.Id == documentId))
        {
            return [];
        }
        var dimensions = catalog.Pages.Where(page => page.Key.Collection == Collection)
            .Select(page => (page.Key.Version, page.Key.Locale)).Distinct()
            .OrderBy(dimension => dimension.Version, StringComparer.Ordinal)
            .ThenBy(dimension => dimension.Locale, StringComparer.Ordinal).ToArray();
        var byDimension = catalog.Pages.Where(page => page.Key.Collection == Collection && page.Key.Id == documentId)
            .ToDictionary(page => (page.Key.Version, page.Key.Locale));
        return dimensions.Select(dimension => byDimension.TryGetValue(dimension, out var page)
            ? new DocumentVariantLink(dimension.Version, dimension.Locale, true, page.Route)
            : new DocumentVariantLink(dimension.Version, dimension.Locale, false, null)).ToArray();
    }

    private IReadOnlyList<DocumentNavigationItem> SelectSidebar(DocumentKey key, string? sidebarName)
    {
        if (sidebarName is not null)
        {
            return Sidebars.TryGetValue(sidebarName, out var named)
                ? named : throw new ArgumentException($"Unknown sidebar '{sidebarName}'.", nameof(sidebarName));
        }
        if (pages.TryGetValue(key, out var page) && page.FrontMatter.DisplayedSidebar is { } displayed)
        {
            return Sidebars.TryGetValue(displayed, out var selected)
                ? selected : throw new ArgumentException($"Unknown sidebar '{displayed}'.", nameof(sidebarName));
        }
        if (defaultSidebar is not null)
        {
            return Sidebars[defaultSidebar];
        }
        return Sidebars.Values.First();
    }

    private static IEnumerable<DocumentKey> Flatten(IEnumerable<DocumentNavigationItem> items) =>
        items.SelectMany(item => (item.Document is null ? Enumerable.Empty<DocumentKey>() : [item.Document]).Concat(Flatten(item.Children)));

    private static IReadOnlyList<DocumentNavigationItem> SnapshotItems(IReadOnlyList<DocumentNavigationItem> items) =>
        Array.AsReadOnly(items.Select(item => item with { Children = SnapshotItems(item.Children) }).ToArray());
}
