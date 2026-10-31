using System.Security.Cryptography;
using System.Text;
using LithoSharp.Diagnostics;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

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
}

/// <summary>Reads files in NFC-normalized ordinal relative-path order. Rejects links/reparse points, normalized path collisions, YAML anchors, aliases and duplicate keys.</summary>
/// <remarks>Preserves the body after the front-matter terminator verbatim; SourceFingerprint hashes exact file bytes, including the BOM.</remarks>
public sealed class MarkdownContentCollectionLoader<TFrontMatter>
    : IContentCollectionLoader<TFrontMatter, string>
    where TFrontMatter : notnull
{
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
        var discovery = ContentPath.Discover(_inputRoot, [".md"], cancellationToken);
        // Underscore-prefixed partials never publish (MDX parity): the Markdown renderer
        // has no partial imports, so they are skipped before front matter parsing instead
        // of failing the whole load on their missing front matter.
        var paths = discovery.Files.Where(static path => !IsPartial(path.RelativePath)).ToArray();
        var entries = new List<ContentEntry<TFrontMatter, string>>(paths.Length);
        var diagnostics = new List<SiteDiagnostic>(discovery.Diagnostics);
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
            yamlResult.Value!,
            new SiteSourceLocation(relativePath, document.YamlStartLine, 1));
        if (!bindResult.IsSuccess)
        {
            return ContentParseResult<ContentEntry<TFrontMatter, string>>.Failure(
                bindResult.Diagnostics);
        }

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
                new ContentEntryId(relativePath),
                relativePath,
                fingerprint,
                bindResult.Value!,
                inclusion.Body,
                new SiteSourceLocation(relativePath, 1, 1))
            {
                DeclaredDependencies = ContentDependency.Snapshot(dependencies),
                CapturedInputs = capturedInputs,
            };
        }
        else
        {
            entry = new ContentEntry<TFrontMatter, string>(
                new ContentEntryId(relativePath),
                relativePath,
                fingerprint,
                bindResult.Value!,
                inclusion.Body,
                new SiteSourceLocation(relativePath, 1, 1))
            { CapturedInputs = capturedInputs };
        }

        var bodyStartLine = new Syntamark.Compilation.SourceText(text).GetLineAndColumn(text.Length - document.Body.Length).Line;
        return ContentParseResult<ContentEntry<TFrontMatter, string>>.Success(
            entry,
            [.. bindResult.Diagnostics,
                .. (Compilation.LithoLimits.FindFootnoteWarning(document.Body, relativePath, bodyStartLine) is { } footnote
                    ? (IReadOnlyList<SiteDiagnostic>) [footnote]
                    : []),
                .. (Compilation.LithoLimits.FindBrowserAssetWarning(document.Body, relativePath, bodyStartLine) is { } browserAsset
                    ? (IReadOnlyList<SiteDiagnostic>) [browserAsset]
                    : [])]);
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
            _isCacheable);

    internal static ContentParseResult<LocatedYamlMapping> ParseYaml(
        string yaml,
        string relativePath,
        int yamlStartLine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var parser = new Parser(new StringReader(yaml));
            if (!parser.MoveNext() || parser.Current is not StreamStart)
            {
                return YamlFailure(
                    MarkdownContentDiagnosticIds.InvalidYaml,
                    "YAML stream could not be read.",
                    relativePath,
                    yamlStartLine,
                    1);
            }

            parser.MoveNext();
            if (parser.Current is not DocumentStart)
            {
                return YamlFailure(
                    MarkdownContentDiagnosticIds.InvalidYaml,
                    "YAML document could not be read.",
                    relativePath,
                    yamlStartLine,
                    1);
            }

            parser.MoveNext();
            var node = ParseYamlNode(
                parser,
                relativePath,
                yamlStartLine,
                cancellationToken);
            if (!node.IsSuccess)
            {
                return ContentParseResult<LocatedYamlMapping>.Failure(node.Diagnostics);
            }

            if (parser.Current is not DocumentEnd)
            {
                return YamlFailure(
                    MarkdownContentDiagnosticIds.InvalidYaml,
                    "YAML document has unexpected trailing content.",
                    relativePath,
                    yamlStartLine,
                    1);
            }

            parser.MoveNext();
            if (parser.Current is not StreamEnd)
            {
                var location = Location(relativePath, parser.Current?.Start ?? Mark.Empty, yamlStartLine);
                return ContentParseResult<LocatedYamlMapping>.Failure(
                    [new SiteDiagnostic(
                        MarkdownContentDiagnosticIds.InvalidYaml,
                        SiteDiagnosticSeverity.Error,
                        "YAML front matter must contain exactly one document.",
                        location)]);
            }

            if (node.Value is not LocatedYamlMapping mapping)
            {
                return YamlFailure(
                    MarkdownContentDiagnosticIds.InvalidFrontMatterRoot,
                    "YAML front matter root must be a mapping.",
                    relativePath,
                    yamlStartLine,
                    1);
            }

            return ContentParseResult<LocatedYamlMapping>.Success(mapping);
        }
        catch (YamlException exception)
        {
            var location = Location(relativePath, exception.Start, yamlStartLine);
            return ContentParseResult<LocatedYamlMapping>.Failure(
                [new SiteDiagnostic(
                    MarkdownContentDiagnosticIds.InvalidYaml,
                    SiteDiagnosticSeverity.Error,
                    exception.Message,
                    location)]);
        }
    }

    internal static ContentParseResult<object> ParseYamlDocument(
        string yaml,
        string relativePath,
        int yamlStartLine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var parser = new Parser(new StringReader(yaml));
            if (!parser.MoveNext() || parser.Current is not StreamStart)
            {
                return ContentParseResult<object>.Failure([
                    new SiteDiagnostic(MarkdownContentDiagnosticIds.InvalidYaml, SiteDiagnosticSeverity.Error,
                        "YAML stream could not be read.", new SiteSourceLocation(relativePath, yamlStartLine, 1))]);
            }

            parser.MoveNext();
            if (parser.Current is not DocumentStart)
            {
                return ContentParseResult<object>.Failure([
                    new SiteDiagnostic(MarkdownContentDiagnosticIds.InvalidYaml, SiteDiagnosticSeverity.Error,
                        "YAML document could not be read.", new SiteSourceLocation(relativePath, yamlStartLine, 1))]);
            }

            parser.MoveNext();
            var node = ParseYamlNode(parser, relativePath, yamlStartLine, cancellationToken);
            if (!node.IsSuccess)
            {
                return node;
            }

            if (parser.Current is not DocumentEnd)
            {
                return ContentParseResult<object>.Failure([
                    new SiteDiagnostic(MarkdownContentDiagnosticIds.InvalidYaml, SiteDiagnosticSeverity.Error,
                        "YAML document has unexpected trailing content.",
                        Location(relativePath, parser.Current?.Start ?? Mark.Empty, yamlStartLine))]);
            }

            parser.MoveNext();
            if (parser.Current is not StreamEnd)
            {
                return ContentParseResult<object>.Failure([
                    new SiteDiagnostic(MarkdownContentDiagnosticIds.InvalidYaml, SiteDiagnosticSeverity.Error,
                        "YAML must contain exactly one document.",
                        Location(relativePath, parser.Current?.Start ?? Mark.Empty, yamlStartLine))]);
            }

            return node;
        }
        catch (YamlException exception)
        {
            return ContentParseResult<object>.Failure([
                new SiteDiagnostic(MarkdownContentDiagnosticIds.InvalidYaml, SiteDiagnosticSeverity.Error,
                    exception.Message, Location(relativePath, exception.Start, yamlStartLine))]);
        }
    }

    internal static ContentParseResult<object> ParseYamlNode(
        IParser parser,
        string relativePath,
        int yamlStartLine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parser.Current is AnchorAlias alias)
        {
            return NodeFailure(
                MarkdownContentDiagnosticIds.AliasNotAllowed,
                $"YAML alias '*{alias.Value}' is not allowed.",
                relativePath,
                alias.Start,
                yamlStartLine);
        }

        if (parser.Current is NodeEvent nodeEvent && !nodeEvent.Anchor.IsEmpty)
        {
            return NodeFailure(
                MarkdownContentDiagnosticIds.AliasNotAllowed,
                $"YAML anchor '&{nodeEvent.Anchor}' is not allowed.",
                relativePath,
                nodeEvent.Start,
                yamlStartLine);
        }

        switch (parser.Current)
        {
            case Scalar scalar:
                {
                    var value = IsNullScalar(scalar) ? null : scalar.Value;
                    var result = new LocatedYamlScalar(
                        value,
                        Location(relativePath, scalar.Start, yamlStartLine));
                    parser.MoveNext();
                    return ContentParseResult<object>.Success(result);
                }

            case SequenceStart sequenceStart:
                {
                    var values = new List<object?>();
                    var location = Location(relativePath, sequenceStart.Start, yamlStartLine);
                    parser.MoveNext();
                    while (parser.Current is not SequenceEnd)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var item = ParseYamlNode(
                            parser,
                            relativePath,
                            yamlStartLine,
                            cancellationToken);
                        if (!item.IsSuccess)
                        {
                            return item;
                        }

                        values.Add(item.Value);
                    }

                    parser.MoveNext();
                    return ContentParseResult<object>.Success(
                        new LocatedYamlSequence(values.AsReadOnly(), location));
                }

            case MappingStart mappingStart:
                {
                    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                    var keyLocations = new Dictionary<string, SiteSourceLocation>(StringComparer.Ordinal);
                    var valueLocations = new Dictionary<string, SiteSourceLocation>(StringComparer.Ordinal);
                    var location = Location(relativePath, mappingStart.Start, yamlStartLine);
                    parser.MoveNext();
                    while (parser.Current is not MappingEnd)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (parser.Current is not Scalar keyScalar || IsNullScalar(keyScalar))
                        {
                            return NodeFailure(
                                MarkdownContentDiagnosticIds.InvalidMappingKey,
                                "YAML mapping keys must be non-null strings.",
                                relativePath,
                                parser.Current?.Start ?? mappingStart.Start,
                                yamlStartLine);
                        }

                        if (!keyScalar.Anchor.IsEmpty)
                        {
                            return NodeFailure(
                                MarkdownContentDiagnosticIds.AliasNotAllowed,
                                $"YAML anchor '&{keyScalar.Anchor}' is not allowed.",
                                relativePath,
                                keyScalar.Start,
                                yamlStartLine);
                        }

                        var key = keyScalar.Value.Normalize(NormalizationForm.FormC);
                        var keyLocation = Location(relativePath, keyScalar.Start, yamlStartLine);
                        parser.MoveNext();
                        if (values.ContainsKey(key))
                        {
                            return ContentParseResult<object>.Failure(
                                [new SiteDiagnostic(
                                MarkdownContentDiagnosticIds.DuplicateKey,
                                SiteDiagnosticSeverity.Error,
                                $"YAML mapping key '{key}' is duplicated.",
                                keyLocation)]);
                        }

                        var value = ParseYamlNode(
                            parser,
                            relativePath,
                            yamlStartLine,
                            cancellationToken);
                        if (!value.IsSuccess)
                        {
                            return value;
                        }

                        values.Add(key, value.Value);
                        keyLocations.Add(key, keyLocation);
                        valueLocations.Add(
                            key,
                            LocatedYamlValue.GetLocation(value.Value) ?? keyLocation);
                    }

                    parser.MoveNext();
                    return ContentParseResult<object>.Success(
                        new LocatedYamlMapping(values, keyLocations, valueLocations, location));
                }

            default:
                return NodeFailure(
                    MarkdownContentDiagnosticIds.InvalidYaml,
                    "YAML contains an unsupported node.",
                    relativePath,
                    parser.Current?.Start ?? Mark.Empty,
                    yamlStartLine);
        }
    }

    private static bool IsNullScalar(Scalar scalar) =>
        scalar.Style is ScalarStyle.Any or ScalarStyle.Plain
        && (scalar.Value.Length == 0
            || scalar.Value == "~"
            || string.Equals(scalar.Value, "null", StringComparison.OrdinalIgnoreCase));

    private static ContentParseResult<object> NodeFailure(
        string id,
        string message,
        string relativePath,
        Mark mark,
        int yamlStartLine) =>
        ContentParseResult<object>.Failure(
            [new SiteDiagnostic(
                id,
                SiteDiagnosticSeverity.Error,
                message,
                Location(relativePath, mark, yamlStartLine))]);

    private static ContentParseResult<LocatedYamlMapping> YamlFailure(
        string id,
        string message,
        string relativePath,
        int line,
        int column) =>
        ContentParseResult<LocatedYamlMapping>.Failure(
            [new SiteDiagnostic(
                id,
                SiteDiagnosticSeverity.Error,
                message,
                new SiteSourceLocation(relativePath, line, column))]);

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

    internal static SiteSourceLocation Location(
        string relativePath,
        Mark mark,
        int yamlStartLine) =>
        new(
            relativePath,
            mark.Line > 0
                ? checked((int)mark.Line + yamlStartLine - 1)
                : yamlStartLine,
            mark.Column > 0 ? checked((int)mark.Column) : 1);

    private static string NormalizeRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Normalize(NormalizationForm.FormC);

    internal static bool IsPartial(string relativePath) =>
        relativePath.Split('/').Any(static segment => segment.StartsWith('_'));

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
                _ => DocumentFailure(
                    MarkdownContentDiagnosticIds.EmptyFrontMatter,
                    "YAML front matter must not be empty.",
                    relativePath,
                    2,
                    1),
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
