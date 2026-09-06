using LithoSharp.Configuration;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Publishing;
using LithoSharp.Routing;

namespace LithoSharp.Content;

/// <summary>型付きコンテンツエントリを生成ファイルへ描画する処理を表します。</summary>
/// <typeparam name="TFrontMatter">フロントマターの型。</typeparam>
/// <typeparam name="TBody">本文の型。</typeparam>
/// <param name="entry">描画するエントリ。</param>
/// <param name="context">ルート、公開情報、レイアウト、および描画ヘルパーを持つコンテキスト。</param>
/// <returns>出力ファイルへ書き込む文字列。</returns>
public delegate string ContentPageRenderer<TFrontMatter, TBody>(
    ContentEntry<TFrontMatter, TBody> entry,
    ContentPageRenderingContext context)
    where TFrontMatter : notnull
    where TBody : notnull;

/// <summary>型付きコンテンツページの描画時に使用する不変のコンテキストです。</summary>
public sealed class ContentPageRenderingContext
{
    private readonly SiteGenerator generator;
    private readonly SiteGenerator.RenderContext configuration;
    private readonly string? socialImageRelativePath;

    internal string? DerivedContent { get; private set; }

    internal ContentPageRenderingContext(
        SiteGenerator generator,
        SiteGenerator.RenderContext configuration,
        SiteRoute route,
        PageMetadata metadata,
        ContentLayoutId? layoutId,
        string? socialImageRelativePath,
        string environmentName,
        AssetRegistry assets)
    {
        this.generator = generator;
        this.configuration = configuration;
        this.socialImageRelativePath = socialImageRelativePath;
        Route = route;
        Metadata = metadata;
        LayoutId = layoutId;
        EnvironmentName = environmentName;
        Assets = assets;
    }

    /// <summary>サイト設定を取得します。</summary>
    public SiteSettings Site => configuration.Site;

    /// <summary>正規化済みの公開ルートを取得します。</summary>
    public SiteRoute Route { get; }

    /// <summary>公開判定済みのページメタデータを取得します。</summary>
    public PageMetadata Metadata { get; }

    /// <summary>コレクションのレイアウト識別子を取得します。未指定の場合は <see langword="null"/> です。</summary>
    public ContentLayoutId? LayoutId { get; }

    /// <summary>UTC に正規化されたビルド時刻を取得します。</summary>
    public DateTimeOffset BuildTimestamp => configuration.BuildTimestamp;

    /// <summary>公開判定に使用した環境名を取得します。</summary>
    public string EnvironmentName { get; }

    /// <summary>このビルドで登録された資産を取得します。</summary>
    public AssetRegistry Assets { get; }

    /// <summary>LithoSharp の安全な設定で Markdown を HTML に変換します。</summary>
    /// <param name="markdown">変換する Markdown。</param>
    /// <returns>変換後の HTML。</returns>
    public string RenderMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return generator.RenderMarkdown(markdown);
    }

    /// <summary>標準のメタデータ、ヘッダー、フッターを持つ HTML 文書を描画します。</summary>
    /// <param name="bodyHtml"><c>main</c> 要素へ配置する HTML。</param>
    /// <returns>完全な HTML 文書。</returns>
    public string RenderDocument(string bodyHtml)
    {
        ArgumentNullException.ThrowIfNull(bodyHtml);
        DerivedContent = bodyHtml;
        return generator.RenderTemplateDocument(
            configuration,
            new SiteTemplateDocument
            {
                Title = string.IsNullOrWhiteSpace(Metadata.Title)
                    ? Route.PublicPath
                    : Metadata.Title,
                RelativePath = Route.RelativeOutputPath,
                BodyHtml = bodyHtml,
                Description = Metadata.Description,
                OpenGraphType = "article",
                PublishedAt = Metadata.PublishFrom,
                SocialImageRelativePath = socialImageRelativePath,
            });
    }
}

/// <summary>生成へ接続する型付きコンテンツコレクションの基底契約です。</summary>
public abstract class SiteContentCollection
{
    private protected SiteContentCollection()
    {
    }

    /// <summary>コレクションの安定した識別子を取得します。</summary>
    public abstract ContentCollectionId Id { get; }

    internal abstract IReadOnlyList<IntegratedContentPage> CreatePages(
        string baseUrl,
        DateTimeOffset buildTimestamp,
        string environmentName,
        SiteRouteTable routeTable,
        ICollection<string> unpublishedPages,
        CancellationToken cancellationToken);
}

