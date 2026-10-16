using System.Collections.ObjectModel;
using System.Text;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Content;

public delegate SiteRoute ContentRouteConvention<TFrontMatter, TBody>(
    ContentEntry<TFrontMatter, TBody> entry)
    where TFrontMatter : notnull
    where TBody : notnull;

public delegate PageMetadata ContentPublicationMapper<TFrontMatter, TBody>(
    ContentEntry<TFrontMatter, TBody> entry)
    where TFrontMatter : notnull
    where TBody : notnull;

public sealed class ContentCollection<TFrontMatter, TBody>
    where TFrontMatter : notnull
    where TBody : notnull
{
    /// <summary>For caching, declare every external delegate/comparer dependency and a stable transformationId that changes whenever the rules change.</summary>
    /// <remarks>The default ordering is ordinal entry ID.</remarks>
    public ContentCollection(
        ContentCollectionId id,
        string inputRoot,
        IEnumerable<ContentEntry<TFrontMatter, TBody>> entries,
        ContentRouteConvention<TFrontMatter, TBody> routeConvention,
        ContentPublicationMapper<TFrontMatter, TBody> publicationMapper,
        ContentLayoutId? layoutId = null,
        IComparer<ContentEntry<TFrontMatter, TBody>>? orderingComparer = null,
        IEnumerable<ContentDependency>? declaredDependencies = null,
        ContentTransformationId? transformationId = null,
        bool isCacheable = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(inputRoot);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(routeConvention);
        ArgumentNullException.ThrowIfNull(publicationMapper);
        if (string.IsNullOrWhiteSpace(inputRoot))
        {
            throw new ArgumentException("A content input root must not be empty.", nameof(inputRoot));
        }

        if (isCacheable && transformationId is null)
        {
            throw new ArgumentException(
                "A cacheable content collection requires a stable transformation identifier.",
                nameof(transformationId));
        }

        Id = id;
        InputRoot = inputRoot.Normalize(NormalizationForm.FormC);
        RouteConvention = routeConvention;
        PublicationMapper = publicationMapper;
        LayoutId = layoutId;
        OrderingComparer = orderingComparer ?? ContentEntryIdComparer.Instance;
        Entries = SnapshotEntries(entries, OrderingComparer);
        DeclaredDependencies = ContentDependency.Snapshot(declaredDependencies);
        TransformationId = transformationId;
        IsCacheable = isCacheable;
    }

    public ContentCollectionId Id { get; }

    public string InputRoot { get; }

    public IReadOnlyList<ContentEntry<TFrontMatter, TBody>> Entries { get; }

    public ContentRouteConvention<TFrontMatter, TBody> RouteConvention { get; }

    public ContentPublicationMapper<TFrontMatter, TBody> PublicationMapper { get; }

    public ContentLayoutId? LayoutId { get; }

    public IComparer<ContentEntry<TFrontMatter, TBody>> OrderingComparer { get; }

    public IReadOnlyList<ContentDependency> DeclaredDependencies { get; }

    public ContentTransformationId? TransformationId { get; }

    public bool IsCacheable { get; }

    /// <summary>Groups published entries by NFC-normalized, case-sensitive keys, deduplicating each entry within a key. Generated pages cannot be aggregated recursively.</summary>
    /// <remarks>Caching requires a cacheable source, complete external dependencies and a versioned transformationId. Default derived surfaces include search, sitemap, per-page social images and llms.txt, excluding RSS and site navigation.</remarks>
    public SiteContentCollection GeneratePages<TPageContent>(
        ContentCollectionId generatedCollectionId,
        ContentPageGroupSelector<TFrontMatter, TBody> groupSelector,
        GeneratedPageFactory<TFrontMatter, TBody, TPageContent> pageFactory,
        GeneratedPageRenderer<TPageContent> renderer,
        IEnumerable<string>? groupKeys = null,
        ContentLayoutId? layoutId = null,
        IComparer<string>? groupOrderingComparer = null,
        IEnumerable<ContentDependency>? declaredDependencies = null,
        ContentTransformationId? transformationId = null,
        bool isCacheable = false,
        GeneratedPageDerivedSurfaces derivedSurfaces = GeneratedPageDerivedSurfaces.Default)
        where TPageContent : notnull =>
        new GeneratedSiteContentCollection<TFrontMatter, TBody, TPageContent>(
            this,
            generatedCollectionId,
            groupSelector,
            pageFactory,
            renderer,
            groupKeys,
            layoutId,
            groupOrderingComparer,
            declaredDependencies,
            transformationId,
            isCacheable,
            derivedSurfaces);

    private static ReadOnlyCollection<ContentEntry<TFrontMatter, TBody>> SnapshotEntries(
        IEnumerable<ContentEntry<TFrontMatter, TBody>> entries,
        IComparer<ContentEntry<TFrontMatter, TBody>> comparer)
    {
        var values = entries.ToArray();
        if (values.Any(static entry => entry is null))
        {
            throw new ArgumentException("Content entries must not contain null.", nameof(entries));
        }

        if (values
            .GroupBy(static entry => entry.Id)
            .Any(static group => group.Skip(1).Any()))
        {
            throw new ArgumentException("Content entry identifiers must be unique.", nameof(entries));
        }

        Array.Sort(values, new DeterministicComparer(comparer));
        return Array.AsReadOnly(values);
    }

    private sealed class ContentEntryIdComparer : IComparer<ContentEntry<TFrontMatter, TBody>>
    {
        public static ContentEntryIdComparer Instance { get; } = new();

        public int Compare(
            ContentEntry<TFrontMatter, TBody>? x,
            ContentEntry<TFrontMatter, TBody>? y) =>
            StringComparer.Ordinal.Compare(x?.Id.Value, y?.Id.Value);
    }

    private sealed class DeterministicComparer(
        IComparer<ContentEntry<TFrontMatter, TBody>> comparer)
        : IComparer<ContentEntry<TFrontMatter, TBody>>
    {
        public int Compare(
            ContentEntry<TFrontMatter, TBody>? x,
            ContentEntry<TFrontMatter, TBody>? y)
        {
            var result = comparer.Compare(x, y);
            return result != 0
                ? result
                : StringComparer.Ordinal.Compare(x?.Id.Value, y?.Id.Value);
        }
    }
}
