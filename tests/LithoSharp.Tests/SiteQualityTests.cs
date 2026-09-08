using LithoSharp.Configuration;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class SiteQualityTests
{
    private const string BaseUrl = "https://example.test/sub/";

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
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "custom.css"), "body { background: url(missing.png); }");
        var css = new SiteAsset("css", workspace.Root, "custom.css", "custom.css");
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = BaseUrl }, [], Path.Combine(workspace.Root, "output"), true,
            new SiteCustomization { Template = new TextTemplate(Page("", "Home", "")) },
            new SiteGenerationOptions { Assets = [css], Quality = new() }, default))
            .Throws<SiteQualityValidationException>();
        await Assert.That(Directory.Exists(Path.Combine(workspace.Root, "output"))).IsFalse();
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
}
