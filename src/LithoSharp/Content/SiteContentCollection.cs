using System.Reflection;
using LithoSharp.Configuration;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Documentation;
using LithoSharp.Pages;
using LithoSharp.Publishing;
using LithoSharp.Routing;

namespace LithoSharp.Content;

public delegate string ContentPageRenderer<TFrontMatter, TBody>(
    ContentEntry<TFrontMatter, TBody> entry,
    ContentPageRenderingContext context)
    where TFrontMatter : notnull
    where TBody : notnull;

public sealed class ContentPageRenderingContext
{
    private readonly SiteGenerator generator;
    private readonly SiteGenerator.RenderContext configuration;
    private readonly string? socialImageRelativePath;
    private readonly string? markdownSourcePath;
    private readonly MarkdownBodyAnalysisSnapshot? bodyAnalysis;
    private readonly DocumentSource? bodySource;
    private readonly Dictionary<string, string> renderedMarkdown = new(StringComparer.Ordinal);
    private readonly List<SiteDiagnostic> markdownDiagnostics = [];
    private readonly List<SiteTemplateHeading>? markdownHeadings;

    internal string? DerivedContent { get; private set; }
    internal void SetDerivedContent(string html) => DerivedContent = html;

    internal ContentPageRenderingContext(
        SiteGenerator generator,
        SiteGenerator.RenderContext configuration,
        SiteRoute route,
        PageMetadata metadata,
        ContentLayoutId? layoutId,
        string? socialImageRelativePath,
        string environmentName,
        AssetRegistry assets,
        string? markdownSourcePath,
        MarkdownBodyAnalysisSnapshot? bodyAnalysis,
        DocumentSource? bodySource,
        bool collectDocsHeadings = false)
    {
        this.generator = generator;
        this.configuration = configuration;
        this.socialImageRelativePath = socialImageRelativePath;
        this.markdownSourcePath = markdownSourcePath;
        this.bodyAnalysis = bodyAnalysis;
        this.bodySource = bodySource;
        markdownHeadings = collectDocsHeadings ? [] : null;
        Route = route;
        Metadata = metadata;
        LayoutId = layoutId;
        EnvironmentName = environmentName;
        Assets = assets;
    }

    public SiteSettings Site => configuration.Site;

    public SiteRoute Route { get; }

    public PageMetadata Metadata { get; }

    public ContentLayoutId? LayoutId { get; }

    /// <summary>UTC-normalized build time.</summary>
    public DateTimeOffset BuildTimestamp => configuration.BuildTimestamp;

    public string EnvironmentName { get; }

    internal PageRenderingContext CreateLayoutContext() => new(generator, configuration, this);

    public AssetRegistry Assets { get; }

    public string RenderMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        configuration.Cancellation.ThrowIfCancellationRequested();
        if (renderedMarkdown.TryGetValue(markdown, out var html))
            return html;
        var compiled = generator.RenderContentMarkdown(configuration, Route.RelativeOutputPath, markdown, markdownSourcePath,
            bodyAnalysis, bodySource, markdownDiagnostics);
        html = compiled.Html;
        markdownHeadings?.AddRange(compiled.Semantics?.Headings
            .Where(heading => heading.OutputLevel is 2 or 3
                && !string.IsNullOrWhiteSpace(heading.Id) && !string.IsNullOrWhiteSpace(heading.Text))
            .Select(heading => new SiteTemplateHeading(heading.OutputLevel, heading.Id!, heading.Text)) ?? []);
        renderedMarkdown.Add(markdown, html);
        return html;
    }

    internal string RenderDocsDocument(string bodyHtml, DocsContentPage page)
    {
        DerivedContent = bodyHtml;
        return generator.RenderTypedDocsDocument(configuration, page, bodyHtml, markdownHeadings ?? [],
            Metadata, socialImageRelativePath);
    }

    internal IReadOnlyList<SiteDiagnostic> SnapshotMarkdownDiagnostics() =>
        Array.AsReadOnly(markdownDiagnostics.ToArray());

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
            }, Metadata);
    }
}

public abstract class SiteContentCollection
{
    private string? rendererFingerprint;

    private protected SiteContentCollection()
    {
    }

    public abstract ContentCollectionId Id { get; }

