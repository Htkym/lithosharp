using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class ContentPageMarkdownProviderTests
{
    [Test]
    public async Task NotRequested_OnlyRendersHtml_WithoutCallingProvider()
    {
        var calls = 0;
        var page = Page(Registration((entry, context) =>
        {
            calls++;
            return entry.Body;
        }));
        page.Render(new SiteGenerator(), Configuration(page), "Production", AssetRegistry.Empty, default);
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task NotRequested_RepeatedRenderAfterClear_UsesFreshContextAndDerivedContent()
    {
        var contexts = new List<ContentPageRenderingContext>();
        var providerCalls = 0;
        var page = Page(Registration((_, _) =>
        {
            providerCalls++;
            return "Markdown";
        }, (_, context) =>
        {
            contexts.Add(context);
            if (contexts.Count == 1)
                context.SetDerivedContent("First derived content");
            return $"<p>Render {contexts.Count}</p>";
        }));
        var generator = new SiteGenerator();
        var configuration = Configuration(page);
        page.Render(generator, configuration, "Production", AssetRegistry.Empty, default);
        await Assert.That(page.DerivedContent).IsEqualTo("First derived content");
        page.ClearRenderedContent();
        page.Render(generator, configuration, "Production", AssetRegistry.Empty, default);
        await Assert.That(page.DerivedContent).IsEqualTo("<p>Render 2</p>");
        page.Render(generator, configuration, "Production", AssetRegistry.Empty, default);
        await Assert.That(page.DerivedContent).IsEqualTo("<p>Render 3</p>");
        await Assert.That(ReferenceEquals(contexts[0], contexts[1])).IsFalse();
        await Assert.That(ReferenceEquals(contexts[1], contexts[2])).IsFalse();
        await Assert.That(providerCalls).IsEqualTo(0);
    }

    [Test]
    public async Task ProviderCancellationAndOutOfMemory_AreNeverSanitizedAsFailed()
    {
        foreach (var failure in new Exception[] { new OperationCanceledException(), new OutOfMemoryException() })
        {
            var page = Page(Registration((_, _) => throw failure));
            Exception? observed = null;
            try
            {
                page.GetPublicMarkdown(new SiteGenerator(), Configuration(page), "Production", AssetRegistry.Empty, default);
            }
            catch (Exception exception)
            {
                observed = exception;
            }
            await Assert.That(ReferenceEquals(failure, observed)).IsTrue();
        }
    }

    [Test]
    public async Task ProviderAndHtml_ShareEntryContextAndSingleCompilation()
    {
        var calls = 0;
        ContentEntry<string, string>? providedEntry = null;
        ContentEntry<string, string>? renderedEntry = null;
        ContentPageRenderingContext? providedContext = null;
        ContentPageRenderingContext? renderedContext = null;
        var registration = Registration((entry, context) =>
        {
            calls++;
            providedEntry = entry;
            providedContext = context;
            return entry.Body;
        }, (entry, context) =>
        {
            renderedEntry = entry;
            renderedContext = context;
            return context.RenderMarkdown(entry.Body);
        });
        var page = Page(registration);
        var generator = new SiteGenerator();
        var configuration = Configuration(page);
        var result = page.GetPublicMarkdown(generator, configuration, "Production", AssetRegistry.Empty, default);
        var compilation = page.CompilePublicMarkdown(result.Markdown!);
        var parses = generator.MarkdownCompiler.ParseCount;
        var second = page.GetPublicMarkdown(generator, configuration, "Production", AssetRegistry.Empty, default);
        page.Render(generator, configuration, "Production", AssetRegistry.Empty, default);
        await Assert.That(ReferenceEquals(result, second)).IsTrue();
        await Assert.That(ReferenceEquals(providedEntry, renderedEntry)).IsTrue();
        await Assert.That(ReferenceEquals(registration.Collection.Entries[0], providedEntry)).IsTrue();
        await Assert.That(ReferenceEquals(providedContext, renderedContext)).IsTrue();
        await Assert.That(ReferenceEquals(compilation.Semantics, page.CompilePublicMarkdown(result.Markdown!).Semantics)).IsTrue();
        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(parses);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(page.Content).Contains("Body");
    }

    [Test]
    public async Task NullIsUnsupported_EmptyIsExplicit_AndStringBodyIsNotImplicit()
    {
        var absent = Evaluate(Registration(null));
        var unsupported = Evaluate(Registration(static (_, _) => null));
        var empty = Evaluate(Registration(static (_, _) => ""));
        await Assert.That(absent.Status).IsEqualTo(ContentPageMarkdownStatus.Unsupported);
        await Assert.That(unsupported.Status).IsEqualTo(ContentPageMarkdownStatus.Unsupported);
        await Assert.That(empty.Status).IsEqualTo(ContentPageMarkdownStatus.Provided);
        await Assert.That(empty.Markdown).IsEqualTo("");
    }

    [Test]
    public async Task ProviderFailure_IsSanitizedCached_AndDoesNotPreventHtml()
    {
        var calls = 0;
        var page = Page(Registration((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("private-token-and-path");
        }));
        var generator = new SiteGenerator();
        var configuration = Configuration(page);
        var result = page.GetPublicMarkdown(generator, configuration, "Production", AssetRegistry.Empty, default);
        page.GetPublicMarkdown(generator, configuration, "Production", AssetRegistry.Empty, default);
        page.Render(generator, configuration, "Production", AssetRegistry.Empty, default);
        await Assert.That(result.Status).IsEqualTo(ContentPageMarkdownStatus.Failed);
        await Assert.That(result.ToString()).DoesNotContain("private-token-and-path");
        await Assert.That(result.Markdown).IsNull();
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(page.Content).IsEqualTo("<p>HTML</p>");
    }

    [Test]
    public async Task Fingerprint_TracksProviderIdentity_WithoutMakingUndeclaredConfigurationCacheable()
    {
        var first = Page(Registration(First));
        var second = Page(Registration(Second));
        var absent = Page(Registration(null));
        var undeclared = Page(Registration(First, fingerprint: null));
        await Assert.That(first.RendererFingerprint == second.RendererFingerprint).IsFalse();
        await Assert.That(first.RendererFingerprint == absent.RendererFingerprint).IsFalse();
        await Assert.That(first.RendererFingerprint!).Contains(typeof(ContentPageMarkdownProviderTests).Module.ModuleVersionId.ToString("N"));
        await Assert.That(first.CanCacheRendering).IsTrue();
        await Assert.That(undeclared.RendererFingerprint).IsNull();
        await Assert.That(undeclared.CanCacheRendering).IsFalse();
    }

    [Test]
    public async Task RequestedCancellation_IsNotConvertedToProviderFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var page = Page(Registration((_, _) =>
        {
            calls++;
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return "";
        }));
        var cancelled = false;
        try
        {
            page.GetPublicMarkdown(new SiteGenerator(), Configuration(page), "Production", AssetRegistry.Empty, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        await Assert.That(cancelled).IsTrue();
        await Assert.That(calls).IsEqualTo(1);
    }

    private static string First(ContentEntry<string, string> entry, ContentPageRenderingContext context) => entry.Body;
    private static string Second(ContentEntry<string, string> entry, ContentPageRenderingContext context) => entry.Body;

    private static SiteContentCollection<string, string> Registration(
        ContentPageMarkdownProvider<string, string>? provider,
        ContentPageRenderer<string, string>? renderer = null,
        string? fingerprint = "configuration:v1") =>
        new(new ContentCollection<string, string>(
            new ContentCollectionId("typed"), Path.GetTempPath(),
            [new ContentEntry<string, string>(new ContentEntryId("entry"), "entry.md", "sha256:entry", "Title", "## Body")],
            static _ => SiteRoute.ForFile("entry.html"), static _ => new PageMetadata("Title"),
            transformationId: new ContentTransformationId("typed:v1"), isCacheable: true),
            renderer ?? (static (_, _) => "<p>HTML</p>"))
        {
            MarkdownProvider = provider,
            RendererFingerprint = fingerprint,
        };

    private static IntegratedContentPage Page(SiteContentCollection registration) =>
        registration.CreatePages("https://example.com/", DateTimeOffset.UnixEpoch, "Production", new SiteRouteTable(), [], default).Single();

    private static ContentPageMarkdownResult Evaluate(SiteContentCollection registration)
    {
        var page = Page(registration);
        return page.GetPublicMarkdown(new SiteGenerator(), Configuration(page), "Production", AssetRegistry.Empty, default);
    }

    private static SiteGenerator.RenderContext Configuration(IntegratedContentPage page) =>
        new(new SiteSettings(), new SiteText(), new SiteThemeOptions(), [], [page], "", false,
            DateTimeOffset.UnixEpoch, new SiteRouteCatalog("https://example.com/", [], []));
}
