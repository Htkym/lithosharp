using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

/// <summary>
/// V110-08: the incremental cache is measurable and can be reclaimed per output without
/// touching the published tree or another output's partition.
/// </summary>
public sealed class SiteGeneratorCacheTests
{
    private static readonly DateTimeOffset FixedBuildTimestamp = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task MeasureAndClearReclaimOnlyTheOwnedPartition()
    {
        using var workspace = new TemporaryWorkspace();
        var firstOutput = Path.Combine(workspace.Root, "first");
        var secondOutput = Path.Combine(workspace.Root, "second");
        var generator = new SiteGenerator();
        var settings = new SiteSettings { BaseUrl = "https://example.test/" };
        var customization = new SiteCustomization { Template = new BlogSiteTemplate() };

        var first = await generator.GenerateWithOptionsAsync(settings, [Post()], firstOutput, clean: true,
            customization, new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp }, CancellationToken.None);
        await generator.GenerateWithOptionsAsync(settings, [Post()], secondOutput, clean: true,
            customization, new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp }, CancellationToken.None);

        var firstUsage = SiteGenerator.MeasureCache(firstOutput);
        var secondUsage = SiteGenerator.MeasureCache(secondOutput);
        await Assert.That(firstUsage.FileCount).IsGreaterThan(0);
        await Assert.That(firstUsage.TotalBytes).IsGreaterThan(0);
        await Assert.That(firstUsage.CacheDirectory.StartsWith(
            Path.Combine(workspace.Root, ".lithosharp"), StringComparison.Ordinal)).IsTrue();
        await Assert.That(firstUsage.CacheDirectory).IsNotEqualTo(secondUsage.CacheDirectory);

        var cleared = SiteGenerator.ClearCache(firstOutput);
        await Assert.That(cleared.FileCount).IsEqualTo(firstUsage.FileCount);
        await Assert.That(SiteGenerator.MeasureCache(firstOutput).FileCount).IsEqualTo(0);
        await Assert.That(SiteGenerator.MeasureCache(secondOutput).FileCount).IsEqualTo(secondUsage.FileCount);

        // The published tree is not touched and a rebuild still converges.
        await Assert.That(File.Exists(Path.Combine(firstOutput, "posts", "alpha.html"))).IsTrue();
        await generator.GenerateWithOptionsAsync(settings, [Post()], firstOutput, clean: false,
            customization, new SiteGenerationOptions
            {
                BuildTimestamp = FixedBuildTimestamp,
                PreviousBuildPlan = first.BuildPlan,
            }, CancellationToken.None);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(firstOutput, "posts", "alpha.html")))
            .Contains("Alpha");
    }

    [Test]
    public async Task MeasureWithoutAnExistingPartitionReportsZero()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        var usage = SiteGenerator.MeasureCache(output);
        await Assert.That(usage.FileCount).IsEqualTo(0);
        await Assert.That(usage.TotalBytes).IsEqualTo(0);
        await Assert.That(SiteGenerator.ClearCache(output).FileCount).IsEqualTo(0);
    }

    [Test]
    public async Task UnavailableOptionalCacheDoesNotFailTheBuild()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        var blockedCache = Path.Combine(workspace.Root, "cache-is-a-file");
        await File.WriteAllTextAsync(blockedCache, "not a directory");

        var result = await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/" },
            [Post()], output, clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() },
            new SiteGenerationOptions
            {
                BuildTimestamp = FixedBuildTimestamp,
                BuildCacheDirectory = blockedCache,
            },
            CancellationToken.None);

        await Assert.That(result.BuildReport.Diagnostics.Any(diagnostic => diagnostic.Severity >= SiteDiagnosticSeverity.Error)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(output, "posts", "alpha.html"))).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "alpha.html"))).Contains("Alpha");
    }

    [Test]
    public async Task ClearCacheWaitsForAnActiveBuildOfTheSameOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        var settings = new SiteSettings { BaseUrl = "https://example.test/" };
        var firstCollection = CacheCollection("first", (entry, context) => context.RenderDocument(entry.Body));
        var first = await new SiteGenerator().GenerateWithOptionsAsync(settings, [], output, clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() },
            new SiteGenerationOptions { ContentCollections = [firstCollection], BuildTimestamp = FixedBuildTimestamp },
            CancellationToken.None);

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var changedCollection = CacheCollection("second", (entry, context) =>
        {
            entered.Set();
            release.Wait();
            return context.RenderDocument(entry.Body);
        });
        var building = new SiteGenerator().GenerateWithOptionsAsync(settings, [], output, clean: false,
            new SiteCustomization { Template = new BlogSiteTemplate() },
            new SiteGenerationOptions
            {
                ContentCollections = [changedCollection],
                BuildTimestamp = FixedBuildTimestamp,
                PreviousBuildPlan = first.BuildPlan,
            },
            CancellationToken.None);
        await Assert.That(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)))).IsTrue();

        using var clearStarted = new ManualResetEventSlim();
        var clearing = Task.Run(() =>
        {
            clearStarted.Set();
            return SiteGenerator.ClearCache(output);
        });
        await Assert.That(await Task.Run(() => clearStarted.Wait(TimeSpan.FromSeconds(5)))).IsTrue();
        await Task.Delay(100);
        await Assert.That(clearing.IsCompleted).IsFalse();
        release.Set();
        await building;
        var cleared = await clearing;

        await Assert.That(cleared.FileCount).IsGreaterThan(0);
        await Assert.That(SiteGenerator.MeasureCache(output).FileCount).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "cache", "index.html"))).Contains("second");
    }

    [Test]
    [Arguments("valid")]
    [Arguments("plaintext")]
    [Arguments("missing")]
    [Arguments("html")]
    [Arguments("span")]
    [Arguments("json")]
    public async Task ChangedPostSearchReusesVerifiedPlainTextAndRejectsCorruption(string cacheState)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        var cleanOutput = Path.Combine(workspace.Root, "clean");
        var settings = new SiteSettings { BaseUrl = "https://example.test/" };
        var customization = new SiteCustomization { Template = new BlogSiteTemplate() };
        var options = new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp };
        var alpha = Post();
        var beta = alpha with
        {
            FilePath = "content/beta.md", Slug = "beta", RelativeOutputPath = "posts/beta.html",
            FrontMatter = alpha.FrontMatter with { Title = "Beta" },
            MarkdownBody = "## 日本語 &amp; API\n\nUntouched **body** with [link](https://example.org/)."
        };
        var initial = await new SiteGenerator().GenerateWithOptionsAsync(settings, [alpha, beta], output,
            clean: true, customization, options, CancellationToken.None);
        if (cacheState != "valid")
        {
            var parses = Path.Combine(SiteGenerator.MeasureCache(output).CacheDirectory, "parses");
            var tamperedRecords = 0;
            foreach (var path in Directory.EnumerateFiles(parses, "*.json"))
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
                if (json["PlainText"]!.GetValue<string>().Contains("Untouched", StringComparison.Ordinal))
                {
                    if (cacheState == "missing") File.Delete(path);
                    else
                    {
                        if (cacheState == "json")
                        {
                            await File.WriteAllTextAsync(path, "{\"PlainText\":");
                            tamperedRecords++;
                            continue;
                        }
                        if (cacheState == "plaintext") json["PlainText"] = "forged search text";
                        else if (cacheState == "html") json["RawHtml"] = "forged HTML";
                        else
                        {
                            // An invalid semantic span must be rejected even with a matching digest.
                            json["Headings"]![0]!["SpanStart"] = -1;
                            var parse = System.Text.Json.JsonSerializer.Deserialize<LithoSharp.Build.CachedPostParse>(json.ToJsonString())!;
                            json["Integrity"] = parse.ComputeIntegrity();
                        }
                        await File.WriteAllTextAsync(path, json.ToJsonString());
                    }
                    tamperedRecords++;
                }
            }
            await Assert.That(tamperedRecords).IsEqualTo(1);
        }
        var changed = alpha with { MarkdownBody = alpha.MarkdownBody + "\n\nChanged body." };
        var generator = new SiteGenerator();
        var incremental = await generator.GenerateWithOptionsAsync(settings, [changed, beta], output,
            clean: false, customization, options with { PreviousBuildPlan = initial.BuildPlan }, CancellationToken.None);
        await new SiteGenerator().GenerateWithOptionsAsync(settings, [changed, beta], cleanOutput,
            clean: true, customization, options, CancellationToken.None);

        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(cacheState == "valid" ? 1 : 2);
        await Assert.That(incremental.BuildReport.Nodes.Single(node => node.NodeId == "index:search").CacheHit).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "search-index.json")))
            .IsEqualTo(await File.ReadAllTextAsync(Path.Combine(cleanOutput, "search-index.json")));
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "beta.html")))
            .IsEqualTo(await File.ReadAllTextAsync(Path.Combine(cleanOutput, "posts", "beta.html")));

        // Metadata forces actual beta rendering without a body edit. A fresh generator
        // must reuse the repaired parse and produce the same HTML/search as a clean build.
        var renamedBeta = beta with { FrontMatter = beta.FrontMatter with { Title = "Renamed Beta" } };
        var nextGenerator = new SiteGenerator();
        await nextGenerator.GenerateWithOptionsAsync(settings, [changed, renamedBeta], output,
            clean: false, customization, options with { PreviousBuildPlan = incremental.BuildPlan }, CancellationToken.None);
        await new SiteGenerator().GenerateWithOptionsAsync(settings, [changed, renamedBeta], cleanOutput,
            clean: true, customization, options, CancellationToken.None);
        await Assert.That(nextGenerator.MarkdownCompiler.ParseCount).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "search-index.json")))
            .IsEqualTo(await File.ReadAllTextAsync(Path.Combine(cleanOutput, "search-index.json")));
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "beta.html")))
            .IsEqualTo(await File.ReadAllTextAsync(Path.Combine(cleanOutput, "posts", "beta.html")));
    }

    [Test]
    [Arguments("measure", "same-root")]
    [Arguments("clear", "same-root")]
    [Arguments("measure", "nested-cache")]
    [Arguments("clear", "nested-cache")]
    [Arguments("measure", "ancestor-cache")]
    [Arguments("clear", "ancestor-cache")]
    [Arguments("measure", "public-root")]
    [Arguments("clear", "public-root")]
    [Arguments("measure", "public-nested")]
    [Arguments("clear", "public-nested")]
    public async Task CacheOperationsRejectOverlapWithoutTouchingPublishedOrPublicFiles(string operation, string overlap)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        var publicRoot = Path.Combine(workspace.Root, "public");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(publicRoot);
        var publishedCanary = Path.Combine(output, "published.html");
        var publicCanary = Path.Combine(publicRoot, "input.txt");
        await File.WriteAllTextAsync(publishedCanary, "published output must survive");
        await File.WriteAllTextAsync(publicCanary, "public input must survive");

        // Derive the output identity through a valid sibling cache. The overlapping
        // API must not be called to arrange its partition: the fixed API rejects it.
        var safeUsage = SiteGenerator.MeasureCache(output, new SiteGenerationOptions
        {
            BuildCacheDirectory = Path.Combine(workspace.Root, "safe-cache"),
        });
        var partitionName = Path.GetFileName(safeUsage.CacheDirectory);
        var cacheRoot = overlap switch
        {
            "same-root" => output,
            "nested-cache" => Path.Combine(output, "nested-cache"),
            "ancestor-cache" => workspace.Root,
            "public-root" => publicRoot,
            "public-nested" => Path.Combine(publicRoot, "nested-cache"),
            _ => throw new ArgumentException("Unknown overlap fixture.", nameof(overlap)),
        };
        var overlappingPartition = Path.Combine(cacheRoot, partitionName);
        Directory.CreateDirectory(overlappingPartition);
        var partitionCanary = Path.Combine(overlappingPartition, "cache-or-published-canary.txt");
        await File.WriteAllTextAsync(partitionCanary, "never reclaim an overlapping partition");
        var options = new SiteGenerationOptions { BuildCacheDirectory = cacheRoot, PublicDirectory = publicRoot };
        var publishedBefore = SnapshotCacheCanaryTree(output);
        var publicBefore = SnapshotCacheCanaryTree(publicRoot);
        var publishedWriteTime = File.GetLastWriteTimeUtc(publishedCanary);
        var publicWriteTime = File.GetLastWriteTimeUtc(publicCanary);

        await Assert.That(() =>
        {
            if (operation == "measure") SiteGenerator.MeasureCache(output, options);
            else if (operation == "clear") SiteGenerator.ClearCache(output, options);
            else throw new InvalidOperationException("Unknown cache operation fixture.");
        }).Throws<ArgumentException>();

        await Assert.That(SnapshotCacheCanaryTree(output)).IsEqualTo(publishedBefore);
        await Assert.That(SnapshotCacheCanaryTree(publicRoot)).IsEqualTo(publicBefore);
        await Assert.That(await File.ReadAllTextAsync(partitionCanary))
            .IsEqualTo("never reclaim an overlapping partition");
        await Assert.That(File.GetLastWriteTimeUtc(publishedCanary)).IsEqualTo(publishedWriteTime);
        await Assert.That(File.GetLastWriteTimeUtc(publicCanary)).IsEqualTo(publicWriteTime);
    }

    [Test]
    public async Task SafeSiblingCacheOperationsPreservePublishedTreesAndOtherOutputPartition()
    {
        using var workspace = new TemporaryWorkspace();
        var firstOutput = Path.Combine(workspace.Root, "first");
        var secondOutput = Path.Combine(workspace.Root, "second");
        Directory.CreateDirectory(firstOutput);
        Directory.CreateDirectory(secondOutput);
        await File.WriteAllTextAsync(Path.Combine(firstOutput, "index.html"), "first published");
        await File.WriteAllTextAsync(Path.Combine(secondOutput, "index.html"), "second published");
        // A sibling with the output's name as a prefix is disjoint, not contained.
        var options = new SiteGenerationOptions { BuildCacheDirectory = Path.Combine(workspace.Root, "first-cache") };
        var firstPartition = SiteGenerator.MeasureCache(firstOutput, options).CacheDirectory;
        var secondPartition = SiteGenerator.MeasureCache(secondOutput, options).CacheDirectory;
        await Assert.That(firstPartition).IsNotEqualTo(secondPartition);
        Directory.CreateDirectory(firstPartition);
        Directory.CreateDirectory(secondPartition);
        await File.WriteAllBytesAsync(Path.Combine(firstPartition, "record.bin"), new byte[] { 1, 2, 3 });
        await File.WriteAllBytesAsync(Path.Combine(secondPartition, "record.bin"), new byte[] { 4, 5, 6, 7 });
        var firstPublished = SnapshotCacheCanaryTree(firstOutput);
        var secondPublished = SnapshotCacheCanaryTree(secondOutput);
        var secondCached = SnapshotCacheCanaryTree(secondPartition);
        var firstUsage = SiteGenerator.MeasureCache(firstOutput, options);
        await Assert.That(firstUsage.FileCount).IsEqualTo(1);
        await Assert.That(firstUsage.TotalBytes).IsEqualTo(3);

        var reclaimed = SiteGenerator.ClearCache(firstOutput, options);
        await Assert.That(reclaimed.FileCount).IsEqualTo(1);
        await Assert.That(reclaimed.TotalBytes).IsEqualTo(3);
        await Assert.That(Directory.Exists(firstPartition)).IsFalse();
        await Assert.That(SiteGenerator.MeasureCache(firstOutput, options).FileCount).IsEqualTo(0);
        var secondUsage = SiteGenerator.MeasureCache(secondOutput, options);
        await Assert.That(secondUsage.FileCount).IsEqualTo(1);
        await Assert.That(secondUsage.TotalBytes).IsEqualTo(4);
        await Assert.That(SnapshotCacheCanaryTree(secondPartition)).IsEqualTo(secondCached);
        await Assert.That(SnapshotCacheCanaryTree(firstOutput)).IsEqualTo(firstPublished);
        await Assert.That(SnapshotCacheCanaryTree(secondOutput)).IsEqualTo(secondPublished);
    }

    private static string SnapshotCacheCanaryTree(string root)
        => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file) + ":" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))))
            .OrderBy(value => value, StringComparer.Ordinal));

    [Test]
    [Arguments("measure", false)]
    [Arguments("clear", false)]
    [Arguments("measure", true)]
    [Arguments("clear", true)]
    public async Task CacheOperationsRejectPublicInputInsideTheResolvedPartition(string operation, bool nested)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "published.html"), "published canary");
        var safeOptions = new SiteGenerationOptions { BuildCacheDirectory = Path.Combine(workspace.Root, "safe-cache") };
        var partition = SiteGenerator.MeasureCache(output, safeOptions).CacheDirectory;
        var publicRoot = nested ? Path.Combine(partition, "public-child") : partition;
        Directory.CreateDirectory(publicRoot);
        var publicCanary = Path.Combine(publicRoot, "input.txt");
        await File.WriteAllTextAsync(publicCanary, "actual public input must not be reclaimed");
        var publishedBefore = SnapshotCacheCanaryTree(output);
        var publicBefore = SnapshotCacheCanaryTree(publicRoot);
        var publicWriteTime = File.GetLastWriteTimeUtc(publicCanary);
        var options = safeOptions with { PublicDirectory = publicRoot };

        await Assert.That(() =>
        {
            if (operation == "measure") SiteGenerator.MeasureCache(output, options);
            else if (operation == "clear") SiteGenerator.ClearCache(output, options);
            else throw new InvalidOperationException("Unknown cache operation fixture.");
        }).Throws<ArgumentException>();

        await Assert.That(SnapshotCacheCanaryTree(output)).IsEqualTo(publishedBefore);
        await Assert.That(SnapshotCacheCanaryTree(publicRoot)).IsEqualTo(publicBefore);
        await Assert.That(await File.ReadAllTextAsync(publicCanary))
            .IsEqualTo("actual public input must not be reclaimed");
        await Assert.That(File.GetLastWriteTimeUtc(publicCanary)).IsEqualTo(publicWriteTime);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GenerationRejectsPublicInputInsideTheResolvedPartitionBeforePublishing(bool nested)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        Directory.CreateDirectory(output);
        var publishedCanary = Path.Combine(output, "published.html");
        await File.WriteAllTextAsync(publishedCanary, "prior published canary");
        var safeOptions = new SiteGenerationOptions
        {
            BuildTimestamp = FixedBuildTimestamp,
            BuildCacheDirectory = Path.Combine(workspace.Root, "safe-cache"),
        };
        var partition = SiteGenerator.MeasureCache(output, safeOptions).CacheDirectory;
        var publicRoot = nested ? Path.Combine(partition, "public-child") : partition;
        Directory.CreateDirectory(publicRoot);
        var publicCanary = Path.Combine(publicRoot, "input.txt");
        await File.WriteAllTextAsync(publicCanary, "source public canary");
        var publishedBefore = SnapshotCacheCanaryTree(output);
        var publicBefore = SnapshotCacheCanaryTree(publicRoot);
        var publishedWriteTime = File.GetLastWriteTimeUtc(publishedCanary);
        var publicWriteTime = File.GetLastWriteTimeUtc(publicCanary);

        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/" }, [Post()], output, clean: false,
            new SiteCustomization { Template = new BlogSiteTemplate() },
            safeOptions with { PublicDirectory = publicRoot }, CancellationToken.None)).Throws<ArgumentException>();

        await Assert.That(SnapshotCacheCanaryTree(output)).IsEqualTo(publishedBefore);
        await Assert.That(SnapshotCacheCanaryTree(publicRoot)).IsEqualTo(publicBefore);
        await Assert.That(File.GetLastWriteTimeUtc(publishedCanary)).IsEqualTo(publishedWriteTime);
        await Assert.That(File.GetLastWriteTimeUtc(publicCanary)).IsEqualTo(publicWriteTime);
    }

    [Test]
    public async Task GenerationWithDisjointPublicInputAndCacheCreatesMissingOutputParents()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "new-parent", "nested-parent", "out");
        var publicRoot = Path.Combine(workspace.Root, "public");
        Directory.CreateDirectory(publicRoot);
        var publicCanary = Path.Combine(publicRoot, "public-canary.txt");
        await File.WriteAllTextAsync(publicCanary, "disjoint public input");
        var publicBefore = SnapshotCacheCanaryTree(publicRoot);
        var publicWriteTime = File.GetLastWriteTimeUtc(publicCanary);
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = FixedBuildTimestamp,
            BuildCacheDirectory = Path.Combine(workspace.Root, "safe-cache"),
            PublicDirectory = publicRoot,
        };
        await Assert.That(Directory.Exists(Path.GetDirectoryName(output)!)).IsFalse();

        var result = await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/" }, [Post()], output, clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() }, options, CancellationToken.None);

        await Assert.That(result.BuildReport.Diagnostics.Any(diagnostic => diagnostic.Severity >= SiteDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "alpha.html"))).Contains("Alpha");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "public-canary.txt")))
            .IsEqualTo("disjoint public input");
        await Assert.That(SnapshotCacheCanaryTree(publicRoot)).IsEqualTo(publicBefore);
        await Assert.That(File.GetLastWriteTimeUtc(publicCanary)).IsEqualTo(publicWriteTime);
        await Assert.That(SiteGenerator.MeasureCache(output, options).FileCount).IsGreaterThan(0);
    }

    private static MarkdownPost Post() => new(
        "content/alpha.md",
        "alpha",
        new PostFrontMatter
        {
            Title = "Alpha",
            Date = FixedBuildTimestamp.AddDays(-1),
            Summary = "summary",
            Tags = ["test"],
        },
        "# Alpha\n\nBody for alpha.",
        "posts/alpha.html");

    private static SiteContentCollection<string, string> CacheCollection(
        string body,
        ContentPageRenderer<string, string> renderer) =>
        new(new ContentCollection<string, string>(new("cache-lock"), Path.GetTempPath(),
            [new(new("entry"), "entry.md", "fingerprint-" + body, "Cache lock", body)],
            _ => SiteRoute.ForDirectoryIndex("cache"), entry => new PageMetadata(entry.FrontMatter),
            transformationId: new("cache-lock:1"), isCacheable: true), renderer)
        { RendererFingerprint = "cache-lock-renderer:1", IsThreadSafe = false };
}
