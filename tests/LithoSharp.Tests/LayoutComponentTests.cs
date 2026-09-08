using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class LayoutComponentTests
{
    [Test]
    public async Task LayoutClassGeneratesTypedCollectionWithoutRendererDelegate()
    {
        using var workspace = new TemporaryWorkspace();
        var entry = new ContentEntry<string, string>(new ContentEntryId("one"), "one.txt", "fixed", "Title", "<script>unsafe</script>");
        var collection = new ContentCollection<string, string>(new ContentCollectionId("pages"), workspace.Root,
            [entry], _ => SiteRoute.ForFile("typed.html"), _ => new PageMetadata("Typed title"));
        var output = Path.Combine(workspace.Root, "output");
        await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/sub/" }, [], output, true, null,
            new SiteGenerationOptions
            {
                ContentCollections = [new SiteContentCollection<string, string>(collection, new TextLayout())]
            }, default);
        var html = await File.ReadAllTextAsync(Path.Combine(output, "typed.html"));
        await Assert.That(html).Contains("&lt;script&gt;unsafe&lt;/script&gt;");
        await Assert.That(html).Contains("https://example.test/sub/typed.html");
        await Assert.That(html).Contains("Typed title");
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings(), [], output, true, null,
            new SiteGenerationOptions
            {
                ContentCollections = [new SiteContentCollection<string, string>(collection, new NullLayout())]
            }, default)).Throws<InvalidOperationException>();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "typed.html"))).IsEqualTo(html);
    }

    [Test]
    public async Task LayoutAndComponentRenderWithoutOutputDirectory()
    {
        var context = PageRenderingContext.Create(new SiteSettings());
        var entry = new ContentEntry<string, string>(new ContentEntryId("one"), "one.txt", "fixed", "Title", "<b>text</b>");
        var page = new SitePage<ContentEntry<string, string>>(new PageId("one"), SiteRoute.ForFile("one.html"), entry, new PageMetadata("Title"));
        var html = new TextLayout().Render(page, context).ToHtmlString();
        await Assert.That(html).Contains("&lt;b&gt;text&lt;/b&gt;");
        await Assert.That(context.Render(new TextComponent(), "<b>").ToHtmlString()).IsEqualTo("&lt;b&gt;");
    }

    [Test]
    public async Task BreadcrumbsEscapeLabelsAndUseValidatedUrls()
    {
        var context = ComponentRenderingContext.Create(new SiteSettings());
        IReadOnlyList<(string Label, SiteUrl Url)> links = [("<Home>", SiteUrl.ForFile("index.html"))];
        var html = context.Render(new Breadcrumbs(), links).ToHtmlString();
        await Assert.That(html).Contains("aria-label=\"Breadcrumb\"");
        await Assert.That(html).Contains("&lt;Home&gt;");
        await Assert.That(html).Contains("href=\"/index.html\"");
        await Assert.That(() => context.Render(new NullComponent(), "text")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task BuiltInTocAndSearchKeepAccessibleMarkupAndEncodeInput()
    {
        var context = ComponentRenderingContext.Create(new SiteSettings());
        IReadOnlyList<SiteTemplateHeading> headings = [new(2, "chapter", "<Chapter>")];
        var toc = context.Render(new TableOfContentsComponent(), headings).ToHtmlString();
        await Assert.That(toc).Contains("class=\"post-toc\"");
        await Assert.That(toc).Contains("href=\"#chapter\"");
        await Assert.That(toc).Contains("&lt;Chapter&gt;");
        var search = context.Render(new SearchFormComponent(), SiteUrl.FromAbsolute("https://example.test/search?a=1&b=2")).ToHtmlString();
        await Assert.That(search).Contains("role=\"search\"");
        await Assert.That(search).Contains("name=\"q\" type=\"search\"");
        await Assert.That(search).Contains("action=\"https://example.test/search?a=1&amp;b=2\"");
        await Assert.That(search).Contains("<label");
    }

    [Test]
    public async Task NavigationPaginationAndFooterPreserveLabelsAndCurrentState()
    {
        var context = ComponentRenderingContext.Create(new SiteSettings());
        var link = new NavigationLink("<Next>", SiteUrl.ForFile("next.html"), "link", true);
        IReadOnlyList<NavigationLink> links = [link];
        var navigation = context.Render(new NavigationComponent(), links).ToHtmlString();
        await Assert.That(navigation).Contains("&lt;Next&gt;");
        await Assert.That(navigation).Contains("aria-current=\"page\"");
        var pagination = context.Render(new PreviousNextComponent(), new PreviousNextComponentProps(null, link)).ToHtmlString();
        await Assert.That(pagination).Contains("<span></span>");
        await Assert.That(pagination).Contains("rel=\"next\"");
        await Assert.That(context.Render(new PreviousNextComponent(), new PreviousNextComponentProps(null, null)).ToHtmlString())
            .IsEqualTo(string.Empty);
        await Assert.That(context.Render(new FooterComponent(), new FooterComponentProps("A&B", true)).ToHtmlString())
            .IsEqualTo("<footer class=\"docs-footer\"><p>Generated by A&amp;B.</p></footer>");
    }

    [Test]
    public async Task HeadersRenderBrandAndAccessibleControls()
    {
        var context = ComponentRenderingContext.Create(new SiteSettings());
        var home = SiteUrl.ForFile("index.html");
        var docs = context.Render(new DocsHeaderComponent(), new NavigationLink("<Brand>", home)).ToHtmlString();
        await Assert.That(docs).Contains("&lt;Brand&gt;");
        await Assert.That(docs).Contains("aria-controls=\"docs-sidebar\"");
        var blog = context.Render(new BlogHeaderComponent(),
            new BlogHeaderComponentProps("Brand", home, [], SiteUrl.ForFile("search.html"))).ToHtmlString();
        await Assert.That(blog).Contains("role=\"search\"");
        await Assert.That(blog).Contains("aria-controls=\"site-menu\"");
        var simple = context.Render(new BlogHeaderComponent(),
            new BlogHeaderComponentProps("Brand", home, [], IncludeNavigation: false)).ToHtmlString();
        await Assert.That(simple).DoesNotContain("site-menu-toggle");
    }

    [Test]
    public async Task BuiltInComponentsRejectNullRequiredNestedInputs()
    {
        var context = ComponentRenderingContext.Create(new SiteSettings());
        var url = SiteUrl.ForFile("index.html");
        var seo = new SeoComponentProps("Title", "Description", url, "website", url, "Preview");
        var root = new SiteTemplateNavigationNode { Label = "Root", Children = [] };
        Action[] invalidRenders =
        [
            () => new TableOfContentsComponent().Render([null!], context),
            () => new NavigationComponent().Render([new NavigationLink("Home", null!)], context),
            () => new PreviousNextComponent().Render(
                new PreviousNextComponentProps(new NavigationLink("Previous", null!), null), context),
            () => new FooterComponent().Render(new FooterComponentProps(null!), context),
            () => new BlogHeaderComponent().Render(
                new BlogHeaderComponentProps("Brand", url, [new NavigationLink("Home", null!)]), context),
            () => new DocsHeaderComponent().Render(new NavigationLink("Docs", null!), context),
            () => new SeoComponent().Render(seo with { CanonicalUrl = null! }, context),
            () => new HeadComponent().Render(
                new HeadComponentProps(seo, null!, new HtmlText(""), new HtmlText("")), context),
            () => new DocsNavigationComponent().Render(
                new DocsNavigationComponentProps(root, [new NavigationLink("More", null!)]), context),
        ];

        foreach (var render in invalidRenders)
            await Assert.That(render).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task LegacyTemplateAdapterPreservesBuildContext()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "asset.txt"), "asset");
        var asset = new SiteAsset("test", workspace.Root, "asset.txt", "assets/test.txt");
        var output = Path.Combine(workspace.Root, "output");
        await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { Title = "Adapter", BaseUrl = "https://example.test/sub/" }, [], output, true,
            new SiteCustomization { Template = new AdapterTemplate(asset) },
            new SiteGenerationOptions
            {
                Assets = [asset], EnvironmentName = "Preview", BuildTimestamp = DateTimeOffset.UnixEpoch
            }, default);
        var html = await File.ReadAllTextAsync(Path.Combine(output, "adapter.html"));
        await Assert.That(html).Contains("Preview|0|/sub/assets/test.");
        await Assert.That(html).Contains("Adapter");
    }

    [Test]
    public async Task DocsNavigationPreservesOrderAndMarksAdditionalCurrentLink()
    {
        var context = ComponentRenderingContext.Create(new SiteSettings());
        var root = new SiteTemplateNavigationNode
        {
            Label = "Root", Children = [new SiteTemplateNavigationNode { Label = "<Folder>", Children = [] }]
        };
        var current = SiteUrl.ForFile("extra.html");
        var html = context.Render(new DocsNavigationComponent(),
            new DocsNavigationComponentProps(root, [new NavigationLink("Extra", current)], current)).ToHtmlString();
        await Assert.That(html).Contains("data-docs-sidebar");
        await Assert.That(html).Contains("<span>&lt;Folder&gt;</span>");
        await Assert.That(html).Contains("href=\"/extra.html\" aria-current=\"page\">Extra");
        await Assert.That(html.IndexOf("&lt;Folder&gt;", StringComparison.Ordinal) < html.IndexOf(">Extra", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task HeadIncludesSiteMetadataAndEncodesText()
    {
        var context = ComponentRenderingContext.Create(new SiteSettings { Title = "Site & name" });
        var seo = new SeoComponentProps("<Title>", "Description & more",
            SiteUrl.FromAbsolute("https://example.test/page.html"), "article",
            SiteUrl.FromAbsolute("https://example.test/social.png"), "Preview", DateTimeOffset.UnixEpoch);
        var html = context.Render(new HeadComponent(), new HeadComponentProps(seo,
            SiteUrl.ForFile("site.css"), Html.UnsafeRaw(string.Empty), Html.UnsafeRaw(string.Empty))).ToHtmlString();
        await Assert.That(html).Contains("<title>&lt;Title&gt;</title>");
        await Assert.That(html).Contains("property=\"og:site_name\" content=\"Site &amp; name\"");
        await Assert.That(html).Contains("name=\"description\" content=\"Description &amp; more\"");
        await Assert.That(html).Contains("article:published_time");
        await Assert.That(html).Contains("name=\"theme-color\"");
        await Assert.That(html).DoesNotContain("application/rss+xml");
        var docsHead = context.Render(new HeadComponent(), new HeadComponentProps(seo,
            SiteUrl.ForFile("site.css"), Html.UnsafeRaw(string.Empty), Html.UnsafeRaw(string.Empty),
            FeedUrl: SiteUrl.ForFile("feed.xml"), Docs: true)).ToHtmlString();
        await Assert.That(docsHead).Contains("application/rss+xml");
    }

    [Test]
    public async Task BuiltInLayoutsComposeRegionsAndResolveSubpathUrls()
    {
        var context = PageRenderingContext.Create(new SiteSettings { BaseUrl = "https://example.test/sub/" });
        var page = new SitePage<PageLayoutContent>(new PageId("standalone"), SiteRoute.ForFile("one.html"),
            new PageLayoutContent(new HtmlText("<Body>"))
            {
                Sidebar = Html.UnsafeRaw("<aside>Sidebar</aside>"),
                TableOfContents = Html.UnsafeRaw("<nav>Contents</nav>"),
                SocialImageUrl = SiteUrl.ForFile("assets/preview.png", "https://example.test/sub/"),
                IncludeBlogNavigation = false
            }, new PageMetadata("Standalone"));
        var docs = new DocsPageLayout().Render(page, context).ToHtmlString();
        await Assert.That(docs).Contains("<aside>Sidebar</aside>");
        await Assert.That(docs).Contains("<nav>Contents</nav>");
        await Assert.That(docs).Contains("&lt;Body&gt;");
        await Assert.That(docs).Contains("https://example.test/sub/one.html");
        await Assert.That(docs).Contains("https://example.test/sub/assets/preview.png");
        var blog = new BlogPageLayout().Render(page, context).ToHtmlString();
        await Assert.That(blog).Contains("&lt;Body&gt;");
        await Assert.That(blog).DoesNotContain("site-menu-toggle");
        await Assert.That(blog).DoesNotContain("application/rss+xml");
    }

    private sealed class AdapterTemplate(SiteAsset asset) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default)
        {
            var page = new SitePage<string>(new PageId("adapter"), SiteRoute.ForFile("adapter.html"), "Body", new PageMetadata("Adapter"));
            return Task.FromResult(new SiteTemplateResult([
                new SiteTemplateFile { RelativePath = "adapter.html", Content = context.RenderLayout(page, new AdapterLayout(asset)).ToHtmlString() }
            ]));
        }
    }

    private sealed class AdapterLayout(SiteAsset asset) : IPageLayout<string>
    {
        public IHtmlContent Render(SitePage<string> page, PageRenderingContext context) => context.RenderDocument(page,
            new HtmlText($"{context.EnvironmentName}|{context.BuildTimestamp.ToUnixTimeSeconds()}|{context.Assets.GetUrl(asset).Value}"));
    }

    private sealed class TextLayout : IPageLayout<ContentEntry<string, string>>
    {
        public IHtmlContent Render(SitePage<ContentEntry<string, string>> page, PageRenderingContext context) =>
            context.RenderDocument(page, new HtmlText(page.Content.Body));
    }

    private sealed class TextComponent : ISiteComponent<string>
    {
        public IHtmlContent Render(string props, ComponentRenderingContext context) => new HtmlText(props);
    }

    private sealed class NullComponent : ISiteComponent<string>
    {
        public IHtmlContent Render(string props, ComponentRenderingContext context) => null!;
    }

    private sealed class NullLayout : IPageLayout<ContentEntry<string, string>>
    {
        public IHtmlContent Render(SitePage<ContentEntry<string, string>> page, PageRenderingContext context) => null!;
    }
}
