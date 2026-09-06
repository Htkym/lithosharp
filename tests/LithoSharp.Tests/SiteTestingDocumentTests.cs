using LithoSharp.Pages;
using LithoSharp.Routing;
using LithoSharp.Testing;

namespace LithoSharp.Tests;

public sealed class SiteTestingDocumentTests
{
    [Test]
    public async Task AssertionsMatchEncodedTextMetaAttributesAndUrls()
    {
        using var document = SiteTestDocument.Parse("<html><head><meta name='description' content='line 1\nline 2'><meta property='og:title' content=\"A 'quoted' value\"></head><body><a href=\"/docs?a=1&amp;b=2\">link</a><img src='/image.png'><p>&lt;safe&gt;</p></body></html>");
        document.AssertElement("p");
        document.AssertText("p", "<safe>");
        document.AssertMeta("description", "line 1\nline 2");
        document.AssertMeta("og:title", "A 'quoted' value", property: true);
        document.AssertLink("/docs?a=1&b=2");
        document.AssertImage("/image.png");
        await Assert.That(document.Document.Title).IsEqualTo("");
    }

    [Test]
    public void RenderHelpersSupportComponentsAndLayouts()
    {
        var site = new LithoSharp.Configuration.SiteSettings { BaseUrl = "https://example.test/" };
        using var component = SiteTestDocument.RenderComponent(new TestComponent(), "component", ComponentRenderingContext.Create(site));
        component.AssertText("p", "component");
        var page = new SitePage<string>(new PageId("page"), SiteRoute.ForFile("page.html"), "layout", new PageMetadata("Page"));
        using var layout = SiteTestDocument.RenderLayout(new TestLayout(), page, PageRenderingContext.Create(site));
        layout.AssertText("main", "layout");
        using var empty = SiteTestDocument.Parse("");
        _ = empty.Document;
    }

    [Test]
    public async Task MismatchesAndInvalidInputsFailClearly()
    {
        using var document = SiteTestDocument.Parse("<p class='actual'>actual text</p>");
        await Assert.That(() => document.AssertElement("missing")).Throws<SiteTestException>();
        await Assert.That(() => document.AssertAttribute("p", "class", "x")).Throws<SiteTestException>();
        await Assert.That(() => document.AssertMeta("description", "x")).Throws<SiteTestException>();
        await Assert.That(() => SiteTestDocument.Parse(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => document.AssertElement(" ")).Throws<ArgumentException>();
        await Assert.That(() => document.AssertAttribute("p", null!, "x")).Throws<ArgumentNullException>();
        await Assert.That(() => document.AssertText("p", "x")).Throws<SiteTestException>();
        await Assert.That(() => SiteTestDocument.RenderComponent(new NullComponent(), "x", ComponentRenderingContext.Create(new LithoSharp.Configuration.SiteSettings()))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task DisposeRejectsFurtherAssertions()
    {
        var document = SiteTestDocument.Parse("<p>x</p>");
        document.Dispose();
        await Assert.That(() => document.AssertElement("p")).Throws<ObjectDisposedException>();
        await Assert.That(() => _ = document.Document).Throws<ObjectDisposedException>();
    }

    private sealed class TestComponent : ISiteComponent<string>
    {
        public IHtmlContent Render(string props, ComponentRenderingContext context) => Html.UnsafeRaw($"<p>{Html.Encode(props)}</p>");
    }

    private sealed class TestLayout : IPageLayout<string>
    {
        public IHtmlContent Render(SitePage<string> page, PageRenderingContext context) => Html.UnsafeRaw($"<main>{Html.Encode(page.Content)}</main>");
    }

    private sealed class NullComponent : ISiteComponent<string>
    {
        public IHtmlContent Render(string props, ComponentRenderingContext context) => null!;
    }
}
