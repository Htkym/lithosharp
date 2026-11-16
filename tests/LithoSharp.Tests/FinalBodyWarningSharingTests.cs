using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        // Verified page nodes retain diagnostics without caching diagnostic-bearing body HTML.
        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(1);
        await Assert.That(warm.BuildReport.CacheMissCount).IsEqualTo(0);
        await Assert.That(WarningSnapshot(warm)).IsEqualTo(WarningSnapshot(clean));

        var freshGenerator = new SiteGenerator();
        var fresh = await GenerateAsync(freshGenerator, post, output, clean: false, warm.BuildPlan);

        await Assert.That(freshGenerator.MarkdownCompiler.ParseCount).IsEqualTo(0);
        await Assert.That(fresh.BuildReport.CacheMissCount).IsEqualTo(0);
        await Assert.That(WarningSnapshot(fresh)).IsEqualTo(WarningSnapshot(clean));
        await Assert.That(await cache.ReadPostParseAsync(generator.MarkdownCompiler.Fingerprint,
            MarkdownDocumentFingerprints.SourceHash(body), body.Length, CancellationToken.None)).IsNull();
    }

    [Test]
    [Arguments("missing")]
    [Arguments("wrong-snapshot")]
    [Arguments("wrong-body-hash")]
    public async Task LegacyPostRegeneratesWhenCachedDiagnosticEvidenceDoesNotMatchItsInput(string corruption)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        const string body = "\uFEFF\r\n\r\n## Final 😀\r\n\r\nSee [^note] and $x$.\r\n";
        var post = Post(body);
        var clean = await GenerateAsync(new SiteGenerator(), post, output, clean: true);
        var articlePath = Path.Combine(output, "posts", "post.html");
        var article = await File.ReadAllBytesAsync(articlePath);
        await Assert.That(CompilerWarnings(clean).Length).IsEqualTo(2);
        await RewriteCachedPostDiagnosticsAsync(output, corruption);

        var generator = new SiteGenerator();
        var repaired = await GenerateAsync(generator, post, output, clean: false, clean.BuildPlan);

        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(1);
        var pageNode = repaired.BuildReport.Nodes.Single(node => node.NodeId == "page:markdown:posts/post.html");
        await Assert.That(pageNode.CacheHit).IsFalse();
        await Assert.That(pageNode.CacheMissReason).IsEqualTo(corruption switch
        {
            "missing" => "The Markdown diagnostic outcome is unavailable.",
            "wrong-snapshot" => "The post Markdown diagnostic snapshot does not match its input.",
            "wrong-body-hash" => "The post Markdown body fingerprint does not match its input.",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        });
        await Assert.That(repaired.BuildReport.Nodes.Single(node => node.NodeId == "asset:site-css").CacheHit).IsTrue();
        await Assert.That(WarningSnapshot(repaired)).IsEqualTo(WarningSnapshot(clean));
        await Assert.That(await File.ReadAllBytesAsync(articlePath)).IsEquivalentTo(article);
        var partition = SiteGenerator.MeasureCache(output).CacheDirectory;
        var cache = new SiteBuildCache(Path.GetDirectoryName(partition)!, Path.GetFileName(partition));
        await Assert.That(await cache.ReadPostParseAsync(generator.MarkdownCompiler.Fingerprint,
            MarkdownDocumentFingerprints.SourceHash(body), body.Length, CancellationToken.None)).IsNull();

        var freshGenerator = new SiteGenerator();
        var reused = await GenerateAsync(freshGenerator, post, output, clean: false, repaired.BuildPlan);

        await Assert.That(freshGenerator.MarkdownCompiler.ParseCount).IsEqualTo(0);
        await Assert.That(reused.BuildReport.CacheMissCount).IsEqualTo(0);
        await Assert.That(WarningSnapshot(reused)).IsEqualTo(WarningSnapshot(clean));
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

        var freshGenerator = new SiteGenerator();
        var fresh = await GenerateAsync(freshGenerator, replaced, output, clean: false, result.BuildPlan);

        await Assert.That(freshGenerator.MarkdownCompiler.ParseCount).IsEqualTo(0);
        await Assert.That(fresh.BuildReport.CacheMissCount).IsEqualTo(0);
        await Assert.That(WarningSnapshot(fresh)).IsEqualTo(WarningSnapshot(result));
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

    private static async Task RewriteCachedPostDiagnosticsAsync(string output, string corruption)
    {
        const string manifestName = ".lithosharp-output-manifest.json";
        const string nodeId = "page:markdown:posts/post.html";
        var outputManifestPath = Path.Combine(output, manifestName);
        var outputManifest = JsonNode.Parse(await File.ReadAllBytesAsync(outputManifestPath))!;
        var digest = outputManifest["buildCache"]!.GetValue<string>();
        var partition = SiteGenerator.MeasureCache(output).CacheDirectory;
        var cache = new SiteBuildCache(Path.GetDirectoryName(partition)!, Path.GetFileName(partition));
        var original = await cache.LoadAsync(digest, CancellationToken.None);
        var originalOutcome = original[nodeId].CollectionDiagnostics
            ?? throw new InvalidOperationException("Expected a known legacy post diagnostic outcome.");
        await Assert.That(originalOutcome.Diagnostics.Count).IsEqualTo(2);
        var manifest = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(partition, "builds", digest + ".json")))!;
        var page = manifest["Nodes"]!.AsArray().Single(node => node!["NodeId"]!.GetValue<string>() == nodeId)!;
        var wrongIdentity = originalOutcome.Diagnostics[0].SnapshotIdentity == new string('f', 64)
            ? new string('e', 64) : new string('f', 64);
        switch (corruption)
        {
            case "missing":
                page.AsObject().Remove("CollectionDiagnostics");
                break;
            case "wrong-snapshot":
                page["CollectionDiagnostics"]!["Diagnostics"]![0]!["SnapshotIdentity"] = wrongIdentity;
                break;
            case "wrong-body-hash":
                page["Inputs"]!.AsArray().Single(input => input!["Key"]!.GetValue<string>() == "page.body")!["Value"] =
                    MarkdownDocumentFingerprints.ExactBodyCacheHash("A different valid body.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var changedDigest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await File.WriteAllBytesAsync(Path.Combine(partition, "builds", changedDigest + ".json"), bytes);
        var changed = await cache.LoadAsync(changedDigest, CancellationToken.None);
        await Assert.That(changed.Count).IsEqualTo(original.Count);
        if (corruption == "missing")
            await Assert.That(changed[nodeId].CollectionDiagnostics).IsNull();
        else if (corruption == "wrong-snapshot")
        {
            await Assert.That(changed[nodeId].CollectionDiagnostics!.IsValid()).IsTrue();
            await Assert.That(changed[nodeId].CollectionDiagnostics!.Diagnostics[0].SnapshotIdentity).IsEqualTo(wrongIdentity);
        }
        else
        {
            await Assert.That(changed[nodeId].Inputs.Single(input => input.Key == "page.body").Value)
                .IsNotEqualTo(original[nodeId].Inputs.Single(input => input.Key == "page.body").Value);
            await Assert.That(changed[nodeId].NodeKey).IsEqualTo(original[nodeId].NodeKey);
        }

        outputManifest["buildCache"] = changedDigest;
        var outputBytes = JsonSerializer.SerializeToUtf8Bytes(outputManifest);
        await File.WriteAllBytesAsync(outputManifestPath, outputBytes);
        // Preserve ownership integrity so the test reaches diagnostic admission, not manifest rejection.
        var ownershipPath = Directory.EnumerateFiles(Path.GetDirectoryName(output)!, ".lithosharp-ownership-*.json")
            .Single(path => !Path.GetFileName(path).StartsWith(".lithosharp-ownership-pending-", StringComparison.Ordinal));
        var ownership = JsonNode.Parse(await File.ReadAllBytesAsync(ownershipPath))!;
        var ownedManifest = ownership["artifacts"]!.AsArray()
            .Single(artifact => artifact!["path"]!.GetValue<string>() == manifestName)!;
        ownedManifest["sha256"] = Convert.ToHexStringLower(SHA256.HashData(outputBytes));
        await File.WriteAllBytesAsync(ownershipPath, JsonSerializer.SerializeToUtf8Bytes(ownership));
    }

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
