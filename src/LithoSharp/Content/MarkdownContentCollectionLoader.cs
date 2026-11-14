using System.Security.Cryptography;
using System.Text;
using LithoSharp.Diagnostics;
using Syntamark;

namespace LithoSharp.Content;

public static class MarkdownContentDiagnosticIds
{
    public const string MissingFrontMatter = "LSM001";

    public const string UnterminatedFrontMatter = "LSM002";

    public const string EmptyFrontMatter = "LSM003";

    public const string InvalidYaml = "LSM004";

    public const string DuplicateKey = "LSM005";

    public const string AliasNotAllowed = "LSM006";

    public const string InvalidMappingKey = "LSM007";

    public const string InvalidFrontMatterRoot = "LSM008";

    public const string InvalidUtf8 = "LSM009";

    public const string DuplicateEntryId = "LSM010";

    public const string UnsupportedYaml = "LSM014";

    public const string YamlResourceLimit = "LSM015";
}

/// <summary>Reads files in NFC-normalized ordinal relative-path order. Rejects links/reparse points, normalized path collisions, YAML anchors, aliases and duplicate keys.</summary>
/// <remarks>Preserves the body after the front-matter terminator verbatim; SourceFingerprint hashes exact file bytes, including the BOM.</remarks>
public sealed class MarkdownContentCollectionLoader<TFrontMatter>
    : IContentCollectionLoader<TFrontMatter, string>
    where TFrontMatter : notnull
{
    internal static readonly YamlParseOptions TypedYamlOptions = new(maxNestingDepth: 64);

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly ContentCollectionId _id;
    private readonly string _inputRoot;
    private readonly IContentFrontMatterBinder<TFrontMatter> _frontMatterBinder;
    private readonly ContentRouteConvention<TFrontMatter, string> _routeConvention;
    private readonly ContentPublicationMapper<TFrontMatter, string> _publicationMapper;
    private readonly ContentLayoutId? _layoutId;
    private readonly IComparer<ContentEntry<TFrontMatter, string>>? _orderingComparer;
    private readonly IReadOnlyList<ContentDependency> _declaredDependencies;
    private readonly ContentTransformationId? _transformationId;
    private readonly bool _isCacheable;
    private readonly StaticContentCatalog? _inputCatalog;

    /// <summary>Loads and checks the same input set and routes declared in a generated Markdown catalog.</summary>
    public static MarkdownContentCollectionLoader<TFrontMatter> FromCatalog(
        StaticContentCatalog catalog,
        string projectDirectory,
        ContentPublicationMapper<TFrontMatter, string> publicationMapper,
        IContentFrontMatterBinder<TFrontMatter>? frontMatterBinder = null,
        ContentLayoutId? layoutId = null,
        IComparer<ContentEntry<TFrontMatter, string>>? orderingComparer = null,
        IEnumerable<ContentDependency>? declaredDependencies = null,
        ContentTransformationId? transformationId = null,
        bool isCacheable = false)
        => new(catalog, projectDirectory, publicationMapper, frontMatterBinder, layoutId,
            orderingComparer, declaredDependencies, transformationId, isCacheable);

    private MarkdownContentCollectionLoader(
        StaticContentCatalog catalog,
        string projectDirectory,
        ContentPublicationMapper<TFrontMatter, string> publicationMapper,
        IContentFrontMatterBinder<TFrontMatter>? frontMatterBinder = null,
        ContentLayoutId? layoutId = null,
        IComparer<ContentEntry<TFrontMatter, string>>? orderingComparer = null,
        IEnumerable<ContentDependency>? declaredDependencies = null,
        ContentTransformationId? transformationId = null,
        bool isCacheable = false)
        : this((catalog ?? throw new ArgumentNullException(nameof(catalog))).CollectionId,
            CatalogRoot(catalog, projectDirectory), entry => catalog.GetRoute(entry.Id), publicationMapper,
            frontMatterBinder, layoutId, orderingComparer, declaredDependencies, transformationId, isCacheable)
    {
        _inputCatalog = catalog;
    }

    private static string CatalogRoot(StaticContentCatalog catalog, string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        if (catalog.InputRoot.Length == 0)
            throw new ArgumentException("Checked discovery requires a catalog with an explicit InputRoot.", nameof(catalog));
        if (catalog.InputProfile != ContentInputProfile.Markdown)
            throw new ArgumentException("The typed Markdown loader requires the Markdown input profile. OKF reserved and auxiliary inputs require an OKF consumer.", nameof(catalog));
        return Path.GetFullPath(catalog.InputRoot, Path.GetFullPath(projectDirectory));
    }

    public MarkdownContentCollectionLoader(
        ContentCollectionId id,
        string inputRoot,
        ContentRouteConvention<TFrontMatter, string> routeConvention,
        ContentPublicationMapper<TFrontMatter, string> publicationMapper,
        IContentFrontMatterBinder<TFrontMatter>? frontMatterBinder = null,
        ContentLayoutId? layoutId = null,
        IComparer<ContentEntry<TFrontMatter, string>>? orderingComparer = null,
        IEnumerable<ContentDependency>? declaredDependencies = null,
        ContentTransformationId? transformationId = null,
        bool isCacheable = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputRoot);
        ArgumentNullException.ThrowIfNull(routeConvention);
        ArgumentNullException.ThrowIfNull(publicationMapper);
        if (isCacheable && transformationId is null)
        {
            throw new ArgumentException(
                "A cacheable content collection requires a stable transformation identifier.",
                nameof(transformationId));
        }

        _id = id;
        _inputRoot = inputRoot;
        _routeConvention = routeConvention;
        _publicationMapper = publicationMapper;
        _frontMatterBinder = frontMatterBinder ?? new ReflectionContentFrontMatterBinder<TFrontMatter>();
        _layoutId = layoutId;
        _orderingComparer = orderingComparer;
        _declaredDependencies = ContentDependency.Snapshot(declaredDependencies);
        _transformationId = transformationId;
        _isCacheable = isCacheable;
    }

    /// <inheritdoc />
    public async ValueTask<ContentLoadResult<TFrontMatter, string>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var discovery = ContentPath.Discover(_inputRoot, ContentInputProfile.Markdown, cancellationToken);
        var paths = discovery.Files;
        var entries = new List<ContentEntry<TFrontMatter, string>>(paths.Count);
        var diagnostics = new List<SiteDiagnostic>(discovery.Diagnostics);
        if (_inputCatalog is not null)
        {
            diagnostics.AddRange(_inputCatalog.ValidateInputs(paths, cancellationToken));
            if (diagnostics.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error))
                return ContentLoadResult<TFrontMatter, string>.Failure(diagnostics);
        }
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await LoadEntryAsync(
                path.FullPath,
                path.RelativePath,
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(result.Diagnostics);
            if (result.IsSuccess)
            {
                entries.Add(result.Value!);
            }
        }

        foreach (var group in entries
            .GroupBy(static entry => entry.Id, EqualityComparer<ContentEntryId>.Default)
            .Where(static group => group.Skip(1).Any()))
        {
            foreach (var entry in group)
            {
                diagnostics.Add(new SiteDiagnostic(
                    MarkdownContentDiagnosticIds.DuplicateEntryId,
                    SiteDiagnosticSeverity.Error,
                    $"Markdown entry identifier '{group.Key.Value}' is duplicated after path normalization.",
                    entry.SourceLocation));
            }
        }

        return diagnostics.Any(static diagnostic =>
            diagnostic.Severity == SiteDiagnosticSeverity.Error)
            ? ContentLoadResult<TFrontMatter, string>.Failure(diagnostics)
            : ContentLoadResult<TFrontMatter, string>.Success(
                CreateCollection(entries),
                diagnostics);
    }

    internal ValueTask<ContentParseResult<ContentEntry<TFrontMatter, string>>> LoadEntryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return LoadEntryAsync(
            path,
            NormalizeRelativePath(_inputRoot, path),
            cancellationToken);
    }

    private async ValueTask<ContentParseResult<ContentEntry<TFrontMatter, string>>> LoadEntryAsync(
        string path,
        string relativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes;
        try
        {
            bytes = await ContentPath.ReadAllBytesAsync(
                _inputRoot,
                path,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(
                ContentPath.UnsafePathDiagnosticId,
                exception.Message,
                relativePath,
                line: 1,
                column: 1);
        }
        catch (IOException exception)
        {
            return Failure(
                "LSC0009",
                exception.Message,
                relativePath,
                line: 1,
                column: 1);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failure(
                "LSC0009",
                exception.Message,
                relativePath,
                line: 1,
                column: 1);
        }
        cancellationToken.ThrowIfCancellationRequested();

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Failure(
                MarkdownContentDiagnosticIds.InvalidUtf8,
                "Markdown source must be valid UTF-8.",
                relativePath,
                line: 1,
                column: 1);
        }

        var documentResult = MarkdownSourceDocument.Parse(
            text,
            relativePath,
            cancellationToken);
        if (!documentResult.IsSuccess)
        {
            return ContentParseResult<ContentEntry<TFrontMatter, string>>.Failure(
                documentResult.Diagnostics);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var document = documentResult.Value!;

        // Code inclusions resolve before binding so every consumer sees the same
        // body. Target bytes fold into the fingerprint: a target-only change
        // invalidates through the existing source-fingerprint inputs.
        var inclusion = await MarkdownCodeInclusion.ResolveAsync(
            text,
            document.Body,
            Path.GetDirectoryName(Path.GetFullPath(path))!,
            Path.GetFullPath(_inputRoot),
            relativePath,
            cancellationToken).ConfigureAwait(false);
        if (inclusion.Diagnostics.Any(static diagnostic =>
                diagnostic.Severity == SiteDiagnosticSeverity.Error))
        {
            return ContentParseResult<ContentEntry<TFrontMatter, string>>.Failure(
                inclusion.Diagnostics);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var yamlResult = ParseYaml(
            document.Yaml,
            relativePath,
            document.YamlStartLine,
            cancellationToken);
        if (!yamlResult.IsSuccess)
        {
            return ContentParseResult<ContentEntry<TFrontMatter, string>>.Failure(
                yamlResult.Diagnostics);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bindResult = _frontMatterBinder.Bind(
            ContentValues.FromYaml(yamlResult.Value!),
            new SiteSourceLocation(relativePath, document.YamlStartLine, 1));
        if (!bindResult.IsSuccess)
        {
            return ContentParseResult<ContentEntry<TFrontMatter, string>>.Failure(
                bindResult.Diagnostics);
        }

        var bodyStartOffset = text.Length - document.Body.Length;
        var bodyStartLine = new Syntamark.Compilation.SourceText(text).GetLineAndColumn(bodyStartOffset).Line;
        // Keep eager original-body warnings separate from the included body.
        // Larger originals use the adapter's explicit 8 Mi UTF-16 warning tier;
        // unavailable recognition fails the load without another parse attempt.
        var originalAnalysis = Compilation.MarkdownAnalysisAdapter.AnalyzeOriginalBody(
            document.Body, relativePath, cancellationToken);
        var compilerDiagnostics = Compilation.MarkdownAnalysisAdapter.BodyDiagnostics(
            originalAnalysis, relativePath, bodyStartLine, cancellationToken);
        if (compilerDiagnostics.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error))
        {
            return ContentParseResult<ContentEntry<TFrontMatter, string>>.Failure(
                [.. bindResult.Diagnostics, .. compilerDiagnostics]);
        }

        var retainedAnalysis = Compilation.MarkdownAnalysisAdapter.RetainForBody(
            originalAnalysis, inclusion.Body, relativePath);
        var bodySource = Compilation.MarkdownSourceMapping.CreateSource(inclusion, relativePath,
            inclusion.Body, bodyStartOffset, bodyStartLine);
        var fingerprint = $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

        // Preserve each original read, including repeated targets; conflicting
        // captures must not be collapsed into a later disk fingerprint.
        var capturedInputs = new[] { new CapturedContentInput(Path.GetFullPath(path), fingerprint[7..]) }
            .Concat(inclusion.Resolved.Select(resolved => new CapturedContentInput(
                Path.GetFullPath(resolved.TargetRelativePath, _inputRoot), resolved.RawHash))).ToArray();
        ContentEntry<TFrontMatter, string> entry;
        if (inclusion.Resolved.Count != 0)
        {
            // Inclusion targets stay as declared file dependencies (hashed at
            // build time) plus a resolved-content value: the source fingerprint
            // remains the raw file hash so MDX integrity checks and raw-byte
            // comparisons keep working, while target-only changes still
            // invalidate through the build inputs.
            using var combined = new MemoryStream();
            foreach (var resolved in inclusion.Resolved)
            {
                combined.Write(resolved.TargetBytes);
                combined.WriteByte(0);
            }

            var resolvedHash = Convert.ToHexString(SHA256.HashData(combined.ToArray())).ToLowerInvariant();
            var dependencies = inclusion.Resolved
                .Select(static resolved => ContentDependency.FromFile(resolved.TargetRelativePath))
                .Append(ContentDependency.FromValue("markdown.inclusion", resolvedHash))
                .ToArray();
            entry = new ContentEntry<TFrontMatter, string>(
                EntryId(relativePath),
                relativePath,
                fingerprint,
                bindResult.Value!,
                inclusion.Body,
                new SiteSourceLocation(relativePath, 1, 1))
            {
                DeclaredDependencies = ContentDependency.Snapshot(dependencies),
                FrontMatterValues = yamlResult.Value,
                CapturedInputs = capturedInputs,
                BodyAnalysis = retainedAnalysis,
                BodySource = bodySource,
            };
        }
        else
        {
            entry = new ContentEntry<TFrontMatter, string>(
                EntryId(relativePath),
                relativePath,
                fingerprint,
                bindResult.Value!,
                inclusion.Body,
                new SiteSourceLocation(relativePath, 1, 1))
            {
                CapturedInputs = capturedInputs,
                FrontMatterValues = yamlResult.Value,
                BodyAnalysis = retainedAnalysis,
                BodySource = bodySource,
            };
        }

        return ContentParseResult<ContentEntry<TFrontMatter, string>>.Success(
            entry,
            [.. bindResult.Diagnostics, .. compilerDiagnostics]);
    }

    private ContentCollection<TFrontMatter, string> CreateCollection(
        IEnumerable<ContentEntry<TFrontMatter, string>> entries) =>
        new(
            _id,
            _inputRoot,
            entries,
            _routeConvention,
            _publicationMapper,
            _layoutId,
            _orderingComparer,
            _declaredDependencies,
            _transformationId,
            _isCacheable)
        {
            StaticSchemaJson = _inputCatalog?.InputProfile == ContentInputProfile.OpenKnowledgeFormat
                ? null : (_frontMatterBinder as IContentFrontMatterSchemaProvider)?.SchemaJson,
        };

    internal static ContentParseResult<LocatedYamlMapping> ParseYaml(
        string yaml,
        string relativePath,
        int yamlStartLine,
        CancellationToken cancellationToken) =>
        ParseYaml(YamlParser.Parse(yaml, TypedYamlOptions, cancellationToken), relativePath, yamlStartLine, cancellationToken);

    internal static ContentParseResult<LocatedYamlMapping> ParseYaml(
        YamlDocument yaml,
        string relativePath,
        int yamlStartLine,
        CancellationToken cancellationToken)
    {
        var parsed = ProjectYamlDocument(yaml, relativePath, yamlStartLine, MarkdownContentDiagnosticIds.InvalidUtf8, cancellationToken);
        if (!parsed.IsSuccess)
            return ContentParseResult<LocatedYamlMapping>.Failure(parsed.Diagnostics);
        if (parsed.Value is not LocatedYamlMapping mapping)
            return ContentParseResult<LocatedYamlMapping>.Failure([
                new SiteDiagnostic(MarkdownContentDiagnosticIds.InvalidFrontMatterRoot,
                    SiteDiagnosticSeverity.Error, "YAML front matter root must be a mapping.",
                    LocatedYamlValue.GetLocation(parsed.Value) ?? new SiteSourceLocation(relativePath, yamlStartLine, 1))]);
        return ContentParseResult<LocatedYamlMapping>.Success(mapping);
    }

    internal static ContentParseResult<object> ParseYamlDocument(
        string yaml,
        string relativePath,
        int yamlStartLine,
        CancellationToken cancellationToken) =>
        ProjectYamlDocument(YamlParser.Parse(yaml, TypedYamlOptions, cancellationToken),
            relativePath, yamlStartLine, MarkdownContentDiagnosticIds.InvalidUtf8, cancellationToken);

    internal static ContentParseResult<object> ParseYamlDocument(
        byte[] yaml,
        string relativePath,
        int yamlStartLine,
        CancellationToken cancellationToken) =>
        ProjectYamlDocument(YamlParser.ParseUtf8(yaml, TypedYamlOptions, cancellationToken),
            relativePath, yamlStartLine, YamlContentDiagnosticIds.InvalidUtf8, cancellationToken);

    private static ContentParseResult<object> ProjectYamlDocument(
        YamlDocument document,
        string relativePath,
        int yamlStartLine,
        string invalidEncodingId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = new Syntamark.Compilation.SourceText(document.RawText);
        var diagnostics = document.Diagnostics.Select(diagnostic => new SiteDiagnostic(
            diagnostic.Reason switch
            {
                "duplicateKey" => MarkdownContentDiagnosticIds.DuplicateKey,
                "alias" => MarkdownContentDiagnosticIds.AliasNotAllowed,
                "invalidKey" or "complexKey" => MarkdownContentDiagnosticIds.InvalidMappingKey,
                _ => diagnostic.Kind switch
                {
                    YamlDiagnosticKind.Unsupported => MarkdownContentDiagnosticIds.UnsupportedYaml,
                    YamlDiagnosticKind.ResourceLimit or YamlDiagnosticKind.Canceled => MarkdownContentDiagnosticIds.YamlResourceLimit,
                    YamlDiagnosticKind.InvalidEncoding => invalidEncodingId,
                    _ => MarkdownContentDiagnosticIds.InvalidYaml,
                },
            },
            SiteDiagnosticSeverity.Error, diagnostic.Message,
            YamlLocation(relativePath, source, diagnostic.RawSpan.Start, diagnostic.RawSpan.Length, yamlStartLine))).ToList();
        if (document.Status != MarkdownParseStatus.Complete || diagnostics.Count != 0)
        {
            if (diagnostics.Count == 0)
                diagnostics.Add(new SiteDiagnostic(MarkdownContentDiagnosticIds.InvalidYaml,
                    SiteDiagnosticSeverity.Error, "YAML analysis is incomplete; unavailable values were not bound.",
                    new SiteSourceLocation(relativePath, yamlStartLine, 1)));
            return ContentParseResult<object>.Failure(diagnostics);
        }
        return document.Root is { } root
            ? ProjectYamlNode(root, relativePath, source, yamlStartLine, cancellationToken)
            : ContentParseResult<object>.Success(new LocatedYamlScalar(null,
                new SiteSourceLocation(relativePath, yamlStartLine, 1)));
    }

    private static ContentParseResult<object> ProjectYamlNode(
        YamlNode node,
        string relativePath,
        Syntamark.Compilation.SourceText source,
        int yamlStartLine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var location = YamlLocation(relativePath, source, node.RawSpan.Start, node.RawSpan.Length, yamlStartLine);
        if (node.Kind == MarkdownYamlKind.Scalar)
        {
            // Typed content explicitly converts decoded text to its declared CLR type.
            return ContentParseResult<object>.Success(new LocatedYamlScalar(
                node.ScalarKind == YamlScalarKind.Null ? null : node.Value, location));
        }
        if (node.Kind == MarkdownYamlKind.Sequence)
        {
            var values = new List<object?>();
            foreach (var child in node.Children)
            {
                var item = ProjectYamlNode(child, relativePath, source, yamlStartLine, cancellationToken);
                if (!item.IsSuccess)
                    return item;
                values.Add(item.Value);
            }
            return ContentParseResult<object>.Success(new LocatedYamlSequence(values.AsReadOnly(), location));
        }
        var mapping = new Dictionary<string, object?>(StringComparer.Ordinal);
        var keyLocations = new Dictionary<string, SiteSourceLocation>(StringComparer.Ordinal);
        var valueLocations = new Dictionary<string, SiteSourceLocation>(StringComparer.Ordinal);
        foreach (var entry in node.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyLocation = YamlLocation(relativePath, source, entry.Key.RawSpan.Start, entry.Key.RawSpan.Length, yamlStartLine);
            if (entry.Key.Kind != MarkdownYamlKind.Scalar || entry.Key.ScalarKind == YamlScalarKind.Null
                || entry.Key.Value is null)
                return ContentParseResult<object>.Failure([
                    new SiteDiagnostic(MarkdownContentDiagnosticIds.InvalidMappingKey,
                        SiteDiagnosticSeverity.Error, "YAML mapping keys must be non-null strings.", keyLocation)]);
            var key = entry.Key.Value.Normalize(NormalizationForm.FormC);
            if (mapping.ContainsKey(key))
                return ContentParseResult<object>.Failure([
                    new SiteDiagnostic(MarkdownContentDiagnosticIds.DuplicateKey, SiteDiagnosticSeverity.Error,
                        $"YAML mapping key '{key}' is duplicated.", keyLocation)]);
            var value = ProjectYamlNode(entry.Value, relativePath, source, yamlStartLine, cancellationToken);
            if (!value.IsSuccess)
                return value;
            mapping.Add(key, value.Value);
            keyLocations.Add(key, keyLocation);
            valueLocations.Add(key, LocatedYamlValue.GetLocation(value.Value) ?? keyLocation);
        }
        return ContentParseResult<object>.Success(new LocatedYamlMapping(mapping, keyLocations, valueLocations, location));
    }

    private static SiteSourceLocation YamlLocation(
        string relativePath,
        Syntamark.Compilation.SourceText source,
        int offset,
        int length,
        int yamlStartLine)
    {
        var position = source.GetLineAndColumn(offset);
        var end = source.GetLineAndColumn(checked(offset + length));
        return new SiteSourceLocation(relativePath, position.Line + yamlStartLine - 1, position.Column,
            end.Line + yamlStartLine - 1, end.Column);
    }

    private static ContentParseResult<ContentEntry<TFrontMatter, string>> Failure(
        string id,
        string message,
        string relativePath,
        int line,
        int column) =>
        ContentParseResult<ContentEntry<TFrontMatter, string>>.Failure(
            [new SiteDiagnostic(
                id,
                SiteDiagnosticSeverity.Error,
                message,
                new SiteSourceLocation(relativePath, line, column))]);

    private static string NormalizeRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Normalize(NormalizationForm.FormC);

    private ContentEntryId EntryId(string relativePath) =>
        _inputCatalog?.GetInput(relativePath).Id ?? new ContentEntryId(relativePath);

    internal static bool IsPartial(string relativePath) => ContentInputPolicy.IsPartial(relativePath);

    internal sealed record MarkdownSourceDocument(string Yaml, string Body, int YamlStartLine)
    {
        public static ContentParseResult<MarkdownSourceDocument> Parse(
            string text,
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var split = Compilation.FrontMatterSplitter.TrySplit(text, cancellationToken);
            return split.Status switch
            {
                Compilation.FrontMatterSplitStatus.Ok => ContentParseResult<MarkdownSourceDocument>.Success(
                    new MarkdownSourceDocument(split.Yaml, split.Body, 2)),
                Compilation.FrontMatterSplitStatus.MissingFrontMatter => DocumentFailure(
                    MarkdownContentDiagnosticIds.MissingFrontMatter,
                    "Markdown document must start with YAML front matter.",
                    relativePath,
                    1,
                    1),
                Compilation.FrontMatterSplitStatus.UnterminatedFrontMatter => DocumentFailure(
                    MarkdownContentDiagnosticIds.UnterminatedFrontMatter,
                    "Markdown document has no closing front matter marker.",
                    relativePath,
                    split.FailureLine,
                    1),
                Compilation.FrontMatterSplitStatus.EmptyFrontMatter => DocumentFailure(
                    MarkdownContentDiagnosticIds.EmptyFrontMatter,
                    "YAML front matter must not be empty.",
                    relativePath,
                    2,
                    1),
                _ => ContentParseResult<MarkdownSourceDocument>.Failure(
                    [Compilation.FrontMatterSplitter.IncompleteDiagnostic(split, text, relativePath)]),
            };
        }

        private static ContentParseResult<MarkdownSourceDocument> DocumentFailure(
            string id,
            string message,
            string relativePath,
            int line,
            int column) =>
            ContentParseResult<MarkdownSourceDocument>.Failure(
                [new SiteDiagnostic(
                    id,
                    SiteDiagnosticSeverity.Error,
                    message,
                    new SiteSourceLocation(relativePath, line, column))]);
    }
}
