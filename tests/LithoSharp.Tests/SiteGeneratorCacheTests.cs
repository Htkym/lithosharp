using LithoSharp.Configuration;
using LithoSharp.Content;

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
}
