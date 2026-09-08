using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Publishing;
using LithoSharp.Routing;

namespace LithoSharp.Content;

/// <summary>公開対象のコンテンツエントリから生成ページのグループキーを選択する処理を表します。</summary>
/// <typeparam name="TFrontMatter">フロントマターの型。</typeparam>
/// <typeparam name="TBody">本文の型。</typeparam>
/// <param name="entry">公開条件を満たしたエントリ。</param>
/// <returns>エントリが属するグループキー。キーを返さない場合は空の列挙。</returns>
public delegate IEnumerable<string> ContentPageGroupSelector<TFrontMatter, TBody>(
    ContentEntry<TFrontMatter, TBody> entry)
    where TFrontMatter : notnull
    where TBody : notnull;

/// <summary>正規化済みグループと公開エントリから型付きページを作成する処理を表します。</summary>
/// <typeparam name="TFrontMatter">フロントマターの型。</typeparam>
/// <typeparam name="TBody">本文の型。</typeparam>
/// <typeparam name="TPageContent">生成ページが保持する型付きコンテンツの型。</typeparam>
/// <param name="group">正規化済みキーと決定的に並べられた公開エントリ。</param>
/// <returns>安定した識別子、ルート、メタデータ、型付きコンテンツを持つページ。</returns>
public delegate SitePage<TPageContent> GeneratedPageFactory<TFrontMatter, TBody, TPageContent>(
    ContentPageGroup<TFrontMatter, TBody> group)
    where TFrontMatter : notnull
    where TBody : notnull
    where TPageContent : notnull;

/// <summary>型付き生成ページを出力文字列へ描画する処理を表します。</summary>
/// <typeparam name="TPageContent">生成ページが保持する型付きコンテンツの型。</typeparam>
/// <param name="page">描画する型付き生成ページ。</param>
/// <param name="context">ルート、メタデータ、レイアウト、および描画ヘルパーを持つコンテキスト。</param>
/// <returns>出力ファイルへ書き込む文字列。</returns>
public delegate string GeneratedPageRenderer<TPageContent>(
    SitePage<TPageContent> page,
    ContentPageRenderingContext context)
    where TPageContent : notnull;

/// <summary>生成した集約ページを含める派生出力を指定します。</summary>
[Flags]
public enum GeneratedPageDerivedSurfaces
{
    /// <summary>派生出力へ含めません。</summary>
    None = 0,

    /// <summary>サイト全体のナビゲーションへ含めます。</summary>
    Navigation = 1 << 0,

    /// <summary>検索インデックスへ含めます。</summary>
    Search = 1 << 1,

    /// <summary>RSS フィードへ含めます。</summary>
    Rss = 1 << 2,

    /// <summary>サイトマップへ含めます。</summary>
    Sitemap = 1 << 3,

    /// <summary>ページ別ソーシャル画像を生成します。</summary>
    SocialImage = 1 << 4,

    /// <summary><c>llms.txt</c> へ含めます。</summary>
    LlmsTxt = 1 << 5,

    /// <summary>
    /// 互換性を保つ検索、サイトマップ、ページ別ソーシャル画像、<c>llms.txt</c> を含む既定値です。
    /// RSS とサイト全体のナビゲーションには、集約ページを明示的に追加した場合だけ含めます。
    /// </summary>
    Default = 58,

    /// <summary>対応するすべての派生出力へ含めます。</summary>
    All = 63,
}

