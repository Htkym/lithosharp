using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;
using LithoSharp.Testing;

namespace LithoSharp.Tests;

[NotInParallel]
public sealed class SiteTestHostTests
{
    [Test]
    public async Task HostIsolatesOutputCachesAndDisposesWithoutChangingSources()
    {
        using var source = new TemporaryWorkspace();
        var sentinel = Path.Combine(source.Root, "mine.txt");
        await File.WriteAllTextAsync(sentinel, "untouched");
        var definition = Definition() with
        {
            OutputDirectory = source.Root,
            Options = new SiteGenerationOptions
            {
                BuildCacheDirectory = source.Root, AssetCacheDirectory = source.Root,
                Quality = new(externalLinks: new(Path.Combine(source.Root, "external.json")))
            }
        };
        var host = await SiteTestHost.CreateAsync(definition);
        var root = Path.GetDirectoryName(host.OutputDirectory)!;
        try
        {
            host.AssertSucceeded();
            host.AssertNoDiagnostics();
            host.AssertRoute("/sub/index.html", "index.html");
            host.AssertNoRoute("/sub/");
            host.AssertArtifact("index.html");
            host.AssertNoArtifact("absent.html");
            using var page = await host.OpenPageAsync("/sub/index.html");
            page.AssertText("h1", "Home");
            page.AssertMeta("description", "Description");
            page.AssertMeta("og:title", "Home", property: true);
            await Assert.That(page.Route!.PublicPath).IsEqualTo("/sub/index.html");
            await Assert.That(host.Result!.BuildReport.BuildTimestamp).IsEqualTo(DateTimeOffset.UnixEpoch);
            await Assert.That(Directory.GetFiles(source.Root).Length).IsEqualTo(1);
            await Assert.That(() => host.AssertArtifact("not-declared.txt")).Throws<SiteTestException>();
            await File.WriteAllTextAsync(Path.Combine(host.OutputDirectory, "not-declared.txt"), "extra");
            await Assert.That(() => host.AssertArtifact("not-declared.txt")).Throws<SiteTestException>();
            await Assert.That(() => host.AssertNoArtifact("not-declared.txt")).Throws<SiteTestException>();
            foreach (var path in new[] { "../mine.txt", "/mine.txt", "a/../../mine.txt", "C:/mine.txt" })
                await Assert.That(() => host.AssertArtifact(path)).Throws<ArgumentException>();
        }
        finally { await host.DisposeAsync(); }
        await host.DisposeAsync();
        await Assert.That(Directory.Exists(root)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("untouched");
        await Assert.That(() => host.AssertSucceeded()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task FailedValidationRemainsInspectableAndRendererExceptionsPropagate()
    {
        await using var failed = await SiteTestHost.CreateAsync(Definition("<a href='missing.html'>Missing</a>"));
        await Assert.That(failed.Succeeded).IsFalse();
        await Assert.That(failed.Result).IsNull();
        failed.AssertDiagnostic("LSQ001", SiteDiagnosticSeverity.Error, "missing.html");
        await Assert.That(() => failed.AssertSucceeded()).Throws<SiteTestException>();
        await Assert.That(() => failed.AssertNoDiagnostics()).Throws<SiteTestException>();
        await Assert.That(() => failed.AssertDiagnostic("missing")).Throws<SiteTestException>();
        await Assert.That(() => failed.AssertDiagnostic("LSQ001", (SiteDiagnosticSeverity)99)).Throws<ArgumentOutOfRangeException>();
        var before = Directory.GetDirectories(Path.GetTempPath(), "lithosharp-test-*").Order().ToArray();
        var throwing = Definition() with { Customization = new() { Template = new ThrowingTemplate() } };
        await Assert.That(async () => await SiteTestHost.CreateAsync(throwing)).Throws<NotSupportedException>();
        using var cancellation = new CancellationTokenSource();
        var cancelled = Definition() with { Customization = new() { Template = new CancellingTemplate(cancellation) } };
        await Assert.That(async () => await SiteTestHost.CreateAsync(cancelled, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(Directory.GetDirectories(Path.GetTempPath(), "lithosharp-test-*").Order().ToArray()).IsEquivalentTo(before);
        await Assert.That(async () => await SiteTestHost.CreateAsync(Definition(), new CancellationToken(true))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task TypedCollectionsPreserveDirectoryAndFileRouteConventions()
    {
        using var source = new TemporaryWorkspace();
        var collection = new ContentCollection<string, string>(new ContentCollectionId("typed"), source.Root,
            [new(new ContentEntryId("one"), "one.txt", "v1", "One", "one"), new(new ContentEntryId("two"), "two.txt", "v1", "Two", "two")],
            entry => entry.Id.Value == "one" ? SiteRoute.ForDirectoryIndex("one") : SiteRoute.ForFile("two/index.html"),
            entry => new PageMetadata(entry.FrontMatter, "Description"));
        var definition = Definition() with
        {
            Options = new SiteGenerationOptions
            {
                ContentCollections = [new SiteContentCollection<string, string>(collection, new CollectionLayout())],
                Quality = new(checkOrphans: false)
            }
        };
        await using var host = await SiteTestHost.CreateAsync(definition);
        host.AssertSucceeded();
        host.AssertRoute("/sub/one/", "one/index.html");
        host.AssertRoute("/sub/two/index.html", "two/index.html");
        host.AssertNoRoute("/sub/two/");
        using var page = await host.OpenPageAsync("/sub/one/");
        page.AssertText("main", "one");
    }

    [Test]
    public async Task ReplacedOutputLinkIsRejectedAndOutsideFilesSurvive()
    {
        using var outside = new TemporaryWorkspace();
        var sentinel = Path.Combine(outside.Root, "index.html");
        await File.WriteAllTextAsync(sentinel, "outside");
        var host = await SiteTestHost.CreateAsync(Definition());
        var directory = Path.Combine(host.OutputDirectory, "linked");
        // Directory links require no privilege on Unix; Windows junctions work without Developer Mode.
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
            start.ArgumentList.Add(directory); start.ArgumentList.Add(outside.Root);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            await Assert.That(process.ExitCode).IsEqualTo(0);
        }
        else Directory.CreateSymbolicLink(directory, outside.Root);
        try
        {
            await Assert.That(() => host.AssertArtifact("linked/index.html")).Throws<InvalidOperationException>();
            await Assert.That(async () => await host.DisposeAsync()).Throws<InvalidOperationException>();
            await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("outside");
        }
        finally { Directory.Delete(directory); await host.DisposeAsync(); }
    }

    [Test]
    public async Task DocsAndBlogFactoriesUsePublicTestingApi()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "LithoSharp.slnx"))) repository = repository.Parent;
        if (repository is null) throw new InvalidOperationException("Repository root not found.");
        foreach (var (factory, folder) in new (ISiteFactory, string)[]
        {
            (new DocsSampleFactory { AssetDemo = true }, "LithoSharp.DocsSample"),
            (new BlogSampleFactory(), "LithoSharp.Sample")
        })
        {
            await using var host = await SiteTestHost.CreateAsync(factory,
                new SiteFactoryContext(Path.Combine(repository.FullName, "samples", folder)));
            host.AssertSucceeded();
            host.AssertNoDiagnostics();
            host.AssertRoute("/index.html", "index.html");
            using var home = await host.OpenPageAsync("/index.html");
            home.AssertElement("main");
            home.AssertMeta("og:url", "https://example.com/index.html", property: true);
            if (factory is DocsSampleFactory)
            {
                var image = host.Result!.Routes.Single(route => route.RelativeOutputPath.StartsWith("images/sample.", StringComparison.Ordinal)
                    && route.RelativeOutputPath.EndsWith(".webp", StringComparison.Ordinal));
                host.AssertArtifact(image.RelativeOutputPath);
                var article = host.Result!.Routes.First(route => route.RelativeOutputPath.StartsWith("articles/", StringComparison.Ordinal));
                using var document = await host.OpenPageAsync(article.PublicPath);
                document.AssertElement("picture img[loading='lazy']");
                document.AssertAttribute("source[type='image/webp']", "srcset", image.PublicPath + " 128w");
            }
        }
    }

    private static SiteDefinition Definition(string body = "") => new(new SiteSettings { BaseUrl = "https://example.test/sub/" }, [])
    {
        Customization = new() { Template = new TextTemplate(Page("/sub/", "Home", "<h1>Home</h1>" + body)) }
    };
    private static string Page(string path, string title, string body) => $"<html><head><title>{title}</title><meta name='description' content='Description'><meta property='og:title' content='{title}'><meta property='og:description' content='Description'><meta property='og:url' content='https://example.test{path}'><link rel='canonical' href='https://example.test{path}'></head><body>{body}</body></html>";
    private sealed class TextTemplate(string html) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult([new SiteTemplateFile { RelativePath = "index.html", Content = html }]));
    }
    private sealed class ThrowingTemplate : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) => throw new NotSupportedException("test renderer failure");
    }
    private sealed class CancellingTemplate(CancellationTokenSource cancellation) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not passed to the renderer.");
        }
    }
    private sealed class CollectionLayout : IPageLayout<ContentEntry<string, string>>
    {
        public IHtmlContent Render(SitePage<ContentEntry<string, string>> page, PageRenderingContext context) =>
            Html.UnsafeRaw(Page(page.Route.PublicPath, page.Metadata.Title!, "<main>" + Html.Encode(page.Content.Body) + "</main>"));
    }
}
