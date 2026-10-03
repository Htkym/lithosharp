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