/// <summary>同じ正規化済みキーに属する公開コンテンツエントリの不変スナップショットを表します。</summary>
/// <typeparam name="TFrontMatter">フロントマターの型。</typeparam>
/// <typeparam name="TBody">本文の型。</typeparam>
public sealed class ContentPageGroup<TFrontMatter, TBody>
    where TFrontMatter : notnull
    where TBody : notnull
{
    internal ContentPageGroup(
        string key,
        IEnumerable<ContentEntry<TFrontMatter, TBody>> entries)
    {
        Key = key;
        Entries = new ReadOnlyCollection<ContentEntry<TFrontMatter, TBody>>(entries.ToArray());
    }

    /// <summary>Unicode 正規化形式 C で正規化された、大文字と小文字を区別するグループキーを取得します。</summary>
    public string Key { get; }

    /// <summary>ソースコレクションの決定的な順序を保つ、公開済みエントリを取得します。</summary>
    public IReadOnlyList<ContentEntry<TFrontMatter, TBody>> Entries { get; }
}

/// <summary>生成ページの検証で使用する安定した診断識別子を提供します。</summary>
public static class GeneratedPageDiagnosticIds
{
    /// <summary>グループセレクターまたは空グループ宣言が無効なキーを返しました。</summary>
    public const string InvalidGroupKey = "LSG001";

    /// <summary>同じ生成ページ集合でページ識別子が重複しています。</summary>
    public const string DuplicatePageId = "LSG002";
}

