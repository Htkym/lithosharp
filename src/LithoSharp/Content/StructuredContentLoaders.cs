using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Diagnostics;

namespace LithoSharp.Content;

/// <summary>JSON ファイルから型付きコンテンツコレクションを読み込むローダーです。</summary>
/// <typeparam name="TFrontMatter">バインドする型。</typeparam>
/// <typeparam name="TBody">各エントリの本文型。</typeparam>
/// <remarks>
/// シンボリックリンク、ジャンクション、リパースポイント、および正規化後に衝突する
/// 入力パスは診断として拒否されます。
/// </remarks>
public sealed class JsonContentCollectionLoader<TFrontMatter, TBody>
    : IContentCollectionLoader<TFrontMatter, TBody>
    where TFrontMatter : notnull
    where TBody : notnull
{
    private readonly string inputRoot;
    private readonly ContentCollectionId collectionId;
    private readonly IContentFrontMatterBinder<TFrontMatter> binder;
    private readonly Func<JsonElement, TBody> bodyFactory;
    private readonly ContentRouteConvention<TFrontMatter, TBody> routeConvention;
    private readonly ContentPublicationMapper<TFrontMatter, TBody> publicationMapper;
    private readonly ContentLayoutId? layoutId;

    /// <summary>JSON ローダーを作成します。</summary>
    public JsonContentCollectionLoader(
        string inputRoot,
        ContentCollectionId collectionId,
        IContentFrontMatterBinder<TFrontMatter> binder,
        Func<JsonElement, TBody> bodyFactory,
        ContentRouteConvention<TFrontMatter, TBody> routeConvention,
        ContentPublicationMapper<TFrontMatter, TBody> publicationMapper,
        ContentLayoutId? layoutId = null)
    {
        this.inputRoot = inputRoot ?? throw new ArgumentNullException(nameof(inputRoot));
        this.collectionId = collectionId ?? throw new ArgumentNullException(nameof(collectionId));
        this.binder = binder ?? throw new ArgumentNullException(nameof(binder));
        this.bodyFactory = bodyFactory ?? throw new ArgumentNullException(nameof(bodyFactory));
        this.routeConvention = routeConvention ?? throw new ArgumentNullException(nameof(routeConvention));
        this.publicationMapper = publicationMapper ?? throw new ArgumentNullException(nameof(publicationMapper));
        this.layoutId = layoutId;
    }

    /// <summary>JSON ファイルを読み込みます。ルートはオブジェクトまたはオブジェクト配列です。</summary>
    /// <param name="cancellationToken">読み込みを取り消すトークン。</param>
    /// <returns>読み込んだコレクション、または入力診断。</returns>
    public async ValueTask<ContentLoadResult<TFrontMatter, TBody>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(collectionId);
        ArgumentNullException.ThrowIfNull(binder);
        ArgumentNullException.ThrowIfNull(bodyFactory);
        ArgumentNullException.ThrowIfNull(routeConvention);
        ArgumentNullException.ThrowIfNull(publicationMapper);

        var diagnostics = new List<SiteDiagnostic>();
        var entries = new List<ContentEntry<TFrontMatter, TBody>>();
        var (files, discoveryDiagnostics) = ContentPath.Discover(
            inputRoot,
            [".json"],
            cancellationToken);
        diagnostics.AddRange(discoveryDiagnostics);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = file.RelativePath;
            var fullPath = file.FullPath;
            byte[] bytes;
            try
            {
                bytes = await ContentPath.ReadAllBytesAsync(
                    inputRoot,
                    fullPath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    ContentPath.UnsafePathDiagnosticId,
                    exception.Message,
                    relativePath,
                    1,
                    1));
                continue;
            }
            catch (IOException exception)
            {
                diagnostics.Add(ContentDiagnostic.Error("LSCJSON002", exception.Message, relativePath));
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(ContentDiagnostic.Error("LSCJSON002", exception.Message, relativePath));
                continue;
            }

            var fingerprint = ContentPath.Hash(bytes);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var roots = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement.EnumerateArray().ToArray()
                    : [document.RootElement];
                for (var index = 0; index < roots.Length; index++)
                {
                    var root = roots[index];
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        diagnostics.Add(ContentDiagnostic.Error(
                            "LSCJSON003", "JSON エントリはオブジェクトである必要があります。",
                            relativePath, 1, 1));
                        continue;
                    }

                    Dictionary<string, object?> values;
                    try
                    {
                        values = ContentValues.FromJson(root);
                    }
                    catch (ArgumentException exception)
                    {
                        diagnostics.Add(ContentDiagnostic.Error(
                            "LSCJSON006", exception.Message, relativePath, 1, 1));
                        continue;
                    }
                    var location = ContentDiagnostic.Location(relativePath);
                    ContentParseResult<TFrontMatter> result;
                    try
                    {
                        result = binder.Bind(values, location);
                    }
                    catch (FormatException exception)
                    {
                        diagnostics.Add(ContentDiagnostic.Error("LSCJSON007", exception.Message, relativePath, 1, 1));
                        continue;
                    }
                    catch (InvalidCastException exception)
                    {
                        diagnostics.Add(ContentDiagnostic.Error("LSCJSON007", exception.Message, relativePath, 1, 1));
                        continue;
                    }
                    catch (OverflowException exception)
                    {
                        diagnostics.Add(ContentDiagnostic.Error("LSCJSON007", exception.Message, relativePath, 1, 1));
                        continue;
                    }
                    diagnostics.AddRange(result.Diagnostics);
                    if (!result.IsSuccess)
                    {
                        continue;
                    }

                    var id = ContentValues.EntryId(values, relativePath, index, roots.Length);
                    try
                    {
                        entries.Add(new ContentEntry<TFrontMatter, TBody>(
                            new ContentEntryId(id), relativePath, fingerprint,
                            result.Value!, bodyFactory(root.Clone()), location));
                    }
                    catch (ArgumentException exception)
                    {
                        diagnostics.Add(ContentDiagnostic.Error("LSCJSON004", exception.Message, relativePath));
                    }
                }
            }
            catch (JsonException exception)
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    "LSCJSON005", exception.Message, relativePath,
                    exception.LineNumber is null ? null : (int)exception.LineNumber.Value + 1,
                    exception.BytePositionInLine is null ? null : (int)exception.BytePositionInLine.Value + 1));
            }
        }

        AddDuplicateIdDiagnostics(entries, diagnostics);
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error))
        {
            return ContentLoadResult<TFrontMatter, TBody>.Failure(diagnostics);
        }

        return ContentLoadResult<TFrontMatter, TBody>.Success(
            new ContentCollection<TFrontMatter, TBody>(
                collectionId, inputRoot, entries, routeConvention, publicationMapper, layoutId),
            diagnostics);
    }

    private static void AddDuplicateIdDiagnostics(
        IEnumerable<ContentEntry<TFrontMatter, TBody>> entries,
        ICollection<SiteDiagnostic> diagnostics)
    {
        foreach (var group in entries.GroupBy(static entry => entry.Id.Value, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1))
        {
            foreach (var entry in group)
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    "LSC0006", $"コンテンツ ID '{group.Key}' が重複しています。",
                    entry.SourcePath, entry.SourceLocation?.Line, entry.SourceLocation?.Column));
            }
        }
    }
}

