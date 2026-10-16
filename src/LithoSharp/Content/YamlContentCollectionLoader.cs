using System.Security.Cryptography;
using System.Text;
using LithoSharp.Diagnostics;

namespace LithoSharp.Content;

public static class YamlContentDiagnosticIds
{
    public const string InvalidUtf8 = "LSY001";

    public const string ReadFailure = "LSY002";

    public const string DuplicateEntryId = "LSY003";

    public const string InvalidYaml = MarkdownContentDiagnosticIds.InvalidYaml;

    public const string DuplicateKey = MarkdownContentDiagnosticIds.DuplicateKey;

    public const string AliasNotAllowed = MarkdownContentDiagnosticIds.AliasNotAllowed;

    public const string InvalidMappingKey = MarkdownContentDiagnosticIds.InvalidMappingKey;

    public const string InvalidRoot = MarkdownContentDiagnosticIds.InvalidFrontMatterRoot;
}

/// <summary>Accepts an object or array of objects. Rejects YAML anchors, aliases, duplicate/non-string keys, links/reparse points and normalized path collisions.</summary>
public sealed class YamlContentCollectionLoader<TFrontMatter, TBody>
    : IContentCollectionLoader<TFrontMatter, TBody>
    where TFrontMatter : notnull
    where TBody : notnull
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string inputRoot;
    private readonly ContentCollectionId collectionId;
    private readonly IContentFrontMatterBinder<TFrontMatter> binder;
    private readonly Func<IReadOnlyDictionary<string, object?>, TBody> bodyFactory;
    private readonly ContentRouteConvention<TFrontMatter, TBody> routeConvention;
    private readonly ContentPublicationMapper<TFrontMatter, TBody> publicationMapper;
    private readonly ContentLayoutId? layoutId;
    private readonly IComparer<ContentEntry<TFrontMatter, TBody>>? orderingComparer;
    private readonly IReadOnlyList<ContentDependency> declaredDependencies;
    private readonly ContentTransformationId? transformationId;
    private readonly bool isCacheable;

    public YamlContentCollectionLoader(
        string inputRoot,
        ContentCollectionId collectionId,
        IContentFrontMatterBinder<TFrontMatter> binder,
        Func<IReadOnlyDictionary<string, object?>, TBody> bodyFactory,
        ContentRouteConvention<TFrontMatter, TBody> routeConvention,
        ContentPublicationMapper<TFrontMatter, TBody> publicationMapper,
        ContentLayoutId? layoutId = null,
        IComparer<ContentEntry<TFrontMatter, TBody>>? orderingComparer = null,
        IEnumerable<ContentDependency>? declaredDependencies = null,
        ContentTransformationId? transformationId = null,
        bool isCacheable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputRoot);
        this.collectionId = collectionId ?? throw new ArgumentNullException(nameof(collectionId));
        this.binder = binder ?? throw new ArgumentNullException(nameof(binder));
        this.bodyFactory = bodyFactory ?? throw new ArgumentNullException(nameof(bodyFactory));
        this.routeConvention = routeConvention ?? throw new ArgumentNullException(nameof(routeConvention));
        this.publicationMapper = publicationMapper ?? throw new ArgumentNullException(nameof(publicationMapper));
        if (isCacheable && transformationId is null)
            throw new ArgumentException("A cacheable content collection requires a stable transformation identifier.", nameof(transformationId));
        this.inputRoot = inputRoot;
        this.layoutId = layoutId;
        this.orderingComparer = orderingComparer;
        this.declaredDependencies = ContentDependency.Snapshot(declaredDependencies);
        this.transformationId = transformationId;
        this.isCacheable = isCacheable;
    }

    /// <inheritdoc />
    public async ValueTask<ContentLoadResult<TFrontMatter, TBody>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<SiteDiagnostic>();
        var entries = new List<ContentEntry<TFrontMatter, TBody>>();
        var discovery = ContentPath.Discover(
            inputRoot,
            [".yaml", ".yml"],
            cancellationToken);
        diagnostics.AddRange(discovery.Diagnostics);

        foreach (var file in discovery.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                bytes = await ContentPath.ReadAllBytesAsync(
                    inputRoot,
                    file.FullPath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    ContentPath.UnsafePathDiagnosticId,
                    ex.Message,
                    file.RelativePath,
                    1,
                    1));
                continue;
            }
            catch (IOException ex)
            {
                diagnostics.Add(ContentDiagnostic.Error(YamlContentDiagnosticIds.ReadFailure, ex.Message, file.RelativePath));
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                diagnostics.Add(ContentDiagnostic.Error(YamlContentDiagnosticIds.ReadFailure, ex.Message, file.RelativePath));
                continue;
            }

            string yaml;
            try { yaml = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException)
            {
                diagnostics.Add(ContentDiagnostic.Error(YamlContentDiagnosticIds.InvalidUtf8,
                    "YAML source must be valid UTF-8.", file.RelativePath, 1, 1));
                continue;
            }

            var parsed = MarkdownContentCollectionLoader<TFrontMatter>.ParseYamlDocument(
                yaml, file.RelativePath, 1, cancellationToken);
            diagnostics.AddRange(parsed.Diagnostics);
            if (!parsed.IsSuccess) continue;

            IReadOnlyList<object?> roots = parsed.Value is LocatedYamlSequence sequence
                ? sequence
                : new object?[] { parsed.Value };
            for (var index = 0; index < roots.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (roots[index] is not LocatedYamlMapping mapping)
                {
                    diagnostics.Add(ContentDiagnostic.Error(YamlContentDiagnosticIds.InvalidRoot,
                        "YAML entries must be mappings.", file.RelativePath,
                        LocatedYamlValue.GetLocation(roots[index])?.Line ?? 1,
                        LocatedYamlValue.GetLocation(roots[index])?.Column ?? 1));
                    continue;
                }

                var location = LocatedYamlValue.GetLocation(mapping) ?? ContentDiagnostic.Location(file.RelativePath);
                var materialized = ContentValues.FromYaml(mapping);
                IReadOnlyDictionary<string, object?> bindingValues =
                    binder is ReflectionContentFrontMatterBinder<TFrontMatter>
                    ? mapping
                    : materialized;
                var bound = binder.Bind(bindingValues, location);
                diagnostics.AddRange(bound.Diagnostics);
                if (!bound.IsSuccess) continue;
                try
                {
                    entries.Add(new ContentEntry<TFrontMatter, TBody>(
                        new ContentEntryId(ContentValues.EntryId(mapping, file.RelativePath, index, roots.Count)),
                        file.RelativePath, ContentPath.Hash(bytes), bound.Value!,
                        bodyFactory(materialized), location));
                }
                catch (ArgumentException ex)
                {
                    diagnostics.Add(ContentDiagnostic.Error(YamlContentDiagnosticIds.ReadFailure, ex.Message, file.RelativePath));
                }
                catch (FormatException ex)
                {
                    diagnostics.Add(ContentDiagnostic.Error(YamlContentDiagnosticIds.ReadFailure, ex.Message, file.RelativePath));
                }
                catch (InvalidCastException ex)
                {
                    diagnostics.Add(ContentDiagnostic.Error(YamlContentDiagnosticIds.ReadFailure, ex.Message, file.RelativePath));
                }
                catch (OverflowException ex)
                {
                    diagnostics.Add(ContentDiagnostic.Error(YamlContentDiagnosticIds.ReadFailure, ex.Message, file.RelativePath));
                }
            }
        }

        foreach (var group in entries.GroupBy(static entry => entry.Id.Value, StringComparer.Ordinal)
                     .Where(static group => group.Count() > 1))
            foreach (var entry in group)
                diagnostics.Add(ContentDiagnostic.Error(YamlContentDiagnosticIds.DuplicateEntryId,
                    $"コンテンツ ID '{group.Key}' が重複しています。", entry.SourcePath,
                    entry.SourceLocation?.Line, entry.SourceLocation?.Column));

        return diagnostics.Any(static d => d.Severity == SiteDiagnosticSeverity.Error)
            ? ContentLoadResult<TFrontMatter, TBody>.Failure(diagnostics)
            : ContentLoadResult<TFrontMatter, TBody>.Success(CreateCollection(entries), diagnostics);
    }

    private ContentCollection<TFrontMatter, TBody> CreateCollection(
        IEnumerable<ContentEntry<TFrontMatter, TBody>> entries) =>
        new(collectionId, inputRoot, entries, routeConvention, publicationMapper, layoutId,
            orderingComparer, declaredDependencies, transformationId, isCacheable);
}
