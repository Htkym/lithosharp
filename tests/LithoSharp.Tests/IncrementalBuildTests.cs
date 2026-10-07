using System.Security.Cryptography;
using System.Text.Json;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

[NotInParallel]
public sealed class IncrementalBuildTests
{
    private static readonly DateTimeOffset FixedBuildTimestamp =
        new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task TrailingOutputSeparator_UsesSiblingDefaultCache()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Output(workspace) + Path.DirectorySeparatorChar;
        var options = new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp };
        var generator = new SiteGenerator();
        await generator.GenerateWithOptionsAsync(Site(), [], output, true, null, options, CancellationToken.None);
        var result = await generator.GenerateWithOptionsAsync(Site(), [], output, false, null, options, CancellationToken.None);
        await Assert.That(result.BuildReport.CacheMissCount).IsEqualTo(0);
        await Assert.That(Directory.Exists(Path.Combine(workspace.Root, ".lithosharp"))).IsTrue();
    }

    [Test]
    public async Task TypedRenderer_ReusesUnchangedFingerprintAndInvalidatesChangedFingerprint()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Output(workspace);
        var calls = 0;
        await GenerateAsync(output, workspace.Root, [Entry("one", "source:1")],
            Registration(() => Interlocked.Increment(ref calls), "renderer:1"), clean: true);

        calls = 0;
        var result = await GenerateAsync(output, workspace.Root, [Entry("one", "source:1")],
            Registration(() => Interlocked.Increment(ref calls), "renderer:1"), clean: false);

        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(PageNode(result, "one").CacheHit).IsTrue();

        calls = 0;
        result = await GenerateAsync(output, workspace.Root, [Entry("one", "source:1")],
            Registration(() => Interlocked.Increment(ref calls), "renderer:2"), clean: false);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(PageNode(result, "one").CacheHit).IsFalse();
    }

    [Test]
    public async Task SourceChange_RegeneratesChangedPageAndDependentIndex_WithCleanEquivalentBytes()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Output(workspace);
        var entries = new[] { Entry("one", "source:1"), Entry("two", "source:1") };
        await GenerateAsync(output, workspace.Root, entries, Registration(null, "renderer:1"), clean: true);

        var changed = new[] { Entry("one", "source:2", "changed"), entries[1] };
        var incremental = await GenerateAsync(output, workspace.Root, changed,
            Registration(null, "renderer:1"), clean: false);
        var cleanOutput = Path.Combine(workspace.Root, "clean");
        await GenerateAsync(cleanOutput, workspace.Root, changed,
            Registration(null, "renderer:1"), clean: true);

        await Assert.That(PageNode(incremental, "one").CacheHit).IsFalse();
        await Assert.That(PageNode(incremental, "two").CacheHit).IsTrue();
        await Assert.That(incremental.BuildReport.Nodes.Single(node => node.NodeId == "index:search").CacheHit)
            .IsFalse();
        await Assert.That(await SnapshotAsync(output)).IsEquivalentTo(await SnapshotAsync(cleanOutput));
    }

    [Test]
    public async Task RouteRename_RemovesOldArtifactAndPreservesUserFile()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Output(workspace);
        await GenerateAsync(output, workspace.Root, [Entry("one", "source:1")],
            Registration(null, "renderer:1"), clean: true, routePrefix: "old");
        var userFile = Path.Combine(output, "user.txt");
        await File.WriteAllTextAsync(userFile, "mine");

        var result = await GenerateAsync(output, workspace.Root, [Entry("one", "source:1")],
            Registration(null, "renderer:1"), clean: false, routePrefix: "new");

        await Assert.That(File.Exists(Path.Combine(output, "old", "one.html"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(output, "new", "one.html"))).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(userFile)).IsEqualTo("mine");
        await Assert.That(result.BuildReport.StaleRemovedArtifacts).Contains("old/one.html");

        result = await GenerateAsync(output, workspace.Root, [],
            Registration(null, "renderer:1"), clean: false, routePrefix: "new");
        await Assert.That(File.Exists(Path.Combine(output, "new", "one.html"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(userFile)).IsEqualTo("mine");
        await Assert.That(result.BuildReport.StaleRemovedArtifacts).Contains("new/one.html");
    }

    [Test]
    public async Task MissingRendererFingerprint_AlwaysCallsTypedRenderer()
    {
        using var workspace = new TemporaryWorkspace();
        var calls = 0;
        await GenerateAsync(Output(workspace), workspace.Root, [Entry("one", "source:1")],
            Registration(() => Interlocked.Increment(ref calls), null), clean: true);

        await GenerateAsync(Output(workspace), workspace.Root, [Entry("one", "source:1")],
            Registration(() => Interlocked.Increment(ref calls), null), clean: false);

        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    [Arguments("manifest")]
    [Arguments("artifact")]
    public async Task CorruptCacheOrArtifact_IsAMissAndRecovers(string target)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Output(workspace);
        await GenerateAsync(output, workspace.Root, [Entry("one", "source:1")],
            Registration(null, "renderer:1"), clean: true);
        var cache = Cache(workspace);
        if (target == "manifest")
            await File.WriteAllTextAsync(Directory.EnumerateFiles(cache, "*.json", SearchOption.AllDirectories).Single(), "corrupt");
        else
            await File.WriteAllTextAsync(Path.Combine(output, "pages", "one.html"), "corrupt");
        var calls = 0;

        var result = await GenerateAsync(output, workspace.Root, [Entry("one", "source:1")],
            Registration(() => Interlocked.Increment(ref calls), "renderer:1"), clean: false);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(PageNode(result, "one").CacheHit).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "pages", "one.html")))
            .Contains("Body one");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MissingOrCorruptDerivedBody_MaterializesOutputBeforeRenderer(bool missing)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Output(workspace);
        var entries = new[] { Entry("one", "source:1") };
        var initial = await GenerateAsync(output, workspace.Root, entries,
            Registration(null, "renderer:1"), clean: true);
        var bodyPath = await DerivedBodyPathAsync(Cache(workspace), initial, "one");
        var originalBody = await File.ReadAllTextAsync(bodyPath);
        await File.WriteAllTextAsync(Path.Combine(output, "unowned.txt"), "preserve this file");
        if (missing) File.Delete(bodyPath);
        else await File.WriteAllTextAsync(bodyPath, "corrupt");
        var renderedAfterCopy = 0;
        var calls = 0;

        var repaired = await GenerateAsync(output, workspace.Root, entries, Registration(() =>
        {
            Interlocked.Increment(ref calls);
            if (StagingContainsSentinel(workspace.Root)) Interlocked.Increment(ref renderedAfterCopy);
        }, "renderer:1"), clean: false);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(renderedAfterCopy).IsEqualTo(1);
        await Assert.That(PageNode(repaired, "one").CacheHit).IsFalse();
        await Assert.That(PageNode(repaired, "one").CacheMissReason)
            .IsEqualTo("The rendered body is missing or corrupt.");
        await AssertRepairedDerivedBodyAsync(output, repaired, "one", bodyPath, originalBody);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "unowned.txt")))
            .IsEqualTo("preserve this file");
        await Assert.That(Directory.EnumerateDirectories(workspace.Root, ".lithosharp-staging-*"))
            .IsEmpty();

        calls = 0;
        var noOp = await GenerateAsync(output, workspace.Root, entries,
            Registration(() => Interlocked.Increment(ref calls), "renderer:1"), clean: false);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(noOp.BuildReport.CacheMissCount).IsEqualTo(0);

        var cleanOutput = Path.Combine(workspace.Root, "clean");
        await GenerateAsync(cleanOutput, workspace.Root, entries,
            Registration(null, "renderer:1"), clean: true);
        await File.WriteAllTextAsync(Path.Combine(cleanOutput, "unowned.txt"), "preserve this file");
        await Assert.That(await SnapshotAsync(output)).IsEquivalentTo(await SnapshotAsync(cleanOutput));
    }

    [Test]
    public async Task ParallelBodyAndArtifactCorruption_MaterializesBeforeBothMissRenderers()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Output(workspace);
        var entries = new[] { Entry("one", "source:1"), Entry("two", "source:1") };
        var initial = await GenerateAsync(output, workspace.Root, entries,
            Registration(null, "renderer:1", threadSafe: true), clean: true, parallelism: 2);
        var bodyPath = await DerivedBodyPathAsync(Cache(workspace), initial, "one");
        var originalBody = await File.ReadAllTextAsync(bodyPath);
        await File.WriteAllTextAsync(bodyPath, "corrupt");
        await File.WriteAllTextAsync(Path.Combine(output, "pages", "two.html"), "corrupt");
        await File.WriteAllTextAsync(Path.Combine(output, "unowned.txt"), "preserve this file");
        var calls = 0;
        var renderedAfterCopy = 0;

        var repaired = await GenerateAsync(output, workspace.Root, entries, Registration(() =>
        {
            Interlocked.Increment(ref calls);
            if (StagingContainsSentinel(workspace.Root)) Interlocked.Increment(ref renderedAfterCopy);
        }, "renderer:1", threadSafe: true), clean: false, parallelism: 2);

        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(renderedAfterCopy).IsEqualTo(2);
        await Assert.That(PageNode(repaired, "one").CacheHit).IsFalse();
        await Assert.That(PageNode(repaired, "two").CacheHit).IsFalse();
        await AssertRepairedDerivedBodyAsync(output, repaired, "one", bodyPath, originalBody);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "unowned.txt")))
            .IsEqualTo("preserve this file");
        await Assert.That(Directory.EnumerateDirectories(workspace.Root, ".lithosharp-staging-*"))
            .IsEmpty();

        calls = 0;
        var noOp = await GenerateAsync(output, workspace.Root, entries,
            Registration(() => Interlocked.Increment(ref calls), "renderer:1", threadSafe: true),
            clean: false, parallelism: 2);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(noOp.BuildReport.CacheMissCount).IsEqualTo(0);

        var cleanOutput = Path.Combine(workspace.Root, "clean");
        await GenerateAsync(cleanOutput, workspace.Root, entries,
            Registration(null, "renderer:1", threadSafe: true), clean: true, parallelism: 2);
        await File.WriteAllTextAsync(Path.Combine(cleanOutput, "unowned.txt"), "preserve this file");
        await Assert.That(await SnapshotAsync(output)).IsEquivalentTo(await SnapshotAsync(cleanOutput));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RendererFailureOrCancellation_KeepsPreviousOutput(bool cancel)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Output(workspace);
        await GenerateAsync(output, workspace.Root, [Entry("one", "source:1")],
            Registration(null, "renderer:1"), clean: true);
        var before = await SnapshotAsync(output);
        using var cancellation = new CancellationTokenSource();
        var registration = Registration(() =>
        {
            if (cancel) cancellation.Cancel();
            else throw new TestRendererException();
        }, "renderer:2");

        Exception? failure = null;
        try
        {
            await GenerateAsync(output, workspace.Root, [Entry("one", "source:1")], registration,
                clean: false, cancellationToken: cancellation.Token);
        }
        catch (Exception exception) when (exception is TestRendererException or OperationCanceledException)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(cancel ? failure is OperationCanceledException : failure is TestRendererException).IsTrue();
        await Assert.That(await SnapshotAsync(output)).IsEquivalentTo(before);
    }

    [Test]
    public async Task ThreadSafeRendererHonorsParallelism_AndDefaultIsSerial()
    {
        using var workspace = new TemporaryWorkspace();
        var entries = Enumerable.Range(0, 6).Select(index => Entry($"p{index}", $"source:{index}")).ToArray();
        var parallel = new ConcurrencyProbe();
        await GenerateAsync(Path.Combine(workspace.Root, "parallel"), workspace.Root, entries,
            Registration(parallel.Invoke, "renderer:1", threadSafe: true), clean: true, parallelism: 4);
        var serial = new ConcurrencyProbe();
        await GenerateAsync(Path.Combine(workspace.Root, "serial"), workspace.Root, entries,
            Registration(serial.Invoke, "renderer:1", threadSafe: true), clean: true);
        var unsafeRenderer = new ConcurrencyProbe();
        await GenerateAsync(Path.Combine(workspace.Root, "unsafe"), workspace.Root, entries,
            Registration(unsafeRenderer.Invoke, "renderer:1"), clean: true, parallelism: 4);

        await Assert.That(parallel.Maximum).IsGreaterThan(1);
        await Assert.That(parallel.Maximum).IsLessThanOrEqualTo(4);
        await Assert.That(serial.Maximum).IsEqualTo(1);
        await Assert.That(unsafeRenderer.Maximum).IsEqualTo(1);
    }

    [Test]
    public async Task MarkdownNavigationChange_InvalidatesDependentPages()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Output(workspace);
        await GenerateAsync(output, workspace.Root, [], Registration(null, "renderer:1"), clean: true,
            posts: [Post("one", "Old title"), Post("two", "Second")], template: new DocsSiteTemplate());

        var result = await GenerateAsync(output, workspace.Root, [], Registration(null, "renderer:1"), clean: false,
            posts: [Post("one", "New title"), Post("two", "Second")], template: new DocsSiteTemplate());

        await Assert.That(result.BuildReport.Nodes.Single(node => node.NodeId == "navigation:docs").CacheHit).IsFalse();
        await Assert.That(result.BuildReport.Nodes.Single(node => node.NodeId == "page:markdown:posts/two.html").CacheHit)
            .IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "two.html")))
            .Contains("New title");
    }

    [Test]
    public async Task SocialImageSourceAddedThenRemoved_UpdatesOutputWithoutLeavingArtifacts()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Output(workspace);
        var faviconRoot = Path.Combine(workspace.Root, "favicon");
        Directory.CreateDirectory(faviconRoot);
        await GenerateAsync(output, workspace.Root, [], Registration(null, "renderer:1"), clean: true,
            faviconRoot: faviconRoot);
        var socialImage = Path.Combine(output, "assets", "social", "og-default.png");
        await Assert.That(File.Exists(socialImage)).IsFalse();

        var source = Path.Combine(faviconRoot, "android-chrome-192x192.png");
        await File.WriteAllBytesAsync(source, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        await GenerateAsync(output, workspace.Root, [], Registration(null, "renderer:1"), clean: false,
            faviconRoot: faviconRoot);
        await Assert.That(File.Exists(socialImage)).IsTrue();

        File.Delete(source);
        await GenerateAsync(output, workspace.Root, [], Registration(null, "renderer:1"), clean: false,
            faviconRoot: faviconRoot);
        await Assert.That(File.Exists(socialImage)).IsFalse();
    }

    [Test]
    public async Task TransformReportReflectsWhetherHandlerActuallyExecuted()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "source.txt"), "source");
        var source = new SiteAsset("source", workspace.Root, "source.txt", "assets/source.txt");
        var transformed = new SiteAssetOutput("result", "assets/result.txt");

        var uncachedCalls = 0;
        var uncachedTransform = Transform(() => Interlocked.Increment(ref uncachedCalls));
        var uncachedOutput = Path.Combine(workspace.Root, "uncached-output");
        await GenerateAsync(uncachedOutput, workspace.Root, [], Registration(null, "renderer:1"), clean: true,
            assets: [source], transforms: [uncachedTransform]);
        var uncached = await GenerateAsync(uncachedOutput, workspace.Root, [], Registration(null, "renderer:1"), clean: false,
            assets: [source], transforms: [uncachedTransform]);

        var cachedCalls = 0;
        var cachedTransform = Transform(() => Interlocked.Increment(ref cachedCalls));
        var cachedOutput = Path.Combine(workspace.Root, "cached-output");
        var transformCache = Path.Combine(workspace.Root, "transform-cache");
        await GenerateAsync(cachedOutput, workspace.Root, [], Registration(null, "renderer:1"), clean: true,
            assets: [source], transforms: [cachedTransform], assetCacheDirectory: transformCache);
        var cached = await GenerateAsync(cachedOutput, workspace.Root, [], Registration(null, "renderer:1"), clean: false,
            assets: [source], transforms: [cachedTransform], assetCacheDirectory: transformCache);

        await Assert.That(uncachedCalls).IsEqualTo(2);
        await Assert.That(uncached.BuildReport.Nodes.Single(node => node.NodeId == "asset-transform:copy").CacheHit)
            .IsFalse();
        await Assert.That(cachedCalls).IsEqualTo(1);
        await Assert.That(cached.BuildReport.Nodes.Single(node => node.NodeId == "asset-transform:copy").CacheHit)
            .IsTrue();

        SiteAssetTransform Transform(Action count) => new(
            "copy", "copy:1", [source], [transformed], async (context, token) =>
            {
                count();
                using var reader = new StreamReader(context.OpenRead(source));
                await context.WriteAsync(transformed,
                    System.Text.Encoding.UTF8.GetBytes(await reader.ReadToEndAsync(token)), token);
            });
    }

    [Test]
    public async Task InvalidRoute_FailsPreflightWithoutCallingRenderer()
    {
        using var workspace = new TemporaryWorkspace();
        var calls = 0;

        await Assert.That(async () => await GenerateAsync(Output(workspace), workspace.Root,
                [Entry("one", "source:1")],
                Registration(() => Interlocked.Increment(ref calls), "renderer:1"),
                clean: true, routePrefix: "../invalid"))
            .Throws<SiteRouteValidationException>();
        await Assert.That(calls).IsEqualTo(0);
    }

    private static bool StagingContainsSentinel(string parentRoot)
    {
        var staging = Directory.EnumerateDirectories(parentRoot, ".lithosharp-staging-*").Single();
        var sentinel = Path.Combine(staging, "unowned.txt");
        return File.Exists(sentinel) && File.ReadAllText(sentinel) == "preserve this file";
    }

    private static async Task<string> DerivedBodyPathAsync(
        string cacheRoot, SiteGenerationResult initial, string id)
    {
        var buildPath = Directory.EnumerateFiles(cacheRoot, "*.json", SearchOption.AllDirectories).Single();
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(buildPath));
        var nodeId = PageNode(initial, id).NodeId;
        var node = manifest.RootElement.GetProperty("Nodes").EnumerateArray()
            .Single(node => node.GetProperty("NodeId").GetString() == nodeId);
        var hash = node.GetProperty("DerivedBodyHash").GetString()!;
        return Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(buildPath))!, "blobs", hash + ".utf8");
    }

    private static async Task AssertRepairedDerivedBodyAsync(
        string output, SiteGenerationResult repaired, string id, string bodyPath, string originalBody)
    {
        using var outputManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(output, ".lithosharp-output-manifest.json")));
        var digest = outputManifest.RootElement.GetProperty("buildCache").GetString()!;
        await Assert.That(digest.Length).IsEqualTo(64);
        var partition = Path.GetDirectoryName(Path.GetDirectoryName(bodyPath))!;
        var buildBytes = await File.ReadAllBytesAsync(Path.Combine(partition, "builds", digest + ".json"));
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(buildBytes))).IsEqualTo(digest);
        using var build = JsonDocument.Parse(buildBytes);
        var nodeId = PageNode(repaired, id).NodeId;
        var node = build.RootElement.GetProperty("Nodes").EnumerateArray()
            .Single(node => node.GetProperty("NodeId").GetString() == nodeId);
        var bodyHash = node.GetProperty("DerivedBodyHash").GetString();
        await Assert.That(bodyHash).IsEqualTo(Path.GetFileNameWithoutExtension(bodyPath));
        var bodyBytes = await File.ReadAllBytesAsync(bodyPath);
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bodyBytes))).IsEqualTo(bodyHash);
        await Assert.That(await File.ReadAllTextAsync(bodyPath)).IsEqualTo(originalBody);
    }

    private static RendererSettings Registration(
        Action? onRender, string? fingerprint, bool threadSafe = false) =>
        new(onRender, fingerprint, threadSafe);

    private static ContentPageRenderer<Row, string> CollectionRenderer(Action? onRender) =>
        (entry, context) =>
        {
            onRender?.Invoke();
            return context.RenderDocument($"<h1>{Html.Encode(entry.FrontMatter.Title)}</h1><p>{Html.Encode(entry.Body)}</p>");
        };

    private static async Task<SiteGenerationResult> GenerateAsync(
        string output, string inputRoot, IReadOnlyList<ContentEntry<Row, string>> entries,
        RendererSettings renderer, bool clean, string routePrefix = "pages",
        int parallelism = 1, IReadOnlyList<MarkdownPost>? posts = null,
        string? faviconRoot = null, ISiteTemplate? template = null,
        IReadOnlyList<SiteAsset>? assets = null,
        IReadOnlyList<SiteAssetTransform>? transforms = null,
        string? assetCacheDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var collection = new ContentCollection<Row, string>(
            new ContentCollectionId("typed"), inputRoot, entries,
            entry => SiteRoute.ForFile($"{routePrefix}/{entry.Id.Value}.html"),
            entry => new PageMetadata(entry.FrontMatter.Title, entry.Body),
            transformationId: new ContentTransformationId("transform:1"), isCacheable: true);
        var registration = new SiteContentCollection<Row, string>(collection, CollectionRenderer(renderer.OnRender))
        {
            RendererFingerprint = renderer.Fingerprint,
            IsThreadSafe = renderer.IsThreadSafe,
        };
        return await new SiteGenerator().GenerateWithOptionsAsync(
            Site(), posts ?? [], output, clean, new SiteCustomization
            {
                Template = template ?? new BlogSiteTemplate(),
                FaviconSourceDirectory = faviconRoot,
            },
            new SiteGenerationOptions
            {
                BuildTimestamp = FixedBuildTimestamp,
                BuildCacheDirectory = Path.Combine(Path.GetDirectoryName(output)!, "cache"),
                ContentCollections = [registration],
                MaxDegreeOfParallelism = parallelism,
                Assets = assets ?? [],
                AssetTransforms = transforms ?? [],
                AssetCacheDirectory = assetCacheDirectory,
            }, cancellationToken);
    }

    private static ContentEntry<Row, string> Entry(string id, string fingerprint, string? body = null) =>
        new(new ContentEntryId(id), $"{id}.data", fingerprint,
            new Row(id, $"Title {id}"), body ?? $"Body {id}");

    private static MarkdownPost Post(string slug, string title) =>
        new($"{slug}.md", slug, new PostFrontMatter
        {
            Title = title,
            Date = FixedBuildTimestamp.AddDays(-1),
            Summary = $"Summary {slug}",
            Tags = [],
        }, $"Body {slug}", $"posts/{slug}.html");

    private static SiteBuildReportNode PageNode(SiteGenerationResult result, string id) =>
        result.BuildReport.Nodes.Single(node => node.NodeId.EndsWith($":{id.Length}:{id}", StringComparison.Ordinal));

    private static async Task<IReadOnlyList<(string Path, string Bytes)>> SnapshotAsync(string root) =>
        await Task.WhenAll(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(async path => (Path.GetRelativePath(root, path).Replace('\\', '/'),
                Convert.ToBase64String(await File.ReadAllBytesAsync(path)))));

    private static string Output(TemporaryWorkspace workspace) => Path.Combine(workspace.Root, "output");

    private static string Cache(TemporaryWorkspace workspace) => Path.Combine(workspace.Root, "cache");

    private static SiteSettings Site() => new()
    {
        Title = "Incremental Site",
        Description = "Incremental build tests.",
        BaseUrl = "https://example.test/",
        Language = "en",
        TimeZone = "UTC",
    };

    private sealed record Row(string Id, string Title);

    private sealed record RendererSettings(Action? OnRender, string? Fingerprint, bool IsThreadSafe);

    private sealed class TestRendererException : Exception
    {
    }

    private sealed class ConcurrencyProbe
    {
        private int current;
        private int maximum;

        public int Maximum => Volatile.Read(ref maximum);

        public void Invoke()
        {
            var active = Interlocked.Increment(ref current);
            int observed;
            while (active > (observed = Volatile.Read(ref maximum)))
                Interlocked.CompareExchange(ref maximum, active, observed);
            Thread.Sleep(40);
            Interlocked.Decrement(ref current);
        }
    }
}
