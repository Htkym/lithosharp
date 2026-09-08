using System.Text.Json;
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;

namespace LithoSharp.Tests;

public sealed class OutputRegressionTests
{
    private static SiteSettings TestSite() => new()
    {
        Title = "Test Site",
        Description = "A test site.",
        BaseUrl = "https://example.test/",
        Language = "en",
        TimeZone = "UTC"
    };

    private static async Task WritePostAsync(string contentDirectory, string fileName, string title, string date, string summary)
    {
        await File.WriteAllTextAsync(Path.Combine(contentDirectory, fileName), $"""
            ---
            title: "{title}"
            date: "{date}"
            summary: "{summary}"
            tags:
              - sample
            sources:
              - type: feed
                name: Example
            ---

            ## Section

            Body text for {title}.
            """);
    }

    private static async Task<(string Output, IReadOnlyList<MarkdownPost> Posts)> GenerateAsync(
        TemporaryWorkspace workspace,
        SiteCustomization? customization = null)
    {
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await WritePostAsync(content, "post.md", "First Post", "2026-01-02T03:04:05Z", "the first post");
        var output = Path.Combine(workspace.Root, "output");
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        customization = (customization ?? new SiteCustomization()) with { Template = new BlogSiteTemplate() };
        await new SiteGenerator().GenerateAsync(TestSite(), posts, output, clean: true, customization);
        return (output, posts);
    }

    [Test]
    public async Task RenderFeed_WritesRssChannelAndItems()
    {
        using var workspace = new TemporaryWorkspace();
        var (output, _) = await GenerateAsync(workspace);

        var feed = await File.ReadAllTextAsync(Path.Combine(output, "feed.xml"));
        await Assert.That(feed).Contains("<rss version=\"2.0\">");
        await Assert.That(feed).Contains("<title>Test Site</title>");
        await Assert.That(feed).Contains("<description>A test site.</description>");
        await Assert.That(feed).Contains("<title>First Post</title>");
        await Assert.That(feed).Contains("<link>https://example.test/posts/post.html</link>");
    }

    [Test]
    public async Task RenderSitemap_ListsCorePagesAndPosts()
    {
        using var workspace = new TemporaryWorkspace();
        var (output, _) = await GenerateAsync(workspace);

        var sitemap = await File.ReadAllTextAsync(Path.Combine(output, "sitemap.xml"));
        await Assert.That(sitemap).Contains("http://www.sitemaps.org/schemas/sitemap/0.9");
        await Assert.That(sitemap).Contains("<loc>https://example.test/index.html</loc>");
        await Assert.That(sitemap).Contains("<loc>https://example.test/archives.html</loc>");
        await Assert.That(sitemap).Contains("<loc>https://example.test/tags.html</loc>");
        await Assert.That(sitemap).Contains("<loc>https://example.test/search.html</loc>");
        await Assert.That(sitemap).Contains("<loc>https://example.test/posts/post.html</loc>");
        await Assert.That(sitemap).Contains("<lastmod>2026-01-02</lastmod>");
    }

    [Test]
    public async Task BuildSearchIndex_WritesDocumentsWithExpectedFields()
    {
        using var workspace = new TemporaryWorkspace();
        var (output, _) = await GenerateAsync(workspace);

        await using var stream = File.OpenRead(Path.Combine(output, "search-index.json"));
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        await Assert.That(root.GetProperty("site").GetString()).IsEqualTo("Test Site");
        await Assert.That(root.TryGetProperty("generated", out _)).IsTrue();

        var documents = root.GetProperty("documents");
        await Assert.That(documents.GetArrayLength()).IsEqualTo(1);
        var first = documents[0];
        await Assert.That(first.GetProperty("title").GetString()).IsEqualTo("First Post");
        await Assert.That(first.GetProperty("summary").GetString()).IsEqualTo("the first post");
        await Assert.That(first.GetProperty("url").GetString()).IsEqualTo("/posts/post.html");
        await Assert.That(first.GetProperty("tags")[0].GetString()).IsEqualTo("sample");
        await Assert.That(first.GetProperty("body").GetString()!).Contains("Body text for First Post.");
    }

    [Test]
    public async Task RenderPost_WritesSeoMetaTags()
    {
        using var workspace = new TemporaryWorkspace();
        var (output, _) = await GenerateAsync(workspace);

        var post = await File.ReadAllTextAsync(Path.Combine(output, "posts", "post.html"));
        await Assert.That(post).Contains("<link rel=\"canonical\" href=\"https://example.test/posts/post.html\">");
        await Assert.That(post).Contains("<meta property=\"og:site_name\" content=\"Test Site\">");
        await Assert.That(post).Contains("<meta property=\"og:title\" content=\"First Post - Test Site\">");
        await Assert.That(post).Contains("<meta property=\"og:description\" content=\"the first post\">");
        await Assert.That(post).Contains("<meta name=\"twitter:card\" content=\"summary\">");
        await Assert.That(post).DoesNotContain("property=\"og:image\"");
        await Assert.That(post).DoesNotContain("name=\"twitter:image\"");
        await Assert.That(post).Contains("<meta property=\"og:url\" content=\"https://example.test/posts/post.html\">");
    }

    [Test]
    public async Task BuildSearchScript_DefaultText_EmitsEnglishStatusMessages()
    {
        using var workspace = new TemporaryWorkspace();
        var (output, _) = await GenerateAsync(workspace);

        var script = await File.ReadAllTextAsync(Path.Combine(output, "assets", "search.js"));
        await Assert.That(script).Contains("No posts match '{query}'.");
        await Assert.That(script).Contains("Could not load the search index. Please try again later.");
        await Assert.That(script.Contains("__LITHOSHARP_SEARCH_MESSAGES__")).IsFalse();
    }

    [Test]
    public async Task BuildSearchScript_JapaneseText_EmitsLocalizedStatusMessages()
    {
        using var workspace = new TemporaryWorkspace();
        var customization = new SiteCustomization { Text = SiteText.Japanese };
        var (output, _) = await GenerateAsync(workspace, customization);

        var script = await File.ReadAllTextAsync(Path.Combine(output, "assets", "search.js"));
        await Assert.That(script).Contains("「{query}」に一致する記事は見つかりませんでした。");
        await Assert.That(script).Contains("検索インデックスを読み込めませんでした。時間をおいて再度お試しください。");
    }
}
