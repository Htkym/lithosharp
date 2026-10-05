using System.Net;
using System.Diagnostics;
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
    private JsonElement? inspection;
    private string? previousTools;
    private IReadOnlyList<ContentDependency> executionDependencies = [];

    /// <summary>Creates an opt-in MDX build extension without starting Node.</summary>
    public MdxSite(MdxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options with { Environment = new Dictionary<string, string>(options.Environment, StringComparer.Ordinal),
            DeclaredInputFiles = options.DeclaredInputFiles.ToArray(), StaticComponents = options.StaticComponents.ToArray(),
            Plugins = options.Plugins.Select(plugin => plugin with { Options = plugin.Options.Clone() }).ToArray(),
            CrossReferences = new Dictionary<string, SiteUrl>(options.CrossReferences, StringComparer.Ordinal) };
        if (options.Timeout <= TimeSpan.Zero || options.MaximumMessageBytes < 1024)
            throw new ArgumentException("Worker timeout and message size must be positive.", nameof(options));
        if (options.Plugins.Any(plugin => plugin.Stage is not ("remark" or "rehype") || string.IsNullOrWhiteSpace(plugin.Module)))
            throw new ArgumentException("MDX plugins require a remark or rehype stage and an explicit module.", nameof(options));
        if (options.Hydration is not ("page" or "selective")) throw new ArgumentException("Hydration must be page or selective.", nameof(options));
        worker = new(this.options);
    }

    /// <summary>The measured work performed by the most recent preparation.</summary>
    public MdxBuildMetrics Metrics { get; private set; } = new();
    /// <inheritdoc />
    public JsonElement? GetInspection() => inspection;

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
            var loadDiagnostics = loaded.Diagnostics;
            var collection = loaded.Collection!;
            var published = collection.Entries.Where(entry => PagePublicationPolicy.ShouldPublish(
                collection.PublicationMapper(entry), context.BuildTimestamp, context.Options.EnvironmentName)).ToArray();
            var pages = published.Select(entry => new Page(
                "mdx-" + Hash(Encoding.UTF8.GetBytes(ContentPageIdentity.Create(collection.Id, entry.Id)))[..24],
                RelativeSource(Path.Combine(collection.InputRoot, entry.SourcePath)), entry.Body.CompilerSource,
                collection.RouteConvention(entry).WithBaseUrl(context.Site.BaseUrl).PublicPath,
                collection.PublicationMapper(entry).Title ?? string.Empty,
                collection.PublicationMapper(entry).Description,
                (publicData?.Invoke(entry) ?? MdxPublicData.Empty).Value, collection.PublicationMapper(entry).Language ?? context.Site.Language,
                entry.DerivedSurfaces != GeneratedPageDerivedSurfaces.None, entry.Body.CapturedInputs)).ToArray();
            return new Loaded(pages, results =>
            {
                var entries = published.Select((entry, index) =>
                {
                    var page = results[pages[index].Id];
                    var html = $"<link rel=\"license\" href=\"{WebUtility.HtmlEncode(AssetUrl(context, "third-party-notices.txt"))}\">" + string.Concat(page.GetProperty("css").EnumerateArray().Select(css =>
                        $"<link rel=\"stylesheet\" href=\"{WebUtility.HtmlEncode(AssetUrl(context, css.GetString()!))}\">"))
                        + $"<div id=\"{pages[index].Id}\">{page.GetProperty("html").GetString()}</div>"
                        + (page.GetProperty("entry").ValueKind == JsonValueKind.Null ? "" : $"<script type=\"module\" src=\"{WebUtility.HtmlEncode(AssetUrl(context, page.GetProperty("entry").GetString()!))}\"></script>");
                    var body = new MdxDocument(entry.Body.Body, entry.Body.BodyStartLine, entry.Body.BodyStartOffset)
                    {
                        RenderedHtml = html,
                        CapturedInputs = entry.Body.CapturedInputs,
                        PlainText = page.GetProperty("text").GetString(),
                        Semantics = MdxSemantics.FromWorker(
                            entry.SourcePath,
                            entry.Body,
                            page.GetProperty("headings"),
                            page.GetProperty("links"),
                            page.GetProperty("islands"),
                            page.GetProperty("text").GetString()),
                    };
                    var dependencies = entry.DeclaredDependencies.Concat(executionDependencies).Append(ContentDependency.FromAsset(AssetId("third-party-notices.txt"))).Concat(
                        page.GetProperty("css").EnumerateArray().Select(css => ContentDependency.FromAsset(AssetId(css.GetString()!))))
                        .Concat(page.GetProperty("entry").ValueKind == JsonValueKind.Null ? [] : new[] { ContentDependency.FromAsset(AssetId(page.GetProperty("entry").GetString()!)) })
                        .Append(ContentDependency.FromValue("mdx-render", Hash(Encoding.UTF8.GetBytes(html)))).ToArray();
                    return new ContentEntry<TFrontMatter, MdxDocument>(entry.Id, entry.SourcePath, entry.SourceFingerprint,
                        entry.FrontMatter, body, entry.SourceLocation) { DeclaredDependencies = dependencies, DerivedSurfaces = entry.DerivedSurfaces, CapturedInputs = entry.CapturedInputs };
                });
                var prepared = new ContentCollection<TFrontMatter, MdxDocument>(collection.Id, collection.InputRoot, entries,
                    collection.RouteConvention, collection.PublicationMapper, collection.LayoutId, collection.OrderingComparer,
                    collection.DeclaredDependencies, collection.TransformationId, collection.IsCacheable);
                return new SiteContentCollection<TFrontMatter, MdxDocument>(prepared,
                    renderer ?? ((entry, rendering) => rendering.RenderDocument(entry.Body.ToHtmlString())))
                { RendererFingerprint = options.Cacheable ? "mdx-v1" : null, IsThreadSafe = renderer is null };
            }, loadDiagnostics);
        });
    }

    /// <inheritdoc />
    public async Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var starts = worker.Starts;
        var work = new WorkAccumulator();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Metrics = new();
            var loaded = new List<Loaded>();
            foreach (var loader in loaders) loaded.Add(await loader(context, cancellationToken).ConfigureAwait(false));
            var pages = loaded.SelectMany(value => value.Pages).ToArray();
            var loadDiagnostics = loaded.SelectMany(value => value.Diagnostics).ToArray();
            if (pages.Length == 0) return new() { Diagnostics = loadDiagnostics };
            var routes = new SiteRouteTable();
            foreach (var page in pages) routes.Register(SiteRoute.ForDirectoryIndex(page.Url.Trim('/')), page.Id);
            routes.ValidateOrThrow();
            var sources = pages.ToDictionary(page => page.Source, page => page.Code, StringComparer.Ordinal);
            var capturedInputs = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var input in pages.SelectMany(page => page.CapturedInputs))
                AddCapturedInput(capturedInputs, input.File, input.Hash);
            var linkMap = pages.ToDictionary(page => page.Source, page => page.Url, StringComparer.Ordinal);
            var tools = await ToolFingerprintAsync(cancellationToken).ConfigureAwait(false);
            if (previousTools is not null && previousTools != tools) await worker.DisposeAsync().ConfigureAwait(false);
            previousTools = tools;
            executionDependencies = [ContentDependency.FromValue("mdx-toolchain", tools), ContentDependency.FromValue("mdx-options", Hash(JsonSerializer.SerializeToUtf8Bytes(options, MdxJson.Options)))];
            var resolutionFiles = Directory.EnumerateFiles(options.ProjectDirectory, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(file => !IsWithin(context.OutputDirectory, file) && !IsWithin(context.CacheDirectory, file)
                    && !Path.GetFileName(file).StartsWith(".lithosharp-", StringComparison.Ordinal))
                .Where(file => !Path.GetRelativePath(options.ProjectDirectory, file).Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj" or ".git"))
                .Order(StringComparer.Ordinal).ToArray();
            var resolutionCandidates = new List<string>();
            foreach (var file in resolutionFiles)
            {
                var relative = Path.GetRelativePath(options.ProjectDirectory, file);
                resolutionCandidates.Add(relative + (Path.GetFileName(file) is "package.json" or "package-lock.json" or "tsconfig.json" or "jsconfig.json"
                    ? ":" + Hash(await ContentPath.ReadAllBytesAsync(options.ProjectDirectory, file, cancellationToken).ConfigureAwait(false)) : ""));
            }
            var signature = Hash(JsonSerializer.SerializeToUtf8Bytes(new { pages, context.Site.BaseUrl, context.BuildTimestamp,
                tools, resolutionCandidates, options.Environment, options.DeclaredInputFiles, options.Plugins, options.ComponentsModule, options.Hydration, options.StaticComponents, options.CrossReferences }, MdxJson.Options));
            var cacheRoot = Path.Combine(context.CacheDirectory, "mdx");
            EnsureSafeDirectory(cacheRoot);
            CleanupStaleScratch(cacheRoot);
            var cachePath = Path.Combine(cacheRoot, signature + ".json");
            JsonElement result = default;
            if (options.Cacheable && File.Exists(cachePath))
            {
                try
                {
                    using var cached = JsonDocument.Parse(await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false));
                    var envelope = cached.RootElement;
                    if (envelope.ValueKind == JsonValueKind.Object
                        && envelope.TryGetProperty("result", out var candidate)
                        && envelope.TryGetProperty("hash", out var checksum) && checksum.ValueKind == JsonValueKind.String
                        && CachedResultHasShape(candidate)
                        && checksum.GetString() == Hash(JsonSerializer.SerializeToUtf8Bytes(candidate))
                        && await InputsMatchAsync(candidate, capturedInputs, cancellationToken).ConfigureAwait(false)) result = candidate.Clone();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or ArgumentException) { }
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
                        JsonElement response;
                        work.RequestAttempts++;
                        var attemptStarted = Stopwatch.GetTimestamp();
                        var received = false;
                        try
                        {
                        response = await worker.SendAsync(new { protocol = 1, type = "compile", requestId,
                            projectRoot = options.ProjectDirectory, workRoot = scratch, allowWorkWithinProject = true,
                            assetBaseUrl = AssetUrl(context, ""), basePath = new Uri(context.Site.BaseUrl).AbsolutePath,
                            timestamp = context.BuildTimestamp, cacheable = options.Cacheable,
                            resolutionFingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(resolutionCandidates, MdxJson.Options)),
                            pages = pages.Select(page => new { page.Id, page.Source, page.Url, page.Title, page.Description, page.Props, page.Locale, page.Discoverable }),
                            sources, capturedInputs, linkMap, crossReferences = options.CrossReferences.ToDictionary(pair => pair.Key, pair => pair.Value.Value), plugins = options.Plugins, componentsModule = options.ComponentsModule, hydration = options.Hydration, staticComponents = options.StaticComponents }, requestId, cancellationToken).ConfigureAwait(false);
                        work.Observe(response);
                        received = true;
                        }
                        finally
                        {
                            work.RequestMilliseconds += Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds;
                            if (!received) work.Complete = false;
                        }
                        if (response.GetProperty("success").GetBoolean()) { result = response.GetProperty("result").Clone(); break; }
                        if (!await CapturedInputsMatchAsync(capturedInputs, cancellationToken).ConfigureAwait(false))
                            throw MdxWorker.Failure("LSMDX005", "An MDX input changed after its source snapshot was captured; retry the build.");
                        var missing = response.GetProperty("requiredSources").EnumerateArray().Select(value => value.GetString()!).ToArray();
                        if (missing.Length == 0 || sources.Count + missing.Length > 10000)
                            throw new SiteBuildExtensionException(response.GetProperty("diagnostics").EnumerateArray()
                                .Select(ReadWorkerDiagnostic));
                        foreach (var file in missing)
                        {
                            var relative = RelativeSource(file);
                            if (sources.ContainsKey(relative)) throw MdxWorker.Failure("LSMDX003", "Worker requested an already validated source.");
                            var partial = await ReadPartialAsync(file, relative, cancellationToken).ConfigureAwait(false);
                            sources.Add(relative, partial.Code);
                            AddCapturedInput(capturedInputs, file, partial.Hash);
                        }
                    }
                    if (!await InputsMatchAsync(result, capturedInputs, cancellationToken).ConfigureAwait(false))
                        throw MdxWorker.Failure("LSMDX005", "An MDX input changed during compilation; retry the build.");
                }
                finally { DeleteScratch(scratch); }
            }
            var assets = ValidateAssets(result);
            var assetIds = assets.Select(asset => asset.Id).ToHashSet();
            var inputPages = pages.ToDictionary(page => page.Id, StringComparer.Ordinal);
            var results = result.GetProperty("pages").EnumerateArray().ToDictionary(page => page.GetProperty("id").GetString()!, StringComparer.Ordinal);
            if (!results.Keys.Order(StringComparer.Ordinal).SequenceEqual(pages.Select(page => page.Id).Order(StringComparer.Ordinal)))
                throw MdxWorker.Failure("LSMDX003", "Worker page identities do not match the request.");
            foreach (var page in results.Values)
                foreach (var path in page.GetProperty("css").EnumerateArray().Select(value => value.GetString()!).Concat(page.GetProperty("entry").ValueKind == JsonValueKind.Null ? [] : new[] { page.GetProperty("entry").GetString()! }))
                    if (!assetIds.Contains(AssetId(path))) throw MdxWorker.Failure("LSMDX003", "A page references an unknown bundle.");
            if (!hit && options.Cacheable)
            {
                var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary,
                        JsonSerializer.SerializeToUtf8Bytes(new { hash = Hash(JsonSerializer.SerializeToUtf8Bytes(result)), result }),
                        cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, cachePath, overwrite: true);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                    && exception is IOException or UnauthorizedAccessException or ArgumentException
                        or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    // The bridge cache only saves work on later builds; it must not fail this one.
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                }
            }
            IReadOnlyList<string> rebundledPageIds = hit
                || !result.TryGetProperty("rebundledPages", out var rebundled)
                || rebundled.ValueKind != JsonValueKind.Array
                ? []
                : rebundled.EnumerateArray().Select(value => value.GetString()!).ToArray();
            Metrics = new() { WorkerStarts = worker.Starts - starts, CacheHit = hit, CompiledModules = hit ? 0 : result.GetProperty("compiledModules").GetInt32(),
                RenderedPages = hit ? 0 : result.GetProperty("renderedPages").GetInt32(), BundledPages = hit ? 0 : result.GetProperty("bundledPages").GetInt32(),
                RebundledPages = rebundledPageIds.Count, RebundledPageIds = rebundledPageIds,
                WorkerMilliseconds = hit ? 0 : result.GetProperty("timings").GetProperty("totalMilliseconds").GetDouble(),
                ServerBundleMilliseconds = hit ? 0 : result.GetProperty("timings").GetProperty("serverBundleMilliseconds").GetDouble(),
                RenderMilliseconds = hit ? 0 : result.GetProperty("timings").GetProperty("renderMilliseconds").GetDouble(),
                BrowserBundleMilliseconds = hit ? 0 : result.GetProperty("timings").GetProperty("browserBundleMilliseconds").GetDouble(),
                NodeHeapUsedBytes = hit ? 0 : result.GetProperty("memory").GetProperty("heapUsed").GetInt64(),
                Work = work.Snapshot(worker.Starts - starts) };
            inspection = JsonSerializer.SerializeToElement(new { kind = "mdx", protocol = 1, node = "24.13.0", mdx = "3.1.1", react = "19.2.4", esbuild = "0.28.2", metrics = Metrics,
                pages = results.Values.Select(page => new { id = page.GetProperty("id").GetString(), entry = page.GetProperty("entry"), hydration = page.GetProperty("hydration"),
                    fallback = page.GetProperty("fallback"), islands = page.GetProperty("islands"), publicProps = inputPages[page.GetProperty("id").GetString()!].Props }),
                assets = assets.Select(asset => new { asset.Id, asset.RelativeOutputPath, asset.ReferencedAssetIds }),
                modules = result.GetProperty("inputs").EnumerateArray().Select(input => new { file = IsWithin(options.ProjectDirectory, input.GetProperty("file").GetString()!)
                    ? RelativeSource(input.GetProperty("file").GetString()!) : "@worker/" + Path.GetRelativePath(options.WorkerDirectory, input.GetProperty("file").GetString()!).Replace('\\', '/'), hash = input.GetProperty("hash").GetString() }) }, MdxJson.Options);
            return new() { Assets = assets, ContentCollections = loaded.Select(value => value.Create(results)).ToArray(), Diagnostics = loadDiagnostics };
        }
        finally
        {
            Metrics = Metrics with { Work = work.Snapshot(worker.Starts - starts) };
            gate.Release();
        }
    }

    private static bool CachedResultHasShape(JsonElement result)
    {
        static bool Has(JsonElement value, string name, JsonValueKind kind) =>
            value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(name, out var field) && field.ValueKind == kind;
        static bool Strings(JsonElement value) => value.ValueKind == JsonValueKind.Array
            && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String);
        static bool NullableString(JsonElement value, string name) =>
            Has(value, name, JsonValueKind.String) || Has(value, name, JsonValueKind.Null);

        if (!Has(result, "inputs", JsonValueKind.Array) || !Has(result, "assets", JsonValueKind.Array)
            || !Has(result, "pages", JsonValueKind.Array)) return false;
        foreach (var input in result.GetProperty("inputs").EnumerateArray())
            if (!Has(input, "file", JsonValueKind.String) || !Has(input, "hash", JsonValueKind.String)) return false;
        foreach (var asset in result.GetProperty("assets").EnumerateArray())
            if (!Has(asset, "path", JsonValueKind.String) || !Has(asset, "bytes", JsonValueKind.String)
                || !Has(asset, "hash", JsonValueKind.String) || !Has(asset, "imports", JsonValueKind.Array)
                || !Strings(asset.GetProperty("imports"))) return false;
        foreach (var page in result.GetProperty("pages").EnumerateArray())
            if (!Has(page, "id", JsonValueKind.String) || !Has(page, "html", JsonValueKind.String)
                || !NullableString(page, "text") || !NullableString(page, "entry")
                || !Has(page, "css", JsonValueKind.Array) || !Strings(page.GetProperty("css"))
                || !Has(page, "headings", JsonValueKind.Array) || !Has(page, "links", JsonValueKind.Array)
                || !Has(page, "islands", JsonValueKind.Array) || !Has(page, "hydration", JsonValueKind.String)
                || !NullableString(page, "fallback")) return false;
        return true;
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

    private static SiteDiagnostic ReadWorkerDiagnostic(JsonElement diagnostic)
    {
        var file = diagnostic.TryGetProperty("file", out var fileValue) && fileValue.ValueKind == JsonValueKind.String
            ? fileValue.GetString()
            : null;
        var line = diagnostic.TryGetProperty("line", out var lineValue) && lineValue.ValueKind == JsonValueKind.Number
            && lineValue.TryGetInt32(out var lineNumber) && lineNumber > 0 ? lineNumber : (int?)null;
        var column = diagnostic.TryGetProperty("column", out var columnValue) && columnValue.ValueKind == JsonValueKind.Number
            && columnValue.TryGetInt32(out var columnNumber) && columnNumber > 0 ? columnNumber : (int?)null;
        SiteSourceLocation? location = string.IsNullOrEmpty(file)
            ? null
            : line is null ? new SiteSourceLocation(file) : new SiteSourceLocation(file, line, column);
        return new SiteDiagnostic(
            diagnostic.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()! : "LSMDX001",
            SiteDiagnosticSeverity.Error,
            diagnostic.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString()! : "MDX worker reported an unspecified error.",
            location);
    }

    private void AddCapturedInput(Dictionary<string, string> inputs, string file, string hash)
    {
        var relative = RelativeSource(file);
        if (inputs.TryGetValue(relative, out var existing) && existing != hash)
            throw MdxWorker.Failure("LSMDX005", "An MDX input changed between source captures; retry the build.");
        inputs[relative] = hash;
    }

    private async Task<bool> CapturedInputsMatchAsync(Dictionary<string, string> capturedInputs, CancellationToken cancellationToken)
    {
        foreach (var (relative, hash) in capturedInputs)
        {
            try
            {
                var bytes = await ContentPath.ReadAllBytesAsync(options.ProjectDirectory,
                    Path.GetFullPath(relative, options.ProjectDirectory), cancellationToken).ConfigureAwait(false);
                if (Hash(bytes) != hash) return false;
            }
            catch (IOException) { return false; }
        }
        return true;
    }

    private async Task<bool> InputsMatchAsync(JsonElement result, Dictionary<string, string> capturedInputs, CancellationToken cancellationToken)
    {
        var remaining = new Dictionary<string, string>(capturedInputs, capturedInputs.Comparer);
        foreach (var input in result.GetProperty("inputs").EnumerateArray())
        {
            var file = input.GetProperty("file").GetString()!;
            var root = IsWithin(options.ProjectDirectory, file) ? options.ProjectDirectory : options.WorkerDirectory;
            if (!IsWithin(root, file)) throw MdxWorker.Failure("LSMDX003", "Worker input is outside its declared roots.");
            if (IsWithin(options.ProjectDirectory, file) && remaining.Remove(RelativeSource(file), out var captured)
                && captured != input.GetProperty("hash").GetString()) return false;
            try
            {
                await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(root, file, asynchronous: true);
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) != input.GetProperty("hash").GetString()) return false;
            }
            catch (IOException) { return false; }
        }
        return remaining.Count == 0;
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
            await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(root, file, asynchronous: true);
            hash.AppendData(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private async Task<(string Code, string Hash)> ReadPartialAsync(string file, string relative, CancellationToken cancellationToken)
    {
        if (!MdxContentCollectionLoader<object>.IsPartial(relative))
            throw MdxWorker.Failure("LSMDX006", "A published entry cannot import an excluded or unregistered document. Move reusable content to an underscore-prefixed partial.", relative);
        var bytes = await ContentPath.ReadAllBytesAsync(options.ProjectDirectory, file, cancellationToken).ConfigureAwait(false);
        var hash = Hash(bytes);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (!text.TrimStart('\uFEFF').StartsWith("---", StringComparison.Ordinal)) return (text, hash);
        var parsed = MarkdownContentCollectionLoader<object>.MarkdownSourceDocument.Parse(text, relative, cancellationToken);
        if (!parsed.IsSuccess) throw new SiteBuildExtensionException(parsed.Diagnostics);
        var document = parsed.Value!;
        var yaml = MarkdownContentCollectionLoader<object>.ParseYaml(document.Yaml, relative, document.YamlStartLine, cancellationToken);
        if (!yaml.IsSuccess) throw new SiteBuildExtensionException(yaml.Diagnostics);
        return (new string('\n', text.AsSpan(0, text.Length - document.Body.Length).Count('\n')) + document.Body, hash);
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
    /// <summary>
    /// A killed host leaves worker scratch directories and partial cache writes behind. Remove
    /// only leftovers old enough that no live build can still own them.
    /// </summary>
    private static void CleanupStaleScratch(string cacheRoot)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(cacheRoot, "work-*"))
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                        || Directory.GetLastWriteTimeUtc(directory) >= cutoff
                        || ContainsReparsePoint(directory))
                    {
                        continue;
                    }

                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Reclaiming leftovers must never fail a build.
                }
            }

            foreach (var file in Directory.EnumerateFiles(cacheRoot, "*.tmp"))
            {
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0
                        && File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Reclaiming leftovers must never fail a build.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Reclaiming leftovers must never fail a build.
        }
    }

    private static bool ContainsReparsePoint(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }

        return false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (!disposed) { disposed = true; await worker.DisposeAsync().ConfigureAwait(false); } }
        finally { gate.Release(); }
    }
    private sealed class WorkAccumulator
    {
        internal int RequestAttempts;
        internal double RequestMilliseconds;
        internal bool Complete = true;
        private readonly long[] counts = new long[7];
        private readonly double[] durations = new double[6];
        private static readonly string[] CountKeys = ["compiledModules", "renderedPages", "mdxCompileInvocations", "renderInvocations", "esbuildInvocations", "browserBuildInvocations", "browserEntryBuildAttempts"];
        private static readonly string[] DurationKeys = ["totalMilliseconds", "serverBundleMilliseconds", "browserBundleMilliseconds", "liveRuntimeMilliseconds", "pluginBundleMilliseconds", "renderMilliseconds"];

        internal void Observe(JsonElement response)
        {
            JsonElement report = default;
            if (response.ValueKind == JsonValueKind.Object)
            {
                if (response.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
                    && response.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                    result.TryGetProperty("workMetrics", out report);
                else response.TryGetProperty("workMetrics", out report);
            }
            if (report.ValueKind != JsonValueKind.Object || !report.TryGetProperty("schema", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1
                || !report.TryGetProperty("complete", out var complete) || complete.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            { Complete = false; return; }
            var nextCounts = new long[counts.Length];
            var nextDurations = new double[durations.Length];
            for (var i = 0; i < CountKeys.Length; i++)
            {
                if (!report.TryGetProperty(CountKeys[i], out var value) || value.ValueKind != JsonValueKind.Number
                    || !value.TryGetInt64(out var count) || count < 0 || count > int.MaxValue)
                { Complete = false; return; }
                nextCounts[i] = counts[i] + count;
            }
            for (var i = 0; i < DurationKeys.Length; i++)
            {
                if (!report.TryGetProperty(DurationKeys[i], out var value) || value.ValueKind != JsonValueKind.Number
                    || !value.TryGetDouble(out var elapsed) || elapsed < 0 || !double.IsFinite(elapsed)
                    || !double.IsFinite(durations[i] + elapsed))
                { Complete = false; return; }
                nextDurations[i] = durations[i] + elapsed;
            }
            Array.Copy(nextCounts, counts, counts.Length);
            Array.Copy(nextDurations, durations, durations.Length);
            Complete &= complete.GetBoolean();
        }

        internal MdxWorkerWorkMetrics Snapshot(int starts) => new()
        {
            RequestAttempts = RequestAttempts, RequestMilliseconds = RequestMilliseconds,
            WorkerStarts = starts, HasCompleteReports = Complete,
            CompiledModules = counts[0], RenderedPages = counts[1], MdxCompileInvocations = counts[2], RenderInvocations = counts[3],
            EsbuildInvocations = counts[4], BrowserBuildInvocations = counts[5], BrowserEntryBuildAttempts = counts[6],
            WorkerMilliseconds = durations[0], ServerBundleMilliseconds = durations[1], BrowserBundleMilliseconds = durations[2],
            LiveRuntimeMilliseconds = durations[3], PluginBundleMilliseconds = durations[4], RenderMilliseconds = durations[5],
        };
    }

    private sealed record Page(string Id, string Source, string Code, string Url, string Title, string? Description, JsonElement Props, string Locale, bool Discoverable, IReadOnlyList<CapturedContentInput> CapturedInputs);
    private sealed record Loaded(Page[] Pages, Func<Dictionary<string, JsonElement>, SiteContentCollection> Create, IReadOnlyList<SiteDiagnostic> Diagnostics);
}
