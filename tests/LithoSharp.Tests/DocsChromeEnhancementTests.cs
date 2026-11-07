using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Testing;

namespace LithoSharp.Tests;

public sealed class DocsChromeEnhancementTests
{
    [Test]
    public async Task FooterEscapesPlainTextAndValidatedLinksForBothShells()
    {
        var context = ComponentRenderingContext.Create(new SiteSettings());
        var footer = new SiteFooterOptions
        {
            Text = "<script>Notice & terms</script>",
            Links = [new NavigationLink("<License>", SiteUrl.FromAbsolute("https://example.test/license?a=1&b=2"))]
        };
        foreach (var docs in new[] { false, true })
        {
            var html = context.Render(new FooterComponent(), new FooterComponentProps("Site", docs) { AdditionalContent = footer }).ToHtmlString();
            using var document = SiteTestDocument.Parse(html);
            document.AssertText(".site-footer-additional p", footer.Text!);
            document.AssertText(".site-footer-additional a", "<License>");
            document.AssertLink("https://example.test/license?a=1&b=2");
            await Assert.That(document.Snapshot.QueryAll("script").Count).IsEqualTo(0);
            await Assert.That(html).Contains("?a=1&amp;b=2");
        }
    }

    [Test]
    public async Task HeaderSearchTextChangesAreScopedToEachGeneration()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var site = new SiteSettings { Title = "Guide", BaseUrl = "https://example.test/sub/", TimeZone = "UTC" };
        var post = new MarkdownPost("guide.md", "guide", new PostFrontMatter
        {
            Title = "Guide",
            Summary = "Summary",
            Date = timestamp.AddDays(-1)
        }, "## Guide\n\nBody", "posts/guide.html");
        var customization = new SiteCustomization
        {
            Template = new DocsSiteTemplate { EnableHeaderSearch = true },
            Text = SiteText.English with { HeaderSearchPlaceholder = "Original search" },
            FaviconSourceDirectory = Path.Combine(workspace.Root, "favicon")
        };
        var options = new SiteGenerationOptions { BuildTimestamp = timestamp };
        var generator = new SiteGenerator();
        var first = await generator.GenerateWithOptionsAsync(site, [post], output, true,
            customization, options, CancellationToken.None);
        using (var home = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "index.html"))))
            home.AssertAttribute("header input[name=q]", "placeholder", customization.Text.HeaderSearchPlaceholder);

        var changed = customization with
        {
            Text = customization.Text with { HeaderSearchPlaceholder = "Updated & localized search" }
        };
        var second = await generator.GenerateWithOptionsAsync(site, [post], output, false,
            changed, options with { PreviousBuildPlan = first.BuildPlan }, CancellationToken.None);
        using (var home = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "index.html"))))
            home.AssertAttribute("header input[name=q]", "placeholder", changed.Text.HeaderSearchPlaceholder);
        var headerInput = second.BuildPlan.Nodes.Single(node => node.Id.Value == "page:index")
            .Inputs.Single(input => input.Key == "text.docsHeader");
        await Assert.That(headerInput.Value).IsEqualTo(System.Text.Json.JsonSerializer.Serialize(changed.Text));

        var unchanged = await generator.GenerateWithOptionsAsync(site, [post], output, false,
            changed, options with { PreviousBuildPlan = second.BuildPlan }, CancellationToken.None);
        await Assert.That(unchanged.BuildReport.Invalidations).IsEmpty();
        await Assert.That(unchanged.BuildReport.CacheMissCount).IsEqualTo(0);
    }

    [Test]
    public async Task HeaderSearchUsesLocalizedBasePathWithoutDuplicateInputsAndInvalidatesChangedChrome()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var site = new SiteSettings { Title = "Guide", BaseUrl = "https://example.test/sub/", Language = "ja" };
        var post = new MarkdownPost("guide.md", "guide", new PostFrontMatter
        {
            Title = "Guide",
            Summary = "Original summary",
            Date = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        }, "## header-search-input\n\nBody", "posts/guide.html");
        var customization = new SiteCustomization
        {
            Text = SiteText.Japanese,
            Footer = new SiteFooterOptions { Text = "Original notice" },
            ExtraPages = [new SiteExtraPage { RelativePath = "about.htm", Title = "About", BodyHtml = "<h1>About</h1>" }],
            Template = new DocsSiteTemplate { EnableHeaderSearch = true, ShowTopicCards = true }
        };
        var generator = new SiteGenerator();
        await generator.GenerateAsync(site, [post], output, true, customization);
        foreach (var relative in new[] { "index.html", "posts/guide.html", "search.html" })
        {
            using var document = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, relative)));
            await Assert.That(document.Snapshot.QueryAll("input[name=q]").Count).IsEqualTo(1);
            if (relative == "search.html")
                await Assert.That(document.Snapshot.QueryAll("header form").Count).IsEqualTo(0);
            else
            {
                document.AssertAttribute("header form", "action", "/sub/search.html");
                document.AssertAttribute("header form", "method", "get");
                document.AssertAttribute("header input", "aria-label", SiteText.Japanese.SearchInputLabel);
                await Assert.That(document.Snapshot.Query("header input")?.GetAttribute("id")).IsNull();
            }
        }
        using (var article = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "posts/guide.html"))))
            await Assert.That(article.Snapshot.QueryAll("#header-search-input").Count).IsEqualTo(1);

        var changed = customization with
        {
            Footer = new SiteFooterOptions { Text = "Updated notice" },
            Template = new DocsSiteTemplate { EnableSearch = true, ShowTopicCards = true, ShowStartLink = false, CompactHome = true }
        };
        post = post with { FrontMatter = post.FrontMatter with { Summary = "Updated summary" } };
        await generator.GenerateAsync(site, [post], output, false, changed);
        using var home = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "index.html")));
        home.AssertText(".site-footer-additional p", "Updated notice");
        home.AssertText(".docs-topic-card p", "Updated summary");
        await Assert.That(home.Snapshot.QueryAll("header form").Count).IsEqualTo(0);
        await Assert.That(home.Snapshot.QueryAll(".docs-primary-link").Count).IsEqualTo(0);
        await Assert.That(home.Snapshot.QueryAll(".docs-hero-compact").Count).IsEqualTo(1);
        home.AssertText("[data-docs-menu-toggle]", "Menu");
        using var extra = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "about.htm")));
        extra.AssertText(".site-footer-additional p", "Updated notice");

        await generator.GenerateAsync(site, [post], output, false, changed with { Template = new DelegatingDocsTemplate() });
        using var delegated = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "index.html")));
        await Assert.That(delegated.Snapshot.QueryAll("header form").Count).IsEqualTo(1);
        await Assert.That(delegated.Snapshot.QueryAll(".docs-topic-card").Count).IsEqualTo(1);
    }

    private sealed class DelegatingDocsTemplate : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) =>
            new DocsSiteTemplate { EnableHeaderSearch = true, ShowTopicCards = true }.RenderAsync(context, cancellationToken);
    }
}
