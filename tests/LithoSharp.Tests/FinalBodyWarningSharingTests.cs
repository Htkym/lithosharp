using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class FinalBodyWarningSharingTests
{
    private static readonly DateTimeOffset BuildTimestamp =
        new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task DirectPostWarningsMatchAcrossCleanAndNoOpBuilds()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        const string body = "\uFEFF\r\n\r\n## Final 😀\r\n\r\nSee [^note] and $x$.\r\n";
        var post = Post(body);
        var generator = new SiteGenerator();

        var clean = await GenerateAsync(generator, post, output, clean: true);

        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(1);
        await Assert.That(CompilerWarnings(clean).Select(diagnostic => diagnostic.Id).ToArray())
            .IsEquivalentTo([LithoLimits.UnsupportedFootnoteDiagnosticId, LithoLimits.BrowserAssetDiagnosticId]);
        await AssertWarningPositionAsync(clean, post, LithoLimits.UnsupportedFootnoteDiagnosticId, "[^note]");
        await AssertWarningPositionAsync(clean, post, LithoLimits.BrowserAssetDiagnosticId, "$x$");
        var partition = SiteGenerator.MeasureCache(output).CacheDirectory;
        var cache = new SiteBuildCache(Path.GetDirectoryName(partition)!, Path.GetFileName(partition));
        await Assert.That(await cache.ReadPostParseAsync(generator.MarkdownCompiler.Fingerprint,
            MarkdownDocumentFingerprints.SourceHash(body), body.Length, CancellationToken.None)).IsNull();

        var warm = await GenerateAsync(generator, post, output, clean: false, clean.BuildPlan);

        // Diagnostic-bearing results are shared within a build, but the v2
        // persistent parse cache deliberately cannot represent their diagnostics.
        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(2);
        await Assert.That(warm.BuildReport.CacheMissCount).IsEqualTo(0);
        await Assert.That(WarningSnapshot(warm)).IsEqualTo(WarningSnapshot(clean));

        var freshGenerator = new SiteGenerator();
        var fresh = await GenerateAsync(freshGenerator, post, output, clean: false, warm.BuildPlan);

        await Assert.That(freshGenerator.MarkdownCompiler.ParseCount).IsEqualTo(1);
        await Assert.That(fresh.BuildReport.CacheMissCount).IsEqualTo(0);
        await Assert.That(WarningSnapshot(fresh)).IsEqualTo(WarningSnapshot(clean));
    }

    [Test]
    public async Task DiagnosticsFreePostsReusePersistentAnalysisOnNoOpBuilds()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var post = Post("## Final heading\n\nBody without unsupported syntax.\n");
        var generator = new SiteGenerator();
        var clean = await GenerateAsync(generator, post, output, clean: true);

        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(1);
        await Assert.That(CompilerWarnings(clean)).IsEmpty();

        var warm = await GenerateAsync(generator, post, output, clean: false, clean.BuildPlan);
        var freshGenerator = new SiteGenerator();
        var fresh = await GenerateAsync(freshGenerator, post, output, clean: false, warm.BuildPlan);

        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(1);
        await Assert.That(freshGenerator.MarkdownCompiler.ParseCount).IsEqualTo(0);
        await Assert.That(warm.BuildReport.CacheMissCount).IsEqualTo(0);
        await Assert.That(fresh.BuildReport.CacheMissCount).IsEqualTo(0);
        await Assert.That(CompilerWarnings(warm)).IsEmpty();
        await Assert.That(CompilerWarnings(fresh)).IsEmpty();
    }

    [Test]
    public async Task ReplacedBodyControlsWarningsAndPreservesParserExclusions()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var post = Post("See [^first] and $x$.\n");
        var generator = new SiteGenerator();
        var first = await GenerateAsync(generator, post, output, clean: true);
        await Assert.That(CompilerWarnings(first).Length).IsEqualTo(2);

        const string literalBody = """
            ## Replacement

            ```text
            [^fenced] $fenced$
            ```

            `[^inline] $inline$`

            \[^escaped]

            <span data-note="[^attribute]">text</span>

            [text](https://example.test/ "[^title]")
            """;
        var replaced = post with { MarkdownBody = literalBody };
        var removed = await GenerateAsync(generator, replaced, output, clean: false, first.BuildPlan);

        await Assert.That(CompilerWarnings(removed)).IsEmpty();

        var added = replaced with { MarkdownBody = literalBody + "\n\nSee [^second] and $y$.\n" };
        var restored = await GenerateAsync(generator, added, output, clean: false, removed.BuildPlan);

        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(3);
        await Assert.That(CompilerWarnings(restored).Length).IsEqualTo(2);
        await AssertWarningPositionAsync(restored, added, LithoLimits.UnsupportedFootnoteDiagnosticId, "[^second]");
        await AssertWarningPositionAsync(restored, added, LithoLimits.BrowserAssetDiagnosticId, "$y$");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "post.html")))
            .Contains("[^second]");
    }

    [Test]
    public async Task ReaderDiagnosticsKeepOriginalSourceSnapshotWhenBodyIsReplaced()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "post.md");
        await File.WriteAllTextAsync(path, """
            ---
            title: "Original"
            date: "2026-01-02T03:04:05Z"
            summary: "Original summary."
            tags:
              - test
            ---

            Note[^original]
            """);
        var read = await new MarkdownPostReader().ReadAsync(path, workspace.Root);
        var replaced = read with { MarkdownBody = "A replacement without unsupported syntax.\n" };
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();
        var result = await GenerateAsync(generator, replaced, output, clean: true);
        var diagnostic = CompilerWarnings(result).Single();

        await Assert.That(diagnostic.Id).IsEqualTo(LithoLimits.UnsupportedFootnoteDiagnosticId);
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo(path);
        await Assert.That(diagnostic.Location.Line).IsEqualTo(9);
        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(1);
        var html = await File.ReadAllTextAsync(Path.Combine(output, "posts", "post.html"));
        await Assert.That(html).Contains("A replacement without unsupported syntax.");
        await Assert.That(html).DoesNotContain("[^original]");
    }

    [Test]
    public async Task IncompleteFinalBodyReportsFileOnlyAndPreservesPublishedOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var post = Post("## Published heading\n\nPublished body.\n");
        var generator = new SiteGenerator();
        var published = await GenerateAsync(generator, post, output, clean: true);
        var articlePath = Path.Combine(output, "posts", "post.html");
        var searchPath = Path.Combine(output, "search-index.json");
        var article = await File.ReadAllBytesAsync(articlePath);
        var search = await File.ReadAllBytesAsync(searchPath);
        const string invalidBody = "## Unpublished heading\n\nKnown 😀 \uD800";
        var invalid = post with { MarkdownBody = invalidBody };
        var bodyDiagnostic = new LithoMarkdownCompiler()
            .Analyze(invalidBody, new DocumentSource(post.FilePath, 0)).Semantics!.Diagnostics.Single();

        // The body analyzer knows this position in its own snapshot. Generation
        // has no source map from a transformed body back to the original file.
        await Assert.That(bodyDiagnostic.Location!.Line).IsEqualTo(3);
        await Assert.That(bodyDiagnostic.Location.Column).IsEqualTo(10);
        SiteQualityValidationException? failure = null;
        try
        {
            await GenerateAsync(generator, invalid, output, clean: false, published.BuildPlan);
        }
        catch (SiteQualityValidationException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        var diagnostic = failure!.Diagnostics.Single(item => item.Id == MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId);
        await Assert.That(diagnostic.Severity).IsEqualTo(SiteDiagnosticSeverity.Error);
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo(post.FilePath);
        await Assert.That(diagnostic.Location.Line).IsNull();
        await Assert.That(diagnostic.Location.Column).IsNull();
        await Assert.That(diagnostic.Location.EndLine).IsNull();
        await Assert.That(diagnostic.Location.EndColumn).IsNull();
        await Assert.That(await File.ReadAllBytesAsync(articlePath)).IsEquivalentTo(article);
        await Assert.That(await File.ReadAllBytesAsync(searchPath)).IsEquivalentTo(search);
        var partition = SiteGenerator.MeasureCache(output).CacheDirectory;
        var cache = new SiteBuildCache(Path.GetDirectoryName(partition)!, Path.GetFileName(partition));
        await Assert.That(await cache.ReadPostParseAsync(generator.MarkdownCompiler.Fingerprint,
            MarkdownDocumentFingerprints.SourceHash(invalidBody), invalidBody.Length, CancellationToken.None)).IsNull();
        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(2);
    }

    private static MarkdownPost Post(string body) => new(
        "content/post.md", "post",
        new PostFrontMatter { Title = "Post", Date = BuildTimestamp.AddDays(-1), Summary = "Post summary." },
        body, "posts/post.html");

    private static Task<SiteGenerationResult> GenerateAsync(
        SiteGenerator generator, MarkdownPost post, string output, bool clean, SiteBuildPlan? previousPlan = null) =>
        generator.GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/" }, [post], output, clean,
            new SiteCustomization { Template = new BlogSiteTemplate() },
            new SiteGenerationOptions { BuildTimestamp = BuildTimestamp, PreviousBuildPlan = previousPlan },
            CancellationToken.None);

    private static SiteDiagnostic[] CompilerWarnings(SiteGenerationResult result) =>
        result.BuildReport.Diagnostics
            .Where(diagnostic => diagnostic.Id is LithoLimits.UnsupportedFootnoteDiagnosticId or LithoLimits.BrowserAssetDiagnosticId)
            .OrderBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ToArray();

    private static string WarningSnapshot(SiteGenerationResult result) =>
        JsonSerializer.Serialize(CompilerWarnings(result));

    private static async Task AssertWarningPositionAsync(
        SiteGenerationResult result, MarkdownPost post, string id, string marker)
    {
        var diagnostic = CompilerWarnings(result).Single(item => item.Id == id);
        var (line, column) = new SourceText(post.MarkdownBody)
            .GetLineAndColumn(post.MarkdownBody.IndexOf(marker, StringComparison.Ordinal));
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo(post.FilePath);
        await Assert.That((diagnostic.Location.Line, diagnostic.Location.Column)).IsEqualTo(((int?)line, (int?)column));
    }
}
