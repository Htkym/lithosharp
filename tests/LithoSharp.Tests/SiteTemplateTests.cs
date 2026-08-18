using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;

namespace LithoSharp.Tests;

public sealed class SiteTemplateTests
{
    private static SiteSettings TestSite() => new()
    {
        Title = "Documentation",
        Description = "Test documentation.",
        BaseUrl = "https://example.test/",
        Language = "en",
        TimeZone = "UTC"
    };

    [Test]
    public async Task GenerateAsync_DefaultTemplate_RendersDocsNavigationTocAndPagination()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        await WriteDocumentAsync(content, "intro.md", "Introduction", "2026-01-01T00:00:00Z", 1, "Getting Started", """
            ## Welcome

            Start here.
            """);
        await WriteDocumentAsync(content, Path.Combine("guides", "install.md"), "Install", "2026-01-02T00:00:00Z", 2, null, """
            ## Install steps

            ### Details

            Install it.
            """);
        await WriteDocumentAsync(content, Path.Combine("guides", "advanced.md"), "Advanced", "2026-01-03T00:00:00Z", null, null, """
            ## Advanced use

            Details.
            """);
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        var output = Path.Combine(workspace.Root, "output");

        await new SiteGenerator().GenerateAsync(TestSite(), posts, output, clean: true);

        var install = await File.ReadAllTextAsync(Path.Combine(output, "posts", "guides", "install.html"));
        await Assert.That(install).Contains("class=\"docs-shell\"");
        await Assert.That(install).Contains("<span>guides</span>");
        await Assert.That(install).Contains("class=\"docs-nav-folder is-ancestor\"");
        await Assert.That(install).Contains("class=\"docs-nav-link is-current\"");
        await Assert.That(install).Contains("aria-current=\"page\"");
        await Assert.That(install).Contains("href=\"#install-steps\"");
        await Assert.That(install).Contains("href=\"#details\"");
        await Assert.That(install).Contains("rel=\"prev\"");
        await Assert.That(install).Contains("rel=\"next\"");

