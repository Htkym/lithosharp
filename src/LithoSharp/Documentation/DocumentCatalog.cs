using System.Globalization;
using System.Text.RegularExpressions;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Documentation;

/// <summary>A stable document identity, independent of its filename and URL.</summary>
public sealed record DocumentKey(string Collection, string Version, string Locale, string Id);

/// <summary>Explicit inputs and public routing for one collection version and language.</summary>
public sealed record DocumentVariant(string Version, string Locale, string InputDirectory, string RoutePrefix)
{
    /// <summary>A display name independent of the version identifier.</summary>
    public string? Label { get; init; }
    /// <summary>An optional banner for archived or development versions.</summary>
    public string? Banner { get; init; }
    /// <summary>Whether the variant should be excluded from search engines.</summary>
    public bool NoIndex { get; init; }
    /// <summary>Whether the language uses right-to-left layout.</summary>
    public bool RightToLeft { get; init; }
    /// <summary>A document ID to use when a switch target is missing. Null reports the missing target.</summary>
    public string? FallbackDocumentId { get; init; }
}

/// <summary>Strict, Docusaurus-compatible document front matter.</summary>
public sealed class DocumentFrontMatter
{
    /// <summary>A stable ID; defaults to the path without numeric prefixes or extension.</summary>
    public string? Id { get; set; }
    /// <summary>The page title.</summary>
    public string Title { get; set; } = "";
    /// <summary>The page description.</summary>
    public string? Description { get; set; }
    /// <summary>An explicit path within the variant.</summary>
    public string? Slug { get; set; }
    /// <summary>A sidebar label independent of the title.</summary>
    public string? SidebarLabel { get; set; }
    /// <summary>An explicit order before filename-derived order.</summary>
    public double? SidebarPosition { get; set; }
    /// <summary>The canonical sidebar membership when a page occurs in several sidebars.</summary>
    public string? DisplayedSidebar { get; set; }
    /// <summary>Whether the document is never published.</summary>
    public bool Draft { get; set; }
    /// <summary>Whether the document is accessible only by direct links.</summary>
    public bool Unlisted { get; set; }
    /// <summary>Excludes this published page from site search without hiding it from navigation.</summary>
    public bool SearchExclude { get; set; }
    /// <summary>The inclusive publication start.</summary>
    public DateTimeOffset? PublishFrom { get; set; }
    /// <summary>The exclusive publication end.</summary>
    public DateTimeOffset? PublishUntil { get; set; }
    /// <summary>Tags used by generated listings and search.</summary>
    public List<string> Tags { get; set; } = [];
    /// <summary>An explicit previous document ID; an empty string hides the link.</summary>
    public string? PaginationPrev { get; set; }
    /// <summary>An explicit next document ID; an empty string hides the link.</summary>
    public string? PaginationNext { get; set; }
    /// <summary>An optional absolute HTTP edit link.</summary>
    public string? CustomEditUrl { get; set; }
    /// <summary>Whether to omit the layout's title.</summary>
    public bool HideTitle { get; set; }
    /// <summary>Whether to omit the layout's table of contents.</summary>
    public bool HideTableOfContents { get; set; }
    /// <summary>Optional explicit update metadata, taking precedence over Git discovery.</summary>
    public DocumentUpdate? LastUpdate { get; set; }
}

/// <summary>The last committed document update or an explicit front-matter override.</summary>
public sealed class DocumentUpdate
{
    /// <summary>The update timestamp.</summary>
    public DateTimeOffset? Date { get; set; }
    /// <summary>The public author display name.</summary>
    public string? Author { get; set; }
}

