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

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var changedCollection = CacheCollection("second", (entry, context) =>
        {
            entered.TrySetResult();
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
        var clearStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<LithoSharp.Build.SiteBuildCacheUsage>? clearing = null;
        Exception? failure = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // ClearCache blocks synchronously; its startup must not compete with
            // the blocked renderer and other parallel tests for a pool worker.
            clearing = Task.Factory.StartNew(() =>
            {
                clearStarted.TrySetResult();
                return SiteGenerator.ClearCache(output);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await clearStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            await Assert.That(clearing.IsCompleted).IsFalse();
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            release.Set();
            try { await Task.WhenAll(building, (Task?)clearing ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception cleanup)
            {
                if (failure is not null) throw new AggregateException("Assertion and owned fixture cleanup failed.", failure, cleanup);
                throw;
            }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        var cleared = await clearing!;

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

    [Test]
    [Arguments("measure", "output-root")]
    [Arguments("clear", "output-root")]
    [Arguments("generate", "output-root")]
    [Arguments("measure", "public-root")]
    [Arguments("clear", "public-root")]
    [Arguments("generate", "public-root")]
    [Arguments("measure", "public-partition")]
    [Arguments("clear", "public-partition")]
    [Arguments("generate", "public-partition")]
    [Arguments("measure", "public-partition-child")]
    [Arguments("clear", "public-partition-child")]
    [Arguments("generate", "public-partition-child")]
    public async Task CacheAdmissionRejectsCaseVariantOverlapWithoutChangingInputs(string operation, string overlap)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        var publicRoot = Path.Combine(workspace.Root, "public");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(publicRoot);
        await File.WriteAllTextAsync(Path.Combine(output, "published.html"), "published canary");
        var safeCache = Path.Combine(workspace.Root, "safe-cache");
        var partitionName = Path.GetFileName(SiteGenerator.MeasureCache(output,
            new SiteGenerationOptions { BuildCacheDirectory = safeCache }).CacheDirectory);
        var cacheRoot = overlap switch
        {
            "output-root" => Path.Combine(workspace.Root, "OUT"),
            "public-root" => Path.Combine(workspace.Root, "PUBLIC"),
            "public-partition" or "public-partition-child" => safeCache,
            _ => throw new InvalidOperationException("Unknown case fixture."),
        };
        var partition = Path.Combine(cacheRoot, partitionName);
        Directory.CreateDirectory(partition);
        await File.WriteAllTextAsync(Path.Combine(partition, "cache-canary.txt"), "cache canary");
        if (overlap is "public-partition" or "public-partition-child")
            publicRoot = Path.Combine(workspace.Root, "SAFE-CACHE", partitionName);
        if (overlap == "public-partition-child") publicRoot = Path.Combine(publicRoot, "public-child");
        Directory.CreateDirectory(publicRoot);
        var publicCanary = Path.Combine(publicRoot, "input.txt");
        await File.WriteAllTextAsync(publicCanary, "public canary");
        var outputBefore = SnapshotCacheCanaryTree(output);
        var publicBefore = SnapshotCacheCanaryTree(publicRoot);
        var cacheBefore = SnapshotCacheCanaryTree(cacheRoot);
        var publicWriteTime = File.GetLastWriteTimeUtc(publicCanary);
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = FixedBuildTimestamp, BuildCacheDirectory = cacheRoot, PublicDirectory = publicRoot,
        };

        await Assert.That(async () => await InvokeCacheAdmissionFixture(operation, output, options))
            .Throws<ArgumentException>();

        await Assert.That(SnapshotCacheCanaryTree(output)).IsEqualTo(outputBefore);
        await Assert.That(SnapshotCacheCanaryTree(publicRoot)).IsEqualTo(publicBefore);
        await Assert.That(SnapshotCacheCanaryTree(cacheRoot)).IsEqualTo(cacheBefore);
        await Assert.That(File.GetLastWriteTimeUtc(publicCanary)).IsEqualTo(publicWriteTime);
    }

    [Test]
    [Arguments("measure", "partition")]
    [Arguments("clear", "partition")]
    [Arguments("generate", "partition")]
    [Arguments("measure", "partition-child")]
    [Arguments("clear", "partition-child")]
    [Arguments("generate", "partition-child")]
    [Arguments("measure", "ancestor")]
    [Arguments("clear", "ancestor")]
    [Arguments("generate", "ancestor")]
    public async Task CacheAdmissionRejectsPublicRootOrAncestorLinkToItsOrdinaryPartition(string operation, string linkKind)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "published.html"), "published canary");
        var cacheRoot = Path.Combine(workspace.Root, "safe-cache");
        var partition = SiteGenerator.MeasureCache(output,
            new SiteGenerationOptions { BuildCacheDirectory = cacheRoot }).CacheDirectory;
        Directory.CreateDirectory(partition);
        var target = linkKind switch
        {
            "partition" or "ancestor" => partition,
            "partition-child" => Path.Combine(partition, "public-child"),
            _ => throw new InvalidOperationException("Unknown link fixture."),
        };
        Directory.CreateDirectory(target);
        var publicCanary = Path.Combine(target, "input.txt");
        await File.WriteAllTextAsync(publicCanary, "linked public input must survive");
        var outputBefore = SnapshotCacheCanaryTree(output);
        var cacheBefore = SnapshotCacheCanaryTree(cacheRoot);
        var writeTime = File.GetLastWriteTimeUtc(publicCanary);
        var link = Path.Combine(workspace.Root, "public-link");
        // Capability failure is an explicit fixture failure, never a silent pass/skip.
        Directory.CreateSymbolicLink(link, linkKind == "ancestor" ? cacheRoot : target);
        try
        {
            var publicRoot = linkKind == "ancestor" ? Path.Combine(link, Path.GetFileName(partition)) : link;
            var options = new SiteGenerationOptions
            {
                BuildTimestamp = FixedBuildTimestamp, BuildCacheDirectory = cacheRoot, PublicDirectory = publicRoot,
            };
            await Assert.That(async () => await InvokeCacheAdmissionFixture(operation, output, options))
                .Throws<InvalidOperationException>();
            await Assert.That(SnapshotCacheCanaryTree(output)).IsEqualTo(outputBefore);
            await Assert.That(SnapshotCacheCanaryTree(cacheRoot)).IsEqualTo(cacheBefore);
            await Assert.That(await File.ReadAllTextAsync(publicCanary)).IsEqualTo("linked public input must survive");
            await Assert.That(File.GetLastWriteTimeUtc(publicCanary)).IsEqualTo(writeTime);
        }
        finally { Directory.Delete(link); }
    }

    private static async Task InvokeCacheAdmissionFixture(string operation, string output, SiteGenerationOptions options)
    {
        if (operation == "measure") SiteGenerator.MeasureCache(output, options);
        else if (operation == "clear") SiteGenerator.ClearCache(output, options);
        else if (operation == "generate")
            await new SiteGenerator().GenerateWithOptionsAsync(
                new SiteSettings { BaseUrl = "https://example.test/" }, [Post()], output, clean: false,
                new SiteCustomization { Template = new BlogSiteTemplate() }, options, CancellationToken.None);
        else throw new InvalidOperationException("Unknown cache operation fixture.");
    }

    [Test]
    [Arguments("measure", "same-output", false)]
    [Arguments("clear", "same-output", false)]
    [Arguments("generate", "same-output", false)]
    [Arguments("measure", "same-output", true)]
    [Arguments("clear", "same-output", true)]
    [Arguments("generate", "same-output", true)]
    [Arguments("measure", "nested-cache", false)]
    [Arguments("clear", "nested-cache", false)]
    [Arguments("generate", "nested-cache", false)]
    [Arguments("measure", "nested-cache", true)]
    [Arguments("clear", "nested-cache", true)]
    [Arguments("generate", "nested-cache", true)]
    [Arguments("measure", "public-partition", false)]
    [Arguments("clear", "public-partition", false)]
    [Arguments("generate", "public-partition", false)]
    [Arguments("measure", "public-partition", true)]
    [Arguments("clear", "public-partition", true)]
    [Arguments("generate", "public-partition", true)]
    [Arguments("measure", "public-child", false)]
    [Arguments("clear", "public-child", false)]
    [Arguments("generate", "public-child", false)]
    [Arguments("measure", "public-child", true)]
    [Arguments("clear", "public-child", true)]
    [Arguments("generate", "public-child", true)]
    public async Task CacheAdmissionRejectsMixedWindowsNamespaceOverlap(string operation, string overlap, bool extendedInput)
    {
        // Extended Windows namespaces have no meaning on Unix. Unix execution is
        // not counted as namespace validation; actual Windows evidence is required.
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        Directory.CreateDirectory(output);
        var published = Path.Combine(output, "published.html");
        await File.WriteAllTextAsync(published, "published namespace canary");
        var safeCache = Path.Combine(workspace.Root, "safe-cache");
        var partitionName = Path.GetFileName(SiteGenerator.MeasureCache(output,
            new SiteGenerationOptions { BuildCacheDirectory = safeCache }).CacheDirectory);
        var cache = overlap switch
        {
            "same-output" => output,
            "nested-cache" => Path.Combine(output, "nested-cache"),
            "public-partition" or "public-child" => safeCache,
            _ => throw new InvalidOperationException("Unknown namespace fixture."),
        };
        var partition = Path.Combine(cache, partitionName);
        var publicRoot = overlap == "public-child" ? Path.Combine(partition, "public-child") : partition;
        Directory.CreateDirectory(publicRoot);
        var canary = Path.Combine(publicRoot, "canary.txt");
        await File.WriteAllTextAsync(canary, "never delete namespace-alias input");
        var before = SnapshotCacheCanaryTree(workspace.Root);
        var stamp = File.GetLastWriteTimeUtc(canary);
        var publishedStamp = File.GetLastWriteTimeUtc(published);
        var outputArgument = extendedInput && overlap is "same-output" or "nested-cache"
            ? ExtendedCacheFixturePath(output) : output;
        var cacheArgument = extendedInput ? cache : ExtendedCacheFixturePath(cache);
        var publicArgument = overlap is "public-partition" or "public-child"
            ? (extendedInput ? ExtendedCacheFixturePath(publicRoot) : publicRoot) : null;
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = FixedBuildTimestamp, BuildCacheDirectory = cacheArgument, PublicDirectory = publicArgument,
        };
        var beforeHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(canary)));
        try
        {
            await Assert.That(async () => await InvokeCacheAdmissionFixture(operation, outputArgument, options))
                .Throws<ArgumentException>();
        }
        finally
        {
            bool? afterExists = null;
            string? afterHash = null;
            long? afterWriteTicks = null;
            string? observationError = null;
            try
            {
                afterExists = File.Exists(canary);
                if (afterExists == true)
                {
                    afterHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(canary)));
                    afterWriteTicks = File.GetLastWriteTimeUtc(canary).Ticks;
                }
            }
            catch (IOException exception) { observationError = exception.GetType().Name; }
            catch (UnauthorizedAccessException exception) { observationError = exception.GetType().Name; }
            Console.WriteLine("LSCACHE_NAMESPACE_CANARY " + System.Text.Json.JsonSerializer.Serialize(new
            {
                operation, overlap, extendedInput, beforeExists = true, beforeSha256 = beforeHash,
                beforeWriteTicks = stamp.Ticks, afterExists, afterSha256 = afterHash, afterWriteTicks, observationError,
            }));
        }
        await Assert.That(SnapshotCacheCanaryTree(workspace.Root)).IsEqualTo(before);
        await Assert.That(await File.ReadAllTextAsync(canary)).IsEqualTo("never delete namespace-alias input");
        await Assert.That(File.GetLastWriteTimeUtc(canary)).IsEqualTo(stamp);
        await Assert.That(File.GetLastWriteTimeUtc(published)).IsEqualTo(publishedStamp);
    }

    [Test]
    [Arguments("measure", "output", "global-root")]
    [Arguments("measure", "output", "dos-device")]
    [Arguments("measure", "output", "nt-object")]
    [Arguments("measure", "cache", "global-root")]
    [Arguments("measure", "cache", "dos-device")]
    [Arguments("measure", "cache", "nt-object")]
    [Arguments("measure", "public", "global-root")]
    [Arguments("measure", "public", "dos-device")]
    [Arguments("measure", "public", "nt-object")]
    [Arguments("clear", "output", "global-root")]
    [Arguments("clear", "output", "dos-device")]
    [Arguments("clear", "output", "nt-object")]
    [Arguments("clear", "cache", "global-root")]
    [Arguments("clear", "cache", "dos-device")]
    [Arguments("clear", "cache", "nt-object")]
    [Arguments("clear", "public", "global-root")]
    [Arguments("clear", "public", "dos-device")]
    [Arguments("clear", "public", "nt-object")]
    [Arguments("generate", "output", "global-root")]
    [Arguments("generate", "output", "dos-device")]
    [Arguments("generate", "output", "nt-object")]
    [Arguments("generate", "cache", "global-root")]
    [Arguments("generate", "cache", "dos-device")]
    [Arguments("generate", "cache", "nt-object")]
    [Arguments("generate", "public", "global-root")]
    [Arguments("generate", "public", "dos-device")]
    [Arguments("generate", "public", "nt-object")]
    [Arguments("measure", "output", "slash-dos")]
    [Arguments("measure", "output", "slash-extended")]
    [Arguments("measure", "cache", "slash-dos")]
    [Arguments("measure", "cache", "slash-extended")]
    [Arguments("measure", "public", "slash-dos")]
    [Arguments("measure", "public", "slash-extended")]
    [Arguments("clear", "output", "slash-dos")]
    [Arguments("clear", "output", "slash-extended")]
    [Arguments("clear", "cache", "slash-dos")]
    [Arguments("clear", "cache", "slash-extended")]
    [Arguments("clear", "public", "slash-dos")]
    [Arguments("clear", "public", "slash-extended")]
    [Arguments("generate", "output", "slash-dos")]
    [Arguments("generate", "output", "slash-extended")]
    [Arguments("generate", "cache", "slash-dos")]
    [Arguments("generate", "cache", "slash-extended")]
    [Arguments("generate", "public", "slash-dos")]
    [Arguments("generate", "public", "slash-extended")]
    public async Task CacheAdmissionRejectsUnsupportedWindowsNamespaceBeforeSideEffects(string operation, string slot, string kind)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        Directory.CreateDirectory(output);
        var canary = Path.Combine(output, "canary.txt");
        await File.WriteAllTextAsync(canary, "unsupported namespace must not mutate");
        var before = SnapshotCacheCanaryTree(workspace.Root);
        var stamp = File.GetLastWriteTimeUtc(canary);
        // Even a broken/old guard can target only this dedicated fixture. No
        // real device volume or fixed user directory is used by the API controls.
        var ownedTarget = slot == "output" ? output : Path.Combine(workspace.Root, slot);
        var unsupported = kind switch
        {
            "global-root" => @"\\?\GLOBALROOT\" + ownedTarget,
            "dos-device" => @"\\.\" + ownedTarget,
            "nt-object" => @"\??\" + ownedTarget,
            "slash-dos" => "//./" + ownedTarget.Replace('\\', '/'),
            "slash-extended" => "//?/" + ownedTarget.Replace('\\', '/'),
            _ => throw new InvalidOperationException("Unknown namespace fixture."),
        };
        var options = new SiteGenerationOptions
        {
            BuildCacheDirectory = slot == "cache" ? unsupported : Path.Combine(workspace.Root, "safe-cache"),
            PublicDirectory = slot == "public" ? unsupported : null,
        };
        await Assert.That(async () => await InvokeCacheAdmissionFixture(operation,
            slot == "output" ? unsupported : output, options)).Throws<ArgumentException>();
        await Assert.That(SnapshotCacheCanaryTree(workspace.Root)).IsEqualTo(before);
        await Assert.That(File.GetLastWriteTimeUtc(canary)).IsEqualTo(stamp);
    }

    [Test]
    [Arguments("measure")]
    [Arguments("clear")]
    [Arguments("generate")]
    public async Task DisjointExtendedWindowsCacheRemainsUsable(string operation)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        Directory.CreateDirectory(output);
        var cache = Path.Combine(workspace.Root, "safe-cache");
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = FixedBuildTimestamp, BuildCacheDirectory = ExtendedCacheFixturePath(cache),
        };
        if (operation == "generate")
        {
            await InvokeCacheAdmissionFixture(operation, output, options);
            await Assert.That(File.Exists(Path.Combine(output, "posts", "alpha.html"))).IsTrue();
        }
        else
        {
            var partition = SiteGenerator.MeasureCache(output, options).CacheDirectory;
            Directory.CreateDirectory(partition);
            await File.WriteAllTextAsync(Path.Combine(partition, "record.txt"), "record");
            var usage = operation == "clear" ? SiteGenerator.ClearCache(output, options) : SiteGenerator.MeasureCache(output, options);
            await Assert.That(usage.FileCount).IsEqualTo(1);
            await Assert.That(usage.TotalBytes).IsEqualTo(6);
        }
    }

    [Test]
    [Arguments(@"\\?\D:\fixture\out", @"D:\fixture\out")]
    [Arguments(@"\\?\UNC\server\share\fixture\out", @"\\server\share\fixture\out")]
    [Arguments(@"\\?\unc\server\share\fixture\out", @"\\server\share\fixture\out")]
    [Arguments(@"D:\fixture\out", @"D:\fixture\out")]
    public async Task WindowsCacheNamespaceComparisonIdentityIsPure(string input, string expected)
    {
        // Pure identity classification only: no UNC share is opened or created.
        var method = typeof(SiteGenerator).GetMethod("NormalizeWindowsCacheNamespacePath",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        await Assert.That((string)method.Invoke(null, [input])!).IsEqualTo(expected);
    }

    [Test]
    [Arguments(@"\\?\D:\fixture\..\out")]
    [Arguments(@"\\?\D:\fixture\.\out")]
    [Arguments(@"\\?\D:\fixture\out.")]
    [Arguments(@"\\?\D:\fixture\out ")]
    [Arguments(@"\\?\UNC\server")]
    [Arguments(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\out")]
    [Arguments(@"\\?\D:\fixture\CON\out")]
    [Arguments(@"\\?\D:\fixture\nul.txt\out")]
    [Arguments(@"\\?\D:\fixture\COM1\out")]
    [Arguments(@"\\?\D:\fixture\LPT²\out")]
    [Arguments("//./D:/fixture/out")]
    [Arguments("//?/D:/fixture/out")]
    [Arguments(@"\\?/D:\fixture\out")]
    [Arguments(@"/\?\D:\fixture\out")]
    [Arguments("/??/D:/fixture/out")]
    public async Task WindowsCacheNamespaceRejectsAmbiguousOrdinaryEquivalence(string input)
    {
        var method = typeof(SiteGenerator).GetMethod("NormalizeWindowsCacheNamespacePath",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        ArgumentException? failure = null;
        try { method.Invoke(null, [input]); }
        catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is ArgumentException rejected)
        { failure = rejected; }
        await Assert.That(failure).IsNotNull();
    }

    private static string ExtendedCacheFixturePath(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ProspectiveCacheOwnershipMatchesActualWithoutCreatingParents(bool parentExists, bool unicode)
    {
        using var workspace = new TemporaryWorkspace();
        var parent = Path.Combine(workspace.Root, unicode ? "caf\u00e9-parent" : "new-parent", "nested-parent");
        var leaf = unicode ? "caf\u00e9-out" : "out";
        if (parentExists) Directory.CreateDirectory(parent);
        var prospective = InvokeCacheOwnershipIdentity("CreateProspectiveOwnershipScope", parent, leaf);
        await Assert.That(Directory.Exists(parent)).IsEqualTo(parentExists);
        Directory.CreateDirectory(parent);
        var actual = InvokeCacheOwnershipIdentity("CreateOwnershipScope", parent, leaf);
        await Assert.That(prospective).IsEqualTo(actual);
        await Assert.That(Directory.Exists(Path.Combine(parent, leaf))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GenerationRejectsProspectivePublicPartitionBeforeExtensionOrParentCreation(bool nested)
    {
        using var workspace = new TemporaryWorkspace();
        var parent = Path.Combine(workspace.Root, "not-created", "nested-parent");
        var output = Path.Combine(parent, "out");
        var scope = InvokeCacheOwnershipIdentity("CreateProspectiveOwnershipScope", parent, "out");
        var identity = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope)));
        var cache = Path.Combine(workspace.Root, "safe-cache");
        var partition = Path.Combine(cache, identity);
        var publicRoot = nested ? Path.Combine(partition, "public-child") : partition;
        Directory.CreateDirectory(publicRoot);
        var canary = Path.Combine(publicRoot, "canary.txt");
        await File.WriteAllTextAsync(canary, "prospective public input must survive");
        var before = SnapshotCacheCanaryTree(workspace.Root);
        var stamp = File.GetLastWriteTimeUtc(canary);
        var extension = new CacheAdmissionExtensionProbe();
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = FixedBuildTimestamp, BuildCacheDirectory = cache,
            PublicDirectory = publicRoot, Extensions = [extension],
        };
        await Assert.That(async () => await InvokeCacheAdmissionFixture("generate", output, options))
            .Throws<ArgumentException>();
        await Assert.That(extension.Calls).IsEqualTo(0);
        await Assert.That(Directory.Exists(parent)).IsFalse();
        await Assert.That(SnapshotCacheCanaryTree(workspace.Root)).IsEqualTo(before);
        await Assert.That(await File.ReadAllTextAsync(canary)).IsEqualTo("prospective public input must survive");
        await Assert.That(File.GetLastWriteTimeUtc(canary)).IsEqualTo(stamp);
    }

    private sealed class CacheAdmissionExtensionProbe : LithoSharp.Build.ISiteBuildExtension
    {
        public int Calls { get; private set; }
        public Task<LithoSharp.Build.SiteBuildContribution> PrepareAsync(LithoSharp.Build.SiteBuildContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new LithoSharp.Build.SiteBuildContribution());
        }
    }

    private static string InvokeCacheOwnershipIdentity(string method, string parent, string leaf)
    {
        var transaction = typeof(SiteGenerator).GetNestedType("OutputTransaction", System.Reflection.BindingFlags.NonPublic)!;
        var implementation = transaction.GetMethod(method, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return (string)implementation.Invoke(null, [parent, leaf])!;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OrdinaryAndExtendedOutputSpellingsShareOwnershipAndCachePartition(bool outputExists)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var parent = Path.Combine(workspace.Root, "parent");
        Directory.CreateDirectory(parent);
        var output = Path.Combine(parent, "out");
        if (outputExists) Directory.CreateDirectory(output);
        var extended = ExtendedCacheFixturePath(output);
        var scope = InvokeCacheOwnershipIdentity("CreateOwnershipScope", parent, "out");
        var aliasScope = InvokeCacheOwnershipIdentity("CreateOwnershipScope", ExtendedCacheFixturePath(parent), "out");
        await Assert.That(aliasScope).IsEqualTo(scope);
        var options = new SiteGenerationOptions { BuildCacheDirectory = Path.Combine(workspace.Root, "safe-cache") };
        var ordinaryUsage = SiteGenerator.MeasureCache(output, options);
        var aliasUsage = SiteGenerator.MeasureCache(extended, options);
        await Assert.That(aliasUsage.CacheDirectory).IsEqualTo(ordinaryUsage.CacheDirectory);
    }

    [Test]
    [Arguments(@"D:\fixture\out", "7bd3462bcf81ea9f2b3bccca3d6edc13ae2bafe28af50ea2c21b4e3db9d8a941")]
    [Arguments(@"d:\FIXTURE\out", "7bd3462bcf81ea9f2b3bccca3d6edc13ae2bafe28af50ea2c21b4e3db9d8a941")]
    [Arguments(@"\\server\share\fixture\out", "0bf52c18b03172bb4e79b70aca76f21cff252598f37b31ad153e7514f0a857e8")]
    [Arguments(@"\\SERVER\SHARE\fixture\out", "0bf52c18b03172bb4e79b70aca76f21cff252598f37b31ad153e7514f0a857e8")]
    public async Task NamespaceLockIdentityPreservesOrdinaryHashAndAliases(string ordinary, string expected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var transaction = typeof(SiteGenerator).GetNestedType("OutputTransaction", System.Reflection.BindingFlags.NonPublic)!;
        var method = transaction.GetMethod("CreateLockIdentity", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var before = (string)method.Invoke(null, [ordinary])!;
        var alias = (string)method.Invoke(null, [ExtendedCacheFixturePath(ordinary)])!;
        await Assert.That(before).IsEqualTo(expected);
        await Assert.That(alias).IsEqualTo(expected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClearCacheWaitsForActiveBuildThroughOppositeWindowsNamespace(bool extendedBuild)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "out");
        var buildOutput = extendedBuild ? ExtendedCacheFixturePath(output) : output;
        var clearOutput = extendedBuild ? output : ExtendedCacheFixturePath(output);
        var cache = Path.Combine(workspace.Root, "safe-cache");
        var settings = new SiteSettings { BaseUrl = "https://example.test/" };
        var firstCollection = CacheCollection("namespace first", (entry, context) => context.RenderDocument(entry.Body));
        var options = new SiteGenerationOptions
        {
            ContentCollections = [firstCollection], BuildTimestamp = FixedBuildTimestamp, BuildCacheDirectory = cache,
        };
        var first = await new SiteGenerator().GenerateWithOptionsAsync(settings, [], buildOutput, clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() }, options, CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var clearStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changedCollection = CacheCollection("namespace second", (entry, context) =>
        {
            entered.TrySetResult(); release.Wait(); return context.RenderDocument(entry.Body);
        });
        var building = new SiteGenerator().GenerateWithOptionsAsync(settings, [], buildOutput, clean: false,
            new SiteCustomization { Template = new BlogSiteTemplate() },
            options with { ContentCollections = [changedCollection], PreviousBuildPlan = first.BuildPlan }, CancellationToken.None);
        Task<LithoSharp.Build.SiteBuildCacheUsage>? clearing = null;
        Exception? failure = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clearing = Task.Factory.StartNew(() =>
            {
                clearStarted.TrySetResult();
                return SiteGenerator.ClearCache(clearOutput, options);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await clearStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            await Assert.That(clearing.IsCompleted).IsFalse();
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            release.Set();
            try { await Task.WhenAll(building, (Task?)clearing ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception cleanup)
            {
                if (failure is not null) throw new AggregateException("Assertion and owned fixture cleanup failed.", failure, cleanup);
                throw;
            }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        var cleared = await clearing!;
        await Assert.That(cleared.FileCount).IsGreaterThan(0);
        await Assert.That(SiteGenerator.MeasureCache(output, options).FileCount).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "cache", "index.html"))).Contains("namespace second");
    }


    [Test]
    [Arguments("asset", false)]
    [Arguments("asset", true)]
    [Arguments("external-cache-output", false)]
    [Arguments("external-cache-output", true)]
    [Arguments("asset-cache-output", false)]
    [Arguments("asset-cache-output", true)]
    [Arguments("public-same-output", false)]
    [Arguments("public-same-output", true)]
    [Arguments("public-child-output", false)]
    [Arguments("public-child-output", true)]
    [Arguments("public-parent-output", false)]
    [Arguments("public-parent-output", true)]
    [Arguments("asset-cache-public", false)]
    [Arguments("asset-cache-public", true)]
    [Arguments("external-cache-public", false)]
    [Arguments("external-cache-public", true)]
    public async Task Lsr21OppositeNamespaceInputMustBeRejectedBeforePublication(string inputKind, bool extendedOutput)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        using var cacheWorkspace = new TemporaryWorkspace();
        var physicalOutput = Path.Combine(workspace.Root, "out");
        Directory.CreateDirectory(physicalOutput);
        var sourceCanary = Path.Combine(physicalOutput, "source.bin");
        var publishedCanary = Path.Combine(physicalOutput, "published.html");
        var sourceBytes = new byte[] { 0, 1, 2, 255, 13, 10, 42 };
        await File.WriteAllBytesAsync(sourceCanary, sourceBytes);
        await File.WriteAllTextAsync(publishedCanary, "prior published input canary");
        var output = extendedOutput ? ExtendedCacheFixturePath(physicalOutput) : physicalOutput;
        string InputSpelling(string path) => extendedOutput ? path : ExtendedCacheFixturePath(path);
        var publicRoot = Path.Combine(workspace.Root, "public");
        Directory.CreateDirectory(publicRoot);
        var publicCanary = Path.Combine(publicRoot, "public.txt");
        await File.WriteAllTextAsync(publicCanary, "public input canary");
        var extension = new CacheAdmissionExtensionProbe();
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = FixedBuildTimestamp, BuildCacheDirectory = Path.Combine(cacheWorkspace.Root, "safe-cache"),
            Extensions = [extension],
        };
        options = inputKind switch
        {
            "asset" => options with { Assets = [new SiteAsset("canary", InputSpelling(physicalOutput), "source.bin", "assets/source.bin")] },
            "external-cache-output" => options with { Quality = new LithoSharp.Quality.SiteQualityOptions(checkOrphans: false,
                externalLinks: new LithoSharp.Quality.ExternalLinkCheckOptions(InputSpelling(Path.Combine(physicalOutput, "links.json")))) },
            "asset-cache-output" => options with { AssetCacheDirectory = InputSpelling(Path.Combine(physicalOutput, "asset-cache")) },
            "public-same-output" => options with { PublicDirectory = InputSpelling(physicalOutput) },
            "public-child-output" => options with { PublicDirectory = InputSpelling(Path.Combine(physicalOutput, "public-child")) },
            "public-parent-output" => options with { PublicDirectory = InputSpelling(workspace.Root) },
            "asset-cache-public" => options with { PublicDirectory = extendedOutput ? ExtendedCacheFixturePath(publicRoot) : publicRoot,
                AssetCacheDirectory = InputSpelling(Path.Combine(publicRoot, "asset-cache")) },
            "external-cache-public" => options with { PublicDirectory = extendedOutput ? ExtendedCacheFixturePath(publicRoot) : publicRoot,
                Quality = new LithoSharp.Quality.SiteQualityOptions(checkOrphans: false,
                    externalLinks: new LithoSharp.Quality.ExternalLinkCheckOptions(InputSpelling(Path.Combine(publicRoot, "links.json")))) },
            _ => throw new InvalidOperationException("Unknown owned input fixture."),
        };
        if (inputKind == "public-child-output") Directory.CreateDirectory(Path.Combine(physicalOutput, "public-child"));
        var before = ObserveLsrCanaries(sourceCanary, publishedCanary, publicCanary);
        var beforeTree = SnapshotCacheCanaryTree(workspace.Root);
        var sourceStamp = File.GetLastWriteTimeUtc(sourceCanary);
        Exception? generationFailure = null;
        var generationCompleted = false;
        try
        {
            await new SiteGenerator().GenerateWithOptionsAsync(
                new SiteSettings { BaseUrl = "https://example.test/" }, [Post()], output, clean: true,
                new SiteCustomization { Template = new BlogSiteTemplate() }, options, CancellationToken.None);
            generationCompleted = true;
        }
        catch (Exception exception) { generationFailure = exception; }
        finally
        {
            WriteLsrCanaryObservation("LSR21", inputKind, extendedOutput, generationCompleted, generationFailure,
                extension.Calls, before, ObserveLsrCanaries(sourceCanary, publishedCanary, publicCanary));
        }
        await Assert.That(generationFailure is ArgumentException).IsTrue();
        var expectedGuardMessage = inputKind switch
        {
            "asset" => "Asset input files must be outside the output directory.",
            "external-cache-output" => "The external link cache must be outside the site output directory.",
            "asset-cache-output" => "The asset cache must be outside the output directory.",
            "asset-cache-public" => "The asset cache must be outside the public input directory.",
            "external-cache-public" => "The external link cache must be outside the public input directory.",
            _ => "The public input directory and output directory must not overlap.",
        };
        await Assert.That(generationFailure?.Message ?? "").Contains(expectedGuardMessage);
        await Assert.That(generationCompleted).IsFalse();
        await Assert.That(extension.Calls).IsEqualTo(0);
        await Assert.That(File.Exists(sourceCanary)).IsTrue();
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(sourceCanary)))
            .IsEqualTo(Convert.ToHexString(sourceBytes));
        await Assert.That(File.GetLastWriteTimeUtc(sourceCanary)).IsEqualTo(sourceStamp);
        await Assert.That(SnapshotCacheCanaryTree(workspace.Root)).IsEqualTo(beforeTree);
        await Assert.That(ObserveLsrCanaries(sourceCanary, publishedCanary, publicCanary)).IsEqualTo(before);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Lsr23QualityReadsExtendedOutputOnUnchangedCacheHit(bool explicitExtendedCache)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var physicalOutput = Path.Combine(workspace.Root, "out");
        var output = ExtendedCacheFixturePath(physicalOutput);
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = FixedBuildTimestamp,
            BuildCacheDirectory = explicitExtendedCache ? ExtendedCacheFixturePath(Path.Combine(workspace.Root, "cache")) : null,
            Quality = new LithoSharp.Quality.SiteQualityOptions(checkOrphans: false),
        };
        var settings = new SiteSettings { BaseUrl = "https://example.test/" };
        var customization = new SiteCustomization { Template = new BlogSiteTemplate() };
        var first = await ObserveLsrInitialGenerationAsync(explicitExtendedCache,
            [Path.Combine(physicalOutput, "posts", "alpha.html"), Path.Combine(physicalOutput, "search-index.json")],
            () => new SiteGenerator().GenerateWithOptionsAsync(settings, [Post()], output,
                clean: true, customization, options, CancellationToken.None));
        var page = Path.Combine(physicalOutput, "posts", "alpha.html");
        var search = Path.Combine(physicalOutput, "search-index.json");
        var before = ObserveLsrCanaries(page, search);
        Exception? failure = null;
        var completed = false;
        try
        {
            var second = await new SiteGenerator().GenerateWithOptionsAsync(settings, [Post()], output,
                clean: false, customization, options with { PreviousBuildPlan = first.BuildPlan }, CancellationToken.None);
            completed = true;
            await Assert.That(second.BuildReport.CacheHitCount).IsGreaterThan(0);
            await Assert.That(second.BuildReport.Nodes.Single(node => node.NodeId == "index:search").CacheHit).IsTrue();
            await Assert.That(second.QualityReport.Diagnostics.Any(diagnostic => diagnostic.Severity >= SiteDiagnosticSeverity.Error)).IsFalse();
            await Assert.That(ObserveLsrCanaries(page, search)).IsEqualTo(before);
        }
        catch (Exception exception) { failure = exception; throw; }
        finally { WriteLsrCanaryObservation("LSR23-quality", "unchanged", explicitExtendedCache, completed, failure, 0, before, ObserveLsrCanaries(page, search)); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Lsr23CollectionSearchReadsCachedBodyThroughExtendedOutput(bool explicitExtendedCache)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var physicalOutput = Path.Combine(workspace.Root, "out");
        var output = ExtendedCacheFixturePath(physicalOutput);
        var renders = 0;
        const string collectionBody = "# Collection\n\nLSR23CachedCollectionBodyMarker";
        var collection = Lsr23CacheCollection(collectionBody, (entry, context) =>
        {
            renders++;
            return context.RenderDocument(entry.Body);
        });
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = FixedBuildTimestamp, ContentCollections = [collection],
            BuildCacheDirectory = explicitExtendedCache ? ExtendedCacheFixturePath(Path.Combine(workspace.Root, "cache")) : null,
        };
        var settings = new SiteSettings { BaseUrl = "https://example.test/" };
        var customization = new SiteCustomization { Template = new BlogSiteTemplate() };
        var first = await ObserveLsrInitialGenerationAsync(explicitExtendedCache,
            [Path.Combine(physicalOutput, "posts", "alpha.html"), Path.Combine(physicalOutput, "search-index.json")],
            () => new SiteGenerator().GenerateWithOptionsAsync(settings, [Post()], output,
                clean: true, customization, options, CancellationToken.None));
        await Assert.That(renders).IsEqualTo(1);
        var page = Path.Combine(physicalOutput, "cache", "index.html");
        var search = Path.Combine(physicalOutput, "search-index.json");
        var before = ObserveLsrCanaries(page, search);
        Exception? failure = null;
        var completed = false;
        try
        {
            var second = await new SiteGenerator().GenerateWithOptionsAsync(settings, [Post()], output,
                clean: false, customization, options with { PreviousBuildPlan = first.BuildPlan }, CancellationToken.None);
            await Assert.That(second.BuildReport.CacheHitCount).IsGreaterThan(0);
            await Assert.That(second.BuildReport.Nodes.Single(node => node.NodeId == "index:search").CacheHit).IsTrue();
            await Assert.That(renders).IsEqualTo(1);
            await Assert.That(ObserveLsrCanaries(page, search)).IsEqualTo(before);
            var changed = Post() with { MarkdownBody = Post().MarkdownBody + "\n\nLSR23ChangedPostBodyMarker" };
            var third = await new SiteGenerator().GenerateWithOptionsAsync(settings, [changed], output,
                clean: false, customization, options with { PreviousBuildPlan = second.BuildPlan }, CancellationToken.None);
            await Assert.That(third.BuildReport.Nodes.Single(node => node.NodeId == "index:search").CacheHit).IsFalse();
            await Assert.That(renders).IsEqualTo(1);
            var searchText = await File.ReadAllTextAsync(search);
            await Assert.That(searchText).Contains("LSR23CachedCollectionBodyMarker");
            await Assert.That(searchText).Contains("LSR23ChangedPostBodyMarker");
            var cleanOutput = Path.Combine(workspace.Root, "clean-reference");
            var cleanCollection = Lsr23CacheCollection(collectionBody, (entry, context) => context.RenderDocument(entry.Body));
            await new SiteGenerator().GenerateWithOptionsAsync(settings, [changed], cleanOutput,
                clean: true, customization, options with { ContentCollections = [cleanCollection], PreviousBuildPlan = null }, CancellationToken.None);
            await Assert.That(searchText).IsEqualTo(await File.ReadAllTextAsync(Path.Combine(cleanOutput, "search-index.json")));
            await Assert.That(await File.ReadAllTextAsync(page))
                .IsEqualTo(await File.ReadAllTextAsync(Path.Combine(cleanOutput, "cache", "index.html")));
            completed = true;
        }
        catch (Exception exception) { failure = exception; throw; }
        finally { WriteLsrCanaryObservation("LSR23-collection", "changed-search", explicitExtendedCache, completed, failure, renders, before, ObserveLsrCanaries(page, search)); }
    }


    private static SiteContentCollection<string, string> Lsr23CacheCollection(
        string body,
        ContentPageRenderer<string, string> renderer) =>
        new(new ContentCollection<string, string>(new("cache-lock"), Path.GetTempPath(),
            [new(new("entry"), "entry.md", Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))), "Cache lock", body)],
            _ => SiteRoute.ForDirectoryIndex("cache"), entry => new PageMetadata(entry.FrontMatter),
            transformationId: new("cache-lock:1"), isCacheable: true), renderer)
        { RendererFingerprint = "cache-lock-renderer:1", IsThreadSafe = false };

    private static async Task<SiteGenerationResult> ObserveLsrInitialGenerationAsync(bool explicitExtendedCache,
        string[] files, Func<Task<SiteGenerationResult>> generate)
    {
        var before = ObserveLsrCanaries(files);
        Exception? failure = null;
        var completed = false;
        try
        {
            var result = await generate();
            completed = true;
            return result;
        }
        catch (Exception exception) { failure = exception; throw; }
        finally { WriteLsrCanaryObservation("LSR23-initial", "cold", explicitExtendedCache, completed, failure, 0, before, ObserveLsrCanaries(files)); }
    }

    private static string ObserveLsrCanaries(params string[] files)
        => System.Text.Json.JsonSerializer.Serialize(files.Select((file, index) =>
        {
            try
            {
                var exists = File.Exists(file);
                return new { index, exists, bytes = exists ? new FileInfo(file).Length : -1,
                    sha256 = exists ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))) : null,
                    lastWriteUtcTicks = exists ? File.GetLastWriteTimeUtc(file).Ticks : (long?)null, error = (string?)null };
            }
            catch (Exception exception)
            {
                return new { index, exists = false, bytes = -1L, sha256 = (string?)null,
                    lastWriteUtcTicks = (long?)null, error = exception.GetType().Name };
            }
        }).ToArray());

    private static void WriteLsrCanaryObservation(string issue, string fixture, bool orientation,
        bool generationCompleted, Exception? failure, int calls, string before, string after)
    {
        // Observations remain available when the following test assertions fail. No fixture paths or input text are logged.
        try
        {
            Console.WriteLine("LSR_CANARY " + System.Text.Json.JsonSerializer.Serialize(new
            {
                issue, fixture, orientation, generationCompleted, exception = failure?.GetType().Name,
                observationError = (string?)null, calls, before = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(before),
                after = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(after),
            }));
        }
        catch (Exception observationFailure)
        {
            Exception? markerFailure = null;
            try
            {
                var message = observationFailure.Message;
                Console.Error.WriteLine("LSR_CANARY_OBSERVATION_ERROR " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    issue, fixture, orientation, observationError = observationFailure.GetType().Name,
                    secondaryMessage = message.Length > 512 ? message[..512] : message,
                    primaryException = failure?.GetType().Name,
                }));
            }
            catch (Exception secondaryMarkerFailure) { markerFailure = secondaryMarkerFailure; }
            if (failure is not null)
                throw new AggregateException("Generation and canary observation both failed.", markerFailure is null
                    ? new Exception[] { failure, observationFailure } : new Exception[] { failure, observationFailure, markerFailure });
            if (markerFailure is not null)
                throw new AggregateException("Canary observation and its error marker both failed.", observationFailure, markerFailure);
            throw;
        }
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

    [Test]
    [Arguments("case", false)]
    [Arguments("case", true)]
    [Arguments("unicode", false)]
    [Arguments("unicode", true)]
    public Task Lsr24PhysicalAssetInputAliasIsRejectedBeforePublication(string aliasKind, bool aliasOutput)
        => RunLsr24PhysicalInputFixture(
            aliasKind == "case" ? "CaseBoundary" : "café",
            aliasKind == "case" ? "caseboundary" : "café",
            aliasKind, aliasOutput, requireDisjoint: false);

    [Test]
    public Task Lsr24OrdinaryDisjointAssetInputStillPublishesCorrectBytes()
        => RunLsr24PhysicalInputFixture("ordinary-output", "ordinary-input",
            "ordinary-disjoint", aliasOutput: false, requireDisjoint: true);

    private static async Task RunLsr24PhysicalInputFixture(string physicalLeaf, string alternativeLeaf,
        string fixture, bool aliasOutput, bool requireDisjoint)
    {
        using var workspace = new TemporaryWorkspace();
        using var cacheWorkspace = new TemporaryWorkspace();
        var parent = InvokeLsr24ExistingDirectoryIdentity(workspace.Root);
        var physical = Path.Combine(parent, physicalLeaf);
        var alternative = Path.Combine(parent, alternativeLeaf);
        Directory.CreateDirectory(physical);
        // Detect physical aliasing before creating any second directory. No OS-wide case assumption.
        var actualAlias = Directory.Exists(alternative);
        if (requireDisjoint) await Assert.That(actualAlias).IsFalse();
        if (!actualAlias) Directory.CreateDirectory(alternative);
        var output = aliasOutput ? alternative : physical;
        var input = aliasOutput ? physical : alternative;
        var source = Path.Combine(input, "source.bin");
        var published = Path.Combine(output, "published.html");
        var sourceBytes = new byte[] { 0, 17, 255, 13, 10, 83, 24 };
        await File.WriteAllBytesAsync(source, sourceBytes);
        await File.WriteAllTextAsync(published, "prior published LSR24 canary");
        var sourceStamp = File.GetLastWriteTimeUtc(source);
        var publishedStamp = File.GetLastWriteTimeUtc(published);
        var publishedBytes = await File.ReadAllBytesAsync(published);
        var before = ObserveLsrCanaries(source, published);
        var beforeTree = SnapshotCacheCanaryTree(parent);
        var beforeDirectories = Lsr24DirectoryInventory(parent);
        var cacheBeforeTree = SnapshotCacheCanaryTree(cacheWorkspace.Root);
        var cacheBeforeDirectories = Lsr24DirectoryInventory(cacheWorkspace.Root);
        var extension = new CacheAdmissionExtensionProbe();
        Exception? failure = null;
        var completed = false;
        SiteGenerationResult? generationResult = null;
        try
        {
            generationResult = await new SiteGenerator().GenerateWithOptionsAsync(
                new SiteSettings { BaseUrl = "https://example.test/" }, [Post()], output, clean: true,
                new SiteCustomization { Template = new BlogSiteTemplate() },
                new SiteGenerationOptions
                {
                    BuildTimestamp = FixedBuildTimestamp,
                    BuildCacheDirectory = Path.Combine(cacheWorkspace.Root, "safe-cache"),
                    Assets = [new SiteAsset("lsr24-canary", input, "source.bin", "assets/source.bin")],
                    Extensions = [extension],
                }, CancellationToken.None);
            completed = true;
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            WriteLsr24PhysicalAliasObservation(fixture, actualAlias, aliasOutput, completed, failure,
                extension.Calls, before, ObserveLsrCanaries(source, published));
        }
        if (actualAlias)
        {
            await Assert.That(failure is ArgumentException).IsTrue();
            await Assert.That(failure?.Message ?? "").Contains("Asset input files must be outside the output directory.");
            await Assert.That(completed).IsFalse();
            await Assert.That(extension.Calls).IsEqualTo(0);
            await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(source))).IsEqualTo(Convert.ToHexString(sourceBytes));
            await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(published))).IsEqualTo(Convert.ToHexString(publishedBytes));
            await Assert.That(File.GetLastWriteTimeUtc(source)).IsEqualTo(sourceStamp);
            await Assert.That(File.GetLastWriteTimeUtc(published)).IsEqualTo(publishedStamp);
            await Assert.That(ObserveLsrCanaries(source, published)).IsEqualTo(before);
            await Assert.That(SnapshotCacheCanaryTree(parent)).IsEqualTo(beforeTree);
            await Assert.That(Lsr24DirectoryInventory(parent)).IsEqualTo(beforeDirectories);
            await Assert.That(SnapshotCacheCanaryTree(cacheWorkspace.Root)).IsEqualTo(cacheBeforeTree);
            await Assert.That(Lsr24DirectoryInventory(cacheWorkspace.Root)).IsEqualTo(cacheBeforeDirectories);
        }
        else
        {
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            await Assert.That(completed).IsTrue();
            await Assert.That(extension.Calls).IsEqualTo(1);
            await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(source))).IsEqualTo(Convert.ToHexString(sourceBytes));
            await Assert.That(File.GetLastWriteTimeUtc(source)).IsEqualTo(sourceStamp);
            await Assert.That(generationResult is not null).IsTrue();
            var assetArtifact = generationResult!.BuildPlan.Artifacts.Single(
                artifact => artifact.Id.Value == "asset:lsr24-canary");
            await Assert.That(Path.IsPathRooted(assetArtifact.RelativeOutputPath)).IsFalse();
            var registeredPath = Path.GetFullPath(Path.Combine(output, assetArtifact.RelativeOutputPath));
            var outputPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output)) + Path.DirectorySeparatorChar;
            await Assert.That(registeredPath.StartsWith(outputPrefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).IsTrue();
            await Assert.That(generationResult.GeneratedFiles.Contains(registeredPath, StringComparer.Ordinal)).IsTrue();
            await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(registeredPath))).IsEqualTo(Convert.ToHexString(sourceBytes));
            await Assert.That(File.Exists(Path.Combine(output, "posts", "alpha.html"))).IsTrue();
        }
    }

    private static string InvokeLsr24ExistingDirectoryIdentity(string directory)
    {
        var transaction = typeof(SiteGenerator).GetNestedType("OutputTransaction", System.Reflection.BindingFlags.NonPublic)!;
        var resolver = transaction.GetMethod("ResolveExistingDirectoryPath",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return (string)resolver.Invoke(null, [directory])!;
    }

    private static string Lsr24DirectoryInventory(string directory)
        => string.Join("\n", Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path)).Order(StringComparer.Ordinal));

    private static void WriteLsr24PhysicalAliasObservation(string fixture, bool actualAlias, bool orientation,
        bool completed, Exception? failure, int calls, string before, string after)
    {
        WriteLsrCanaryObservation("LSR24", fixture, orientation, completed, failure, calls, before, after);
        try
        {
            Console.WriteLine("LSR24_ALIAS " + System.Text.Json.JsonSerializer.Serialize(new
            {
                fixture, actualAlias, orientation, windows = OperatingSystem.IsWindows(),
                macOS = OperatingSystem.IsMacOS(), linux = OperatingSystem.IsLinux(),
                detectionBeforeSecondDirectoryCreation = true,
                outcome = actualAlias ? "overlap-rejection-required" : "distinct-input-success-required",
            }));
        }
        catch (Exception observationFailure)
        {
            if (failure is not null)
                throw new AggregateException("Generation and physical-alias observation both failed.", failure, observationFailure);
            throw;
        }
    }
}
