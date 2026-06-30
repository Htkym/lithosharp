using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Validation;

namespace LithoSharp.Tests;

public sealed class SiteGeneratorTests
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

    private static async Task<IReadOnlyList<MarkdownPost>> ReadPostsAsync(string contentDirectory) =>
        await new MarkdownPostReader().ReadAllAsync(contentDirectory);

    [Test]
    public async Task GenerateAsync_WithoutBundledAssets_WritesCorePagesAndSkipsBinaries()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await WritePostAsync(content, "post.md", "First Post", "2026-01-02T03:04:05Z", "the first post");
        var output = Path.Combine(workspace.Root, "output");
        var posts = await ReadPostsAsync(content);

        var result = await new SiteGenerator().GenerateAsync(TestSite(), posts, output, clean: true);

        await Assert.That(result.PostCount).IsEqualTo(1);
        await Assert.That(File.Exists(Path.Combine(output, "index.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, "search-index.json"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, "feed.xml"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, "sitemap.xml"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, posts[0].RelativeOutputPath))).IsTrue();
        // No favicon source directory exists, so binary assets degrade gracefully.
        await Assert.That(File.Exists(Path.Combine(output, "site.webmanifest"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(output, "assets", "social", "og-default.png"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(output, "llms.txt"))).IsFalse();
    }

    [Test]
    public async Task GenerateAsync_WithLlmsTxtEnabled_WritesLlmsTxt()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await WritePostAsync(content, "post.md", "First Post", "2026-01-02T03:04:05Z", "the first post");
        var output = Path.Combine(workspace.Root, "output");
        var posts = await ReadPostsAsync(content);
        var customization = new SiteCustomization { GenerateLlmsTxt = true };

        await new SiteGenerator().GenerateAsync(TestSite(), posts, output, clean: true, customization);

        var llms = await File.ReadAllTextAsync(Path.Combine(output, "llms.txt"));
        await Assert.That(llms).StartsWith("# Test Site");
        await Assert.That(llms).Contains("> A test site.");
        await Assert.That(llms).Contains("- [First Post](https://example.test/posts/post.html): the first post");
    }

    [Test]
    public async Task GenerateAsync_WritesExtraPageWithNavLabel()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await WritePostAsync(content, "post.md", "First Post", "2026-01-02T03:04:05Z", "the first post");
        var output = Path.Combine(workspace.Root, "output");
        var posts = await ReadPostsAsync(content);
        var customization = new SiteCustomization
        {
            ExtraPages =
            [
                new SiteExtraPage
                {
                    RelativePath = "about.html",
                    Title = "About",
                    NavLabel = "About",
                    BodyHtml = "<section><h1>About</h1></section>"
                }
            ]
        };

        await new SiteGenerator().GenerateAsync(TestSite(), posts, output, clean: true, customization);

        var about = await File.ReadAllTextAsync(Path.Combine(output, "about.html"));
        await Assert.That(about).Contains("<section><h1>About</h1></section>");
        var index = await File.ReadAllTextAsync(Path.Combine(output, "index.html"));
        await Assert.That(index).Contains(">About</a>");
    }

    [Test]
    public async Task Validate_DefaultValidator_RejectsMissingSummary()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "post.md"), """
            ---
            title: "No Summary"
            date: "2026-01-02T03:04:05Z"
            tags:
              - sample
            ---

            ## Section

            Body.
            """);
        var posts = await ReadPostsAsync(content);

        await Assert.That(async () => SiteGenerator.Validate(TestSite(), content, posts))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("missing summary");
    }

    [Test]
    public async Task Validate_DefaultValidator_AcceptsValidPosts()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await WritePostAsync(content, "post.md", "First Post", "2026-01-02T03:04:05Z", "the first post");
        var posts = await ReadPostsAsync(content);

        SiteGenerator.Validate(TestSite(), content, posts);
    }

    [Test]
    public async Task Validate_CustomValidator_IsApplied()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await WritePostAsync(content, "post.md", "First Post", "2026-01-02T03:04:05Z", "the first post");
        var posts = await ReadPostsAsync(content);
        var customization = new SiteCustomization { Validators = [new RejectAllValidator()] };

        await Assert.That(() => SiteGenerator.Validate(TestSite(), content, posts, customization))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("rejected");
    }

    [Test]
    public async Task GenerateAsync_WithThemeCustomization_SubstitutesBrandAndAppendsAdditionalCss()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await WritePostAsync(content, "post.md", "First Post", "2026-01-02T03:04:05Z", "the first post");
        var output = Path.Combine(workspace.Root, "output");
        var posts = await ReadPostsAsync(content);
        var customization = new SiteCustomization
        {
            Theme = new SiteThemeOptions
            {
                BrandPrefix = "acme / ",
                AdditionalCss = ":root { --accent: #ff0066; }"
            }
        };

        await new SiteGenerator().GenerateAsync(TestSite(), posts, output, clean: true, customization);

        var css = await File.ReadAllTextAsync(Path.Combine(output, "assets", "site.css"));
        await Assert.That(css).Contains("content: \"acme / \";");
        await Assert.That(css).Contains(":root { --accent: #ff0066; }");
        await Assert.That(css.Contains("__LITHOSHARP_BRAND_PREFIX__")).IsFalse();
    }

    [Test]
    public async Task GenerateAsync_WithDefaultTheme_DoesNotAppendAdditionalCss()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await WritePostAsync(content, "post.md", "First Post", "2026-01-02T03:04:05Z", "the first post");
        var output = Path.Combine(workspace.Root, "output");
        var posts = await ReadPostsAsync(content);

        await new SiteGenerator().GenerateAsync(TestSite(), posts, output, clean: true);

        var css = await File.ReadAllTextAsync(Path.Combine(output, "assets", "site.css"));
        await Assert.That(css.TrimEnd()).EndsWith("}");
        await Assert.That(css.Contains("__LITHOSHARP_BRAND_PREFIX__")).IsFalse();
    }

    private sealed class RejectAllValidator : IContentValidator
    {
        public void Validate(MarkdownPost post, ContentValidationContext context) =>
            throw new InvalidOperationException($"Post '{post.FilePath}' rejected.");
    }
}
