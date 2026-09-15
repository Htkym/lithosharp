using LithoSharp.Routing;

namespace LithoSharp.Documentation;

/// <summary>カタログ上の文書の公開状態を表します。</summary>
/// <remarks>下書きは公開判定の前に除外されるためカタログに入りません。未解決のキーやrouteは Try 系の false で表します。</remarks>
public enum DocumentPublicationState
{
    /// <summary>公開され、navigationに載ります。</summary>
    Published = 0,
    /// <summary>直接のrouteでは公開されますが、navigationと検索からは外れます。</summary>
    Unlisted = 1,
}

/// <summary>公開文書とそのnavigation相互参照を表します。</summary>
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

    /// <summary>collection・version・localeを含む安定した文書識別子を取得します。</summary>
    public DocumentKey Key { get; }

    /// <summary>variant入力内の相対source pathを取得します。</summary>
    public string SourcePath { get; }

    /// <summary>公開routeを取得します。</summary>
    public SiteRoute Route { get; }

    /// <summary>表示ラベルを取得します。</summary>
    public string Label { get; }

    /// <summary>公開状態を取得します。</summary>
    public DocumentPublicationState Publication { get; }

    /// <summary>この文書を含むsidebar名を取得します。unlistedは空です。</summary>
    public IReadOnlyList<string> Sidebars { get; }
}

/// <summary>version・localeの切替先を表します。欠落も情報として持ちます。</summary>
public sealed class DocumentVariantLink
{
    internal DocumentVariantLink(string version, string locale, bool exists, SiteRoute? route)
    {
        Version = version;
        Locale = locale;
        Exists = exists;
        Route = route;
    }

    /// <summary>切替先のversionを取得します。</summary>
    public string Version { get; }

    /// <summary>切替先のlocaleを取得します。</summary>
    public string Locale { get; }

    /// <summary>切替先の文書が存在するかどうかを取得します。</summary>
    public bool Exists { get; }

    /// <summary>切替先の公開routeを取得します。欠落時は <see langword="null"/> です。</summary>
    public SiteRoute? Route { get; }
}

