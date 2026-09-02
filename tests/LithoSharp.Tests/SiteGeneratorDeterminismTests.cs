using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LithoSharp.Configuration;
using LithoSharp.Content;

namespace LithoSharp.Tests;

[NotInParallel]
public sealed class SiteGeneratorDeterminismTests
{
    private static readonly DateTimeOffset FixedBuildTimestamp =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Test]
    public async Task GenerateAsync_ExplicitTimestamp_TakesPrecedenceAndDrivesSearchArtifacts()
    {
        using var environment = new EnvironmentVariableScope("SOURCE_DATE_EPOCH", "invalid");
        using var workspace = new TemporaryWorkspace();
        var (posts, output) = await PrepareBlogAsync(workspace, "explicit");
        var options = new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp };

        await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            posts,
            output,
            clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() },
            options,
            CancellationToken.None);

        var generated = await ReadGeneratedTimestampAsync(output);
        var searchPage = await File.ReadAllTextAsync(Path.Combine(output, "search.html"));
        var searchIndexBytes = await File.ReadAllBytesAsync(
            Path.Combine(output, "search-index.json"));
        await Assert.That(generated).IsEqualTo(FixedBuildTimestamp);
        await Assert.That(searchPage).Contains($"?v={Sha256(searchIndexBytes)}");
    }

    [Test]
    public async Task GenerateAsync_SourceDateEpoch_DrivesSearchArtifacts()
    {
        var epoch = FixedBuildTimestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var environment = new EnvironmentVariableScope("SOURCE_DATE_EPOCH", epoch);
        using var workspace = new TemporaryWorkspace();
        var (posts, output) = await PrepareBlogAsync(workspace, "source-date-epoch");

        await new SiteGenerator().GenerateAsync(
            TestSite(),
            posts,
            output,
            clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() });

        var generated = await ReadGeneratedTimestampAsync(output);
        var searchPage = await File.ReadAllTextAsync(Path.Combine(output, "search.html"));
        var searchIndexBytes = await File.ReadAllBytesAsync(
            Path.Combine(output, "search-index.json"));
        await Assert.That(generated).IsEqualTo(FixedBuildTimestamp);
        await Assert.That(searchPage).Contains($"?v={Sha256(searchIndexBytes)}");
    }

    [Test]
    public async Task GenerateAsync_InvalidSourceDateEpoch_FailsExplicitly()
    {
        using var environment = new EnvironmentVariableScope("SOURCE_DATE_EPOCH", "not-a-unix-timestamp");
        using var workspace = new TemporaryWorkspace();
        var (posts, output) = await PrepareBlogAsync(workspace, "invalid-source-date-epoch");

        await Assert.That(async () => await new SiteGenerator().GenerateAsync(
                TestSite(),
                posts,
                output,
                clean: true,
                new SiteCustomization { Template = new BlogSiteTemplate() }))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("SOURCE_DATE_EPOCH")
            .And
            .WithMessageContaining("valid Unix timestamp");
        await Assert.That(Directory.Exists(output)).IsFalse();
    }

    [Test]
    public async Task GenerateAsync_LegacyApi_UsesCurrentUtcWhenNoTimestampIsConfigured()
    {
        using var environment = new EnvironmentVariableScope("SOURCE_DATE_EPOCH", null);
        using var workspace = new TemporaryWorkspace();
        var (posts, output) = await PrepareBlogAsync(workspace, "legacy");
        var before = DateTimeOffset.UtcNow;

        await new SiteGenerator().GenerateAsync(
            TestSite(),
            posts,
            output,
            clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() },
            default);

        var after = DateTimeOffset.UtcNow;
        var generated = await ReadGeneratedTimestampAsync(output);
        await Assert.That(generated >= before && generated <= after).IsTrue();
    }

    [Test]
    public async Task GenerateAsync_TwoCleanBuilds_HaveByteIdenticalTextArtifacts()
    {
        using var environment = new EnvironmentVariableScope("SOURCE_DATE_EPOCH", null);
        using var workspace = new TemporaryWorkspace();
        var (posts, firstOutput) = await PrepareBlogAsync(workspace, "first");
        var secondOutput = Path.Combine(workspace.Root, "second-output");
        var customization = new SiteCustomization { Template = new BlogSiteTemplate() };
        var options = new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp };
        var generator = new SiteGenerator();

        await generator.GenerateWithOptionsAsync(
            TestSite(),
            posts,
            firstOutput,
            clean: true,
            customization,
            options,
            CancellationToken.None);
        await generator.GenerateWithOptionsAsync(
            TestSite(),
            posts,
            secondOutput,
            clean: true,
            customization,
            options,
            CancellationToken.None);

        var firstFiles = EnumerateRelativeFiles(firstOutput);
        var secondFiles = EnumerateRelativeFiles(secondOutput);
        await Assert.That(secondFiles.SequenceEqual(firstFiles)).IsTrue();
        foreach (var relativePath in firstFiles)
        {
            var firstBytes = await File.ReadAllBytesAsync(Path.Combine(firstOutput, relativePath));
            var secondBytes = await File.ReadAllBytesAsync(Path.Combine(secondOutput, relativePath));
            await Assert.That(secondBytes.SequenceEqual(firstBytes)).IsTrue();
        }

        var firstIndex = await File.ReadAllBytesAsync(
            Path.Combine(firstOutput, "search-index.json"));
        var firstSearch = await File.ReadAllTextAsync(Path.Combine(firstOutput, "search.html"));
        var secondSearch = await File.ReadAllTextAsync(Path.Combine(secondOutput, "search.html"));
        await Assert.That(firstSearch).Contains($"?v={Sha256(firstIndex)}");
        await Assert.That(secondSearch).IsEqualTo(firstSearch);
    }

    [Test]
    public async Task GenerateAsync_SameTimestampChangedSearchContentChangesCacheVersion()
    {
        using var workspace = new TemporaryWorkspace();
        var original = await PrepareBlogAsync(workspace, "original");
        var changedPosts = original.Posts
            .Select(post => post with { MarkdownBody = post.MarkdownBody + "\nChanged." })
            .ToArray();
        var changedOutput = Path.Combine(workspace.Root, "changed-output");
        var customization = new SiteCustomization { Template = new BlogSiteTemplate() };
        var options = new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp };
        var generator = new SiteGenerator();

        await generator.GenerateWithOptionsAsync(
            TestSite(),
            original.Posts,
            original.Output,
            clean: true,
            customization,
            options,
            CancellationToken.None);
        await generator.GenerateWithOptionsAsync(
            TestSite(),
            changedPosts,
            changedOutput,
            clean: true,
            customization,
            options,
            CancellationToken.None);

        var originalIndex = await File.ReadAllBytesAsync(
            Path.Combine(original.Output, "search-index.json"));
        var changedIndex = await File.ReadAllBytesAsync(
            Path.Combine(changedOutput, "search-index.json"));
        var originalHash = Sha256(originalIndex);
        var changedHash = Sha256(changedIndex);
        await Assert.That(changedHash).IsNotEqualTo(originalHash);
        await Assert.That(await File.ReadAllTextAsync(
                Path.Combine(original.Output, "search.html")))
            .Contains($"?v={originalHash}");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(changedOutput, "search.html")))
            .Contains($"?v={changedHash}");
    }

    [Test]
    public async Task GenerateAsync_TextArtifacts_UseLfLineEndings()
    {
        using var environment = new EnvironmentVariableScope("SOURCE_DATE_EPOCH", null);
        using var workspace = new TemporaryWorkspace();
        var (posts, output) = await PrepareBlogAsync(workspace, "line-endings");
        var customization = new SiteCustomization
        {
            Template = new BlogSiteTemplate(),
            ExtraPages =
            [
                new SiteExtraPage
                {
                    RelativePath = "mixed-line-endings.html",
                    Title = "Line endings",
                    BodyHtml = "<p>first</p>\r\n<p>second</p>\r<p>third</p>\n"
                }
            ]
        };

        await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            posts,
            output,
            clean: true,
            customization,
            new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp },
            CancellationToken.None);

        foreach (var relativePath in EnumerateRelativeFiles(output))
        {
            var text = await File.ReadAllTextAsync(Path.Combine(output, relativePath));
            await Assert.That(text.Contains('\r')).IsFalse();
        }

        var extraPage = await File.ReadAllTextAsync(Path.Combine(output, "mixed-line-endings.html"));
        await Assert.That(extraPage).Contains("<p>first</p>\n<p>second</p>\n<p>third</p>\n");
        var feed = await File.ReadAllTextAsync(Path.Combine(output, "feed.xml"));
        await Assert.That(feed).Contains("\n");
    }

    private static SiteSettings TestSite() => new()
    {
        Title = "Test Site",
        Description = "A test site.",
        BaseUrl = "https://example.test/",
        Language = "en",
        TimeZone = "UTC"
    };

    private static async Task<(IReadOnlyList<MarkdownPost> Posts, string Output)> PrepareBlogAsync(
        TemporaryWorkspace workspace,
        string name)
    {
        var content = Path.Combine(workspace.Root, $"{name}-content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "post.md"), """
            ---
            title: "First Post"
            date: "2026-01-02T03:04:05Z"
            summary: "the first post"
            tags:
              - sample
            ---

            Body text.
            """);
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        return (posts, Path.Combine(workspace.Root, $"{name}-output"));
    }

    private static async Task<DateTimeOffset> ReadGeneratedTimestampAsync(string output)
    {
        await using var stream = File.OpenRead(Path.Combine(output, "search-index.json"));
        using var document = await JsonDocument.ParseAsync(stream);
        return DateTimeOffset.Parse(
            document.RootElement.GetProperty("generated").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    private static string[] EnumerateRelativeFiles(string output) =>
        Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(output, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _originalValue);
    }
}
