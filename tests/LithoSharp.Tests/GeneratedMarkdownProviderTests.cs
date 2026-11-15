using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class GeneratedMarkdownProviderTests
{
    [Test]
    public async Task GeneratedProvider_SharesFactoryPageAndContext_Once()
    {
        var factories = 0;
        var providers = 0;
        SitePage<string>? factoryPage = null;
        SitePage<string>? markdownPage = null;
        SitePage<string>? htmlPage = null;
        ContentPageRenderingContext? markdownContext = null;
        ContentPageRenderingContext? htmlContext = null;
        var collection = Source().GeneratePagesWithMarkdown(new("generated"), static _ => ["group"], group =>
        {
            factories++;
            return factoryPage = new(new("group"), SiteRoute.ForFile("group.html"), group.Entries[0].Body, new("Title"));
        }, (page, context) =>
        {
            htmlPage = page;
            htmlContext = context;
            return "<p>HTML</p>";
        },
        (page, context) =>
        {
            providers++;
            markdownPage = page;
            markdownContext = context;
            return page.Content;
        },
        "configuration:v1", transformationId: new("generated:v1"), isCacheable: true);
        var page = Page(collection);
        var generator = new SiteGenerator();
        var configuration = Configuration(page);
        var result = page.GetPublicMarkdown(generator, configuration, "Production", AssetRegistry.Empty, default);
        page.GetPublicMarkdown(generator, configuration, "Production", AssetRegistry.Empty, default);
        page.Render(generator, configuration, "Production", AssetRegistry.Empty, default);
        await Assert.That(factories).IsEqualTo(1);
        await Assert.That(providers).IsEqualTo(1);
        await Assert.That(ReferenceEquals(factoryPage, markdownPage) && ReferenceEquals(factoryPage, htmlPage)).IsTrue();
        await Assert.That(ReferenceEquals(markdownContext, htmlContext)).IsTrue();
        await Assert.That(result.Markdown).IsEqualTo("Body");
        await Assert.That(page.CanCacheRendering).IsTrue();
    }

    [Test]
    public async Task GeneratedProvider_OffDoesNotInvoke_AndMissingFingerprintDisablesCache()
    {
        var calls = 0;
        var page = Page(Register((_, _) =>
        {
            calls++;
            return "Body";
        }));
        page.Render(new SiteGenerator(), Configuration(page), "Production", AssetRegistry.Empty, default);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(page.CanCacheRendering).IsFalse();
    }

    [Test]
    public async Task GeneratedProvider_NullEmptyFailureAndCancellationRetainSharedContract()
    {
        foreach (var mode in new[] { "null", "empty", "failure", "cancel" })
        {
            var page = Page(Register((_, _) => mode switch
            {
                "null" => null,
                "empty" => "",
                "failure" => throw new InvalidOperationException("private-secret"),
                _ => throw new OperationCanceledException(),
            }));
            ContentPageMarkdownResult? result = null;
            var cancelled = false;
            try
            {
                result = page.GetPublicMarkdown(new SiteGenerator(), Configuration(page), "Production", AssetRegistry.Empty, default);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            if (mode == "cancel")
                await Assert.That(cancelled).IsTrue();
            else
            {
                await Assert.That(result!.Status).IsEqualTo(mode == "null" ? ContentPageMarkdownStatus.Unsupported
                    : mode == "empty" ? ContentPageMarkdownStatus.Provided : ContentPageMarkdownStatus.Failed);
                await Assert.That(result.ToString()).DoesNotContain("private-secret");
            }
        }
    }

    [Test]
    public async Task GeneratedProvider_ImplementationIdentityChangesFingerprint()
    {
        var first = Page(Register(static (_, _) => "first", "configuration:v1"));
        var second = Page(Register(static (_, _) => "second", "configuration:v1"));
        await Assert.That(first.RendererFingerprint == second.RendererFingerprint).IsFalse();
    }

    private static SiteContentCollection Register(GeneratedPageMarkdownProvider<string> provider, string? fingerprint = null) =>
        Source().GeneratePagesWithMarkdown(new("generated"), static _ => ["group"],
            static _ => new SitePage<string>(new("group"), SiteRoute.ForFile("group.html"), "Body", new("Title")),
            static (_, _) => "<p>HTML</p>", provider, fingerprint,
            transformationId: new("generated:v1"), isCacheable: true);

    private static ContentCollection<string, string> Source() => new(new("source"), Path.GetTempPath(),
        [new ContentEntry<string, string>(new("entry"), "entry.md", "sha256:entry", "Title", "Body")],
        static _ => SiteRoute.ForFile("entry.html"), static _ => new PageMetadata("Title"),
        transformationId: new("source:v1"), isCacheable: true);

    internal static IntegratedContentPage Page(SiteContentCollection collection) =>
        collection.CreatePages("https://example.com/", DateTimeOffset.UnixEpoch, "Production", new SiteRouteTable(), [], default).Single();

    internal static SiteGenerator.RenderContext Configuration(IntegratedContentPage page) =>
        new(new SiteSettings(), new SiteText(), new SiteThemeOptions(), [], [page], "", false,
            DateTimeOffset.UnixEpoch, new SiteRouteCatalog("https://example.com/", [], []));
}
