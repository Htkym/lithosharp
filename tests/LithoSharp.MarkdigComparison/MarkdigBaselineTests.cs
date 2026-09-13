using System.Text.Json;
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;

namespace LithoSharp.Tests;

/// <summary>
/// C00 baseline: pins the current Markdig rendering contract before the
/// Content Compiler migration. Same input must be re-measurable.
/// This suite does not optimize; it only fixes behavior.
/// </summary>
public sealed class MarkdigBaselineTests
{
    private static string FixturePath(string fileName) =>
        Path.Combine(RepoRoot(), "tests", "LithoSharp.Tests", "Fixtures", "MarkdigBaseline", fileName);

    private static string RepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
            {
                return path.FullName;
            }
        }

        throw new DirectoryNotFoundException("The baseline fixtures require the repository root.");
    }

    private static async Task<string> ReadFixtureAsync(string fileName) =>
        await File.ReadAllTextAsync(FixturePath(fileName));

    private static SiteSettings TestSite() => new()
    {
        Title = "Baseline Site",
        Description = "Markdig baseline.",
        BaseUrl = "https://example.test/",
        Language = "en",
        TimeZone = "UTC",
    };

    private static async Task<string> GeneratePostHtmlAsync(string markdownBody)
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "post.md"), """
            ---
            title: "Baseline Post"
            date: "2026-01-02T03:04:05Z"
            summary: "baseline summary"
            tags:
              - baseline
            ---

            """ + markdownBody);
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        var output = Path.Combine(workspace.Root, "output");
        await new SiteGenerator(new ReferenceMarkdigCompiler()).GenerateAsync(
            TestSite(), posts, output, clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() });
        return await File.ReadAllTextAsync(Path.Combine(output, "posts", "post.html"));
    }

    [Test]
    public async Task RenderMarkdown_PreservesCurrentPipelineContract()
    {
        var generator = new SiteGenerator(new ReferenceMarkdigCompiler());
        var body = await ReadFixtureAsync("body-basics.md");
        var html = generator.RenderMarkdown(body);

        // Body h1 is rendered as h1 here; demotion to h2 happens in post-processing.
        await Assert.That(html).Contains("<h1");
        // Auto-identifiers assign ids to headings.
        await Assert.That(html).Contains("id=\"");
        // Fenced code with language is preserved.
        await Assert.That(html).Contains("language-csharp");
        // Images render with src.
        await Assert.That(html).Contains("<img");
        await Assert.That(html).Contains("assets/image.png");
        // Inline code renders.
        await Assert.That(html).Contains("<code>");
    }

    [Test]
    public async Task BlogPost_DemotesBodyH1ToH2()
    {
        var html = await GeneratePostHtmlAsync(await ReadFixtureAsync("body-basics.md"));

        // The page title h1 remains, but the Markdown body h1 must be demoted.
        await Assert.That(html).Contains("<h1 class=\"post-title\">Baseline Post</h1>");
        await Assert.That(html).DoesNotContain("<h1 id=\"body-title-becomes-h2\">");
        await Assert.That(html).Contains("<h2");
        await Assert.That(html).Contains("Body Title Becomes H2");
    }

    [Test]
    public async Task BlogPost_DuplicateHeadingsGetUniqueIds()
    {
        var html = await GeneratePostHtmlAsync(await ReadFixtureAsync("body-basics.md"));
        var headings = SiteGenerator.ExtractTemplateHeadings(html);

        var sections = headings.Where(heading => heading.Text == "Section").ToArray();
        await Assert.That(sections.Length).IsGreaterThanOrEqualTo(2);
        var ids = sections.Select(section => section.Id).Distinct(StringComparer.Ordinal).ToArray();
        await Assert.That(ids.Length).IsEqualTo(sections.Length);
    }

    [Test]
    public async Task BlogPost_ExternalLinksOpenInNewTab_InternalLinksDoNot()
    {
        var html = await GeneratePostHtmlAsync(await ReadFixtureAsync("body-basics.md"));

        await Assert.That(html).Contains("https://example.org/docs");
        await Assert.That(html).Contains("target=\"_blank\"");
        await Assert.That(html).Contains("rel=\"noopener noreferrer\"");
        // Internal and anchor links must not gain new-tab attributes.
        await Assert.That(html).Contains("href=\"/posts/post.html\"");
    }

    [Test]
    public async Task RenderMarkdown_DisablesRawHtml()
    {
        var generator = new SiteGenerator(new ReferenceMarkdigCompiler());
        var html = generator.RenderMarkdown(await ReadFixtureAsync("advanced-extensions.md"));

        await Assert.That(html).DoesNotContain("<div class=\"raw\">");
        await Assert.That(html).Contains("&lt;div");
        await Assert.That(html).DoesNotContain("<script>alert");
    }

    [Test]
    public async Task RenderMarkdown_SupportsAdvancedExtensionsBeyondCommonMarkGfm()
    {
        var generator = new SiteGenerator(new ReferenceMarkdigCompiler());
        var html = generator.RenderMarkdown(await ReadFixtureAsync("gfm-extensions.md"));

        // Pipe tables (GFM + advanced).
        await Assert.That(html).Contains("<table");
        // Task lists.
        await Assert.That(html.ToLowerInvariant()).Contains("checkbox");
        // Strikethrough via emphasis extras.
        await Assert.That(html).Contains("<del>");
        // Autolinks.
        await Assert.That(html).Contains("https://example.org/auto");
        // Footnotes.
        await Assert.That(html).Contains("footnote");
        // Definition lists.
        await Assert.That(html).Contains("<dl");
    }

    [Test]
    public async Task RenderMarkdown_SupportsAlertContainerMathDiagramExtensions()
    {
        var generator = new SiteGenerator(new ReferenceMarkdigCompiler());
        var html = generator.RenderMarkdown(await ReadFixtureAsync("advanced-extensions.md"));

        // Alert blocks, custom containers, math, and diagrams must not silently disappear.
        // Exact markup may evolve, but content must survive rendering.
        await Assert.That(html).Contains("Alert body text.");
        await Assert.That(html).Contains("Container body text.");
        await Assert.That(html.ToLowerInvariant()).Contains("mermaid");
    }

    [Test]
    public async Task SearchIndex_PlainTextContainsRenderedBody()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "post.md"), """
            ---
            title: "Baseline Post"
            date: "2026-01-02T03:04:05Z"
            summary: "baseline summary"
            tags:
              - baseline
            ---

            ## Searchable

            Searchable body text with `code` and [link](https://example.org/docs).
            """);
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        var output = Path.Combine(workspace.Root, "output");
        await new SiteGenerator(new ReferenceMarkdigCompiler()).GenerateAsync(
            TestSite(), posts, output, clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() });

        await using var stream = File.OpenRead(Path.Combine(output, "search-index.json"));
        using var document = await JsonDocument.ParseAsync(stream);
        var body = document.RootElement.GetProperty("documents")[0].GetProperty("body").GetString()!;
        await Assert.That(body).Contains("Searchable body text");
    }

    [Test]
    public async Task FrontMatter_IsStillRequiredForLegacyPosts()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "post.md"), "No front matter here.\n");

        await Assert.That(async () => await new MarkdownPostReader().ReadAllAsync(content))
            .Throws<InvalidOperationException>();
    }
}