/// <summary>A published document with resolved identity and route.</summary>
public sealed record DocumentPage(DocumentKey Key, string SourcePath, DocumentFrontMatter FrontMatter, SiteRoute Route)
{
    /// <summary>The filename-derived position used when front matter does not set one.</summary>
    public double Position => FrontMatter.SidebarPosition ?? NumericPosition(SourcePath);
    /// <summary>The displayed sidebar label.</summary>
    public string Label => FrontMatter.SidebarLabel ?? FrontMatter.Title;
    internal static double NumericPosition(string path) => double.TryParse(
        Regex.Match(Path.GetFileName(path), @"^\d+").Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : double.MaxValue;
}

/// <summary>A manual, automatic, category, or external sidebar entry.</summary>
public sealed record SidebarItem(string Type, string? Id = null, string? Label = null)
{
    /// <summary>An external HTTP URL for a link entry.</summary>
    public SiteUrl? Href { get; init; }
    /// <summary>The source subdirectory selected by an autogenerated entry.</summary>
    public string Directory { get; init; } = "";
    /// <summary>Nested category entries.</summary>
    public IReadOnlyList<SidebarItem> Items { get; init; } = [];
    /// <summary>Whether a category starts collapsed.</summary>
    public bool Collapsed { get; init; } = true;
}

/// <summary>Resolved navigation shared by sidebars, breadcrumbs and previous/next links.</summary>
public sealed record DocumentNavigationItem(string Label, SiteUrl? Url, DocumentKey? Document,
    IReadOnlyList<DocumentNavigationItem> Children, bool Collapsed = false);

/// <summary>Metadata for an automatically discovered document category.</summary>
public sealed class DocumentCategory
{
    /// <summary>The category label.</summary>
    public string? Label { get; set; }
    /// <summary>An explicit position before filename ordering.</summary>
    public double? Position { get; set; }
    /// <summary>Whether the category starts collapsed.</summary>
    public bool Collapsed { get; set; } = true;
    /// <summary>A linked document or generated category index.</summary>
    public DocumentCategoryLink? Link { get; set; }
}
/// <summary>A category introduction link.</summary>
public sealed class DocumentCategoryLink
{
    /// <summary>Either doc or generated-index.</summary>
    public string Type { get; set; } = "generated-index";
    /// <summary>A document ID when type is doc.</summary>
    public string? Id { get; set; }
    /// <summary>An optional generated index title.</summary>
    public string? Title { get; set; }
    /// <summary>An optional generated index description.</summary>
    public string? Description { get; set; }
    /// <summary>An optional generated index slug within the variant.</summary>
    public string? Slug { get; set; }
}

/// <summary>Published document routing and variant switching, shared by C# and MDX.</summary>
public sealed class DocumentCatalog
{
    private readonly Dictionary<DocumentKey, DocumentPage> pages;
    /// <summary>Validates document identities and public route ownership.</summary>
    public DocumentCatalog(IEnumerable<DocumentPage> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        pages = documents.ToDictionary(page => page.Key);
        var routes = new SiteRouteTable();
        foreach (var page in pages.Values)
        {
            _ = new ContentCollectionId(page.Key.Collection);
            _ = new ContentEntryId(page.Key.Id);
            _ = CultureInfo.GetCultureInfo(page.Key.Locale);
            routes.Register(page.Route, page.Key.ToString());
        }
        routes.ValidateOrThrow();
    }
    /// <summary>All published pages in stable identity order.</summary>
    public IReadOnlyList<DocumentPage> Pages => pages.Values.OrderBy(page => page.Key.ToString(), StringComparer.Ordinal).ToArray();
    /// <summary>Resolves a document without changing its collection, version or language implicitly.</summary>
    public DocumentPage Resolve(DocumentKey key) => pages.TryGetValue(key, out var page) ? page : throw new KeyNotFoundException($"Missing document: {key}.");
    /// <summary>Switches to the same document or an explicitly declared fallback.</summary>
    public DocumentPage Switch(DocumentKey source, string version, string locale, string? fallbackDocumentId = null)
    {
        var key = source with { Version = version, Locale = locale };
        return pages.TryGetValue(key, out var page) ? page : fallbackDocumentId is null ? Resolve(key) : Resolve(key with { Id = fallbackDocumentId });
    }
    /// <summary>Returns only translations that actually exist in the same version.</summary>
    public IReadOnlyDictionary<string, SiteUrl> Alternates(DocumentKey key) => pages.Values
        .Where(page => page.Key.Collection == key.Collection && page.Key.Version == key.Version && page.Key.Id == key.Id)
        .ToDictionary(page => page.Key.Locale, page => SiteUrl.FromRoute(page.Route), StringComparer.Ordinal);

