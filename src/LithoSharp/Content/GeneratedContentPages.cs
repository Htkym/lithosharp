using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Publishing;
using LithoSharp.Routing;

namespace LithoSharp.Content;

public delegate IEnumerable<string> ContentPageGroupSelector<TFrontMatter, TBody>(
    ContentEntry<TFrontMatter, TBody> entry)
    where TFrontMatter : notnull
    where TBody : notnull;

public delegate SitePage<TPageContent> GeneratedPageFactory<TFrontMatter, TBody, TPageContent>(
    ContentPageGroup<TFrontMatter, TBody> group)
    where TFrontMatter : notnull
    where TBody : notnull
    where TPageContent : notnull;

public delegate string GeneratedPageRenderer<TPageContent>(
    SitePage<TPageContent> page,
    ContentPageRenderingContext context)
    where TPageContent : notnull;

[Flags]
public enum GeneratedPageDerivedSurfaces
{
    None = 0,

    Navigation = 1 << 0,

    Search = 1 << 1,

    Rss = 1 << 2,

    Sitemap = 1 << 3,

    SocialImage = 1 << 4,

    LlmsTxt = 1 << 5,

    Default = 58,

    All = 63,
}

/// <summary>Published entries retain source ordering; keys are NFC-normalized and case-sensitive.</summary>
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

    public string Key { get; }

    public IReadOnlyList<ContentEntry<TFrontMatter, TBody>> Entries { get; }
}

public static class GeneratedPageDiagnosticIds
{
    public const string InvalidGroupKey = "LSG001";

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
                FrontMatterSchema = SiteContentCollection.FrontMatterSchemaName(typeof(TFrontMatter)),
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
