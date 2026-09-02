using System.Security.Cryptography;
using System.Text;
using LithoSharp.Diagnostics;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace LithoSharp.Content;

/// <summary>Markdown コレクションの読み込み診断識別子を提供します。</summary>
public static class MarkdownContentDiagnosticIds
{
    /// <summary>文書の先頭に YAML フロントマターがありません。</summary>
    public const string MissingFrontMatter = "LSM001";

    /// <summary>YAML フロントマターの終了マーカーがありません。</summary>
    public const string UnterminatedFrontMatter = "LSM002";

    /// <summary>YAML フロントマターが空です。</summary>
    public const string EmptyFrontMatter = "LSM003";

    /// <summary>YAML の構文が不正です。</summary>
    public const string InvalidYaml = "LSM004";

    /// <summary>YAML マッピングに重複したキーがあります。</summary>
    public const string DuplicateKey = "LSM005";

    /// <summary>YAML のアンカーまたはエイリアスは許可されていません。</summary>
    public const string AliasNotAllowed = "LSM006";

    /// <summary>YAML のマッピングキーが文字列ではありません。</summary>
    public const string InvalidMappingKey = "LSM007";

    /// <summary>YAML フロントマターのルートがマッピングではありません。</summary>
    public const string InvalidFrontMatterRoot = "LSM008";

    /// <summary>Markdown ファイルが有効な UTF-8 ではありません。</summary>
    public const string InvalidUtf8 = "LSM009";

    /// <summary>正規化後の Markdown エントリ識別子が重複しています。</summary>
    public const string DuplicateEntryId = "LSM010";
}

/// <summary>
/// Markdown ファイルを再帰的に検出し、型付きフロントマターと生の Markdown 本文を読み込みます。
/// </summary>
/// <typeparam name="TFrontMatter">フロントマターの型。</typeparam>
/// <remarks>
/// ファイルは NFC 正規化した相対パスの序数順で読み込みます。YAML の未知フィールドは
/// 既定のバインダーではエラーになり、重複キー、アンカー、エイリアスも拒否されます。
/// シンボリックリンク、ジャンクション、リパースポイント、および正規化後に衝突する
/// 入力パスは診断として拒否されます。
/// 本文は終了マーカー直後からファイル末尾までの文字列を改変せずに保持し、
/// ソース指紋は BOM を含むファイルの正確なバイト列から SHA-256 で生成します。
/// </remarks>
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

    /// <summary>Markdown コレクションローダーを作成します。</summary>
    /// <param name="id">ビルド間で安定したコレクション識別子。</param>
    /// <param name="inputRoot">Markdown ファイルを再帰的に検索する入力ルート。</param>
    /// <param name="routeConvention">各エントリの公開ルートを決定する規約。</param>
    /// <param name="publicationMapper">各エントリの公開情報と公開条件を作成する変換。</param>
    /// <param name="frontMatterBinder">
    /// YAML のキーと値を型付き値へ変換するバインダー。指定しない場合は未知フィールドを拒否する
    /// <see cref="ReflectionContentFrontMatterBinder{TFrontMatter}"/> を使用します。
    /// </param>
    /// <param name="layoutId">使用するレイアウトの識別子。指定しない場合は <see langword="null"/>。</param>
    /// <param name="orderingComparer">最終的なエントリ順を決める比較方法。</param>
    /// <param name="declaredDependencies">コレクションが参照する宣言済み外部依存。</param>
    /// <param name="transformationId">変換規則の安定した識別子。</param>
    /// <param name="isCacheable">将来のキャッシュ対象にできる場合は <see langword="true"/>。</param>
    /// <exception cref="ArgumentNullException">必須引数が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException">
    /// 入力ルートが空か、キャッシュ可能なコレクションに変換規則の識別子がありません。
    /// </exception>
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
        var paths = discovery.Files;
        var entries = new List<ContentEntry<TFrontMatter, string>>(paths.Count);
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
        var entry = new ContentEntry<TFrontMatter, string>(
            new ContentEntryId(relativePath),
            relativePath,
            fingerprint,
            bindResult.Value!,
            document.Body,
            new SiteSourceLocation(relativePath, 1, 1));
        return ContentParseResult<ContentEntry<TFrontMatter, string>>.Success(
            entry,
            bindResult.Diagnostics);
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

    private static ContentParseResult<LocatedYamlMapping> ParseYaml(
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

    private sealed record MarkdownSourceDocument(string Yaml, string Body, int YamlStartLine)
    {
        public static ContentParseResult<MarkdownSourceDocument> Parse(
            string text,
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
            var openingEnd = FindLineEnd(text, start);
            if (!LineEquals(text, start, openingEnd, "---"))
            {
                return DocumentFailure(
                    MarkdownContentDiagnosticIds.MissingFrontMatter,
                    "Markdown document must start with YAML front matter.",
                    relativePath,
                    1,
                    1);
            }

            var yamlStart = SkipLineBreak(text, openingEnd);
            var line = 2;
            var lineStart = yamlStart;
            while (lineStart < text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lineEnd = FindLineEnd(text, lineStart);
                if (LineEquals(text, lineStart, lineEnd, "---"))
                {
                    var bodyStart = SkipLineBreak(text, lineEnd);
                    var yaml = text[yamlStart..lineStart];
                    if (string.IsNullOrWhiteSpace(yaml))
                    {
                        return DocumentFailure(
                            MarkdownContentDiagnosticIds.EmptyFrontMatter,
                            "YAML front matter must not be empty.",
                            relativePath,
                            2,
                            1);
                    }

                    return ContentParseResult<MarkdownSourceDocument>.Success(
                        new MarkdownSourceDocument(yaml, text[bodyStart..], 2));
                }

                lineStart = SkipLineBreak(text, lineEnd);
                line++;
            }

            return DocumentFailure(
                MarkdownContentDiagnosticIds.UnterminatedFrontMatter,
                "Markdown document has no closing front matter marker.",
                relativePath,
                line,
                1);
        }

        private static int FindLineEnd(string text, int start)
        {
            var index = text.IndexOf('\n', start);
            return index < 0 ? text.Length : index;
        }

        private static int SkipLineBreak(string text, int lineEnd) =>
            lineEnd < text.Length ? lineEnd + 1 : lineEnd;

        private static bool LineEquals(string text, int start, int end, string expected)
        {
            if (end > start && text[end - 1] == '\r')
            {
                end--;
            }

            return text.AsSpan(start, end - start).SequenceEqual(expected);
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