internal sealed class GeneratedSiteContentCollection<TFrontMatter, TBody, TPageContent>
    : SiteContentCollection
    where TFrontMatter : notnull
    where TBody : notnull
    where TPageContent : notnull
{
    private readonly ContentCollection<TFrontMatter, TBody> source;
    private readonly ContentPageGroupSelector<TFrontMatter, TBody> groupSelector;
    private readonly GeneratedPageFactory<TFrontMatter, TBody, TPageContent> pageFactory;
    private readonly GeneratedPageRenderer<TPageContent> renderer;
    private readonly string rendererImplementationIdentity;
    private readonly IReadOnlyList<string?> groupKeys;
    private readonly ContentLayoutId? layoutId;
    private readonly IComparer<string> groupOrderingComparer;
    private readonly IReadOnlyList<ContentDependency> declaredDependencies;
    private readonly ContentTransformationId? transformationId;
    private readonly bool isCacheable;
    private readonly GeneratedPageDerivedSurfaces derivedSurfaces;

    public GeneratedSiteContentCollection(
        ContentCollection<TFrontMatter, TBody> source,
        ContentCollectionId id,
        ContentPageGroupSelector<TFrontMatter, TBody> groupSelector,
        GeneratedPageFactory<TFrontMatter, TBody, TPageContent> pageFactory,
        GeneratedPageRenderer<TPageContent> renderer,
        IEnumerable<string>? groupKeys,
        ContentLayoutId? layoutId,
        IComparer<string>? groupOrderingComparer,
        IEnumerable<ContentDependency>? declaredDependencies,
        ContentTransformationId? transformationId,
        bool isCacheable,
        GeneratedPageDerivedSurfaces derivedSurfaces)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(id);
        this.groupSelector = groupSelector ?? throw new ArgumentNullException(nameof(groupSelector));
        this.pageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));
        this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        rendererImplementationIdentity = string.Join("|",
            CaptureRendererIdentity(renderer),
            CaptureRendererIdentity(pageFactory),
            CaptureRendererIdentity(groupSelector));
        if (id.Equals(source.Id))
        {
            throw new ArgumentException(
                "A generated page collection must use an identifier different from its source collection.",
                nameof(id));
        }

        if (isCacheable && transformationId is null)
        {
            throw new ArgumentException(
                "A cacheable generated page collection requires a stable transformation identifier.",
                nameof(transformationId));
        }

        if (isCacheable && !source.IsCacheable)
        {
            throw new ArgumentException(
                "A generated page collection cannot be cacheable when its source collection is not cacheable.",
                nameof(isCacheable));
        }

        if ((derivedSurfaces & ~GeneratedPageDerivedSurfaces.All) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(derivedSurfaces),
                derivedSurfaces,
                "Generated page derived surfaces contain an undefined value.");
        }

        Id = id;
        this.groupKeys = Array.AsReadOnly(groupKeys?.Cast<string?>().ToArray() ?? []);
        this.layoutId = layoutId;
        this.groupOrderingComparer = groupOrderingComparer ?? StringComparer.Ordinal;
        this.declaredDependencies = ContentDependency.Snapshot(
            source.DeclaredDependencies.Concat(declaredDependencies ?? []));
        this.transformationId = transformationId;
        this.isCacheable = isCacheable;
        this.derivedSurfaces = derivedSurfaces;
    }

    public override ContentCollectionId Id { get; }

    internal override IReadOnlyList<IntegratedContentPage> CreatePages(
        string baseUrl,
        DateTimeOffset buildTimestamp,
        string environmentName,
        SiteRouteTable routeTable,
        ICollection<string> unpublishedPages,
        CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, List<ContentEntry<TFrontMatter, TBody>>>(
            StringComparer.Ordinal);
        foreach (var key in groupKeys)
        {
            AddGroupKey(key, sourceLocation: null, entry: null, groups, routeTable);
        }

        foreach (var entry in source.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = source.PublicationMapper(entry)
                ?? throw new InvalidOperationException(
                    $"Content collection '{source.Id}' returned null publication metadata for entry '{entry.Id}'.");
            if (!PagePublicationPolicy.ShouldPublish(metadata, buildTimestamp, environmentName))
            {
                unpublishedPages.Add(ContentPageIdentity.Create(source.Id, entry.Id));
                continue;
            }

            var selectedKeys = groupSelector(entry);
            if (selectedKeys is null)
            {
                AddInvalidKeyDiagnostic(
                    routeTable,
                    value: null,
                    entry.SourceLocation,
                    $"Generated page group selector for collection '{Id}' returned null for entry '{entry.Id}'.");
                continue;
            }

            var entryKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in selectedKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryNormalizeKey(key, out var normalized, out var failure))
                {
                    if (entryKeys.Add(normalized!))
                    {
                        if (!groups.TryGetValue(normalized!, out var entries))
                        {
                            entries = [];
                            groups.Add(normalized!, entries);
                        }

                        entries.Add(entry);
                    }
                }
                else
                {
                    AddInvalidKeyDiagnostic(
                        routeTable,
                        key,
                        entry.SourceLocation,
                        $"Generated page group key for collection '{Id}' and entry '{entry.Id}' is invalid: {failure}");
                }
            }
        }

        var pages = new List<IntegratedContentPage>(groups.Count);
        var pageIds = new Dictionary<PageId, string>();
        foreach (var pair in groups
                     .OrderBy(item => item.Key, new DeterministicStringComparer(groupOrderingComparer)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = new ContentPageGroup<TFrontMatter, TBody>(pair.Key, pair.Value);
            var fallbackOwnerId = ContentPageIdentity.Create(Id, pair.Key);
            var page = pageFactory(group)
                ?? throw new InvalidOperationException(
                    $"Generated page factory for collection '{Id}' returned null for group '{pair.Key}'.");
            SiteRoute route;
            try
            {
                route = page.Route.WithBaseUrl(baseUrl);
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException)
            {
                routeTable.RegisterInvalidRoute(
                    pair.Key,
                    fallbackOwnerId,
                    exception,
                    pair.Value.FirstOrDefault()?.SourceLocation);
                continue;
            }

            if (!PagePublicationPolicy.ShouldPublish(page.Metadata, buildTimestamp, environmentName))
            {
                unpublishedPages.Add(ContentPageIdentity.Create(Id, page.Id.Value));
                continue;
            }

            var ownerId = ContentPageIdentity.Create(Id, page.Id.Value);
            if (pageIds.TryGetValue(page.Id, out var previousKey))
            {
                routeTable.RegisterDiagnostic(new SiteDiagnostic(
                    GeneratedPageDiagnosticIds.DuplicatePageId,
                    SiteDiagnosticSeverity.Error,
                    $"生成ページ集合 '{Id}' のページ識別子 '{page.Id}' はグループ '{previousKey}' と '{pair.Key}' で重複しています。",
                    pair.Value.FirstOrDefault()?.SourceLocation));
            }
            else
            {
                pageIds.Add(page.Id, pair.Key);
            }

            var sources = pair.Value
                .Select(entry => new IntegratedContentSource(
                    source.Id,
                    entry.Id,
                    entry.SourcePath,
                    entry.SourceFingerprint,
                    entry.SourceLocation))
                .ToArray();
            var fingerprint = CreateSourceFingerprint(source.Id, pair.Key, sources);
            var entryId = new ContentEntryId(page.Id.Value);
            pages.Add(new IntegratedContentPage(
                Id,
                entryId,
                source.InputRoot,
                $"generated:{pair.Key}",
                fingerprint,
                pair.Value.FirstOrDefault()?.SourceLocation,
                page.Metadata,
                route,
                layoutId,
                transformationId,
                source.TransformationId,
                isCacheable,
                ContentDependency.Snapshot(declaredDependencies.Concat(pair.Value.SelectMany(entry => entry.DeclaredDependencies))),
                sources,
                ownerId,
                page.Id,
                derivedSurfaces,
                context => renderer(page, context))
            {
                RendererFingerprint = this.RendererFingerprint is null
                    ? null
                    : this.RendererFingerprint + "|" + rendererImplementationIdentity,
                IsThreadSafe = this.IsThreadSafe,
            });
            routeTable.Register(
                route,
                ownerId,
                pair.Value.FirstOrDefault()?.SourceLocation);
        }

        return pages;
    }

    private static void AddGroupKey(
        string? key,
        SiteSourceLocation? sourceLocation,
        ContentEntry<TFrontMatter, TBody>? entry,
        IDictionary<string, List<ContentEntry<TFrontMatter, TBody>>> groups,
        SiteRouteTable routeTable)
    {
        if (TryNormalizeKey(key, out var normalized, out var failure))
        {
            if (!groups.ContainsKey(normalized!))
            {
                groups.Add(normalized!, []);
            }

            if (entry is not null)
            {
                groups[normalized!].Add(entry);
            }

            return;
        }

        AddInvalidKeyDiagnostic(
            routeTable,
            key,
            sourceLocation,
            $"Generated empty group key is invalid: {failure}");
    }

    private static bool TryNormalizeKey(
        string? key,
        out string? normalized,
        out string? failure)
    {
        try
        {
            normalized = ContentIdentity.Normalize(key!, nameof(key));
            failure = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException)
        {
            normalized = null;
            failure = exception.Message;
            return false;
        }
    }

    private static void AddInvalidKeyDiagnostic(
        SiteRouteTable routeTable,
        string? value,
        SiteSourceLocation? location,
        string message) =>
        routeTable.RegisterDiagnostic(new SiteDiagnostic(
            GeneratedPageDiagnosticIds.InvalidGroupKey,
            SiteDiagnosticSeverity.Error,
            $"{message} 値: '{value ?? "<null>"}'。",
            location));

    private static string CreateSourceFingerprint(
        ContentCollectionId sourceCollectionId,
        string groupKey,
        IReadOnlyList<IntegratedContentSource> sources)
    {
        var value = JsonSerializer.Serialize(new
        {
            Collection = sourceCollectionId.Value,
            Group = groupKey,
            Sources = sources.Select(sourceEntry => new
            {
                Collection = sourceEntry.CollectionId.Value,
                Entry = sourceEntry.EntryId.Value,
                sourceEntry.SourcePath,
                sourceEntry.SourceFingerprint,
            }),
        });
        return $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}";
    }

    private sealed class DeterministicStringComparer(IComparer<string> comparer)
        : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            var result = comparer.Compare(x!, y!);
            return result != 0 ? result : StringComparer.Ordinal.Compare(x, y);
        }
    }
}
