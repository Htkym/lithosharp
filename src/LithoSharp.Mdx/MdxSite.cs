using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Publishing;
using LithoSharp.Routing;

namespace LithoSharp.Mdx;

/// <summary>Compiles all registered MDX collections with one shared React graph and persistent worker.</summary>
/// <remarks>Dispose after the final build or watch session. MDX is trusted build code, not a sandbox.</remarks>
public sealed class MdxSite : ISiteBuildExtension, IAsyncDisposable
{
    private readonly MdxOptions options;
    private readonly MdxWorker worker;
    private readonly List<Func<SiteBuildContext, CancellationToken, Task<Loaded>>> loaders = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    /// <summary>Creates an opt-in MDX build extension without starting Node.</summary>
    public MdxSite(MdxOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.Timeout <= TimeSpan.Zero || options.MaximumMessageBytes < 1024)
            throw new ArgumentException("Worker timeout and message size must be positive.", nameof(options));
        worker = new(options);
    }

    /// <summary>The measured work performed by the most recent preparation.</summary>
    public MdxBuildMetrics Metrics { get; private set; } = new();

    /// <summary>Registers typed content, an explicit browser-data selector and an optional C# layout renderer.</summary>
    public void AddCollection<TFrontMatter>(IContentCollectionLoader<TFrontMatter, MdxDocument> loader,
        Func<ContentEntry<TFrontMatter, MdxDocument>, MdxPublicData>? publicData = null,
        ContentPageRenderer<TFrontMatter, MdxDocument>? renderer = null)
        where TFrontMatter : notnull
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(loader);
        loaders.Add(async (context, cancellationToken) =>
        {
            var loaded = await loader.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess) throw new SiteBuildExtensionException(loaded.Diagnostics);
            var collection = loaded.Collection!;
            var published = collection.Entries.Where(entry => PagePublicationPolicy.ShouldPublish(
                collection.PublicationMapper(entry), context.BuildTimestamp, context.Options.EnvironmentName)).ToArray();
            var pages = published.Select(entry => new Page(
                "mdx-" + Hash(Encoding.UTF8.GetBytes(ContentPageIdentity.Create(collection.Id, entry.Id)))[..24],
                RelativeSource(Path.Combine(collection.InputRoot, entry.SourcePath)), entry.Body.CompilerSource,
                collection.RouteConvention(entry).WithBaseUrl(context.Site.BaseUrl).PublicPath,
                collection.PublicationMapper(entry).Title ?? string.Empty,
                (publicData?.Invoke(entry) ?? MdxPublicData.Empty).Value)).ToArray();
            return new Loaded(pages, results =>
            {
                var entries = published.Select((entry, index) =>
                {
                    var page = results[pages[index].Id];
                    var html = string.Concat(page.GetProperty("css").EnumerateArray().Select(css =>
                        $"<link rel=\"stylesheet\" href=\"{WebUtility.HtmlEncode(AssetUrl(context, css.GetString()!))}\">"))
                        + $"<div id=\"{pages[index].Id}\">{page.GetProperty("html").GetString()}</div>"
                        + $"<script type=\"module\" src=\"{WebUtility.HtmlEncode(AssetUrl(context, page.GetProperty("entry").GetString()!))}\"></script>";
                    var body = new MdxDocument(entry.Body.Body, entry.Body.BodyStartLine) { RenderedHtml = html };
                    var dependencies = entry.DeclaredDependencies.Concat(
                        page.GetProperty("css").EnumerateArray().Select(css => ContentDependency.FromAsset(AssetId(css.GetString()!))))
                        .Append(ContentDependency.FromAsset(AssetId(page.GetProperty("entry").GetString()!)))
                        .Append(ContentDependency.FromValue("mdx-render", Hash(Encoding.UTF8.GetBytes(html)))).ToArray();
                    return new ContentEntry<TFrontMatter, MdxDocument>(entry.Id, entry.SourcePath, entry.SourceFingerprint,
                        entry.FrontMatter, body, entry.SourceLocation) { DeclaredDependencies = dependencies, DerivedSurfaces = entry.DerivedSurfaces };
                });
                var prepared = new ContentCollection<TFrontMatter, MdxDocument>(collection.Id, collection.InputRoot, entries,
                    collection.RouteConvention, collection.PublicationMapper, collection.LayoutId, collection.OrderingComparer,
                    collection.DeclaredDependencies, collection.TransformationId, collection.IsCacheable);
                return new SiteContentCollection<TFrontMatter, MdxDocument>(prepared,
                    renderer ?? ((entry, rendering) => rendering.RenderDocument(entry.Body.ToHtmlString())))
                { RendererFingerprint = options.Cacheable ? "mdx-v1" : null, IsThreadSafe = renderer is null };
            });
        });
    }

    /// <inheritdoc />
    public async Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Metrics = new();
            var loaded = new List<Loaded>();
            foreach (var loader in loaders) loaded.Add(await loader(context, cancellationToken).ConfigureAwait(false));
            var pages = loaded.SelectMany(value => value.Pages).ToArray();
            if (pages.Length == 0) return new();
            var routes = new SiteRouteTable();
            foreach (var page in pages) routes.Register(SiteRoute.ForDirectoryIndex(page.Url.Trim('/')), page.Id);
            routes.ValidateOrThrow();
            var sources = pages.ToDictionary(page => page.Source, page => page.Code, StringComparer.Ordinal);
            var linkMap = pages.ToDictionary(page => page.Source, page => page.Url, StringComparer.Ordinal);
            var tools = await ToolFingerprintAsync(cancellationToken).ConfigureAwait(false);
            var resolutionCandidates = Directory.EnumerateFiles(options.ProjectDirectory, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(file => !IsWithin(context.OutputDirectory, file) && !IsWithin(context.CacheDirectory, file)
                    && !Path.GetFileName(file).StartsWith(".lithosharp-", StringComparison.Ordinal))
                .Select(file => Path.GetRelativePath(options.ProjectDirectory, file)).Order(StringComparer.Ordinal).ToArray();
            var signature = Hash(JsonSerializer.SerializeToUtf8Bytes(new { pages, context.Site.BaseUrl, context.BuildTimestamp,
                tools, resolutionCandidates, options.Environment, options.DeclaredInputFiles }, MdxJson.Options));
            var cacheRoot = Path.Combine(context.CacheDirectory, "mdx");
            EnsureSafeDirectory(cacheRoot);
            var cachePath = Path.Combine(cacheRoot, signature + ".json");
            JsonElement result = default;
            if (options.Cacheable && File.Exists(cachePath))
            {
                try
                {
                    using var cached = JsonDocument.Parse(await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false));
                    var envelope = cached.RootElement;
                    var candidate = envelope.GetProperty("result");
                    if (envelope.GetProperty("hash").GetString() == Hash(JsonSerializer.SerializeToUtf8Bytes(candidate))
                        && await InputsMatchAsync(candidate, cancellationToken).ConfigureAwait(false)) result = candidate.Clone();
                }
                catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or ArgumentException) { }
            }
            var hit = result.ValueKind != JsonValueKind.Undefined;
            if (!hit)
            {
                var scratch = Path.Combine(cacheRoot, "work-" + Guid.NewGuid().ToString("N"));
                EnsureSafeDirectory(scratch);
                try
                {
                    while (true)
                    {
                        var requestId = Guid.NewGuid().ToString("N");
                        var response = await worker.SendAsync(new { protocol = 1, type = "compile", requestId,
                            projectRoot = options.ProjectDirectory, workRoot = scratch, allowWorkWithinProject = true,
                            assetBaseUrl = AssetUrl(context, ""), basePath = new Uri(context.Site.BaseUrl).AbsolutePath,
                            timestamp = context.BuildTimestamp, cacheable = options.Cacheable, pages = pages.Select(page => new { page.Id, page.Source, page.Url, page.Title, page.Props, locale = "" }),
                            sources, linkMap }, requestId, cancellationToken).ConfigureAwait(false);
                        if (response.GetProperty("success").GetBoolean()) { result = response.GetProperty("result").Clone(); break; }
                        var missing = response.GetProperty("requiredSources").EnumerateArray().Select(value => value.GetString()!).ToArray();
                        if (missing.Length == 0 || sources.Count + missing.Length > 10000)
                            throw new SiteBuildExtensionException(response.GetProperty("diagnostics").EnumerateArray().Select(diagnostic =>
                                new SiteDiagnostic(diagnostic.GetProperty("id").GetString()!, SiteDiagnosticSeverity.Error,
                                    diagnostic.GetProperty("message").GetString()!, string.IsNullOrEmpty(diagnostic.GetProperty("file").GetString()) ? null : new SiteSourceLocation(diagnostic.GetProperty("file").GetString()!,
                                        diagnostic.GetProperty("line").GetInt32(), diagnostic.GetProperty("column").GetInt32()))));
                        foreach (var file in missing)
                        {
                            var relative = RelativeSource(file);
                            if (sources.ContainsKey(relative)) throw MdxWorker.Failure("LSMDX003", "Worker requested an already validated source.");
                            sources.Add(relative, await ReadPartialAsync(file, relative, cancellationToken).ConfigureAwait(false));
                        }
                    }
                    if (!await InputsMatchAsync(result, cancellationToken).ConfigureAwait(false))
                        throw MdxWorker.Failure("LSMDX005", "An MDX input changed during compilation; retry the build.");
                }
                finally { DeleteScratch(scratch); }
            }
            var assets = ValidateAssets(result);
            var results = result.GetProperty("pages").EnumerateArray().ToDictionary(page => page.GetProperty("id").GetString()!, StringComparer.Ordinal);
            if (!results.Keys.Order(StringComparer.Ordinal).SequenceEqual(pages.Select(page => page.Id).Order(StringComparer.Ordinal)))
                throw MdxWorker.Failure("LSMDX003", "Worker page identities do not match the request.");
            foreach (var page in results.Values)
                foreach (var path in page.GetProperty("css").EnumerateArray().Select(value => value.GetString()!).Append(page.GetProperty("entry").GetString()!))
                    if (!assets.Any(asset => asset.Id == AssetId(path))) throw MdxWorker.Failure("LSMDX003", "A page references an unknown bundle.");
            if (!hit && options.Cacheable)
            {
                var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(new { hash = Hash(JsonSerializer.SerializeToUtf8Bytes(result)), result }), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, cachePath, overwrite: true);
            }
            Metrics = new() { CacheHit = hit, CompiledModules = hit ? 0 : result.GetProperty("compiledModules").GetInt32(),
                RenderedPages = hit ? 0 : result.GetProperty("renderedPages").GetInt32(), BundledPages = hit ? 0 : result.GetProperty("bundledPages").GetInt32() };
            return new() { Assets = assets, ContentCollections = loaded.Select(value => value.Create(results)).ToArray() };
        }
        finally { gate.Release(); }
    }

    private IReadOnlyList<SiteGeneratedAsset> ValidateAssets(JsonElement result)
    {
        var assets = result.GetProperty("assets").EnumerateArray().Select(asset =>
        {
            var path = asset.GetProperty("path").GetString()!;
            if (SiteRoute.NormalizeRelativeOutputPath(path) != path) throw MdxWorker.Failure("LSMDX003", "Worker returned a noncanonical asset path.");
            var bytes = asset.GetProperty("bytes").GetBytesFromBase64();
            if (Hash(bytes) != asset.GetProperty("hash").GetString()) throw MdxWorker.Failure("LSMDX003", "Worker asset hash mismatch.");
            return new SiteGeneratedAsset(AssetId(path), "_mdx/" + path, bytes,
                [BuildInput.FromValue("mdx-bytes", Hash(bytes))], asset.GetProperty("imports").EnumerateArray().Select(value => AssetId(value.GetString()!)));
        }).ToArray();
        var ids = assets.Select(asset => asset.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != assets.Length || assets.Any(asset => asset.ReferencedAssetIds.Any(id => !ids.Contains(id))))
            throw MdxWorker.Failure("LSMDX003", "Worker returned duplicate assets or unresolved chunk references.");
        return assets;
    }

    private async Task<bool> InputsMatchAsync(JsonElement result, CancellationToken cancellationToken)
    {
        foreach (var input in result.GetProperty("inputs").EnumerateArray())
        {
            var file = input.GetProperty("file").GetString()!;
            var root = IsWithin(options.ProjectDirectory, file) ? options.ProjectDirectory : options.WorkerDirectory;
            if (!IsWithin(root, file)) throw MdxWorker.Failure("LSMDX003", "Worker input is outside its declared roots.");
            try
            {
                var bytes = await ContentPath.ReadAllBytesAsync(root, file, cancellationToken).ConfigureAwait(false);
                if (Hash(bytes) != input.GetProperty("hash").GetString()) return false;
            }
            catch (IOException) { return false; }
        }
        return true;
    }

    private async Task<string> ToolFingerprintAsync(CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(options.WorkerDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(file => !Path.GetRelativePath(options.WorkerDirectory, file).Split(Path.DirectorySeparatorChar).Contains(".cache"))
            .Concat(options.DeclaredInputFiles.Select(file => Path.GetFullPath(file, options.ProjectDirectory))).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file));
            var root = IsWithin(options.WorkerDirectory, file) ? options.WorkerDirectory : options.ProjectDirectory;
            hash.AppendData(await ContentPath.ReadAllBytesAsync(root, file, cancellationToken).ConfigureAwait(false));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private async Task<string> ReadPartialAsync(string file, string relative, CancellationToken cancellationToken)
    {
        var text = new UTF8Encoding(false, true).GetString(await ContentPath.ReadAllBytesAsync(options.ProjectDirectory, file, cancellationToken).ConfigureAwait(false));
        if (!text.TrimStart('\uFEFF').StartsWith("---", StringComparison.Ordinal)) return text;
        var parsed = MarkdownContentCollectionLoader<object>.MarkdownSourceDocument.Parse(text, relative, cancellationToken);
        if (!parsed.IsSuccess) throw new SiteBuildExtensionException(parsed.Diagnostics);
        var document = parsed.Value!;
        var yaml = MarkdownContentCollectionLoader<object>.ParseYaml(document.Yaml, relative, document.YamlStartLine, cancellationToken);
        if (!yaml.IsSuccess) throw new SiteBuildExtensionException(yaml.Diagnostics);
        return new string('\n', text.AsSpan(0, text.Length - document.Body.Length).Count('\n')) + document.Body;
    }

    private string RelativeSource(string file)
    {
        if (!IsWithin(options.ProjectDirectory, file)) throw new ArgumentException("MDX sources must be inside the project directory.");
        return Path.GetRelativePath(options.ProjectDirectory, file).Replace('\\', '/');
    }
    private static bool IsWithin(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private static string AssetId(string path) => "mdx:" + path;
    private static string AssetUrl(SiteBuildContext context, string path) => new Uri(context.Site.BaseUrl).AbsolutePath.TrimEnd('/') + "/_mdx/" + path;
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void EnsureSafeDirectory(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("MDX cache must not traverse symbolic directories.");
        Directory.CreateDirectory(directory);
    }
    private static void DeleteScratch(string directory)
    {
        EnsureSafeDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories).Any(file => (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("MDX scratch contains a symbolic path and was preserved.");
        Directory.Delete(directory, recursive: true);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (!disposed) { disposed = true; await worker.DisposeAsync().ConfigureAwait(false); } }
        finally { gate.Release(); }
    }
    private sealed record Page(string Id, string Source, string Code, string Url, string Title, JsonElement Props);
    private sealed record Loaded(Page[] Pages, Func<Dictionary<string, JsonElement>, SiteContentCollection> Create);
}
