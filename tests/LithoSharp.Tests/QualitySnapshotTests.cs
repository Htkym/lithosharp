using System.Security.Cryptography;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Documentation;
using LithoSharp.HtmlParsing;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class QualitySnapshotTests
{
    [Test]
    public async Task IntegratedSnapshotsPreserveFrozenDiagnosticsAndResourceClosure()
    {
        var index = Document("Home", "/index.html", """
            <a href='guide.html#legacy'>OK</a>
            <a href='guide.html#missing'>Missing</a>
            <a href='guide.html#missing'>Duplicate</a>
            <link rel=stylesheet href='styles/root.css'>
            <img src='images/from-html.png'>
            <div style='background:url(missing-inline.png)'></div>
            """);
        var guide = Document("Guide", "/guide.html", "<p id=x></p><p id=x></p><a name=legacy></a>");
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["index.html"] = index,
            ["guide.html"] = guide,
            ["isolated.html"] = Document("Island", "/isolated.html", string.Empty),
            ["styles/root.css"] = "@import \"nested.css\"; /* url(ignored.png) */",
            ["styles/nested.css"] = """p{background:url('../images/\66rom-css.png')}""",
        };
        string[] assets = ["styles/root.css", "styles/nested.css", "images/from-html.png", "images/from-css.png", "unused.png"];
        var routes = files.Keys.Concat(assets).Distinct(StringComparer.Ordinal).ToDictionary(path => path,
            path => SiteRoute.ForFile(path, "https://example.test/"), StringComparer.Ordinal);
        await using var owner = new BuildHtmlFacts(default, rawDemand: HtmlRawFactsDemand.Quality);
        foreach (var html in files.Where(pair => pair.Key.EndsWith(".html", StringComparison.Ordinal)))
            owner.Get(html.Value, default);
        var before = owner.Counters.ParseCount;
        await owner.BeginQualityAsync();
        var reads = new Dictionary<string, int>(StringComparer.Ordinal);
        var report = await SiteQualityValidator.ValidateAsync("https://example.test/", files.Keys.ToArray(),
            (path, _) =>
            {
                reads[path] = reads.GetValueOrDefault(path) + 1;
                return Task.FromResult(files[path]);
            },
            routes, assets.ToHashSet(StringComparer.Ordinal), new Dictionary<string, SiteRoute>(), new(), default, sharedFacts: owner);
        var expected = new SiteQualityReport([
            new("LSQ001", SiteDiagnosticSeverity.Error, "Internal target 'missing-inline.png' does not exist in this site.", new("index.html", 8, 1)),
            new("LSQ002", SiteDiagnosticSeverity.Error, "Anchor 'missing' does not exist in 'guide.html'.", new("index.html", 4, 1)),
            new("LSQ002", SiteDiagnosticSeverity.Error, "Anchor 'missing' does not exist in 'guide.html'.", new("index.html", 5, 1)),
            new("LSQ007", SiteDiagnosticSeverity.Warning, "Page is not reachable from the site's home page.", new("isolated.html")),
            new("LSQ008", SiteDiagnosticSeverity.Warning, "Registered asset is not reachable through HTML, CSS or declared transitive references.", new("unused.png")),
        ]);
        await Assert.That(report.Format(SiteDiagnosticFormat.Json)).IsEqualTo(expected.Format(SiteDiagnosticFormat.Json));
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(before);
        await Assert.That(before).IsEqualTo(3);
        await Assert.That(reads.Count == files.Count && reads.Values.All(count => count == 1)).IsTrue();
        var guideSnapshot = report.HtmlReferences.Single(page => page.Identity.OutputPath == "guide.html");
        await Assert.That(string.Join(",", guideSnapshot.Anchors.Select(anchor => anchor.Decoded))).IsEqualTo("x,x,legacy");
        await Assert.That(guideSnapshot.Metadata.Count).IsEqualTo(4);
        await Assert.That(report.CssReferences.Single(css => css.OutputPath == "styles/nested.css")
            .References.Single().ResolvedUrl!.AbsoluteUri).IsEqualTo("https://example.test/images/from-css.png");
        await Assert.That(report.HtmlReferences.All(page => page.Coverage.Where(c => c.Projection != HtmlFactsProjection.Search)
            .All(c => c.Status == HtmlFactsCoverageStatus.Complete))).IsTrue();
        var rawIndex = report.HtmlReferences.Single(page => page.Identity.OutputPath == "index.html");
        var decodedFacts = await owner.GetQualityAsync(index, default);
        var decodedIndex = BuildHtmlFacts.BindDecodedQuality(decodedFacts.Facts, rawIndex.Identity, rawIndex.PageUrl, default);
        await Assert.That(rawIndex.CssReferences.Single().Decoded).IsEqualTo("background:url(missing-inline.png)");
        await Assert.That(decodedIndex.CssReferences.Single().Decoded).IsEqualTo(rawIndex.CssReferences.Single().Decoded);
        await Assert.That(decodedIndex.CssReferences.Single().UnresolvedValue).IsEqualTo("missing-inline.png");
        await Assert.That(decodedIndex.CssReferences.Single().Source is null).IsTrue();
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(before);
        await owner.ReleaseAsync();
        await Assert.That(owner.Counters.EntryCount).IsEqualTo(0);
    }

    [Test]
    public async Task GeneratedDocumentIdentityRebindsOnDecodedCacheHits()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(input);
        foreach (var locale in new[] { "en", "ja" })
            await File.WriteAllTextAsync(Path.Combine(input, locale + ".txt"), locale);
        var version = "v1";
        var collection = new ContentCollection<string, string>(new("docs"), input,
            new[] { "en", "ja" }.Select(locale => new ContentEntry<string, string>(new(locale), locale + ".txt", "sha256:" + locale, locale, string.Empty)),
            entry => SiteRoute.ForFile("docs/" + entry.Id.Value + ".html"),
            entry => new PageMetadata("Guide " + entry.Id.Value) { Document = new("docs", version, entry.Id.Value, "guide") });
        var registration = new SiteContentCollection<string, string>(collection,
            (entry, context) => Document("Guide " + entry.Id.Value, context.Route.PublicPath, "<a href='/'>Home</a>"));
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = DateTimeOffset.UnixEpoch,
            Quality = new(checkOrphans: false),
            ContentCollections = [registration]
        };
        var customization = new SiteCustomization
        {
            Template = new FilesTemplate(
            Document("Home", "/index.html", "<a href='docs/en.html'>EN</a><a href='docs/ja.html'>JA</a>"), "p{color:red}")
        };
        var generator = new SiteGenerator();
        var site = new SiteSettings { Title = "Site", BaseUrl = "https://example.test/", TimeZone = "UTC" };
        var output = Path.Combine(workspace.Root, "output");
        var first = await generator.GenerateWithOptionsAsync(site, [], output, true, customization, options, default);
        version = "v2";
        var warm = await generator.GenerateWithOptionsAsync(site, [], output, false, customization, options, default);
        await Assert.That(warm.QualityReport.HtmlParseCount).IsEqualTo(0);
        await Assert.That(warm.QualityReport.HtmlFactsCacheHitCount > 0).IsTrue();
        foreach (var locale in new[] { "en", "ja" })
        {
            var old = first.QualityReport.HtmlReferences.Single(page => page.Identity.OutputPath == "docs/" + locale + ".html");
            var current = warm.QualityReport.HtmlReferences.Single(page => page.Identity.OutputPath == "docs/" + locale + ".html");
            await Assert.That(old.Identity.Document).IsEqualTo(new DocumentKey("docs", "v1", locale, "guide"));
            await Assert.That(current.Identity.Document).IsEqualTo(new DocumentKey("docs", "v2", locale, "guide"));
            await Assert.That(current.Identity.PageId).IsEqualTo("page:collection:4:docs:2:" + locale);
            await Assert.That(current.Identity.PublicPath).IsEqualTo("/docs/" + locale + ".html");
            await Assert.That(current.Coverage.Single(c => c.Projection == HtmlFactsProjection.RawAttributes).Status)
                .IsEqualTo(HtmlFactsCoverageStatus.NotRequested);
        }
        await Assert.That(first.QualityReport.Format(SiteDiagnosticFormat.Json)).IsEqualTo(warm.QualityReport.Format(SiteDiagnosticFormat.Json));
    }

    [Test]
    public async Task RequiredCssCoveragePreservesPublishedOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var generator = new SiteGenerator();
        var site = new SiteSettings { Title = "Coverage", BaseUrl = "https://example.test/", TimeZone = "UTC" };
        var output = Path.Combine(workspace.Root, "output");
        var html = Document("Home", "/index.html", "<link rel=stylesheet href='styles/site.css'>");
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = DateTimeOffset.UnixEpoch,
            Quality = new(checkOrphans: false),
            RawHtmlFactsDemand = HtmlRawFactsDemand.Quality
        };
        await generator.GenerateWithOptionsAsync(site, [], output, true, new() { Template = new FilesTemplate(html, "p{color:red}") }, options, default);
        var before = Snapshot(output);
        var incomplete = CssReferenceSnapshot.Create("styles/site.css", new("https://example.test/styles/site.css"),
            "p{background:url(var(--asset))}", default);
        await Assert.That(incomplete.Coverage.Status).IsEqualTo(HtmlFactsCoverageStatus.Partial);
        await Assert.That(incomplete.Coverage.Reasons.Any(reason => reason.Code == "css_url_unresolved")).IsTrue();
        foreach (var operand in new[] { "#bad", "@bad" })
        {
            var css = "@import " + operand + " url(ok.css); @import 'next.css'; p{background:url(after.png)}";
            var collected = CssUrlScanner.Collect(css, default, includeImports: true);
            await Assert.That(collected.Coverage.Status).IsEqualTo(HtmlFactsCoverageStatus.Partial);
            await Assert.That(collected.Coverage.Reasons.Any(reason => reason.Code == "css_import_unresolved")).IsTrue();
            await Assert.That(collected.Values.SequenceEqual(new[] { "ok.css", "next.css", "after.png" })).IsTrue();
            await Assert.That(CssUrlScanner.Read(css, default, includeImports: true)
                .SequenceEqual(new[] { "ok.css", "next.css", "after.png" })).IsTrue();
        }
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(site, [], output, true,
            new() { Template = new FilesTemplate(html, "p{background:url(var(--asset))}") }, options, default))
            .Throws<HtmlQualitySnapshotIncompleteException>();
        await Assert.That(Snapshot(output)).IsEquivalentTo(before);
    }

    private static string Document(string title, string canonical, string body) =>
        "<!doctype html><title>" + title + "</title><link rel=canonical href='" + canonical + "'>\n"
        + "<meta name=description content=D><meta property=og:title content=T><meta property=og:description content=D><meta property=og:url content='" + canonical + "'>\n"
        + body;

    private static string[] Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(path, file) + ":"
            + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();

    private sealed class FilesTemplate(string html, string css) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult([
                new SiteTemplateFile { RelativePath = "index.html", Content = html },
                new SiteTemplateFile { RelativePath = "styles/site.css", Content = css },
            ]));
    }
}