/// <summary>1つのcollection variantについて解決済みnavigationの相互参照を表します。</summary>
/// <remarks>
/// <see cref="DocumentCatalog"/> を再利用します。routeの一意性と衝突の検証はカタログ構築時の
/// <c>SiteRouteTable</c> 検証に委ね、このsnapshotは別のroute模型を持ちません。
/// sidebarsを読み直さなくても階層と前後移動を再現できます。未知のrouteやキーは例外ではなく
/// 空の結果で表します。衝突や未解決のsidebar参照は構築時に例外として明示します。
/// </remarks>
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

    /// <summary>対象のcollectionを取得します。</summary>
    public string Collection { get; }

    /// <summary>対象のversionを取得します。</summary>
    public string Version { get; }

    /// <summary>対象のlocaleを取得します。</summary>
    public string Locale { get; }

    /// <summary>sidebar名ごとの解決済み階層を取得します。category階層を含みます。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DocumentNavigationItem>> Sidebars { get; }

    /// <summary>対象variantの公開文書を取得します。unlistedを含みます。</summary>
    public IReadOnlyList<DocumentNavigationEntry> Documents { get; }

    /// <summary>カタログとsidebar定義からsnapshotを作ります。</summary>
    /// <param name="catalog">公開文書のカタログ。</param>
    /// <param name="collection">対象のcollection。</param>
    /// <param name="version">対象のversion。</param>
    /// <param name="locale">対象のlocale。</param>
    /// <param name="sidebars">sidebar名ごとの定義。ない場合は自動navigationの <c>default</c> を使います。</param>
    /// <param name="categories">自動sidebar用のcategory情報。ない場合は <see langword="null"/> です。</param>
    /// <param name="defaultSidebar">既定の正準sidebar。ない場合は最初のsidebarを使います。</param>
    /// <returns>相互参照を持つ不変snapshot。</returns>
    /// <exception cref="ArgumentException">sidebar定義が未解決の文書を参照するか、未知の既定sidebarです。</exception>
    /// <remarks>未解決参照の例外は <see cref="DocumentCatalog.Sidebar"/> と同じもので、黙って欠落させません。</remarks>
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

    /// <summary>識別子で文書を探します。</summary>
    /// <param name="key">collection・version・localeを含む文書識別子。</param>
    /// <param name="document">見つかった文書。ない場合は <see langword="null"/> です。</param>
    /// <returns>対象variantに存在する場合に <see langword="true"/> を返します。</returns>
    public bool TryGet(DocumentKey key, out DocumentNavigationEntry? document) =>
        entries.TryGetValue(key, out document);

    /// <summary>source pathから文書を探します。</summary>
    /// <param name="sourcePath">variant入力内の相対source path。</param>
    /// <param name="document">見つかった文書。ない場合は <see langword="null"/> です。</param>
    /// <returns>対象variantに存在する場合に <see langword="true"/> を返します。</returns>
    public bool TryGetBySourcePath(string sourcePath, out DocumentNavigationEntry? document) =>
        bySource.TryGetValue(sourcePath, out document);

    /// <summary>公開pathから文書を探します。</summary>
    /// <param name="publicPath">公開URLのpath部分。</param>
    /// <param name="document">見つかった文書。ない場合は <see langword="null"/> です。</param>
    /// <returns>対象variantに存在する場合に <see langword="true"/> を返します。</returns>
    /// <remarks>未知routeは例外ではなく false で表します。</remarks>
    public bool TryGetByPublicPath(string publicPath, out DocumentNavigationEntry? document) =>
        byPublicPath.TryGetValue(publicPath, out document);

    /// <summary>出力相対pathから文書を探します。</summary>
    /// <param name="relativeOutputPath">出力ルートからの相対ファイルパス。</param>
    /// <param name="document">見つかった文書。ない場合は <see langword="null"/> です。</param>
    /// <returns>対象variantに存在する場合に <see langword="true"/> を返します。</returns>
    public bool TryGetByOutputPath(string relativeOutputPath, out DocumentNavigationEntry? document) =>
        byOutputPath.TryGetValue(relativeOutputPath, out document);

    /// <summary>文書のbreadcrumb階層を取得します。</summary>
    /// <param name="key">対象の文書識別子。</param>
    /// <param name="sidebarName">使うsidebar名。ない場合は表示指定・既定・最初の順で選びます。</param>
    /// <returns>根から文書までの階層。未知の文書は空です。</returns>
    public IReadOnlyList<DocumentNavigationItem> Breadcrumbs(DocumentKey key, string? sidebarName = null)
    {
        if (!pages.ContainsKey(key))
        {
            return [];
        }
        return DocumentCatalog.Breadcrumbs(key, SelectSidebar(key, sidebarName));
    }

    /// <summary>同じ正準sidebarでの前後文書を取得します。</summary>
    /// <param name="key">対象の文書識別子。</param>
    /// <param name="sidebarName">使うsidebar名。ない場合は表示指定・既定・最初の順で選びます。</param>
    /// <returns>前後文書。ない側や未知の文書は <see langword="null"/> です。</returns>
    public (DocumentNavigationEntry? Previous, DocumentNavigationEntry? Next) Adjacent(DocumentKey key, string? sidebarName = null)
    {
        if (!pages.ContainsKey(key))
        {
            return (null, null);
        }
        var (previous, next) = catalog.Adjacent(key, SelectSidebar(key, sidebarName));
        return (previous is null ? null : entries[previous.Key], next is null ? null : entries[next.Key]);
    }

    /// <summary>同じ文書IDのversion・locale切替先を取得します。</summary>
    /// <param name="documentId">collection内で共通の文書ID。</param>
    /// <returns>既知のversion・locale次元の切替先。欠落は <c>Exists</c> false で表します。未知IDは空です。</returns>
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
