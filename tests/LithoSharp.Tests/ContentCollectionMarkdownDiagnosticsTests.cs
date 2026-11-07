using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class ContentCollectionMarkdownDiagnosticsTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task RepeatedMarkdownSharesAnalysisAndDistinctBodiesKeepTheirDiagnostics()
    {
        using var workspace = new TemporaryWorkspace();
        var compiler = new LithoMarkdownCompiler();
        const string body = "## Body\n\nSee [^note] and $x$.";
        var registration = Registration(workspace.Root, body, (entry, context) =>
            context.RenderDocument(context.RenderMarkdown(entry.Body)
                + context.RenderMarkdown(entry.Body) + context.RenderMarkdown("Other [^second].")));

        var result = await GenerateAsync(new SiteGenerator(compiler), workspace.Root, registration, clean: true);

        await Assert.That(compiler.ParseCount).IsEqualTo(2);
        await Assert.That(Warnings(result).Count(diagnostic => diagnostic.Id == "LIT001")).IsEqualTo(2);
        await Assert.That(Warnings(result).Count(diagnostic => diagnostic.Id == "LIT002")).IsEqualTo(1);
        await Assert.That(Warnings(result).All(diagnostic => diagnostic.Location?.FilePath == "entry.md"
            && diagnostic.Location.Line is null)).IsTrue();
        var expected = new LithoMarkdownCompiler().Compile(body).Html;
        var html = await File.ReadAllTextAsync(Path.Combine(workspace.Root, "output", "entry.html"));
        await Assert.That(html).Contains(expected + expected);
        var freshCompiler = new LithoMarkdownCompiler();
        var reused = await GenerateAsync(new SiteGenerator(freshCompiler), workspace.Root, registration, clean: false);
        await Assert.That(freshCompiler.ParseCount).IsEqualTo(0);
        await Assert.That(Warnings(reused).Count(diagnostic => diagnostic.Id == "LIT001")).IsEqualTo(2);
        await Assert.That(Signatures(Warnings(reused)).SequenceEqual(Signatures(Warnings(result)))).IsTrue();
    }

    [Test]
    [Arguments("## Plain body")]
    [Arguments("See [^note] and $x$.")]
    public async Task FreshGeneratorReplaysKnownDiagnosticsWithoutRenderingOrParsing(string body)
    {
        using var workspace = new TemporaryWorkspace();
        var rendered = 0;
        var registration = Registration(workspace.Root, body, (entry, context) =>
        {
            rendered++;
            return context.RenderDocument(context.RenderMarkdown(entry.Body));
        });
        var firstCompiler = new LithoMarkdownCompiler();
        var first = await GenerateAsync(new SiteGenerator(firstCompiler), workspace.Root, registration, clean: true);
        var secondCompiler = new LithoMarkdownCompiler();
        var second = await GenerateAsync(new SiteGenerator(secondCompiler), workspace.Root, registration, clean: false);

        await Assert.That(firstCompiler.ParseCount).IsEqualTo(1);
        await Assert.That(secondCompiler.ParseCount).IsEqualTo(0);
        await Assert.That(rendered).IsEqualTo(1);
        await Assert.That(second.BuildReport.Nodes.Single(node => node.NodeId.StartsWith("page:collection:", StringComparison.Ordinal)).CacheHit).IsTrue();
        await Assert.That(Signatures(Warnings(second)).SequenceEqual(Signatures(Warnings(first)))).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LoaderAnalysisRetainsOriginalPositionsOnlyForTheExactRenderedBody(bool transform)
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "entry.md"),
            "\uFEFF---\r\ntitle: Entry\r\n---\r\n## Heading\r\n\r\nSee [^note] and $x$.");
        var load = await new MarkdownContentCollectionLoader<FrontMatter>(new ContentCollectionId("pages"), input,
            static _ => SiteRoute.ForFile("entry.html"), static entry => new PageMetadata(entry.FrontMatter.Title)).LoadAsync();
        await Assert.That(load.IsSuccess).IsTrue();
        var registration = new SiteContentCollection<FrontMatter, string>(load.Collection!, (entry, context) =>
            context.RenderDocument(context.RenderMarkdown(transform ? "Prefix\r\n" + entry.Body : entry.Body)));
        var compiler = new LithoMarkdownCompiler();

        var result = await GenerateAsync(new SiteGenerator(compiler), workspace.Root, registration, clean: true);

        await Assert.That(compiler.ParseCount).IsEqualTo(transform ? 1 : 0);
        var original = load.Diagnostics.Single(diagnostic => diagnostic.Id == "LIT001");
        var final = Warnings(result).Single(diagnostic => diagnostic.Id == "LIT001");
        await Assert.That(original.Location!.Line).IsEqualTo(6);
        await Assert.That(final.Location!.FilePath).IsEqualTo("entry.md");
        await Assert.That(final.Location.Line).IsEqualTo(transform ? null : (int?)6);
        await Assert.That(load.Diagnostics.Count(diagnostic => diagnostic.Id == "LIT001")).IsEqualTo(1);
    }

    [Test]
    public async Task TypedLayoutUsesTheCollectionDiagnosticStream()
    {
        using var workspace = new TemporaryWorkspace();
        var collection = Collection(workspace.Root, "See [^note].");
        var compiler = new LithoMarkdownCompiler();
        var registration = new SiteContentCollection<string, string>(collection, new MarkdownLayout());

        var result = await GenerateAsync(new SiteGenerator(compiler), workspace.Root, registration, clean: true);

        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        await Assert.That(Warnings(result).Single().Id).IsEqualTo("LIT001");
    }

    [Test]
    public async Task GeneratedAggregateMarkdownDoesNotInventAnOriginalFile()
    {
        using var workspace = new TemporaryWorkspace();
        var generated = Collection(workspace.Root, "source").GeneratePages(
            new ContentCollectionId("groups"), static _ => ["first", "second"],
            static group => new SitePage<string>(new PageId(group.Key), SiteRoute.ForFile($"{group.Key}.html"),
                "See [^note].", new PageMetadata("Group")),
            static (page, context) => context.RenderDocument(context.RenderMarkdown(page.Content)),
            derivedSurfaces: GeneratedPageDerivedSurfaces.None);

        var result = await GenerateAsync(new SiteGenerator(), workspace.Root, generated, clean: true);

        await Assert.That(Warnings(result).Length).IsEqualTo(2);
        await Assert.That(Warnings(result).All(diagnostic => diagnostic.Location is null)).IsTrue();
    }

    [Test]
    public async Task IncompleteCollectionAnalysisPreservesPublishedOutputAndCache()
    {
        using var workspace = new TemporaryWorkspace();
        var generator = new SiteGenerator();
        await GenerateAsync(generator, workspace.Root, Registration(workspace.Root, "Published body."), clean: true);
        var pagePath = Path.Combine(workspace.Root, "output", "entry.html");
        var published = await File.ReadAllBytesAsync(pagePath);
        var cache = SiteGenerator.MeasureCache(Path.Combine(workspace.Root, "output"));
        var originalCacheFiles = Directory.EnumerateFiles(cache.CacheDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        SiteQualityValidationException? failure = null;

        try
        {
            await GenerateAsync(generator, workspace.Root,
                Registration(workspace.Root, new string('x', 1024 * 1024 + 1)), clean: false);
        }
        catch (SiteQualityValidationException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        var diagnostic = failure!.Diagnostics.Single(item => item.Id == "LSM011");
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("entry.md");
        await Assert.That(diagnostic.Location.Line).IsNull();
        await Assert.That(await File.ReadAllBytesAsync(pagePath)).IsEquivalentTo(published);
        await Assert.That(Directory.EnumerateFiles(cache.CacheDirectory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).SequenceEqual(originalCacheFiles)).IsTrue();
    }

    [Test]
    public async Task MissingSemanticOutcomeIsAnErrorBeforeCollectionPublication()
    {
        using var workspace = new TemporaryWorkspace();
        SiteQualityValidationException? failure = null;
        try
        {
            await GenerateAsync(new SiteGenerator(new UnavailableCompiler()), workspace.Root,
                Registration(workspace.Root, "Body."), clean: true);
        }
        catch (SiteQualityValidationException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Diagnostics.Single().Id).IsEqualTo("LSM011");
        await Assert.That(File.Exists(Path.Combine(workspace.Root, "output", "entry.html"))).IsFalse();
    }

    [Test]
    [Arguments("same")]
    [Arguments("body")]
    [Arguments("path")]
    public async Task LegacyPostReusesReaderAnalysisOnlyForItsExactBodyAndPath(string change)
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(input);
        var path = Path.Combine(input, "post.md");
        await File.WriteAllTextAsync(path,
            "---\ntitle: Post\ndate: 2026-09-06T00:00:00Z\nsummary: Summary\n---\n## Body\n\nSee [^note].");
        var post = await new MarkdownPostReader().ReadAsync(path, input);
        var original = post.CompilerDiagnostics!.Single(diagnostic => diagnostic.Id == "LIT001");
        var candidate = change switch
        {
            "body" => post with { MarkdownBody = "Prefix\n" + post.MarkdownBody },
            "path" => post with { FilePath = Path.Combine(input, "other.md") },
            _ => post,
        };
        var compiler = new LithoMarkdownCompiler();

        var result = await new SiteGenerator(compiler).GenerateWithOptionsAsync(Site(), [candidate],
            Path.Combine(workspace.Root, "output"), true, Customization(workspace.Root),
            new SiteGenerationOptions { BuildTimestamp = Timestamp }, CancellationToken.None);

        await Assert.That(compiler.ParseCount).IsEqualTo(change == "same" ? 0 : 1);
        await Assert.That(Signatures(result.BuildReport.Diagnostics.Where(diagnostic => diagnostic.Id == "LIT001"))
            .SequenceEqual(Signatures([original]))).IsTrue();
    }

    [Test]
    public async Task ExactReaderBodyPersistsCanonicalSpansAndReusesThemAcrossGenerators()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(input);
        const string body = "## H\n\n[go](target.md)\n\n![pic](image.png)";
        var path = Path.Combine(input, "post.md");
        await File.WriteAllTextAsync(path,
            "---\ntitle: Post\ndate: 2026-09-06T00:00:00Z\nsummary: Summary\n---\n" + body);
        var post = await new MarkdownPostReader().ReadAsync(path, input);
        await Assert.That(post.BodySource!.BodyStartOffset).IsGreaterThan(body.Length);
        var firstCompiler = new LithoMarkdownCompiler();
        var output = Path.Combine(workspace.Root, "output");
        await new SiteGenerator(firstCompiler).GenerateWithOptionsAsync(Site(), [post], output, true,
            Customization(workspace.Root), new SiteGenerationOptions { BuildTimestamp = Timestamp }, CancellationToken.None);
        var partition = SiteGenerator.MeasureCache(output).CacheDirectory;
        var cache = new SiteBuildCache(Path.GetDirectoryName(partition)!, Path.GetFileName(partition));
        var canonical = await cache.ReadPostParseAsync(firstCompiler.Fingerprint,
            MarkdownDocumentFingerprints.SourceHash(body), body.Length, CancellationToken.None)
            ?? throw new InvalidOperationException("Expected a reusable body-relative parse record.");

        await Assert.That(firstCompiler.ParseCount).IsEqualTo(0);
        await Assert.That(canonical.Headings.Single().SpanStart).IsEqualTo(0);
        await Assert.That(canonical.Links.All(link => link.SpanStart == body.IndexOf(link.RawText, StringComparison.Ordinal))).IsTrue();
        await Assert.That(canonical.Assets.Single().SpanStart).IsEqualTo(body.IndexOf("![pic]", StringComparison.Ordinal));
        await Assert.That(canonical.HasValidContent()).IsTrue();
        var secondCompiler = new LithoMarkdownCompiler();
        await new SiteGenerator(secondCompiler).GenerateWithOptionsAsync(Site(),
            [post with { FrontMatter = post.FrontMatter with { Title = "Changed title" } }], output, false,
            Customization(workspace.Root), new SiteGenerationOptions { BuildTimestamp = Timestamp }, CancellationToken.None);
        await Assert.That(secondCompiler.ParseCount).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "post.html"))).Contains("Changed title");
    }

    [Test]
    public async Task MalformedUtf16CannotReuseReplacementCharacterOutputOrParseCache()
    {
        using var workspace = new TemporaryWorkspace();
        const string valid = "## Body\n\nLiteral \uFFFD.";
        const string malformed = "## Body\n\nLiteral \uD800.";
        await Assert.That(MarkdownDocumentFingerprints.SourceHash(valid))
            .IsEqualTo(MarkdownDocumentFingerprints.SourceHash(malformed));
        await Assert.That(MarkdownDocumentFingerprints.ExactBodyCacheHash(valid))
            .IsEqualTo(MarkdownDocumentFingerprints.SourceHash(valid));
        await Assert.That(MarkdownDocumentFingerprints.ExactBodyCacheHash(malformed))
            .IsNotEqualTo(MarkdownDocumentFingerprints.ExactBodyCacheHash(valid));
        var post = new MarkdownPost("content/post.md", "post",
            new PostFrontMatter { Title = "Post", Date = Timestamp, Summary = "Summary" }, valid, "posts/post.html");
        var output = Path.Combine(workspace.Root, "output");
        var firstCompiler = new LithoMarkdownCompiler();
        await new SiteGenerator(firstCompiler).GenerateWithOptionsAsync(Site(), [post], output, true,
            Customization(workspace.Root), new SiteGenerationOptions { BuildTimestamp = Timestamp }, CancellationToken.None);
        var pagePath = Path.Combine(output, "posts", "post.html");
        var searchPath = Path.Combine(output, "search-index.json");
        var page = await File.ReadAllBytesAsync(pagePath);
        var search = await File.ReadAllBytesAsync(searchPath);
        var partition = SiteGenerator.MeasureCache(output).CacheDirectory;
        var cache = new SiteBuildCache(Path.GetDirectoryName(partition)!, Path.GetFileName(partition));
        await Assert.That(await cache.ReadPostParseAsync(firstCompiler.Fingerprint,
            MarkdownDocumentFingerprints.SourceHash(valid), valid.Length, CancellationToken.None)).IsNotNull();
        var freshCompiler = new LithoMarkdownCompiler();
        SiteQualityValidationException? failure = null;
        try
        {
            await new SiteGenerator(freshCompiler).GenerateWithOptionsAsync(Site(),
                [post with { MarkdownBody = malformed }], output, false, Customization(workspace.Root),
                new SiteGenerationOptions { BuildTimestamp = Timestamp }, CancellationToken.None);
        }
        catch (SiteQualityValidationException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        var diagnostic = failure!.Diagnostics.Single(item => item.Id == "LSM011");
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo(post.FilePath);
        await Assert.That(diagnostic.Location.Line).IsNull();
        await Assert.That(freshCompiler.ParseCount).IsEqualTo(1);
        await Assert.That(await File.ReadAllBytesAsync(pagePath)).IsEquivalentTo(page);
        await Assert.That(await File.ReadAllBytesAsync(searchPath)).IsEquivalentTo(search);
        await Assert.That(await cache.ReadPostParseAsync(freshCompiler.Fingerprint,
            MarkdownDocumentFingerprints.ExactBodyCacheHash(malformed), malformed.Length, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task GenericDiagnosticReportsKeepTheirExistingDeduplication()
    {
        var diagnostic = new SiteDiagnostic("TEST", SiteDiagnosticSeverity.Warning, "Repeated generic warning.");
        var report = new SiteBuildReport(Timestamp, "Production", "Test", diagnostics: [diagnostic, diagnostic]);
        await Assert.That(report.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(new SiteQualityReport([diagnostic, diagnostic]).Diagnostics.Count).IsEqualTo(1);
    }

    private static SiteContentCollection<string, string> Registration(string root, string body,
        ContentPageRenderer<string, string>? renderer = null) =>
        new(Collection(root, body), renderer ?? (static (entry, context) => context.RenderDocument(context.RenderMarkdown(entry.Body))))
        { RendererFingerprint = "markdown-diagnostics:1" };

    private static ContentCollection<string, string> Collection(string root, string body) =>
        new(new ContentCollectionId("pages"), root,
            [new ContentEntry<string, string>(new ContentEntryId("entry"), "entry.md",
                MarkdownDocumentFingerprints.SourceHash(body), "Entry", body)],
            static _ => SiteRoute.ForFile("entry.html"), static _ => new PageMetadata("Entry"),
            transformationId: new ContentTransformationId("markdown-diagnostics:1"), isCacheable: true);

    private static Task<SiteGenerationResult> GenerateAsync(SiteGenerator generator, string root,
        SiteContentCollection registration, bool clean) => generator.GenerateWithOptionsAsync(Site(), [],
            Path.Combine(root, "output"), clean, Customization(root),
            new SiteGenerationOptions { BuildTimestamp = Timestamp, ContentCollections = [registration] }, CancellationToken.None);

    private static SiteSettings Site() => new() { BaseUrl = "https://example.test/" };

    private static SiteCustomization Customization(string root) => new()
    { Template = new BlogSiteTemplate(), FaviconSourceDirectory = Path.Combine(root, "missing-favicon") };

    private static SiteDiagnostic[] Warnings(SiteGenerationResult result) => result.BuildReport.Diagnostics
        .Where(diagnostic => diagnostic.Id is "LIT001" or "LIT002").ToArray();

    private static IEnumerable<string> Signatures(IEnumerable<SiteDiagnostic> diagnostics) => diagnostics.Select(diagnostic =>
        $"{diagnostic.Id}|{diagnostic.Severity}|{diagnostic.Message}|{LocationSignature(diagnostic.Location)}|{diagnostic.Category}|{string.Join(';', diagnostic.RelatedLocations.Select(LocationSignature))}");

    private static string LocationSignature(SiteSourceLocation? location) => location is null ? "<none>"
        : $"{location.FilePath}:{location.Line}:{location.Column}-{location.EndLine}:{location.EndColumn}";

    private sealed class FrontMatter
    {
        public string Title { get; init; } = string.Empty;
    }

    private sealed class MarkdownLayout : IPageLayout<ContentEntry<string, string>>
    {
        public IHtmlContent Render(SitePage<ContentEntry<string, string>> page, PageRenderingContext context) =>
            context.RenderDocument(page, context.RenderMarkdown(page.Content.Body));
    }

    private sealed class UnavailableCompiler : IMarkdownCompiler
    {
        private readonly LithoMarkdownCompiler compiler = new();
        public string Frontend => compiler.Frontend;
        public MarkdownCompilerOptions Options => compiler.Options;
        public string Fingerprint => compiler.Fingerprint;
        public int ParseCount => compiler.ParseCount;
        public MarkdownCompilationResult Compile(string markdown) => compiler.Compile(markdown);
        public MarkdownCompilationResult Analyze(string markdown, DocumentSource? source = null) => Compile(markdown);
        public MarkdownCompilationResult Analyze(string markdown, DocumentSource? source, CancellationToken cancellationToken) => Compile(markdown);
        public string RenderPlainText(string markdown) => compiler.RenderPlainText(markdown);
    }
}