/// <summary>型付きコンテンツコレクションとページ描画処理を生成へ登録します。</summary>
/// <typeparam name="TFrontMatter">フロントマターの型。</typeparam>
/// <typeparam name="TBody">本文の型。</typeparam>
public sealed class SiteContentCollection<TFrontMatter, TBody> : SiteContentCollection
    where TFrontMatter : notnull
    where TBody : notnull
{
    /// <summary>生成へ接続するコレクションを作成します。</summary>
    /// <param name="collection">ルート、公開情報、レイアウト、依存関係を持つコレクション。</param>
    /// <param name="renderer">公開対象の各エントリを描画する処理。</param>
    public SiteContentCollection(
        ContentCollection<TFrontMatter, TBody> collection,
        ContentPageRenderer<TFrontMatter, TBody> renderer)
    {
        Collection = collection ?? throw new ArgumentNullException(nameof(collection));
        Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    /// <summary>登録した型付きコンテンツコレクションを取得します。</summary>
    public ContentCollection<TFrontMatter, TBody> Collection { get; }

    /// <summary>登録したページ描画処理を取得します。</summary>
    public ContentPageRenderer<TFrontMatter, TBody> Renderer { get; }

    /// <inheritdoc />
    public override ContentCollectionId Id => Collection.Id;

    internal override IReadOnlyList<IntegratedContentPage> CreatePages(
        string baseUrl,
        DateTimeOffset buildTimestamp,
        string environmentName,
        SiteRouteTable routeTable,
        ICollection<string> unpublishedPages,
        CancellationToken cancellationToken)
    {
        var pages = new List<IntegratedContentPage>(Collection.Entries.Count);
        foreach (var entry in Collection.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = Collection.PublicationMapper(entry)
                ?? throw new InvalidOperationException(
                    $"Content collection '{Collection.Id}' returned null publication metadata for entry '{entry.Id}'.");
            if (!PagePublicationPolicy.ShouldPublish(metadata, buildTimestamp, environmentName))
            {
                unpublishedPages.Add(ContentPageIdentity.Create(Collection.Id, entry.Id));
                continue;
            }

            var ownerId = ContentPageIdentity.Create(Collection.Id, entry.Id);
            try
            {
                var route = Collection.RouteConvention(entry)
                    ?? throw new InvalidOperationException(
                        $"Content collection '{Collection.Id}' returned no route for entry '{entry.Id}'.");
                route = route.WithBaseUrl(baseUrl);
                var page = new IntegratedContentPage(
                    Collection.Id,
                    entry.Id,
                    Collection.InputRoot,
                    entry.SourcePath,
                    entry.SourceFingerprint,
                    entry.SourceLocation,
                    metadata,
                    route,
                    Collection.LayoutId,
                    Collection.TransformationId,
                    null,
                    Collection.IsCacheable,
                    Collection.DeclaredDependencies,
                    [
                        new IntegratedContentSource(
                            Collection.Id,
                            entry.Id,
                            entry.SourcePath,
                            entry.SourceFingerprint,
                            entry.SourceLocation),
                    ],
                    ownerId,
                    new PageId(ownerId),
                    GeneratedPageDerivedSurfaces.All,
                    context => Renderer(entry, context));
                pages.Add(page);
                routeTable.Register(route, ownerId, entry.SourceLocation);
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException)
            {
                routeTable.RegisterInvalidRoute(
                    entry.SourcePath,
                    ownerId,
                    exception,
                    entry.SourceLocation);
            }
        }

        return pages;
    }
}

internal sealed class IntegratedContentPage(
    ContentCollectionId collectionId,
    ContentEntryId entryId,
    string inputRoot,
    string sourcePath,
    string sourceFingerprint,
    SiteSourceLocation? sourceLocation,
    PageMetadata metadata,
    SiteRoute route,
    ContentLayoutId? layoutId,
    ContentTransformationId? transformationId,
    ContentTransformationId? sourceTransformationId,
    bool isCacheable,
    IReadOnlyList<ContentDependency> declaredDependencies,
    IReadOnlyList<IntegratedContentSource> sources,
    string ownerId,
    PageId pageId,
    GeneratedPageDerivedSurfaces derivedSurfaces,
    Func<ContentPageRenderingContext, string> renderer)
{
    public ContentCollectionId CollectionId { get; } = collectionId;

    public ContentEntryId EntryId { get; } = entryId;

    public string InputRoot { get; } = inputRoot;

    public string SourcePath { get; } = sourcePath;

    public string SourceFingerprint { get; } = sourceFingerprint;

    public SiteSourceLocation? SourceLocation { get; } = sourceLocation;

    public PageMetadata Metadata { get; } = metadata;

    public SiteRoute Route { get; } = route;

    public ContentLayoutId? LayoutId { get; } = layoutId;

    public ContentTransformationId? TransformationId { get; } = transformationId;

    public ContentTransformationId? SourceTransformationId { get; } = sourceTransformationId;

    public bool IsCacheable { get; } = isCacheable;

    public IReadOnlyList<ContentDependency> DeclaredDependencies { get; } = declaredDependencies;

    public IReadOnlyList<IntegratedContentSource> Sources { get; } = sources;

    public string OwnerId { get; } = ownerId;

    public string? Content { get; private set; }

    public string? DerivedContent { get; private set; }

    public PageId PageId { get; } = pageId;

    public GeneratedPageDerivedSurfaces DerivedSurfaces { get; } = derivedSurfaces;

    public bool IsIncludedIn(GeneratedPageDerivedSurfaces surface) =>
        (DerivedSurfaces & surface) == surface;

    public RenderedPage Render(
        SiteGenerator generator,
        SiteGenerator.RenderContext configuration,
        string environmentName,
        AssetRegistry assets,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = new ContentPageRenderingContext(
            generator,
            configuration,
            Route,
            Metadata,
            LayoutId,
            IsIncludedIn(GeneratedPageDerivedSurfaces.SocialImage)
                ? configuration.Routes.ContentSocialImage(Route).RelativeOutputPath
                : null,
            environmentName,
            assets);
        Content = renderer(context)
            ?? throw new InvalidOperationException(
                $"Content renderer for collection '{CollectionId}' and entry '{EntryId}' returned null.");
        DerivedContent = context.DerivedContent ?? Content;
        return new RenderedPage(PageId, Route, Content, Metadata);
    }
}

internal sealed record IntegratedContentSource(
    ContentCollectionId CollectionId,
    ContentEntryId EntryId,
    string SourcePath,
    string SourceFingerprint,
    SiteSourceLocation? SourceLocation);

internal static class ContentPageIdentity
{
    public static string Create(ContentCollectionId collectionId, ContentEntryId entryId) =>
        Create(collectionId, entryId.Value);

    public static string Create(ContentCollectionId collectionId, string pageId) =>
        $"page:collection:{collectionId.Value.Length}:{collectionId.Value}:{pageId.Length}:{pageId}";
}
