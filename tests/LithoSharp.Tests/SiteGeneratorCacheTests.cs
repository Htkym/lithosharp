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