    /// <summary>A stable renderer configuration identity; rendering is not cached when omitted.</summary>
    public string? RendererFingerprint
    {
        get => rendererFingerprint;
        init
        {
            if (value is not null && string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("A renderer fingerprint must not be empty.", nameof(RendererFingerprint));
            rendererFingerprint = value;
        }
    }

    /// <summary>Whether concurrent calls to this renderer are safe.</summary>
    public bool IsThreadSafe { get; init; }

    internal static string CaptureRendererIdentity(Delegate renderer, Type? layoutType = null)
    {
        var method = renderer.Method;
        var token = TryGetMetadataToken(method);
        var identity = $"{AssemblyIdentity(method.Module.Assembly)}:{method.Module.ModuleVersionId:N}:{token}:{method.DeclaringType?.AssemblyQualifiedName}:{method}";
        return layoutType is null
            ? identity
            : $"{identity}|layout:{AssemblyIdentity(layoutType.Assembly)}:{layoutType.Module.ModuleVersionId:N}:{layoutType.FullName}";

        static string AssemblyIdentity(Assembly assembly) => assembly.FullName ?? assembly.GetName().Name ?? "unknown";
        static string TryGetMetadataToken(MethodInfo method)
        {
            try
            {
                return method.MetadataToken.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (InvalidOperationException)
            {
                return "dynamic";
            }
        }
    }

    internal static string FrontMatterSchemaName(Type frontMatterType)
    {
        ArgumentNullException.ThrowIfNull(frontMatterType);
        if (frontMatterType == typeof(DocumentFrontMatter))
            return "document";
        if (frontMatterType == typeof(PostFrontMatter))
            return "post";
        return frontMatterType.Name;
    }

    internal abstract IReadOnlyList<IntegratedContentPage> CreatePages(
        string baseUrl,
        DateTimeOffset buildTimestamp,
        string environmentName,
        SiteRouteTable routeTable,
        ICollection<string> unpublishedPages,
        CancellationToken cancellationToken);
}

public sealed class SiteContentCollection<TFrontMatter, TBody> : SiteContentCollection
    where TFrontMatter : notnull
    where TBody : notnull
{
    private readonly string rendererImplementationIdentity;

    internal Func<ContentEntry<TFrontMatter, TBody>, DocsPagePresentation>? DocsPresentationSelector { get; init; }

    public SiteContentCollection(
        ContentCollection<TFrontMatter, TBody> collection,
        ContentPageRenderer<TFrontMatter, TBody> renderer)
    {
        Collection = collection ?? throw new ArgumentNullException(nameof(collection));
        Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        rendererImplementationIdentity = CaptureRendererIdentity(renderer);
    }

    /// <summary>A layout returning null causes InvalidOperationException during generation.</summary>
    public SiteContentCollection(
        ContentCollection<TFrontMatter, TBody> collection,
        IPageLayout<ContentEntry<TFrontMatter, TBody>> layout)
        : this(collection, (entry, context) =>
        {
            var page = new SitePage<ContentEntry<TFrontMatter, TBody>>(
                new PageId(ContentPageIdentity.Create(collection.Id, entry.Id)),
                context.Route,
                entry,
                context.Metadata);
            var rendered = layout.Render(page, context.CreateLayoutContext())
                ?? throw new InvalidOperationException(
                    $"Page layout '{layout.GetType().FullName}' returned null.");
            return rendered.ToHtmlString();
        })
    {
        ArgumentNullException.ThrowIfNull(layout);
        rendererImplementationIdentity = CaptureRendererIdentity(
            (Func<SitePage<ContentEntry<TFrontMatter, TBody>>, PageRenderingContext, IHtmlContent>)layout.Render,
            layout.GetType());
    }

    public ContentCollection<TFrontMatter, TBody> Collection { get; }

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
                var presentation = DocsPresentationSelector?.Invoke(entry)
                    ?? (DocsPresentationSelector is null ? null : throw new InvalidOperationException(
                        $"Content collection '{Collection.Id}' returned no Docs presentation for entry '{entry.Id}'."));
                if (presentation is not null)
                {
                    if (string.IsNullOrWhiteSpace(presentation.Title))
                        throw new InvalidOperationException(
                            $"Content collection '{Collection.Id}' returned an empty Docs title for entry '{entry.Id}'.");
                    if (metadata.Document is { } document
                        && ((presentation.Version is not null && presentation.Version != document.Version)
                            || (presentation.Locale is not null && presentation.Locale != document.Locale)))
                        throw new InvalidOperationException(
                            $"Docs presentation for entry '{entry.Id}' conflicts with its canonical document version or locale.");
                    presentation = presentation with
                    {
                        Version = metadata.Document?.Version ?? presentation.Version,
                        Locale = metadata.Document?.Locale ?? presentation.Locale,
                    };
                }
                var isUnlisted = entry.FrontMatter is DocumentFrontMatter { Unlisted: true }
                    || presentation?.Unlisted == true;
                var docs = presentation is null ? null : new DocsContentPage(Collection.Id, entry.Id,
                    entry.SourcePath, route, presentation,
                    !isUnlisted && entry.DerivedSurfaces.HasFlag(GeneratedPageDerivedSurfaces.Navigation));
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
                    ContentDependency.Snapshot(Collection.DeclaredDependencies.Concat(entry.DeclaredDependencies)),
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
                    entry.DerivedSurfaces,
                    context =>
                    {
                        var body = Renderer(entry, context);
                        return docs is null ? body : context.RenderDocsDocument(body, docs);
                    })
                {
                    RendererFingerprint = this.RendererFingerprint is null
                        ? null
                        : this.RendererFingerprint + "|" + rendererImplementationIdentity,
                    IsThreadSafe = this.IsThreadSafe,
                    FrontMatterSchema = FrontMatterSchemaName(typeof(TFrontMatter)),
                    IsUnlisted = isUnlisted,
                    Docs = docs,
                    MarkdownSourcePath = entry.SourcePath,
                    BodyAnalysis = entry.BodyAnalysis,
                    BodySource = entry.BodySource,
                };
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
    private string? derivedContent;
    private Func<string?>? derivedContentProvider;

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

    public string? RendererFingerprint { get; init; }

    public string FrontMatterSchema { get; init; } = "custom";

    public bool IsUnlisted { get; init; }

    internal DocsContentPage? Docs { get; init; }

    public bool IsThreadSafe { get; init; }

    internal bool CanCacheRendering => IsCacheable && RendererFingerprint is not null;

    internal string? MarkdownSourcePath { get; init; }

    internal MarkdownBodyAnalysisSnapshot? BodyAnalysis { get; init; }

    internal DocumentSource? BodySource { get; init; }

    // Null means the renderer has not completed and no verified cache outcome was replayed.
    internal IReadOnlyList<SiteDiagnostic>? MarkdownDiagnostics { get; private set; }

    public IReadOnlyList<ContentDependency> DeclaredDependencies { get; } = declaredDependencies;

    public IReadOnlyList<IntegratedContentSource> Sources { get; } = sources;

    public string OwnerId { get; } = ownerId;

    public string? Content { get; private set; }

    public string? DerivedContent => derivedContent ?? derivedContentProvider?.Invoke();

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
            configuration.HasSocialImage && IsIncludedIn(GeneratedPageDerivedSurfaces.SocialImage)
                ? configuration.Routes.ContentSocialImage(Route).RelativeOutputPath
                : null,
            environmentName,
            assets,
            MarkdownSourcePath,
            BodyAnalysis,
            BodySource,
            collectDocsHeadings: Docs is not null);
        Content = renderer(context)
            ?? throw new InvalidOperationException(
                $"Content renderer for collection '{CollectionId}' and entry '{EntryId}' returned null.");
        derivedContent = context.DerivedContent ?? Content;
        MarkdownDiagnostics = context.SnapshotMarkdownDiagnostics();
        return new RenderedPage(PageId, Route, Content, Metadata);
    }

    internal void SetMarkdownDiagnostics(IReadOnlyList<SiteDiagnostic> diagnostics) =>
        MarkdownDiagnostics = Array.AsReadOnly(diagnostics.ToArray());

    internal void SetDerivedContentProvider(Func<string?> provider) =>
        derivedContentProvider = provider ?? throw new ArgumentNullException(nameof(provider));

    internal void ClearRenderedContent()
    {
        Content = null;
        derivedContent = null;
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
