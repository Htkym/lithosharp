using LithoSharp.Configuration;
using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.HtmlParsing;
using LithoSharp.Quality;
using LithoSharp.Routing;
using System.Text.Json;

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
        (string Id, SiteDiagnosticSeverity Severity, string Message, string? Path, int? Line, int? Column)[] expected =
        [
            ("LSQ003", SiteDiagnosticSeverity.Error, "The canonical URL must identify this page's public route.", "index.html", 1, 47),
            ("LSQ003", SiteDiagnosticSeverity.Error, "A page must declare exactly one canonical URL.", "orphan.html", null, null),
            ("LSQ009", SiteDiagnosticSeverity.Warning, "Page title 'Same' is duplicated.", "index.html", null, null),
            ("LSQ009", SiteDiagnosticSeverity.Warning, "Page title 'Same' is duplicated.", "other.html", null, null),
            ("LSQ010", SiteDiagnosticSeverity.Warning, "SEO field 'description' is missing or empty.", "orphan.html", null, null),
            ("LSQ010", SiteDiagnosticSeverity.Warning, "SEO field 'og:description' is missing or empty.", "orphan.html", null, null),
            ("LSQ010", SiteDiagnosticSeverity.Warning, "SEO field 'og:title' is missing or empty.", "orphan.html", null, null),
            ("LSQ010", SiteDiagnosticSeverity.Warning, "SEO field 'og:url' is missing or empty.", "orphan.html", null, null),
        ];
        var actual = report.Diagnostics.Where(d => d.Id is "LSQ003" or "LSQ009" or "LSQ010")
            .Select(d => (d.Id, d.Severity, d.Message, d.Location?.FilePath, d.Location?.Line, d.Location?.Column));
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task SrcsetCandidatesFollowSpecificationFixturesAndKeepQualityResolutionAndLocations()
    {
        using var corpus = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "SrcsetUrlCases.json")));
        foreach (var row in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var expected = row.GetProperty("urls").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var input = row.GetProperty("input").GetString()!;
            var candidates = SrcsetUrlScanner.ReadCandidates(input, default).ToArray();
            await Assert.That(candidates.Select(candidate => candidate.Value).SequenceEqual(expected)).IsTrue();
            var from = 0;
            foreach (var candidate in candidates)
            {
                var start = input.IndexOf(candidate.Value, from, StringComparison.Ordinal);
                await Assert.That(candidate.Source).IsEqualTo(new HtmlSpan(start, candidate.Value.Length));
                from = candidate.Source.End;
            }
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => SrcsetUrlScanner.ReadCandidates("x.png 1x", cancellation.Token).ToArray())
            .Throws<OperationCanceledException>();

        var files = new Dictionary<string, string>
        {
            ["index.html"] = Page("", "Home", "<base href='/sub/gallery/'>\r\n"
                + "<img srcset='data:image/png;base64,AAAA 1x, ../images/p.png?tag=a&amp;b=b .5x, "
                + "../images/orphan.png 1x 2x, phantom-missing.png future(nested-missing.png 1x, leaked-missing.png 2x), "
                + "../images/missing.png 640w 480h'>")
        };
        var report = await Validate(files, ["images/p.png", "images/orphan.png"]);
        var missing = report.Diagnostics.Single(diagnostic => diagnostic.Id == "LSQ001");
        await Assert.That(missing.Message).Contains("../images/missing.png");
        await Assert.That(missing.Location!.FilePath).IsEqualTo("index.html");
        await Assert.That(missing.Location.Line).IsEqualTo(2);
        await Assert.That(missing.Location.Column).IsEqualTo(1);
        await Assert.That(report.Diagnostics.Where(diagnostic => diagnostic.Id == "LSQ008")
            .Select(diagnostic => diagnostic.Location!.FilePath)).IsEquivalentTo(["images/orphan.png"]);
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
        var original = await File.ReadAllBytesAsync(Path.Combine(output, "index.html"));
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output, true, new SiteCustomization { Template = new TextTemplate(Page("", "Home", "<a href='missing'>broken</a>")) },
            new SiteGenerationOptions { Quality = new() }, default))
            .Throws<SiteQualityValidationException>();
        await Assert.That((await File.ReadAllBytesAsync(Path.Combine(output, "index.html"))).SequenceEqual(original)).IsTrue();
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output, true,
            new SiteCustomization { Template = new TextTemplate(Page("", "Home", "<img srcset='ignored-missing.png 1x 2x, real-missing.png 2x'>")) },
            new SiteGenerationOptions { Quality = new() }, default)).Throws<SiteQualityValidationException>();
        await Assert.That((await File.ReadAllBytesAsync(Path.Combine(output, "index.html"))).SequenceEqual(original)).IsTrue();
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output, true, new SiteCustomization { Template = new TextTemplate("<link rel='canonical' href='" + BaseUrl + "'>") },
            new SiteGenerationOptions { Quality = new(SiteDiagnosticSeverity.Warning) }, default))
            .Throws<SiteQualityValidationException>();
        await Assert.That((await File.ReadAllBytesAsync(Path.Combine(output, "index.html"))).SequenceEqual(original)).IsTrue();
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
        try
        {
            await generator.GenerateWithOptionsAsync(settings, [], output, true, customization, options, default);
        }
        catch (SiteQualityValidationException error)
        {
            failure = error;
        }
        await Assert.That(failure?.Diagnostics.Any(diagnostic => diagnostic.Id == "LSQ001"
            && diagnostic.Location?.FilePath.EndsWith(".css", StringComparison.Ordinal) == true
            && diagnostic.Message.Contains("missing.png", StringComparison.Ordinal)) == true).IsTrue();
        await Assert.That(Directory.Exists(output)).IsFalse();

        const string valid = """
            /* url(comment-missing.png) */
            body::before { content: "url(string-missing.png)"; --ignored: myurl(name-missing.png) }
            body { background: u\72l('images/\71 uoted.svg'/* trailing comment */); mask: url(images/un\quoted.svg) }
            """;
        await File.WriteAllTextAsync(source, valid);
        var result = await generator.GenerateWithOptionsAsync(settings, [], output, true, customization, options, default);
        await Assert.That(result.QualityReport.Diagnostics.Any(diagnostic => diagnostic.Id is "LSQ001" or "LSQ008")).IsFalse();
        var originalHtml = await File.ReadAllBytesAsync(Path.Combine(output, "index.html"));
        var publishedCss = Directory.EnumerateFiles(output, "custom*.css").Single();
        var originalCss = await File.ReadAllBytesAsync(publishedCss);
        await File.WriteAllTextAsync(source, valid + "\n.bad { background: url(bad value\\) url(remnant-missing.png)); mask: url(images/unquoted.svg) }");
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output, true, customization, options, default))
            .Throws<HtmlQualitySnapshotIncompleteException>();
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(output, "index.html"))).IsEquivalentTo(originalHtml);
        await Assert.That(await File.ReadAllBytesAsync(publishedCss)).IsEquivalentTo(originalCss);
        await Assert.That(Directory.EnumerateFiles(output, "custom*.css").Single()).IsEqualTo(publishedCss);
        await File.WriteAllTextAsync(source, valid + "\n.missing { background: url(real-missing.svg) }");
        failure = null;
        try
        {
            await generator.GenerateWithOptionsAsync(settings, [], output, true, customization, options, default);
        }
        catch (SiteQualityValidationException error)
        {
            failure = error;
        }
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
    public async Task LiteralCssImportStringsFollowStylesheetTokensWithoutLeakingFromDeclarations()
    {
        const string css = """
            /* @import "comment.css"; */
            @IMPORT/**/'css/\61.css';
            @\69mport "css/b.css" layer(theme) screen;
            @import url("css/c.css");
            .x { content: "@import 'string.css'"; background: url(image.png); @import "nested.css"; }
            func(@import "function.css");
            [attr=@import "selector.css"] {}
            @imported "other-rule.css";
            """;
        await Assert.That(CssUrlScanner.Read(css, default, includeImports: true).SequenceEqual(
            ["css/a.css", "css/b.css", "css/c.css", "image.png"])).IsTrue();
        await Assert.That(CssUrlScanner.Read(css, default).SequenceEqual(["css/c.css", "image.png"])).IsTrue();
        await Assert.That(CssUrlScanner.Read("@import \"bad\nstring.css\";", default, includeImports: true).Any()).IsFalse();
        await Assert.That(CssUrlScanner.Read(
            "@unknown @import \"ghost.css\" url(kept.png); @import \"real.css\";", default, includeImports: true)
            .SequenceEqual(["kept.png", "real.css"])).IsTrue();
        await Assert.That(CssUrlScanner.Read(
            ".fake ; @import \"ghost.css\" url(kept.png) {} @import \"real.css\";", default, includeImports: true)
            .SequenceEqual(["kept.png", "real.css"])).IsTrue();
    }

    [Test]
    public async Task LiteralCssImportsJoinDeclaredClosureWithoutOrphanStylesheetsMaskingTheirAssets()
    {
        var files = new Dictionary<string, string>
        {
            // A generated stylesheet need not itself be a registered asset to root its references.
            ["index.html"] = Page("", "Home", "<link rel='stylesheet' href='css/main.css'>"),
            ["css/main.css"] = "@import 'sub/child.css';",
            ["css/sub/child.css"] = "@import '../main.css';",
            ["css/generated.css"] = "body { background: url('../images/declared.png'); }",
            ["css/orphan.css"] = "body { background: url('../images/orphan.png'); }"
        };
        string[] assets = ["css/sub/child.css", "css/generated.css", "css/orphan.css", "images/declared.png", "images/orphan.png"];
        var routes = files.Keys.Concat(assets).Distinct(StringComparer.Ordinal).ToDictionary(path => path,
            path => path == "index.html" ? SiteRoute.ForDirectoryIndex("", BaseUrl) : SiteRoute.ForFile(path, BaseUrl));
        var child = new BuildNodeId("imported-css");
        var bridge = new BuildNodeId("no-artifact-dependency");
        var generated = new BuildNodeId("declared-css");
        BuildNode[] nodes =
        [
            new(child, dependencies: [bridge], artifacts: [new(new BuildArtifactId("imported-css"), child, "css/sub/child.css")]),
            new(bridge, dependencies: [generated]),
            new(generated, artifacts: [new(new BuildArtifactId("declared-css"), generated, "css/generated.css")])
        ];
        var report = await SiteQualityValidator.ValidateAsync(BaseUrl, files.Keys.ToArray(),
            (path, token) => Task.FromResult(files[path]), routes, assets.ToHashSet(StringComparer.Ordinal),
            new Dictionary<string, SiteRoute>(), new(), default, assetNodes: nodes);
        await Assert.That(report.Diagnostics.Any(diagnostic => diagnostic.Id == "LSQ001")).IsFalse();
        await Assert.That(report.Diagnostics.Where(diagnostic => diagnostic.Id == "LSQ008")
            .Select(diagnostic => diagnostic.Location!.FilePath).Order(StringComparer.Ordinal).SequenceEqual(
                ["css/orphan.css", "images/orphan.png"])).IsTrue();

        // Even an unreachable stylesheet is validated; closure does not suppress missing targets.
        files["css/orphan.css"] = "@import 'missing.css'; " + files["css/orphan.css"];
        var missing = await SiteQualityValidator.ValidateAsync(BaseUrl, files.Keys.ToArray(),
            (path, token) => Task.FromResult(files[path]), routes, assets.ToHashSet(StringComparer.Ordinal),
            new Dictionary<string, SiteRoute>(), new(), default, assetNodes: nodes);
        await Assert.That(missing.Diagnostics.Single(diagnostic => diagnostic.Id == "LSQ001").Location!.FilePath)
            .IsEqualTo("css/orphan.css");

        // The same closure works without declared nodes: style is a root, style attributes are declarations.
        var inlineFiles = new Dictionary<string, string>
        {
            ["index.html"] = Page("", "Home", "<style>@import 'css/main.css';</style>"
                + "<div style=\"@import 'css/attribute-only.css';\">Body</div>"),
            ["css/main.css"] = "@import 'sub/child.css';",
            ["css/sub/child.css"] = "@import '../main.css'; body { background: url('../../images/inline.png'); }",
            ["css/attribute-only.css"] = ""
        };
        string[] inlineAssets = ["css/main.css", "css/sub/child.css", "css/attribute-only.css", "images/inline.png"];
        var inlineRoutes = inlineFiles.Keys.Concat(inlineAssets).Distinct(StringComparer.Ordinal).ToDictionary(path => path,
            path => path == "index.html" ? SiteRoute.ForDirectoryIndex("", BaseUrl) : SiteRoute.ForFile(path, BaseUrl));
        var inlineReport = await SiteQualityValidator.ValidateAsync(BaseUrl, inlineFiles.Keys.ToArray(),
            (path, token) => Task.FromResult(inlineFiles[path]), inlineRoutes, inlineAssets.ToHashSet(StringComparer.Ordinal),
            new Dictionary<string, SiteRoute>(), new(), default);
        await Assert.That(inlineReport.Diagnostics.Any(diagnostic => diagnostic.Id == "LSQ001")).IsFalse();
        await Assert.That(inlineReport.Diagnostics.Where(diagnostic => diagnostic.Id == "LSQ008")
            .Select(diagnostic => diagnostic.Location!.FilePath).SequenceEqual(["css/attribute-only.css"])).IsTrue();
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