/// <summary>RFC 4180 の基本規則に対応した CSV ファイルローダーです。</summary>
/// <typeparam name="TFrontMatter">バインドする型。</typeparam>
/// <typeparam name="TBody">各エントリの本文型。</typeparam>
/// <remarks>
/// シンボリックリンク、ジャンクション、リパースポイント、および正規化後に衝突する
/// 入力パスは診断として拒否されます。
/// </remarks>
public sealed class CsvContentCollectionLoader<TFrontMatter, TBody>
    : IContentCollectionLoader<TFrontMatter, TBody>
    where TFrontMatter : notnull
    where TBody : notnull
{
    private readonly string inputRoot;
    private readonly ContentCollectionId collectionId;
    private readonly IContentFrontMatterBinder<TFrontMatter> binder;
    private readonly Func<IReadOnlyDictionary<string, object?>, TBody> bodyFactory;
    private readonly ContentRouteConvention<TFrontMatter, TBody> routeConvention;
    private readonly ContentPublicationMapper<TFrontMatter, TBody> publicationMapper;
    private readonly ContentLayoutId? layoutId;

    /// <summary>CSV ローダーを作成します。</summary>
    public CsvContentCollectionLoader(
        string inputRoot,
        ContentCollectionId collectionId,
        IContentFrontMatterBinder<TFrontMatter> binder,
        Func<IReadOnlyDictionary<string, object?>, TBody> bodyFactory,
        ContentRouteConvention<TFrontMatter, TBody> routeConvention,
        ContentPublicationMapper<TFrontMatter, TBody> publicationMapper,
        ContentLayoutId? layoutId = null)
    {
        this.inputRoot = inputRoot ?? throw new ArgumentNullException(nameof(inputRoot));
        this.collectionId = collectionId ?? throw new ArgumentNullException(nameof(collectionId));
        this.binder = binder ?? throw new ArgumentNullException(nameof(binder));
        this.bodyFactory = bodyFactory ?? throw new ArgumentNullException(nameof(bodyFactory));
        this.routeConvention = routeConvention ?? throw new ArgumentNullException(nameof(routeConvention));
        this.publicationMapper = publicationMapper ?? throw new ArgumentNullException(nameof(publicationMapper));
        this.layoutId = layoutId;
    }

    /// <summary>CSV ファイルを読み込みます。先頭行をヘッダーとして扱います。</summary>
    /// <param name="cancellationToken">読み込みを取り消すトークン。</param>
    /// <returns>読み込んだコレクション、または入力診断。</returns>
    public async ValueTask<ContentLoadResult<TFrontMatter, TBody>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<SiteDiagnostic>();
        var entries = new List<ContentEntry<TFrontMatter, TBody>>();
        var (files, discoveryDiagnostics) = ContentPath.Discover(
            inputRoot,
            [".csv"],
            cancellationToken);
        diagnostics.AddRange(discoveryDiagnostics);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = file.RelativePath;
            var fullPath = file.FullPath;
            byte[] bytes;
            try
            {
                bytes = await ContentPath.ReadAllBytesAsync(
                    inputRoot,
                    fullPath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception)
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    ContentPath.UnsafePathDiagnosticId,
                    exception.Message,
                    relativePath,
                    1,
                    1));
                continue;
            }
            catch (IOException exception)
            {
                diagnostics.Add(ContentDiagnostic.Error("LSCCSV002", exception.Message, relativePath));
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(ContentDiagnostic.Error("LSCCSV002", exception.Message, relativePath));
                continue;
            }

            var fingerprint = ContentPath.Hash(bytes);
            string text;
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    "LSCCSV007", exception.Message, relativePath, 1, exception.Index + 1));
                continue;
            }

            var parsed = CsvParser.Parse(text, relativePath);
            diagnostics.AddRange(parsed.Diagnostics);
            if (parsed.Rows.Count == 0)
            {
                continue;
            }

            var headers = parsed.Rows[0].Fields;
            var duplicates = headers.GroupBy(static h => h, StringComparer.Ordinal)
                .Where(static group => group.Count() > 1).Select(static group => group.Key);
            foreach (var duplicate in duplicates)
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    "LSCCSV003", $"CSV ヘッダー '{duplicate}' が重複しています。", relativePath,
                    parsed.Rows[0].Line, parsed.Rows[0].Column));
            }
            if (parsed.Diagnostics.Any(static d => d.Id == "LSCCSV003" && d.Severity == SiteDiagnosticSeverity.Error)
                || duplicates.Any())
            {
                continue;
            }

            for (var rowIndex = 1; rowIndex < parsed.Rows.Count; rowIndex++)
            {
                var row = parsed.Rows[rowIndex];
                if (row.Fields.Count != headers.Count)
                {
                    diagnostics.Add(ContentDiagnostic.Error(
                        "LSCCSV004", "CSV の列数がヘッダーと一致しません。", relativePath, row.Line, row.Column));
                    continue;
                }

                var values = headers
                    .Select((header, index) => (header, value: (object?)row.Fields[index]))
                    .ToDictionary(static pair => pair.header, static pair => pair.value, StringComparer.Ordinal);
                var location = new SiteSourceLocation(relativePath, row.Line, row.Column);
                ContentParseResult<TFrontMatter> result;
                try
                {
                    result = binder.Bind(values, location);
                }
                catch (FormatException exception)
                {
                    diagnostics.Add(ContentDiagnostic.Error("LSCCSV006", exception.Message, relativePath, row.Line, row.Column));
                    continue;
                }
                catch (InvalidCastException exception)
                {
                    diagnostics.Add(ContentDiagnostic.Error("LSCCSV006", exception.Message, relativePath, row.Line, row.Column));
                    continue;
                }
                catch (OverflowException exception)
                {
                    diagnostics.Add(ContentDiagnostic.Error("LSCCSV006", exception.Message, relativePath, row.Line, row.Column));
                    continue;
                }
                diagnostics.AddRange(result.Diagnostics);
                if (!result.IsSuccess)
                {
                    continue;
                }

                var id = ContentValues.EntryId(values, relativePath, rowIndex - 1, parsed.Rows.Count - 1);
                try
                {
                    entries.Add(new ContentEntry<TFrontMatter, TBody>(
                        new ContentEntryId(id), relativePath, fingerprint,
                        result.Value!, bodyFactory(values), location));
                }
                catch (ArgumentException exception)
                {
                    diagnostics.Add(ContentDiagnostic.Error("LSCCSV005", exception.Message, relativePath, row.Line, row.Column));
                }
            }
        }

        AddDuplicateIdDiagnostics(entries, diagnostics);
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error))
        {
            return ContentLoadResult<TFrontMatter, TBody>.Failure(diagnostics);
        }

        return ContentLoadResult<TFrontMatter, TBody>.Success(
            new ContentCollection<TFrontMatter, TBody>(
                collectionId, inputRoot, entries, routeConvention, publicationMapper, layoutId),
            diagnostics);
    }

    private static void AddDuplicateIdDiagnostics(
        IEnumerable<ContentEntry<TFrontMatter, TBody>> entries,
        ICollection<SiteDiagnostic> diagnostics)
    {
        foreach (var group in entries.GroupBy(static entry => entry.Id.Value, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1))
        {
            foreach (var entry in group)
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    "LSC0006", $"コンテンツ ID '{group.Key}' が重複しています。",
                    entry.SourcePath, entry.SourceLocation?.Line, entry.SourceLocation?.Column));
            }
        }
    }
}