    /// <summary>Resolves a sidebar once for use by every navigation surface.</summary>
    public IReadOnlyList<DocumentNavigationItem> Sidebar(string collection, string version, string locale, IEnumerable<SidebarItem>? items = null,
        IReadOnlyDictionary<string, DocumentCategory>? categories = null)
    {
        var candidates = pages.Values.Where(page => page.Key.Collection == collection && page.Key.Version == version && page.Key.Locale == locale && !page.FrontMatter.Unlisted && !page.Key.Id.StartsWith("category:", StringComparison.Ordinal)).ToArray();
        DocumentNavigationItem Page(DocumentPage page) => new(page.Label, SiteUrl.FromRoute(page.Route), page.Key, []);
        IReadOnlyList<DocumentNavigationItem> Auto(string directory)
        {
            var prefix = directory.Trim('/');
            if (prefix.Length > 0) prefix += "/";
            var selected = candidates.Where(page => page.SourcePath.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            return selected.GroupBy(page => page.SourcePath[prefix.Length..].Split('/')[0])
                .OrderBy(group => categories?.GetValueOrDefault((prefix + group.Key).Trim('/'))?.Position ?? group.Min(page => page.Position)).ThenBy(group => group.Key, StringComparer.Ordinal)
                .SelectMany(group => group.First().SourcePath[prefix.Length..].Contains('/')
                    ? [Category(prefix + group.Key, group.Key)]
                    : group.OrderBy(page => page.Position).ThenBy(page => page.Key.Id, StringComparer.Ordinal).Select(Page)).ToArray();
        }
        DocumentNavigationItem Category(string path, string name)
        {
            var category = categories?.GetValueOrDefault(path.Trim('/'));
            var id = category?.Link?.Type == "doc" ? category.Link.Id : category?.Link?.Type == "generated-index" ? "category:" + path.Trim('/') : null;
            var target = id is null ? null : Resolve(new(collection, version, locale, id));
            return new(category?.Label ?? RemoveNumericPrefix(name), target is null ? null : SiteUrl.FromRoute(target.Route), target?.Key, Auto(path), category?.Collapsed ?? true);
        }
        IReadOnlyList<DocumentNavigationItem> Expand(IEnumerable<SidebarItem> entries) => entries.SelectMany(item => item.Type switch
        {
            "autogenerated" => Auto(item.Directory),
            "doc" => new[] { Page(candidates.SingleOrDefault(page => page.Key.Id == item.Id) ?? throw new ArgumentException($"Sidebar references missing or unlisted document '{item.Id}'.")) with { Label = item.Label ?? Resolve(new(collection, version, locale, item.Id!)).Label } },
            "link" => new[] { new DocumentNavigationItem(item.Label ?? item.Href?.Value ?? "", item.Href ?? throw new ArgumentException("A link requires href."), null, []) },
            "category" => new[] { new DocumentNavigationItem(item.Label ?? throw new ArgumentException("A category requires a label."), item.Id is null ? null : SiteUrl.FromRoute(Resolve(new(collection, version, locale, item.Id)).Route), item.Id is null ? null : new DocumentKey(collection, version, locale, item.Id), Expand(item.Items), item.Collapsed) },
            _ => throw new ArgumentException($"Unknown sidebar entry type '{item.Type}'.")
        }).ToArray();
        return Expand(items ?? [new("autogenerated")]);
    }

    /// <summary>Finds the canonical path through a resolved sidebar.</summary>
    public static IReadOnlyList<DocumentNavigationItem> Breadcrumbs(DocumentKey key, IReadOnlyList<DocumentNavigationItem> navigation)
    {
        foreach (var item in navigation)
        {
            if (item.Document == key) return [item];
            var nested = Breadcrumbs(key, item.Children);
            if (nested.Count > 0) return [item, .. nested];
        }
        return [];
    }
    /// <summary>Finds adjacent unique pages in the same canonical sidebar, honoring explicit overrides.</summary>
    public (DocumentPage? Previous, DocumentPage? Next) Adjacent(DocumentKey key, IReadOnlyList<DocumentNavigationItem> navigation)
    {
        IEnumerable<DocumentKey> Flatten(IEnumerable<DocumentNavigationItem> items) => items.SelectMany(item =>
            (item.Document is null ? Enumerable.Empty<DocumentKey>() : [item.Document]).Concat(Flatten(item.Children)));
        var order = Flatten(navigation).Distinct().ToList();
        var index = order.IndexOf(key);
        var page = Resolve(key);
        DocumentPage? Find(string? requested, int offset) => requested is not null
            ? requested.Length == 0 ? null : Resolve(key with { Id = requested })
            : index >= 0 && index + offset >= 0 && index + offset < order.Count ? Resolve(order[index + offset]) : null;
        return (Find(page.FrontMatter.PaginationPrev, -1), Find(page.FrontMatter.PaginationNext, 1));
    }
    /// <summary>Removes a numeric filename prefix while preserving the rest of the identifier.</summary>
    public static string RemoveNumericPrefix(string value) => Regex.Replace(value, @"^\d+[-_]", "");
    /// <summary>Derives a stable ID without coupling it to the public route.</summary>
    public static string DefaultId(string sourcePath) => string.Join('/', Path.ChangeExtension(sourcePath, null).Replace('\\', '/').Split('/').Select(RemoveNumericPrefix));
}