        var introAt = install.IndexOf(">Getting Started</a>", StringComparison.Ordinal);
        var guidesAt = install.IndexOf("<span>guides</span>", StringComparison.Ordinal);
        var installAt = install.IndexOf(">Install</a>", StringComparison.Ordinal);
        var advancedAt = install.IndexOf(">Advanced</a>", StringComparison.Ordinal);
        await Assert.That(introAt).IsGreaterThanOrEqualTo(0);
        await Assert.That(guidesAt).IsGreaterThan(introAt);
        await Assert.That(installAt).IsGreaterThan(guidesAt);
        await Assert.That(advancedAt).IsGreaterThan(installAt);
    }

    [Test]
    public async Task GenerateAsync_DefaultTemplate_UsesDocsShellForEveryHtmlPage()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        await WriteDocumentAsync(content, "guide.md", "Guide", "2026-01-01T00:00:00Z", null, null, "Body");
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        var output = Path.Combine(workspace.Root, "output");
        var customization = new SiteCustomization
        {
            ExtraPages =
            [
                new SiteExtraPage
                {
                    RelativePath = "about.html",
                    Title = "About",
                    NavLabel = "About",
                    BodyHtml = "<h1>About</h1>"
                }
            ]
        };

        await new SiteGenerator().GenerateAsync(TestSite(), posts, output, clean: true, customization);

        foreach (var path in Directory.EnumerateFiles(output, "*.html", SearchOption.AllDirectories))
        {
            var html = await File.ReadAllTextAsync(path);
            await Assert.That(html).Contains("class=\"docs-shell\"");
        }

        await Assert.That(File.Exists(Path.Combine(output, "archives.html"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(output, "feed.xml"))).IsFalse();
        var about = await File.ReadAllTextAsync(Path.Combine(output, "about.html"));
        await Assert.That(about).Contains(">About</a>");
        await Assert.That(about).Contains("class=\"docs-nav-link is-current\"");
    }

    [Test]
    public async Task GenerateAsync_DefaultTemplate_RendersDocumentAndChildrenWithTheSamePath()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        await WriteDocumentAsync(content, "guides.md", "Guides", "2026-01-01T00:00:00Z", 1, null, "Guides");
        await WriteDocumentAsync(content, Path.Combine("guides", "install.md"), "Install", "2026-01-02T00:00:00Z", 1, null, "Install");
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        var output = Path.Combine(workspace.Root, "output");

        await new SiteGenerator().GenerateAsync(TestSite(), posts, output, clean: true);

        var guides = await File.ReadAllTextAsync(Path.Combine(output, "posts", "guides.html"));
        await Assert.That(guides).Contains(">Guides</a>");
        await Assert.That(guides).Contains(">Install</a>");
    }

    [Test]
    public async Task GenerateAsync_BlogTemplate_PreservesBlogPagesAndFeed()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        await WriteDocumentAsync(content, "post.md", "A post", "2026-01-01T00:00:00Z", null, null, "## Section");
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        var output = Path.Combine(workspace.Root, "output");
        var customization = new SiteCustomization { Template = new BlogSiteTemplate() };

        await new SiteGenerator().GenerateAsync(TestSite(), posts, output, clean: true, customization);

        await Assert.That(File.Exists(Path.Combine(output, "archives.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, "tags.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, "search.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, "feed.xml"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, "sitemap.xml"))).IsTrue();
        var post = await File.ReadAllTextAsync(Path.Combine(output, "posts", "post.html"));
        await Assert.That(post).Contains("<link rel=\"canonical\" href=\"https://example.test/posts/post.html\">");
        await Assert.That(post).Contains("class=\"post-layout\"");
    }

    [Test]
    public async Task GenerateAsync_CustomTemplate_WritesItsFiles()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        await WriteDocumentAsync(content, "custom.md", "Custom page", "2026-01-01T00:00:00Z", 1, null, "## Custom heading");
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        var output = Path.Combine(workspace.Root, "output");
        var customization = new SiteCustomization { Template = new CustomTemplate() };

        await new SiteGenerator().GenerateAsync(TestSite(), posts, output, clean: true, customization);

        var html = await File.ReadAllTextAsync(Path.Combine(output, "custom.html"));
        await Assert.That(html).Contains("<header class=\"site-header\">");
        await Assert.That(html).Contains("Custom heading");
        await Assert.That(html).Contains("href=\"#custom-heading\"");
        await Assert.That(html).Contains("href=\"/posts/custom.html\"");
        await Assert.That(File.Exists(Path.Combine(output, "assets", "site.css"))).IsTrue();
        await Assert.That(html.Contains("archives.html")).IsFalse();
        await Assert.That(html.Contains("tags.html")).IsFalse();
        await Assert.That(html.Contains("search.html")).IsFalse();
        await Assert.That(html.Contains("feed.xml")).IsFalse();
    }

    [Test]
    public async Task GenerateAsync_RejectsTemplateFileThatCollidesWithCommonArtifact()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var customization = new SiteCustomization
        {
            Template = new LlmsCollisionTemplate(),
            GenerateLlmsTxt = true
        };

        await Assert.That(async () =>
                await new SiteGenerator().GenerateAsync(TestSite(), [], output, clean: true, customization))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("conflicts with a common artifact");
    }

    private static async Task WriteDocumentAsync(
        string contentRoot,
        string relativePath,
        string title,
        string date,
        int? sidebarPosition,
        string? sidebarLabel,
        string body)
    {
        var path = Path.Combine(contentRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sidebarPositionYaml = sidebarPosition is null ? string.Empty : $"sidebar_position: {sidebarPosition}\n";
        var sidebarLabelYaml = sidebarLabel is null ? string.Empty : $"sidebar_label: \"{sidebarLabel}\"\n";
        await File.WriteAllTextAsync(path, $"""
            ---
            title: "{title}"
            date: "{date}"
            summary: "{title} summary"
            {sidebarPositionYaml}{sidebarLabelYaml}---

            {body}
            """);
    }

    private sealed class CustomTemplate : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(
            SiteTemplateContext context,
            CancellationToken cancellationToken = default)
        {
            var page = context.Pages.Single();
            var navigation = context.Navigation.Children.Single();
            var body = $"""
                <nav><a href="{navigation.Page!.Url}">{Html.Encode(navigation.Label)}</a></nav>
                {page.ContentHtml}
                {context.RenderTableOfContents(page.Headings)}
                """;
            return Task.FromResult(new SiteTemplateResult(
            [
                new SiteTemplateFile
                {
                    RelativePath = "custom.html",
                    Content = context.RenderDocument(new SiteTemplateDocument
                    {
                        Title = page.Post.FrontMatter.Title,
                        RelativePath = "custom.html",
                        BodyHtml = body,
                        Description = page.Post.FrontMatter.Summary
                    })
                },
                new SiteTemplateFile
                {
                    RelativePath = "assets/site.css",
                    Content = "body { color: rebeccapurple; }"
                }
            ]));
        }
    }

    private sealed class LlmsCollisionTemplate : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(
            SiteTemplateContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult(
            [
                new SiteTemplateFile
                {
                    RelativePath = "llms.txt",
                    Content = "collision"
                }
            ]));
    }
}