internal static class ContentPath
{
    private const string NormalizedPathCollision = "LSC0007";
    internal const string UnsafePathDiagnosticId = "LSC0008";
    private const string DiscoveryFailure = "LSC0009";

    public static (IReadOnlyList<ContentFile> Files, IReadOnlyList<SiteDiagnostic> Diagnostics) Discover(
        string root,
        IReadOnlyCollection<string> extensions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(extensions);
        if (extensions.Count == 0)
        {
            throw new ArgumentException("At least one extension is required.", nameof(extensions));
        }

        var configuredExtensions = extensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (configuredExtensions.Any(static extension =>
                string.IsNullOrWhiteSpace(extension)
                || extension[0] != '.'))
        {
            throw new ArgumentException(
                "Configured extensions must start with '.'.",
                nameof(extensions));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var files = new List<ContentFile>();
        var diagnostics = new List<SiteDiagnostic>();
        if (!TryGetRootAttributes(fullRoot, diagnostics, out var rootAttributes))
        {
            return (files, diagnostics);
        }

        if (!EnsureDirectoryIsSafe(fullRoot, fullRoot, ".", rootAttributes, diagnostics))
        {
            return (files, diagnostics);
        }

        var pending = new Stack<string>();
        pending.Push(fullRoot);
        var visitedDirectories = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var directoryIdentity = Path.GetFullPath(directory);
            if (!visitedDirectories.Add(directoryIdentity))
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    UnsafePathDiagnosticId,
                    $"Directory '{RelativeIdentity(fullRoot, directory)}' was encountered more than once.",
                    RelativeIdentity(fullRoot, directory),
                    1,
                    1));
                continue;
            }

            var directoryRelativePath = RelativeIdentity(fullRoot, directory);
            if (!TryGetAttributes(directory, directoryRelativePath, diagnostics, out var directoryAttributes)
                || !EnsureDirectoryIsSafe(
                    fullRoot,
                    directory,
                    directoryRelativePath,
                    directoryAttributes,
                    diagnostics))
            {
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (IOException exception)
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    DiscoveryFailure,
                    exception.Message,
                    directoryRelativePath,
                    1,
                    1));
                continue;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    DiscoveryFailure,
                    exception.Message,
                    directoryRelativePath,
                    1,
                    1));
                continue;
            }

            Array.Sort(entries, StringComparer.Ordinal);
            for (var index = entries.Length - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(entries[index]);
                var relativePath = RelativeIdentity(fullRoot, fullPath);
                if (!TryGetAttributes(fullPath, relativePath, diagnostics, out var attributes))
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (EnsureDirectoryIsSafe(
                        fullRoot,
                        fullPath,
                        relativePath,
                        attributes,
                        diagnostics))
                    {
                        pending.Push(fullPath);
                    }

                    continue;
                }

                if (!configuredExtensions.Contains(Path.GetExtension(fullPath)))
                {
                    continue;
                }

                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    diagnostics.Add(ContentDiagnostic.Error(
                        UnsafePathDiagnosticId,
                        $"Content file '{relativePath}' is a reparse point, symbolic link, or device.",
                        relativePath,
                        1,
                        1));
                    continue;
                }

                try
                {
                    using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(
                        fullRoot,
                        fullPath);
                }
                catch (InvalidOperationException exception)
                {
                    diagnostics.Add(ContentDiagnostic.Error(
                        UnsafePathDiagnosticId,
                        exception.Message,
                        relativePath,
                        1,
                        1));
                    continue;
                }
                catch (IOException exception)
                {
                    diagnostics.Add(ContentDiagnostic.Error(
                        DiscoveryFailure,
                        exception.Message,
                        relativePath,
                        1,
                        1));
                    continue;
                }
                catch (UnauthorizedAccessException exception)
                {
                    diagnostics.Add(ContentDiagnostic.Error(
                        DiscoveryFailure,
                        exception.Message,
                        relativePath,
                        1,
                        1));
                    continue;
                }

                files.Add(new ContentFile(fullPath, relativePath));
            }
        }

        var collidedPaths = files
            .GroupBy(static file => file.RelativePath, StringComparer.Ordinal)
            .Where(static group => group.Skip(1).Any())
            .ToArray();
        foreach (var group in collidedPaths)
        {
            foreach (var file in group.OrderBy(static file => file.FullPath, StringComparer.Ordinal))
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    NormalizedPathCollision,
                    $"Content path '{file.FullPath}' collides at normalized relative path '{group.Key}'.",
                    group.Key,
                    1,
                    1));
            }
        }

        var collisions = collidedPaths
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return (
            files
                .Where(file => !collisions.Contains(file.RelativePath))
                .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
                .ThenBy(static file => file.FullPath, StringComparer.Ordinal)
                .ToArray(),
            diagnostics);
    }

    public static async ValueTask<byte[]> ReadAllBytesAsync(
        string root,
        string fullPath,
        CancellationToken cancellationToken)
    {
        await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(
            root,
            fullPath,
            asynchronous: true);
        if (stream.Length > int.MaxValue)
        {
            throw new IOException($"Content file '{fullPath}' is too large.");
        }

        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    public static string Hash(byte[] bytes) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";

    private static bool TryGetRootAttributes(
        string fullRoot,
        ICollection<SiteDiagnostic> diagnostics,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(fullRoot);
            return true;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException exception)
        {
            diagnostics.Add(ContentDiagnostic.Error(
                DiscoveryFailure,
                exception.Message,
                ".",
                1,
                1));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(ContentDiagnostic.Error(
                DiscoveryFailure,
                exception.Message,
                ".",
                1,
                1));
        }

        attributes = default;
        return false;
    }

    private static bool TryGetAttributes(
        string fullPath,
        string relativePath,
        ICollection<SiteDiagnostic> diagnostics,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(fullPath);
            return true;
        }
        catch (IOException exception)
        {
            diagnostics.Add(ContentDiagnostic.Error(
                DiscoveryFailure,
                exception.Message,
                relativePath,
                1,
                1));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(ContentDiagnostic.Error(
                DiscoveryFailure,
                exception.Message,
                relativePath,
                1,
                1));
        }

        attributes = default;
        return false;
    }

    private static bool EnsureDirectoryIsSafe(
        string root,
        string fullPath,
        string relativePath,
        FileAttributes attributes,
        ICollection<SiteDiagnostic> diagnostics)
    {
        if ((attributes & FileAttributes.Directory) == 0)
        {
            diagnostics.Add(ContentDiagnostic.Error(
                UnsafePathDiagnosticId,
                $"Content directory '{relativePath}' is not a directory.",
                relativePath,
                1,
                1));
            return false;
        }

        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            diagnostics.Add(ContentDiagnostic.Error(
                UnsafePathDiagnosticId,
                $"Content directory '{relativePath}' is a reparse point, symbolic link, junction, or device.",
                relativePath,
                1,
                1));
            return false;
        }

        try
        {
            SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(
                root,
                fullPath);
        }
        catch (InvalidOperationException exception)
        {
            diagnostics.Add(ContentDiagnostic.Error(
                UnsafePathDiagnosticId,
                exception.Message,
                relativePath,
                1,
                1));
            return false;
        }
        catch (IOException exception)
        {
            diagnostics.Add(ContentDiagnostic.Error(
                DiscoveryFailure,
                exception.Message,
                relativePath,
                1,
                1));
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(ContentDiagnostic.Error(
                DiscoveryFailure,
                exception.Message,
                relativePath,
                1,
                1));
            return false;
        }

        return true;
    }

    private static string RelativeIdentity(string root, string fullPath)
    {
        var relativePath = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || relativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Discovered content path '{fullPath}' escapes input root '{root}'.");
        }

        return relativePath == "."
            ? "."
            : relativePath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/')
                .Normalize(NormalizationForm.FormC);
    }
}

