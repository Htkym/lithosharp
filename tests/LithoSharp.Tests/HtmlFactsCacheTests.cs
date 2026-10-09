using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Diagnostics;
using LithoSharp.HtmlParsing;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class HtmlFactsCacheTests
{
    private const string BaseUrl = "https://example.test/sub/";
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static SiteSettings Site => new() { Title = "Cached site", Description = "Description", BaseUrl = BaseUrl, TimeZone = "UTC" };
    private static SiteGenerationOptions Options => new() { BuildTimestamp = Timestamp, Quality = new(checkOrphans: false) };

    [Test]
    public async Task PipelineNoOpParsesNothingAndCacheOffHasIdenticalOutputAndDiagnostics()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        using var oldCancellation = new CancellationTokenSource();
        var customization = new SiteCustomization { Template = new BlogSiteTemplate() };
        var first = await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, true, customization, Options, oldCancellation.Token);
        oldCancellation.Cancel();
        var snapshot = Snapshot(output);
        var second = await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, false, customization, Options, default);
        await Assert.That(first.QualityReport.HtmlParseCount > 0).IsTrue();
        await Assert.That(second.QualityReport.HtmlParseCount).IsEqualTo(0);
        await Assert.That(second.QualityReport.HtmlFactsCacheHitCount).IsEqualTo(first.QualityReport.HtmlParseCount);
        await Assert.That(second.BuildReport.CacheMissCount).IsEqualTo(0);
        var uncached = await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, false, customization,
            Options with { HtmlFactsCacheEnabled = false }, default);
        await Assert.That(uncached.QualityReport.HtmlParseCount).IsEqualTo(first.QualityReport.HtmlParseCount);
        await Assert.That(uncached.QualityReport.HtmlFactsCacheHitCount).IsEqualTo(0);
        await Assert.That(uncached.QualityReport.Format(SiteDiagnosticFormat.Json)).IsEqualTo(second.QualityReport.Format(SiteDiagnosticFormat.Json));
        await Assert.That(Snapshot(output)).IsEquivalentTo(snapshot);
        Console.WriteLine("HT06_CACHE_PARITY " + System.Text.Json.JsonSerializer.Serialize(new
        {
            firstParse = first.QualityReport.HtmlParseCount, noOpParse = second.QualityReport.HtmlParseCount,
            noOpHit = second.QualityReport.HtmlFactsCacheHitCount, cacheOffParse = uncached.QualityReport.HtmlParseCount,
            outputsEqual = true, diagnosticsEqual = true,
        }));

        var other = Path.Combine(workspace.Root, "other");
        var isolated = await new SiteGenerator().GenerateWithOptionsAsync(Site, [], other, true, customization, Options, default);
        await Assert.That(isolated.QualityReport.HtmlFactsCacheHitCount).IsEqualTo(0);
        await Assert.That(SiteGenerator.ClearCache(output).FileCount > 0).IsTrue();
        var cleared = await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, false, customization, Options, default);
        await Assert.That(cleared.QualityReport.HtmlParseCount).IsEqualTo(first.QualityReport.HtmlParseCount);
        var retained = await new SiteGenerator().GenerateWithOptionsAsync(Site, [], other, false, customization, Options, default);
        await Assert.That(retained.QualityReport.HtmlParseCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("anchor", "LSQ002")]
    [Arguments("route", "LSQ001")]
    [Arguments("asset", "LSQ001")]
    [Arguments("base", "LSQ003")]
    public async Task ChangedTargetsRecheckUnchangedReferrerAndPreserveLastPublication(string change, string diagnostic)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var files = new Dictionary<string, string>
        {
            ["index.html"] = Page("", "Home", "<a href='target.html#old'>Target</a><img src='pixel.png'>"),
            ["target.html"] = Page("target.html", "Target", "<h2 id=old>Target</h2>")
        };
        var asset = new SiteGeneratedAsset("pixel", "pixel.png", new byte[] { 1 });
        var options = Options with { GeneratedAssets = [asset] };
        var customization = new SiteCustomization { Template = new FilesTemplate(files) };
        var first = await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, true, customization, options, default);
        var original = Snapshot(output);
        var records = CacheSnapshot(workspace.Root);
        if (change == "anchor") files["target.html"] = files["target.html"].Replace("id=old", "id=new", StringComparison.Ordinal);
        if (change == "route") files.Remove("target.html");
        if (change == "asset") options = options with { GeneratedAssets = [] };
        var settings = change == "base" ? Site with { BaseUrl = "https://example.test/changed/" } : Site;
        options = options with { PreviousBuildPlan = first.BuildPlan };
        SiteQualityValidationException? failure = null;
        try { await new SiteGenerator().GenerateWithOptionsAsync(settings, [], output, false, customization, options, default); }
        catch (SiteQualityValidationException exception) { failure = exception; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Diagnostics.Any(value => value.Id == diagnostic)).IsTrue();
        await Assert.That(failure.Report.HtmlFactsCacheHitCount > 0).IsTrue();
        await Assert.That(Snapshot(output)).IsEquivalentTo(original);
        await Assert.That(CacheSnapshot(workspace.Root)).IsEquivalentTo(records);
        await Assert.That(Directory.GetDirectories(workspace.Root).Any(path => Path.GetFileName(path).Contains("staging", StringComparison.Ordinal))).IsFalse();
        // A rejected update must not poison a later successful build's local facts.
        files["target.html"] = Page("target.html", "Target", "<h2 id=old>Target</h2>");
        var repaired = await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, false, customization,
            options with { GeneratedAssets = [asset] }, default);
        await Assert.That(repaired.QualityReport.HtmlParseCount).IsEqualTo(0);
    }

    [Test]
    public async Task GeneratedHtmlAssetsAreInspectedBeforePublication()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, true, null, Options, default);
        var original = Snapshot(output);
        var bad = new SiteGeneratedAsset("prepared-page", "prepared.html", Encoding.UTF8.GetBytes(Page("prepared.html", "Prepared", "<a href='missing.html'>Broken</a>")));
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, false, null,
            Options with { GeneratedAssets = [bad] }, default)).Throws<SiteQualityValidationException>();
        await Assert.That(Snapshot(output)).IsEquivalentTo(original);
    }

    [Test]
    public async Task SubsetChecksRetainedHtmlAndQualityOffDoesNotParseOrPersistFacts()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var customization = new SiteCustomization { Template = new BlogSiteTemplate() };
        await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, true, customization, Options, default);
        var tags = Path.Combine(output, "tags.html");
        await File.WriteAllTextAsync(tags, (await File.ReadAllTextAsync(tags)).Replace("</body>", "<a href='missing.html'>Broken</a></body>", StringComparison.Ordinal));
        var original = Snapshot(output);
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, false, customization,
            Options with { OutputScope = ["index.html"] }, default)).Throws<SiteQualityValidationException>();
        await Assert.That(Snapshot(output)).IsEquivalentTo(original);
        var offOutput = Path.Combine(workspace.Root, "off");
        var off = await new SiteGenerator().GenerateWithOptionsAsync(Site, [], offOutput, true,
            new() { Template = new FilesTemplate(new() { ["index.html"] = "<template shadowrootmode=open></template>" }) },
            Options with { Quality = null }, default);
        await Assert.That(off.QualityReport.HtmlParseCount).IsEqualTo(0);
        await Assert.That(off.QualityReport.Diagnostics).IsEmpty();
        await Assert.That(SiteGenerator.MeasureCache(offOutput).FileCount).IsEqualTo(0);
        await Assert.That(File.ReadAllText(Path.Combine(offOutput, "index.html"))).Contains("shadowrootmode");
    }

    [Test]
    public async Task AssetAndAnchorEditsUseExistingPlanInvalidationsWhileReusingUnchangedFacts()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var files = new Dictionary<string, string> { ["index.html"] = Page("", "Home", "<h2 id=old>Heading</h2><img src='pixel.png'>") };
        var customization = new SiteCustomization { Template = new FilesTemplate(files) };
        var options = Options with { GeneratedAssets = [new("pixel", "pixel.png", new byte[] { 1 })] };
        var first = await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, true, customization, options, default);
        options = options with { GeneratedAssets = [new("pixel", "pixel.png", new byte[] { 2 })] };
        var changed = await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, false, customization,
            options with { PreviousBuildPlan = first.BuildPlan }, default);
        await Assert.That(changed.BuildReport.Invalidations.Any(value => value.NodeId == "asset:pixel")).IsTrue();
        await Assert.That(changed.QualityReport.HtmlParseCount).IsEqualTo(0);
        files["index.html"] = files["index.html"].Replace("id=old", "id=new", StringComparison.Ordinal);
        var anchor = await new SiteGenerator().GenerateWithOptionsAsync(Site, [], output, false, customization,
            options with { PreviousBuildPlan = changed.BuildPlan }, default);
        await Assert.That(anchor.BuildReport.Invalidations.Count > 0).IsTrue();
        await Assert.That(anchor.QualityReport.HtmlParseCount).IsEqualTo(1);
        Console.WriteLine("HT06_INVALIDATION " + System.Text.Json.JsonSerializer.Serialize(new
        {
            asset = changed.BuildReport.Invalidations, anchor = anchor.BuildReport.Invalidations,
            assetParse = changed.QualityReport.HtmlParseCount, anchorParse = anchor.QualityReport.HtmlParseCount,
        }));
    }

    [Test]
    public async Task InspectionBindsRawBytesAndDetectsSameLengthMutationAndCancellationBeforePublish()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "index.html");
        const string content = "<!doctype html>\r\n<title>😀 Old</title>";
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true));
        var inspected = new Dictionary<string, CachedBuildArtifact>();
        var read = await SiteGenerator.ReadQualityTextAsync(workspace.Root, "index.html", inspected, default);
        await Assert.That(read).IsEqualTo(content);
        await Assert.That(inspected["index.html"].Sha256).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        await SiteGenerator.VerifyQualityTextAsync(workspace.Root, inspected["index.html"], default);
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, content.Replace("Old", "New", StringComparison.Ordinal), new UTF8Encoding(true));
        File.SetLastWriteTimeUtc(path, timestamp);
        await Assert.That(async () => await SiteGenerator.VerifyQualityTextAsync(workspace.Root, inspected["index.html"], default)).Throws<IOException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await SiteGenerator.VerifyQualityTextAsync(workspace.Root, inspected["index.html"], cancellation.Token)).Throws<OperationCanceledException>();
        File.Delete(path); // Both inspection and verification released their handles, including failure paths.
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    [Arguments("schema")]
    [Arguments("integrity")]
    [Arguments("truncated")]
    [Arguments("oversized")]
    public async Task CorruptFactsSafelyReparseAndRepair(string corruption)
    {
        using var workspace = new TemporaryWorkspace();
        var partition = Path.Combine(workspace.Root, "cache");
        var html = Page("", "Home", "<style>body{color:red}</style>😀");
        using (var first = new SiteHtmlFactsCache(partition)) { await first.GetAsync(html, default); await first.PublishAsync(); }
        var path = Directory.GetFiles(Path.Combine(partition, "html-facts")).Single();
        if (corruption == "truncated") await File.WriteAllTextAsync(path, "{");
        else if (corruption == "oversized") await File.WriteAllBytesAsync(path, new byte[SiteHtmlFactsCache.MaxRecordBytes + 1]);
        else
        {
            var value = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            value[corruption == "schema" ? "Schema" : "Integrity"] = "foreign";
            await File.WriteAllTextAsync(path, value.ToJsonString());
        }
        using (var repaired = new SiteHtmlFactsCache(partition))
        {
            var facts = await repaired.GetAsync(html, default);
            await Assert.That(facts.Title).IsEqualTo("Home");
            await Assert.That(repaired.ParseCount).IsEqualTo(1);
            await repaired.PublishAsync();
        }
        using var hit = new SiteHtmlFactsCache(partition);
        await hit.GetAsync(html, default);
        await Assert.That(hit.HitCount).IsEqualTo(1);
    }

    [Test]
    public async Task SameLengthSameTimestampUpdateUsesContentHash()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "source.html");
        var partition = Path.Combine(workspace.Root, "cache");
        await File.WriteAllTextAsync(path, Page("", "Home", "<h2 id=old>A</h2>"));
        var timestamp = File.GetLastWriteTimeUtc(path);
        using (var first = new SiteHtmlFactsCache(partition)) { await first.GetAsync(await File.ReadAllTextAsync(path), default); await first.PublishAsync(); }
        await File.WriteAllTextAsync(path, Page("", "Home", "<h2 id=new>A</h2>"));
        File.SetLastWriteTimeUtc(path, timestamp);
        using var second = new SiteHtmlFactsCache(partition);
        var facts = await second.GetAsync(await File.ReadAllTextAsync(path), default);
        await Assert.That(second.ParseCount).IsEqualTo(1);
        await Assert.That(facts.Anchors).IsEquivalentTo(["new"]);
    }

    [Test]
    public async Task CancellationAndIncompleteCoverageDiscardPendingFactsAndReleaseOwner()
    {
        using var workspace = new TemporaryWorkspace();
        var cache = new SiteHtmlFactsCache(Path.Combine(workspace.Root, "cache"));
        using var cancellation = new CancellationTokenSource();
        var html = Page("", "Home", "");
        var files = new[] { "index.html", "second.html" };
        Task<string> Read(string path, CancellationToken token)
        {
            if (path == "second.html") cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(html);
        }
        await Assert.That(async () => await SiteQualityValidator.ValidateAsync(BaseUrl, files, Read,
            files.ToDictionary(path => path, path => SiteRoute.ForFile(path, BaseUrl)), new HashSet<string>(),
            new Dictionary<string, SiteRoute>(), new(), cancellation.Token, factsCache: cache)).Throws<OperationCanceledException>();
        await Assert.That(cache.PendingBytes > 0).IsTrue();
        cache.Dispose();
        await Assert.That(cache.PendingBytes).IsEqualTo(0);
        await Assert.That(Directory.Exists(Path.Combine(workspace.Root, "cache", "html-facts"))).IsFalse();
        await Assert.That(async () => await cache.GetAsync(html, default)).Throws<ObjectDisposedException>();
        using var fresh = new SiteHtmlFactsCache(Path.Combine(workspace.Root, "cache"));
        await Assert.That(async () => await fresh.GetAsync(Page("", "Home", "<template shadowrootmode=open></template>"), default)).Throws<HtmlFactsIncompleteException>();
        await Assert.That(async () => await fresh.GetAsync(Page("", "Home", string.Concat(Enumerable.Repeat("<div>", 520))), default)).Throws<HtmlFactsIncompleteException>();
        await Assert.That(fresh.PendingBytes).IsEqualTo(0);
        await fresh.GetAsync(html, default);
        await Assert.That(fresh.ParseCount).IsEqualTo(3);
    }

    [Test]
    public async Task BoundedAdmissionEvictionAndLargeRecordFallback()
    {
        using var workspace = new TemporaryWorkspace();
        var partition = Path.Combine(workspace.Root, "cache");
        for (var generation = 0; generation < 2; generation++)
        {
            using var cache = new SiteHtmlFactsCache(partition);
            for (var index = 0; index < SiteHtmlFactsCache.MaxRecords + 1; index++)
                await cache.GetAsync(Page("", $"Home {generation}/{index}", ""), default);
            await Assert.That(cache.PendingBytes <= SiteHtmlFactsCache.MaxBytes).IsTrue();
            await cache.PublishAsync();
            var files = new DirectoryInfo(Path.Combine(partition, "html-facts")).GetFiles();
            await Assert.That(files.Length <= SiteHtmlFactsCache.MaxRecords).IsTrue();
            await Assert.That(files.Sum(file => file.Length) <= SiteHtmlFactsCache.MaxBytes).IsTrue();
            await Assert.That(files.All(file => file.Extension == ".json")).IsTrue();
        }
        using var large = new SiteHtmlFactsCache(partition);
        await large.GetAsync(Page("", new string('x', SiteHtmlFactsCache.MaxRecordBytes), ""), default);
        await Assert.That(large.PendingBytes).IsEqualTo(0);
    }

    private static string Page(string path, string title, string body) =>
        $"<!doctype html><html><head><title>{title}</title><link rel='canonical' href='{BaseUrl}{path}'><meta name='description' content='Description'><meta property='og:title' content='{title}'><meta property='og:description' content='Description'><meta property='og:url' content='{BaseUrl}{path}'></head><body>{body}</body></html>";
    private static string[] Snapshot(string output) => Directory.GetFiles(output, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(output, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private static string[] CacheSnapshot(string root) => Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "html-facts")
        .Order(StringComparer.Ordinal).Select(path => path + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private sealed class FilesTemplate(Dictionary<string, string> files) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult(files.Select(pair => new SiteTemplateFile { RelativePath = pair.Key, Content = pair.Value }).ToArray()));
    }
}
