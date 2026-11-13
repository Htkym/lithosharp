using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class RenderedMarkdownOriginTests
{
    private const string BaseUrl = "https://example.test/base/";
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task NativeTargetsUseFullBodyCoordinatesAcrossQuoteContinuationAndEncodedAttributes()
    {
        const string body = "# 😀 Guide\r\n\r\n> Quote 😀\r\n> continued [bad](<missing.html#lost>) and ![art](images/missing.png)\r\n\r\n[encoded](gone.html?a=1&b=2)\r\n";
        const string header = "\uFEFF---\r\ntitle: 😀\r\n---\r\n";
        var raw = header + body;
        var source = new DocumentSource("guide.md", header.Length) { BodyStartLine = 4, ExactBody = body };
        var compiled = new LithoMarkdownCompiler().Analyze(body, source);
        var html = "<!doctype html><html><head><title>Guide</title></head><body>" + compiled.Html + "</body></html>";
        var bound = RenderedHtmlOrigins.Bind(compiled.Html, body, compiled.RenderOrigins, source);
        var origins = RenderedHtmlOrigins.Embed(html, [new(compiled.Html, bound)]);
        var report = await Validate(html);
        var mapper = origins.CreateMapper();
        foreach (var token in new[] { "missing.html#lost", "images/missing.png", "gone.html?a=1&b=2" })
        {
            var diagnostic = mapper(report.Diagnostics.Single(item => item.Id == "LSQ001" && item.Message.Contains(token, StringComparison.Ordinal)));
            await AssertLocation(diagnostic, raw, token, "guide.md");
            await Assert.That(diagnostic.MappingPrecision).IsEqualTo(token.Contains('&') ? "CoveringTokens" : "Exact");
            await Assert.That(diagnostic.RelatedLocations.Single().FilePath).IsEqualTo("guide/index.html");
        }
        await Assert.That(origins.IsValid()).IsTrue();
    }

    [Test]
    public async Task ComposedTargetMapsToIncludedSnapshotAndKeepsTheDirectiveAndHtmlArtifact()
    {
        const string body = "[bad](missing.html#lost)";
        const string included = "\uFEFF// 😀\r\n" + body;
        var input = new MdSourceSnapshot("code/example.md", "include:1", included);
        var output = new MdSourceSnapshot("expanded", "expanded:1", body);
        var source = new DocumentSource("guide.md", 20)
        {
            ExactBody = body,
            SourceMap = new MdSourceMap(output, [new MdSourceMapSegment(new(0, body.Length),
                new(input, new(included.Length - body.Length, body.Length)), MdSegmentKind.Linear)]),
            InclusionOrigins = [new(new(0, body.Length), new("guide.md", 7, 1, 7, 35))],
        };
        var compiled = new LithoMarkdownCompiler().Analyze(body, source);
        var bound = RenderedHtmlOrigins.Bind(compiled.Html, body, compiled.RenderOrigins, source);
        var diagnostic = bound.MapDiagnostic((await Validate(compiled.Html)).Diagnostics.Single(item => item.Id == "LSQ001"));

        await AssertLocation(diagnostic, included, "missing.html#lost", "code/example.md");
        await Assert.That(diagnostic.MappingPrecision).IsEqualTo("Exact");
        await Assert.That(diagnostic.RelatedLocations.Select(static location => location.FilePath))
            .IsEquivalentTo(["guide.md", "guide/index.html"]);
        using var sarif = JsonDocument.Parse(new SiteQualityReport([diagnostic]).Format(SiteDiagnosticFormat.Sarif));
        await Assert.That(sarif.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("properties").GetProperty("mappingPrecision").GetString()).IsEqualTo("Exact");
    }

    [Test]
    public async Task GeneratedComponentAndRepeatedFragmentKeepHtmlLocationsWithoutInventingSourceTargets()
    {
        const string body = ":::component name=\"bad-link\"\n:::";
        var compiler = new LithoMarkdownCompiler(MarkdownCompilerOptions.Default with
        {
            Markdown = new MarkdownOptions
            {
                ProfileId = MarkdownOptions.DocumentationProfile,
                Components = new([MarkdownComponentRegistration.Create("bad-link", new BadLink(),
                    new ReflectionContentFrontMatterBinder<NoProps>(), "bad-link:v1")]),
            },
        });
        var source = new DocumentSource("guide.md", 0) { ExactBody = body };
        var compiled = compiler.Analyze(body, source, default, null, ComponentRenderingContext.Create(new SiteSettings()));
        var bound = RenderedHtmlOrigins.Bind(compiled.Html, body, compiled.RenderOrigins, source);
        var diagnostic = bound.MapDiagnostic((await Validate(compiled.Html)).Diagnostics.Single(item => item.Id == "LSQ001"));
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("guide/index.html");
        await Assert.That(diagnostic.MappingPrecision).IsEqualTo("Unknown");
        await Assert.That(diagnostic.RelatedLocations.Single().FilePath).IsEqualTo("guide.md");
        await Assert.That(diagnostic.RelatedLocations[0].Line).IsEqualTo(1);

        const string markdown = "[bad](missing.html)";
        var ordinary = new LithoMarkdownCompiler().Analyze(markdown);
        var fragment = new RenderedHtmlFragment(ordinary.Html, RenderedHtmlOrigins.Bind(ordinary.Html, markdown,
            ordinary.RenderOrigins, new DocumentSource("other.md", 0) { ExactBody = markdown }));
        var repeated = ordinary.Html + ordinary.Html;
        var ambiguous = RenderedHtmlOrigins.Embed(repeated, [fragment]);
        foreach (var issue in (await Validate(repeated)).Diagnostics.Where(static item => item.Id == "LSQ001"))
        {
            var mapped = ambiguous.MapDiagnostic(issue);
            await Assert.That(mapped.Location!.FilePath).IsEqualTo("guide/index.html");
            await Assert.That(mapped.MappingPrecision).IsEqualTo("Unknown");
            await Assert.That(mapped.RelatedLocations.Single().FilePath).IsEqualTo("other.md");
        }
    }

    [Test]
    public async Task TypedPageKeepsOriginalAnchorLocationAfterCodeExpansionAndWarmArtifactReuse()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "example.cs"), "\uFEFFalpha 😀\r\nbeta\r\ngamma\r\n", new UTF8Encoding(false));
        const string raw = "\uFEFF---\r\ntitle: Guide\r\n---\r\n# Guide\r\n\r\n```cs source=\"example.cs\"\r\nold\r\n```\r\n\r\n😀 [bad](#gone)\r\n";
        await File.WriteAllTextAsync(Path.Combine(content, "guide.md"), raw, new UTF8Encoding(false));
        var loaded = await new MarkdownContentCollectionLoader<FrontMatter>(new("guides"), content,
            static _ => SiteRoute.ForDirectoryIndex("guide"), static entry => new PageMetadata(entry.FrontMatter.Title),
            transformationId: new("origin:v1"), isCacheable: true).LoadAsync();
        await Assert.That(loaded.IsSuccess).IsTrue();
        var generator = new SiteGenerator();
        var output = Path.Combine(workspace.Root, "output");
        var options = Options(workspace.Root) with
        {
            ContentCollections = [new SiteContentCollection<FrontMatter, string>(loaded.Collection!,
                static (entry, context) => context.RenderDocument(context.RenderMarkdown(entry.Body)))
            {
                RendererFingerprint = "origin-document:v1",
            }],
        };
        await Build(generator, [], output, options);
        var warm = await Build(generator, [], output, options);
        await Assert.That(warm.BuildReport.Nodes.Single(static node => node.NodeId.StartsWith("page:collection:", StringComparison.Ordinal)).CacheHit).IsTrue();
        var failure = await Failure(() => Build(generator, [], output, options with { Quality = new(checkOrphans: false) }));
        var diagnostic = failure.Diagnostics.Single(static item => item.Id == "LSQ002");
        await AssertLocation(diagnostic, raw, "#gone", "guide.md");
        await Assert.That(diagnostic.MappingPrecision).IsEqualTo("Exact");
        await Assert.That(diagnostic.RelatedLocations.Any(static location => location.FilePath == "guide/index.html")).IsTrue();
    }

    [Test]
    public async Task LegacyPostCacheRebindsSameBodyAfterHeaderOnlyOffsetChanges()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "guide.md");
        const string raw = "---\r\ntitle: Guide\r\ndate: 2026-11-12T00:00:00Z\r\nsummary: Guide\r\n---\r\n\r\n# Guide\r\n\r\n😀 [external](https://outside.test/) [bad](#gone)\r\n";
        await File.WriteAllTextAsync(path, raw);
        var reader = new MarkdownPostReader();
        var post = await reader.ReadAsync(path, workspace.Root);
        var generator = new SiteGenerator();
        var output = Path.Combine(workspace.Root, "output");
        var options = Options(workspace.Root);
        await Build(generator, [post], output, options);
        var warm = await Build(generator, [post], output, options);
        await Assert.That(warm.BuildReport.Nodes.Single(static node => node.NodeId == "page:markdown:posts/guide.html").CacheHit).IsTrue();
        var failure = await Failure(() => Build(generator, [post], output, options with { Quality = new(checkOrphans: false) }));
        await AssertLocation(failure.Diagnostics.Single(static item => item.Id == "LSQ002"), raw, "#gone", path);

        var changed = raw.Replace("summary: Guide", "# Header-only extra line\r\nsummary: Guide", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, changed);
        post = await reader.ReadAsync(path, workspace.Root);
        var rebuilt = await Build(generator, [post], output, options);
        await Assert.That(rebuilt.BuildReport.Nodes.Single(static node => node.NodeId == "page:markdown:posts/guide.html").CacheHit).IsFalse();
        var relocated = await Failure(() => Build(generator, [post], output, options with { Quality = new(checkOrphans: false) }));
        await AssertLocation(relocated.Diagnostics.Single(static item => item.Id == "LSQ002"), changed, "#gone", path);

        var otherPath = Path.Combine(workspace.Root, "other.md");
        var otherRaw = changed.Replace("title: Guide", "# Another header line\r\ntitle: Other", StringComparison.Ordinal);
        await File.WriteAllTextAsync(otherPath, otherRaw);
        var other = await reader.ReadAsync(otherPath, workspace.Root);
        var rebound = await Failure(() => Build(generator, [post, other], output, options with { Quality = new(checkOrphans: false) }));
        await AssertLocation(rebound.Diagnostics.Single(item => item.Id == "LSQ002" && item.Location!.FilePath == otherPath), otherRaw, "#gone", otherPath);
    }

    [Test]
    public async Task ArtifactOriginMetadataRoundTripsAndRejectsAnUnboundHash()
    {
        using var workspace = new TemporaryWorkspace();
        const string markdown = "[bad](missing.html)";
        var compiled = new LithoMarkdownCompiler().Analyze(markdown);
        var origins = RenderedHtmlOrigins.Bind(compiled.Html, markdown, compiled.RenderOrigins,
            new DocumentSource("guide.md", 0) { ExactBody = markdown });
        var root = Path.Combine(workspace.Root, "cache");
        var identity = new string('a', 64);
        var cache = new SiteBuildCache(root, identity);
        var node = new CachedBuildNode("page:guide", new string('b', 64),
            [new("guide", "guide/index.html", Encoding.UTF8.GetByteCount(compiled.Html), origins.HtmlHash)])
        {
            HtmlOrigins = [new("guide/index.html", origins)],
        };
        var digest = await cache.SaveAsync([node], default);
        var restored = (await cache.LoadAsync(digest, default))["page:guide"].HtmlOrigins.Single().Origins;
        var diagnostic = restored.MapDiagnostic((await Validate(compiled.Html)).Diagnostics.Single(static item => item.Id == "LSQ001"));
        await AssertLocation(diagnostic, markdown, "missing.html", "guide.md");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, identity, "builds", digest + ".json")))!;
        manifest["Nodes"]![0]!["HtmlOrigins"]![0]!["Origins"]!["HtmlHash"] = new string('c', 64);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var corruptedDigest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await File.WriteAllBytesAsync(Path.Combine(root, identity, "builds", corruptedDigest + ".json"), bytes);
        await Assert.That(await cache.LoadAsync(corruptedDigest, default)).IsEmpty();

        var unversioned = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, identity, "builds", digest + ".json")))!;
        await Assert.That(unversioned["Nodes"]![0]!["HtmlOrigins"]![0]!["Origins"]!.AsObject().Remove("Version")).IsTrue();
        var unversionedBytes = JsonSerializer.SerializeToUtf8Bytes(unversioned);
        var unversionedDigest = Convert.ToHexStringLower(SHA256.HashData(unversionedBytes));
        await File.WriteAllBytesAsync(Path.Combine(root, identity, "builds", unversionedDigest + ".json"), unversionedBytes);
        await Assert.That(await cache.LoadAsync(unversionedDigest, default)).IsEmpty();
    }

    [Test]
    [Arguments("\r\n", false)]
    [Arguments("\r", false)]
    [Arguments("\n", false)]
    [Arguments("\f", false)]
    [Arguments("\u0085", false)]
    [Arguments("\u2028", false)]
    [Arguments("\u2029", false)]
    [Arguments("\r\n", true)]
    public async Task WrittenArtifactNormalizationKeepsDistinctTargetsBoundAfterWarmCache(string separator, bool leadingBom)
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        const string body = "[first](#gone)\r\n\r\n[second](#gone)\r\n";
        const string raw = "\uFEFF---\r\ntitle: Guide\r\n---\r\n" + body;
        await File.WriteAllBytesAsync(Path.Combine(content, "guide.md"), new UTF8Encoding(false, true).GetBytes(raw));
        var loaded = await new MarkdownContentCollectionLoader<FrontMatter>(new("guides"), content,
            static _ => SiteRoute.ForDirectoryIndex("guide"), static entry => new PageMetadata(entry.FrontMatter.Title),
            transformationId: new("artifact-normalization:v1"), isCacheable: true).LoadAsync();
        await Assert.That(loaded.IsSuccess).IsTrue();

        var fragment = new LithoMarkdownCompiler().Analyze(body).Html;
        var first = fragment.IndexOf("#gone", StringComparison.Ordinal);
        var second = fragment.IndexOf("#gone", first + 1, StringComparison.Ordinal);
        await Assert.That(first >= 0 && second > first).IsTrue();
        // Before the fix, CRLF removal made the second written target collide with the first raw origin.
        var prefix = string.Concat(Enumerable.Repeat(separator, second - first));
        if (separator == "\r\n")
        {
            var beforeWrite = prefix + fragment;
            var written = beforeWrite.ReplaceLineEndings("\n");
            await Assert.That(written.IndexOf("#gone", written.IndexOf("#gone", StringComparison.Ordinal) + 1, StringComparison.Ordinal))
                .IsEqualTo(beforeWrite.IndexOf("#gone", StringComparison.Ordinal));
        }
        var generator = new SiteGenerator();
        var output = Path.Combine(workspace.Root, "output");
        var options = Options(workspace.Root) with
        {
            ContentCollections = [new SiteContentCollection<FrontMatter, string>(loaded.Collection!, (entry, context) =>
                (leadingBom ? "\uFEFF" : string.Empty) + context.RenderDocument(prefix + context.RenderMarkdown(entry.Body)))
            {
                RendererFingerprint = "artifact-normalization:v1:"
                    + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prefix))) + ":" + leadingBom,
            }],
        };
        await Build(generator, [], output, options);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(output, "guide", "index.html"));
        await Assert.That(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })).IsEqualTo(leadingBom);
        var inspected = new Dictionary<string, CachedBuildArtifact>(StringComparer.Ordinal);
        var writtenHtml = await SiteGenerator.ReadQualityTextAsync(output, "guide/index.html", inspected, default);
        await Assert.That(writtenHtml.StartsWith('\uFEFF')).IsFalse();
        await Assert.That(writtenHtml).IsEqualTo(writtenHtml.ReplaceLineEndings("\n"));
        await Assert.That(inspected["guide/index.html"].Sha256)
            .IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(bytes)));

        var warm = await Build(generator, [], output, options);
        await Assert.That(warm.BuildReport.Nodes.Single(static node => node.NodeId.StartsWith("page:collection:", StringComparison.Ordinal)).CacheHit).IsTrue();
        var failure = await Failure(() => Build(generator, [], output, options with { Quality = new(checkOrphans: false) }));
        var issues = failure.Diagnostics.Where(static diagnostic => diagnostic.Id == "LSQ002")
            .OrderBy(static diagnostic => diagnostic.Location!.Line).ThenBy(static diagnostic => diagnostic.Location!.Column).ToArray();
        await Assert.That(issues.Length).IsEqualTo(2);
        var original = new SourceText(raw);
        var sourceStart = raw.IndexOf("#gone", StringComparison.Ordinal);
        foreach (var issue in issues)
        {
            var from = original.GetLineAndColumn(sourceStart);
            var to = original.GetLineAndColumn(sourceStart + "#gone".Length);
            await Assert.That(issue.Location!.FilePath).IsEqualTo("guide.md");
            await Assert.That((issue.Location.Line, issue.Location.Column, issue.Location.EndLine, issue.Location.EndColumn))
                .IsEqualTo(((int?)from.Line, (int?)from.Column, (int?)to.Line, (int?)to.Column));
            await Assert.That(issue.MappingPrecision).IsEqualTo("Exact");
            await Assert.That(issue.RelatedLocations.Any(static location => location.FilePath == "guide/index.html")).IsTrue();
            sourceStart = raw.IndexOf("#gone", sourceStart + 1, StringComparison.Ordinal);
        }
    }

    private static async Task<SiteQualityReport> Validate(string html)
    {
        await using var shared = new BuildHtmlFacts(default, rawDemand: HtmlRawFactsDemand.Quality);
        await shared.BeginQualityAsync();
        return await SiteQualityValidator.ValidateAsync(BaseUrl, ["guide/index.html"], (_, _) => Task.FromResult(html),
            new Dictionary<string, SiteRoute> { ["guide/index.html"] = SiteRoute.ForDirectoryIndex("guide", BaseUrl) },
            new HashSet<string>(), new Dictionary<string, SiteRoute>(), new(checkOrphans: false), default,
            sharedFacts: shared);
    }

    private static SiteGenerationOptions Options(string root) => new()
    {
        BuildTimestamp = Timestamp,
        BuildCacheDirectory = Path.Combine(root, "cache"),
    };

    private static Task<SiteGenerationResult> Build(SiteGenerator generator, IReadOnlyList<MarkdownPost> posts, string output, SiteGenerationOptions options) =>
        generator.GenerateWithOptionsAsync(new SiteSettings { BaseUrl = BaseUrl, Title = "Origins" }, posts, output, false,
            new() { Template = new BlogSiteTemplate(), FaviconSourceDirectory = Path.Combine(Path.GetDirectoryName(output)!, "no-favicon") }, options, default);

    private static async Task<SiteQualityValidationException> Failure(Func<Task<SiteGenerationResult>> build)
    {
        try
        {
            await build();
        }
        catch (SiteQualityValidationException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Expected a quality failure.");
    }

    private static async Task AssertLocation(SiteDiagnostic diagnostic, string raw, string token, string path)
    {
        var start = raw.IndexOf(token, StringComparison.Ordinal);
        var source = new SourceText(raw);
        var from = source.GetLineAndColumn(start);
        var to = source.GetLineAndColumn(start + token.Length);
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo(path);
        await Assert.That((diagnostic.Location.Line, diagnostic.Location.Column, diagnostic.Location.EndLine, diagnostic.Location.EndColumn))
            .IsEqualTo(((int?)from.Line, (int?)from.Column, (int?)to.Line, (int?)to.Column));
    }

    public sealed class FrontMatter { public string Title { get; init; } = string.Empty; }
    private sealed class NoProps { }
    private sealed class BadLink : ISiteComponent<NoProps>
    {
        public IHtmlContent Render(NoProps props, ComponentRenderingContext context) => Html.UnsafeRaw("<a href=\"missing.html\">Generated</a>");
    }
}