internal sealed record ContentFile(string FullPath, string RelativePath);

internal static class ContentValues
{
    public static Dictionary<string, object?> FromYaml(IReadOnlyDictionary<string, object?> values) =>
        values.ToDictionary(
            static pair => pair.Key.Normalize(NormalizationForm.FormC),
            static pair => FromYamlValue(pair.Value),
            StringComparer.Ordinal);

    private static object? FromYamlValue(object? value) => value switch
    {
        LocatedYamlScalar scalar => scalar.Value,
        IReadOnlyDictionary<string, object?> mapping => FromYaml(mapping),
        IReadOnlyList<object?> sequence => sequence.Select(FromYamlValue).ToList(),
        _ => value
    };

    public static Dictionary<string, object?> FromJson(JsonElement element) =>
        element.EnumerateObject().ToDictionary(
            static property => property.Name.Normalize(NormalizationForm.FormC),
            static property => ToValue(property.Value),
            StringComparer.Ordinal);

    public static string EntryId(IReadOnlyDictionary<string, object?> values, string path, int index, int count)
    {
        if (values.TryGetValue("id", out var value)
            && LocatedYamlValue.Unwrap(value) is string id
            && !string.IsNullOrWhiteSpace(id))
        {
            return id.Normalize(NormalizationForm.FormC);
        }

        return count == 1 ? path : $"{path}#{index + 1}";
    }

