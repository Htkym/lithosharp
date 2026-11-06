using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class MarkdownBodyAnalysisCompilerTests
{
    [Test]
    [Arguments("\n", "")]
    [Arguments("\r\n", "\uFEFF")]
    public async Task AnalyzeRetainsLeadingDelimitersAndOriginalBodySnapshot(string newline, string bom)
    {
        var body = bom + string.Join(newline, "---", "", "# Visible &amp; **body**", "",
            "See [go](doc😀.md).", "", "---", "");
        var compiler = new LithoMarkdownCompiler();
        var analyzed = compiler.Analyze(body);

        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        await Assert.That(analyzed.Html).IsEqualTo(new LithoMarkdownCompiler().Compile(body).Html);
        await Assert.That(analyzed.Semantics!.Headings.Single().Text).IsEqualTo("Visible & body");
        await Assert.That(analyzed.Semantics.Headings.Single().Span.Start)
            .IsEqualTo(body.IndexOf("# Visible", StringComparison.Ordinal));
        await Assert.That(analyzed.Semantics.Links.Single().RawText).IsEqualTo("[go](doc😀.md)");
        await Assert.That(analyzed.Semantics.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task AnalyzePreservesSiteHeadingTextAndDuplicateSlugs()
    {
        const string body = "# A ![hidden](pic.png) $x$ &amp;\n\n# A ![hidden](other.png) $x$ &amp;\n";
        var analyzed = new LithoMarkdownCompiler().Analyze(body);
        var headings = analyzed.Semantics!.Headings;

        await Assert.That(analyzed.Html).IsEqualTo(new LithoMarkdownCompiler().Compile(body).Html);
        await Assert.That(headings.Count).IsEqualTo(2);
        await Assert.That(headings.All(heading => heading.Text == @"A  \(x\) &")).IsTrue();
        await Assert.That(headings[0].Id == headings[1].Id).IsFalse();
        await Assert.That(headings.All(heading => heading.RawLevel == 1 && heading.OutputLevel == 2)).IsTrue();
        await Assert.That(analyzed.Semantics.Assets.Count).IsEqualTo(2);
        await Assert.That(analyzed.Semantics.Diagnostics.Single().Id).IsEqualTo(LithoLimits.BrowserAssetDiagnosticId);
    }

    [Test]
    [Arguments("```md\n[^ignored]\n$x$\n```\n")]
    [Arguments("`[^ignored] $x$`\n")]
    [Arguments("\\[^ignored]\n")]
    [Arguments("<span data-note=\"[^ignored]\">text</span>\n")]
    [Arguments("[text](target \"[^ignored]\")\n")]
    public async Task AnalyzeUsesCandidateAndTypedBlockExclusions(string body)
    {
        var analyzed = new LithoMarkdownCompiler().Analyze(body, new DocumentSource("body.md", 0));

        await Assert.That(analyzed.Semantics!.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task AnalyzeMapsCandidateAndLinkPositionsFromTheSameRawBody()
    {
        const string body = "\uFEFF\r\n\r\n# Heading 😀\r\n\r\nSee [go](doc😀.md), [^note] and $x$.\r\n";
        var source = new DocumentSource("docs/body.md", 53) { BodyStartLine = 6 };
        var compiler = new LithoMarkdownCompiler();
        var analyzed = compiler.Analyze(body, source);
        var semantics = analyzed.Semantics!;
        var locator = new SourceText(body);

        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        await Assert.That(semantics.Headings.Single().Span.Start)
            .IsEqualTo(source.BodyStartOffset + body.IndexOf("# Heading", StringComparison.Ordinal));
        await Assert.That(semantics.Links.Single().Span.Start)
            .IsEqualTo(source.BodyStartOffset + body.IndexOf("[go]", StringComparison.Ordinal));
        foreach (var (id, token) in new[]
        {
            (LithoLimits.UnsupportedFootnoteDiagnosticId, "[^note]"),
            (LithoLimits.BrowserAssetDiagnosticId, "$x$"),
        })
        {
            var diagnostic = semantics.Diagnostics.Single(item => item.Id == id);
            var (line, column) = locator.GetLineAndColumn(body.IndexOf(token, StringComparison.Ordinal));
            await Assert.That(diagnostic.Location!.FilePath).IsEqualTo(source.FilePath);
            await Assert.That((diagnostic.Location.Line, diagnostic.Location.Column))
                .IsEqualTo(((int?)(source.BodyStartLine + line - 1), (int?)column));
        }
    }

    [Test]
    public async Task FailedBodyAnalysisReportsOnlyKnownSourcePositions()
    {
        const string body = "# Body\r\n😀\uD800";
        var source = new DocumentSource("body.md", 31) { BodyStartLine = 7 };
        var analyzed = new LithoMarkdownCompiler().Analyze(body, source);
        var diagnostic = analyzed.Semantics!.Diagnostics.Single();
        var (line, column) = new SourceText(body).GetLineAndColumn(body.Length - 1);

        await Assert.That(diagnostic.Id).IsEqualTo(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId);
        await Assert.That(diagnostic.Severity).IsEqualTo(SiteDiagnosticSeverity.Error);
        await Assert.That((diagnostic.Location!.Line, diagnostic.Location.Column,
            diagnostic.Location.EndLine, diagnostic.Location.EndColumn))
            .IsEqualTo(((int?)(source.BodyStartLine + line - 1), (int?)column,
                (int?)(source.BodyStartLine + line - 1), (int?)(column + 1)));
        await Assert.That(new LithoMarkdownCompiler().Analyze(body).Semantics!.Diagnostics.Single().Location).IsNull();
    }

    [Test]
    public async Task InputLimitKeepsUnknownLocationAndRenderOnlyCompilationAvailable()
    {
        var body = new string('x', 1024 * 1024 + 1);
        var analyzed = new LithoMarkdownCompiler().Analyze(body, new DocumentSource("body.md", 19) { BodyStartLine = 5 });
        var diagnostic = analyzed.Semantics!.Diagnostics.Single();

        await Assert.That(diagnostic.Id).IsEqualTo(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId);
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("body.md");
        await Assert.That(diagnostic.Location.Line).IsNull();
        await Assert.That(diagnostic.Location.Column).IsNull();
        await Assert.That(new LithoMarkdownCompiler().Compile(body).Html.Length).IsGreaterThan(body.Length);
    }

    [Test]
    public async Task IncompletePostAnalysisPreservesPublishedArticleAndSearchWithoutCachingFailure()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var compiler = new LithoMarkdownCompiler();
        var generator = new SiteGenerator(compiler);
        var timestamp = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        var post = new MarkdownPost("content/post.md", "post",
            new PostFrontMatter { Title = "Post", Date = timestamp, Summary = "Published summary." },
            "## Published heading\n\nPublished body.", "posts/post.html");
        var settings = new SiteSettings { BaseUrl = "https://example.test/" };
        var customization = new SiteCustomization { Template = new BlogSiteTemplate() };
        var options = new SiteGenerationOptions { BuildTimestamp = timestamp };
        var first = await generator.GenerateWithOptionsAsync(settings, [post], output, clean: true,
            customization, options, CancellationToken.None);
        var articlePath = Path.Combine(output, "posts", "post.html");
        var searchPath = Path.Combine(output, "search-index.json");
        var article = await File.ReadAllBytesAsync(articlePath);
        var search = await File.ReadAllBytesAsync(searchPath);
        var partition = SiteGenerator.MeasureCache(output).CacheDirectory;
        var cache = new SiteBuildCache(Path.GetDirectoryName(partition)!, Path.GetFileName(partition));
        var oversized = post with { MarkdownBody = new string('x', 1024 * 1024 + 1) };
        SiteQualityValidationException? failure = null;

        try
        {
            await generator.GenerateWithOptionsAsync(settings, [oversized], output, clean: false,
                customization, options with { PreviousBuildPlan = first.BuildPlan }, CancellationToken.None);
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
        await Assert.That(await File.ReadAllBytesAsync(articlePath)).IsEquivalentTo(article);
        await Assert.That(await File.ReadAllBytesAsync(searchPath)).IsEquivalentTo(search);
        await Assert.That(await cache.ReadPostParseAsync(compiler.Fingerprint,
            MarkdownDocumentFingerprints.SourceHash(oversized.MarkdownBody), oversized.MarkdownBody.Length, CancellationToken.None))
            .IsNull();
        await Assert.That(await cache.ReadPostParseAsync(compiler.Fingerprint,
            MarkdownDocumentFingerprints.SourceHash(post.MarkdownBody), post.MarkdownBody.Length, CancellationToken.None))
            .IsNotNull();
        await Assert.That(compiler.ParseCount).IsEqualTo(2);
    }

    [Test]
    public async Task PartialAnalysisRetainsRenderingBlocksAndReportsUnavailableFacts()
    {
        const string body = "# Guide\n\nA paragraph.\n";
        var analysis = MdParser.AnalyzeWithBlocks(body, MdAnalysisParts.Headings | MdAnalysisParts.Syntax,
            "lithosharp", "body.md", null, new MdOptions(MdInputKind.Body, maxOutputItems: 4), CancellationToken.None);

        await Assert.That(analysis.Status).IsEqualTo(MdParseStatus.Partial);
        await Assert.That(analysis.Blocks).IsNotNull();
        await Assert.That(analysis.Root).IsNull();
        await Assert.That(LithoMarkdownCompiler.RenderTree(analysis.Blocks!, ["guide"]))
            .IsEqualTo(new LithoMarkdownCompiler().Compile(body).Html);
        var diagnostic = MarkdownAnalysisAdapter.IncompleteDiagnostic(analysis, "body.md")!;
        await Assert.That(diagnostic.Id).IsEqualTo(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId);
        await Assert.That(diagnostic.Location!.Line).IsNull();
    }

    [Test]
    public async Task BodyOptionsArePartOfTheCompilerFingerprint()
    {
        var analysis = MarkdownAnalysisAdapter.AnalyzeBody("# Body", null, CancellationToken.None);

        await Assert.That(analysis.ProfileId).IsEqualTo("lithosharp-markdown/1");
        await Assert.That(analysis.OptionsHash).IsEqualTo(MarkdownAnalysisAdapter.BodyOptionsHash);
        await Assert.That(analysis.OptionsHash == new MdOptions().Hash).IsFalse();
        await Assert.That(analysis.RequestedParts).IsEqualTo(MdAnalysisParts.UnsupportedSyntax);
        await Assert.That(analysis.Root).IsNull();
        await Assert.That(new LithoMarkdownCompiler().Fingerprint)
            .Contains($"/options={analysis.OptionsHash}/");
    }

    [Test]
    public async Task CancelledBodyAnalysisDoesNotReturnACompilation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.That(() => new LithoMarkdownCompiler().Analyze("# Body", null, cancellation.Token))
            .Throws<OperationCanceledException>();
    }
}
