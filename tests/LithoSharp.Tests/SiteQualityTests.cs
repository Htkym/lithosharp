using LithoSharp.Configuration;
using LithoSharp.Diagnostics;
using LithoSharp.HtmlParsing;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class SiteQualityTests
{
    private const string BaseUrl = "https://example.test/sub/";

    [Test]
    public async Task SharedFacts_ReadEachFileOnceAndPreserveBaseAnchorsSrcsetCssAndRawLocations()
    {
        const string inlineCss = """
            <div style="content:'url(../attribute-string-missing.png)';--ignored:myurl('../attribute-name-missing.png');background:u\72l('../images/\61 ttribute.png')"></div>
            <style>
              /* url('../comment-missing.png') */
              .inline { background: UrL(../images/in\line.png) }
              .inline::after { content: "url('../style-string-missing.png')"; --hash: #url('../hash-missing.png') }
            </style>
            """;
        var files = new Dictionary<string, string>
        {
            ["index.html"] = Page("", "Home", "<base href='/sub/guide/'><a href='#section'>Guide</a>"
                + "<img srcset='data:image/png;base64,AAAA 1x, ../images/p.png 2x'><link rel=stylesheet href='../site.css'>"
                + inlineCss),
            ["guide/index.html"] = Page("guide/", "Guide", "<h2 id=section>Section</h2><a name=legacy></a><a href='../'>Home</a>"
                + "<template><h2 id=hidden>Inert</h2><img src=missing.png></template>"),
            ["site.css"] = """
                /* url('registered-comment-missing.png') */
                body { background: URL('images/\72 egistered.png') }
                body::before { content: 'url(registered-string-missing.png)'; --ignored: myurl(registered-name-missing.png) }
                """
        };
        var assets = new HashSet<string>(StringComparer.Ordinal) { "images/p.png", "images/attribute.png", "images/inline.png", "images/registered.png" };
        var routes = files.Keys.Concat(assets).ToDictionary(path => path,
            path => path.EndsWith("index.html", StringComparison.Ordinal)
                ? SiteRoute.ForDirectoryIndex(path[..^10], BaseUrl) : SiteRoute.ForFile(path, BaseUrl));
        var reads = new Dictionary<string, int>();
        Task<string> Read(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            reads[path] = reads.GetValueOrDefault(path) + 1;
            return Task.FromResult(files[path]);
        }
        var redirects = new Dictionary<string, SiteRoute>();
        var report = await SiteQualityValidator.ValidateAsync(BaseUrl, files.Keys.ToArray(), Read, routes, assets, redirects, new(), default);
        await Assert.That(report.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(reads.Values).IsEquivalentTo([1, 1, 1]);

        reads.Clear();
        files["index.html"] = Page("", "Home", "<base href='/sub/guide/'>\r\n😀<a href='#hidden'>Missing</a>"
            + "<a href='#legacy'>Legacy</a><img src='../images/p.png'><link rel=stylesheet href='../site.css'>" + inlineCss);
        report = await SiteQualityValidator.ValidateAsync(BaseUrl, files.Keys.ToArray(), Read, routes, assets, redirects, new(), default);
        var missing = report.Diagnostics.Single(diagnostic => diagnostic.Id == "LSQ002");
        await Assert.That(missing.Location!.Line).IsEqualTo(2);
        await Assert.That(missing.Location.Column).IsEqualTo(3);
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Id is "LSQ001" or "LSQ008")).IsFalse();
        await Assert.That(reads.Values).IsEquivalentTo([1, 1, 1]);
    }

    [Test]
    public async Task IncompleteTreeCoverage_StopsQualityBeforeReplacingPublishedOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();
        var settings = new SiteSettings { BaseUrl = BaseUrl };
        await generator.GenerateWithOptionsAsync(settings, [], output, true,
            new() { Template = new TextTemplate(Page("", "Home", "")) }, new() { Quality = new() }, default);
        var original = await File.ReadAllBytesAsync(Path.Combine(output, "index.html"));
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output, true,
            new() { Template = new TextTemplate(Page("", "Home", "<template shadowrootmode=open><p>Unsupported</p></template>")) },
            new() { Quality = new() }, default)).Throws<HtmlFactsIncompleteException>();
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(output, "index.html"))).IsEquivalentTo(original);
    }

    [Test]
    public async Task DeclaredChunkDependenciesAreReachableButUnusedRootsRemainWarnings()
    {
        var files = new Dictionary<string, string> { ["index.html"] = Page("", "Home", "<script src='entry.js'></script>") };
        var assets = new[] { "entry.js", "shared.js", "lazy.js", "unused.js" };
        var routes = files.Keys.Concat(assets).ToDictionary(path => path, path => path == "index.html" ? SiteRoute.ForDirectoryIndex("", BaseUrl) : SiteRoute.ForFile(path, BaseUrl));
        var nodes = assets.Select(path => new LithoSharp.Build.BuildNode(new(path),
            dependencies: path == "entry.js" ? [new("shared.js")] : path == "shared.js" ? [new("lazy.js")] : [],
            artifacts: [new(new(path), new(path), path)])).ToArray();
        var report = await SiteQualityValidator.ValidateAsync(BaseUrl, files.Keys.ToArray(), (path, _) => Task.FromResult(files[path]), routes,
            assets.ToHashSet(StringComparer.Ordinal), new Dictionary<string, SiteRoute>(), new(), default, nodes);
        await Assert.That(report.Diagnostics.Where(diagnostic => diagnostic.Id == "LSQ008").Select(diagnostic => diagnostic.Location!.FilePath)).IsEquivalentTo(["unused.js"]);
    }

    [Test]
    [Arguments("xlink:href='missing.html' href='valid.html'")]
    [Arguments("href='valid.html' xlink:href='missing.html'")]
    public async Task SvgHrefRemainsDistinctFromXLinkRegardlessOfAttributeOrder(string attributes)
    {
        var files = new Dictionary<string, string>
        {
            ["index.html"] = Page("", "Home", $"<svg><a {attributes}>Valid</a></svg>"),
            ["valid.html"] = Page("valid.html", "Valid", "")
        };
        var facts = LithoSharp.HtmlParsing.HtmlFacts.Parse(files["index.html"]);
        var link = facts.Elements.Single(element => element.Name == "a");
        await Assert.That(LithoSharp.HtmlParsing.HtmlFacts.Attribute(link, "href")).IsEqualTo("valid.html");
        await Assert.That(link.Attributes.Single(attribute => attribute.Namespace == LithoSharp.HtmlParsing.HtmlNamespaces.XLink).Value.Value)
            .IsEqualTo("missing.html");
        var report = await Validate(files, []);
        await Assert.That(report.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DomResolvesRelativeLinksEntitiesFragmentsImagesAndBasePath()
    {
        var files = new Dictionary<string, string>
        {
            ["index.html"] = Page("", "Home", "<a href='guide/?a=1&amp;b=2#章'>Guide</a><img srcset='data:image/png;base64,AAAA 1x, images/p.png 2x'><link rel='stylesheet' href='site.css'>"),
            ["guide/index.html"] = Page("guide/", "Guide", "<h2 id='章'>Heading</h2><a href='../'>Home</a>"),
            ["site.css"] = "body { background: url('images/p.png'); }"
        };
        var report = await Validate(files, ["images/p.png"]);
        await Assert.That(report.Diagnostics.Count).IsEqualTo(0);
        files["index.html"] = Page("", "Home", "<base href='/sub/guide/'><a href='#章'>Guide</a><img src='../images/p.png'>");
        report = await Validate(files, ["images/p.png"]);
        await Assert.That(report.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BrokenTargetsAnchorsCanonicalAndOrphansHaveLocations()
    {
        var files = new Dictionary<string, string>
        {
            ["index.html"] = Page("wrong/", "Same", "\n<a href='missing.html'>Missing</a><a href='other.html#absent'>Anchor</a><img src='/outside.png'><img src=''><link rel='stylesheet' href=''>"),
            ["other.html"] = Page("other.html", "Same", ""),
            ["orphan.html"] = "<html><head><title>Orphan</title></head><body></body></html>"
        };
        var report = await Validate(files, ["unused.png"]);
        foreach (var id in new[] { "LSQ001", "LSQ002", "LSQ003", "LSQ007", "LSQ008", "LSQ009", "LSQ010" })
            await Assert.That(report.Diagnostics.Any(d => d.Id == id)).IsTrue();
        await Assert.That(report.Diagnostics.First(d => d.Id == "LSQ001").Location!.Line).IsNotNull();
        await Assert.That(report.Diagnostics.Any(d => d.Id == "LSQ007" && d.Location!.FilePath == "other.html")).IsFalse();
    }

    [Test]
    public async Task QualityFailureAndWarningThresholdPreservePreviousOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();
        var settings = new SiteSettings { BaseUrl = BaseUrl };
        await generator.GenerateWithOptionsAsync(settings, [], output, true, new SiteCustomization { Template = new TextTemplate(Page("", "Home", "")) },
            new SiteGenerationOptions { Quality = new() }, default);
        var original = await File.ReadAllTextAsync(Path.Combine(output, "index.html"));
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output, true, new SiteCustomization { Template = new TextTemplate(Page("", "Home", "<a href='missing'>broken</a>")) },
            new SiteGenerationOptions { Quality = new() }, default))
            .Throws<SiteQualityValidationException>();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "index.html"))).IsEqualTo(original);
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output, true, new SiteCustomization { Template = new TextTemplate("<link rel='canonical' href='" + BaseUrl + "'>") },
            new SiteGenerationOptions { Quality = new(SiteDiagnosticSeverity.Warning) }, default))
            .Throws<SiteQualityValidationException>();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "index.html"))).IsEqualTo(original);
        var result = await generator.GenerateWithOptionsAsync(settings, [], output, true, new SiteCustomization { Template = new TextTemplate("<link rel='canonical' href='" + BaseUrl + "'>") },
            new SiteGenerationOptions { Quality = new() }, default);
        await Assert.That(result.QualityReport.Diagnostics.Any(d => d.Id == "LSQ010")).IsTrue();
    }

    [Test]
    public async Task RedirectIsOwnedArtifactAndRejectsCyclesChainsMissingAndCollisions()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();
        var settings = new SiteSettings { BaseUrl = BaseUrl };
        var customization = new SiteCustomization { Template = new TextTemplate(Page("", "Home", "")) };
        var options = new SiteGenerationOptions
        {
            Quality = new(),
            Redirects = [new(SiteRoute.ForDirectoryIndex("old"), SiteRoute.ForDirectoryIndex(""))]
        };
        var result = await generator.GenerateWithOptionsAsync(settings, [], output, true, customization, options, default);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "old", "index.html"))).Contains("url=/sub/");
        await Assert.That(result.QualityReport.Diagnostics.Count).IsEqualTo(0);
        foreach (var redirects in new SiteRedirect[][]
        {
            [new(SiteRoute.ForFile("a.html"), SiteRoute.ForFile("a.html"))],
            [new(SiteRoute.ForFile("a.html"), SiteRoute.ForFile("b.html")), new(SiteRoute.ForFile("b.html"), SiteRoute.ForFile("a.html"))],
            [new(SiteRoute.ForFile("a.html"), SiteRoute.ForFile("b.html")), new(SiteRoute.ForFile("b.html"), SiteRoute.ForDirectoryIndex(""))],
            [new(SiteRoute.ForFile("a.html"), SiteRoute.ForFile("missing.html"))]
        })
            await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output, true, customization,
                options with { Redirects = redirects }, default)).Throws<SiteQualityValidationException>();
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output, true, customization,
            options with { Redirects = [new(SiteRoute.ForDirectoryIndex(""), SiteRoute.ForDirectoryIndex(""))] }, default))
            .Throws<SiteRouteValidationException>();
        await generator.GenerateWithOptionsAsync(settings, [], output, true, customization, options with { Redirects = [] }, default);
        await Assert.That(File.Exists(Path.Combine(output, "old", "index.html"))).IsFalse();
    }

    [Test]
    public async Task RegisteredCssIsCheckedBeforeOutputCommit()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "custom.css");
        var output = Path.Combine(workspace.Root, "output");
        var publicRoot = Path.Combine(workspace.Root, "public");
        Directory.CreateDirectory(Path.Combine(publicRoot, "images"));
        await File.WriteAllTextAsync(Path.Combine(publicRoot, "images", "quoted.svg"), "<svg xmlns='http://www.w3.org/2000/svg'></svg>");
        await File.WriteAllTextAsync(Path.Combine(publicRoot, "images", "unquoted.svg"), "<svg xmlns='http://www.w3.org/2000/svg'></svg>");
        await File.WriteAllTextAsync(source, "body { background: url(missing.png); }");
        var css = new SiteAsset("css", workspace.Root, "custom.css", "custom.css");
        var generator = new SiteGenerator();
        var settings = new SiteSettings { BaseUrl = BaseUrl };
        var customization = new SiteCustomization { Template = new CssTemplate(css) };
        var options = new SiteGenerationOptions { Assets = [css], PublicDirectory = publicRoot, Quality = new() };
        SiteQualityValidationException? failure = null;
        try { await generator.GenerateWithOptionsAsync(settings, [], output, true, customization, options, default); }
        catch (SiteQualityValidationException error) { failure = error; }
        await Assert.That(failure?.Diagnostics.Any(diagnostic => diagnostic.Id == "LSQ001"
            && diagnostic.Location?.FilePath.EndsWith(".css", StringComparison.Ordinal) == true
            && diagnostic.Message.Contains("missing.png", StringComparison.Ordinal)) == true).IsTrue();
        await Assert.That(Directory.Exists(output)).IsFalse();

        const string valid = """
            /* url(comment-missing.png) */
            body::before { content: "url(string-missing.png)"; --ignored: myurl(name-missing.png) }
            body { background: u\72l('images/\71 uoted.svg'/* trailing comment */); mask: url(images/un\quoted.svg) }
            .bad { background: url(bad value\) url(remnant-missing.png)); mask: url(images/unquoted.svg) }
            """;
        await File.WriteAllTextAsync(source, valid);
        var result = await generator.GenerateWithOptionsAsync(settings, [], output, true, customization, options, default);
        await Assert.That(result.QualityReport.Diagnostics.Any(diagnostic => diagnostic.Id is "LSQ001" or "LSQ008")).IsFalse();
        var originalHtml = await File.ReadAllBytesAsync(Path.Combine(output, "index.html"));
        var publishedCss = Directory.EnumerateFiles(output, "custom*.css").Single();
        var originalCss = await File.ReadAllBytesAsync(publishedCss);
        await File.WriteAllTextAsync(source, valid + "\n.missing { background: url(real-missing.svg) }");
        failure = null;
        try { await generator.GenerateWithOptionsAsync(settings, [], output, true, customization, options, default); }
        catch (SiteQualityValidationException error) { failure = error; }
        await Assert.That(failure?.Diagnostics.Any(diagnostic => diagnostic.Id == "LSQ001"
            && diagnostic.Message.Contains("real-missing.svg", StringComparison.Ordinal)) == true).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(output, "index.html"))).IsEquivalentTo(originalHtml);
        await Assert.That(await File.ReadAllBytesAsync(publishedCss)).IsEquivalentTo(originalCss);
        await Assert.That(Directory.EnumerateFiles(output, "custom*.css").Single()).IsEqualTo(publishedCss);
    }

    [Test]
    public async Task CssUrlRecoveryDecodesEscapesWithoutReenteringBadUrlsAndObservesCancellationDuringScanning()
    {
        var css = "u\\72l(\\000061 b.png) url(bad value\\) url(remnant.png)) url(kept.png)"
            + " url('im\\\r\nage.png') url(\\0.png) url(\\d800.png) url(\\110000.png) url(final.png";
        await Assert.That(CssUrlScanner.Read(css, default).SequenceEqual(
            ["ab.png", "kept.png", "image.png", "\uFFFD.png", "\uFFFD.png", "\uFFFD.png", "final.png"])).IsTrue();
        await Assert.That(CssUrlScanner.Read("url('images/\\61\r\nttribute.png')", default).Single()).IsEqualTo("images/attribute.png");
        await Assert.That(CssUrlScanner.Read("url('quoted-eof.png", default).Single()).IsEqualTo("quoted-eof.png");
        using var cancellation = new CancellationTokenSource();
        using var urls = CssUrlScanner.Read("url(first.png) /*" + new string('x', 32768), cancellation.Token).GetEnumerator();
        await Assert.That(urls.MoveNext()).IsTrue();
        await Assert.That(urls.Current).IsEqualTo("first.png");
        cancellation.Cancel();
        await Assert.That(() => urls.MoveNext()).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task CancellationAndExternalCacheInsideOutputAreRejected()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await Validate(new() { ["index.html"] = Page("", "Home", "") }, [], cancellation.Token))
            .Throws<OperationCanceledException>();
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], output, true, null,
            new SiteGenerationOptions { Quality = new(externalLinks: new(Path.Combine(output, "cache.json"))) }, default))
            .Throws<ArgumentException>();
    }

    private static string Page(string path, string title, string body) =>
        $"<!doctype html><html><head><title>{title}</title><link rel='canonical' href='{BaseUrl}{path}'><meta name='description' content='Description'><meta property='og:title' content='{title}'><meta property='og:description' content='Description'><meta property='og:url' content='{BaseUrl}{path}'></head><body>{body}</body></html>";

    private static Task<SiteQualityReport> Validate(Dictionary<string, string> files, string[] assets, CancellationToken cancellationToken = default) =>
        SiteQualityValidator.ValidateAsync(BaseUrl, files,
            files.Keys.Concat(assets).ToDictionary(path => path, path => path.EndsWith("index.html", StringComparison.Ordinal)
                ? SiteRoute.ForDirectoryIndex(path[..^10], BaseUrl) : SiteRoute.ForFile(path, BaseUrl)),
            assets.ToHashSet(StringComparer.Ordinal), new Dictionary<string, SiteRoute>(), new(), cancellationToken);

    private sealed class TextTemplate(string html) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult([new SiteTemplateFile { RelativePath = "index.html", Content = html }]));
    }

    private sealed class CssTemplate(SiteAsset css) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) =>
            new TextTemplate(Page("", "Home", $"<link rel='stylesheet' href='{Html.Encode(context.Assets.GetUrl(css).Value)}'>"))
                .RenderAsync(context, cancellationToken);
    }
}