    private static object? ToValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(
            static property => property.Name.Normalize(NormalizationForm.FormC),
            static property => ToValue(property.Value),
            StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(ToValue).ToList(),
        _ => value.Clone(),
    };
}

internal static class ContentDiagnostic
{
    public static SiteDiagnostic Error(string id, string message, string path, int? line = null, int? column = null) =>
        new(id, SiteDiagnosticSeverity.Error, message, new SiteSourceLocation(path, line, column));

    public static SiteSourceLocation Location(string path) => new(path, 1, 1);
}

internal sealed record CsvRow(IReadOnlyList<string> Fields, int Line, int Column);

internal sealed class CsvParseResult(IReadOnlyList<CsvRow> rows, IReadOnlyList<SiteDiagnostic> diagnostics)
{
    public IReadOnlyList<CsvRow> Rows { get; } = rows;
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; } = diagnostics;
}

internal static class CsvParser
{
    public static CsvParseResult Parse(string text, string path)
    {
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var rows = new List<CsvRow>();
        var diagnostics = new List<SiteDiagnostic>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var justClosedQuote = false;
        var line = 1;
        var column = 1;
        var rowLine = 1;
        var rowColumn = 1;

        for (var i = 0; i < text.Length; i++, column++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"'); i++; column++; continue;
                }
                if (ch == '"') { quoted = false; justClosedQuote = true; continue; }
                field.Append(ch);
                if (ch == '\n') { line++; column = 0; }
                continue;
            }

            if (justClosedQuote)
            {
                if (ch == ',')
                {
                    fields.Add(field.ToString()); field.Clear(); justClosedQuote = false; continue;
                }
                if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    fields.Add(field.ToString()); field.Clear(); justClosedQuote = false;
                    rows.Add(new CsvRow(fields.ToArray(), rowLine, rowColumn));
                    fields.Clear(); line++; column = 0; rowLine = line; rowColumn = 1; continue;
                }
                diagnostics.Add(ContentDiagnostic.Error(
                    "LSCCSV005", "CSV の closing quote の後に不正な文字があります。", path, line, column));
                justClosedQuote = false;
            }
            if (ch == '"' && field.Length == 0) { quoted = true; continue; }
            if (ch == '"')
            {
                diagnostics.Add(ContentDiagnostic.Error(
                    "LSCCSV005", "CSV の引用符の位置が不正です。", path, line, column));
                field.Append(ch);
                continue;
            }
            if (ch == ',') { fields.Add(field.ToString()); field.Clear(); continue; }
            if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(field.ToString()); field.Clear();
                rows.Add(new CsvRow(fields.ToArray(), rowLine, rowColumn));
                fields.Clear(); line++; column = 0; rowLine = line; rowColumn = 1; continue;
            }
            field.Append(ch);
        }

        if (quoted)
        {
            diagnostics.Add(ContentDiagnostic.Error("LSCCSV005", "CSV の引用符が閉じられていません。", path, line, column));
        }
        else if (field.Length > 0 || fields.Count > 0 || text.Length == 0)
        {
            fields.Add(field.ToString());
            rows.Add(new CsvRow(fields.ToArray(), rowLine, rowColumn));
        }

        return new CsvParseResult(rows, diagnostics);
    }
}
