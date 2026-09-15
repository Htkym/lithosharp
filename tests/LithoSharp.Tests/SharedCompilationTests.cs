using System.Text.Json;
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;

namespace LithoSharp.Tests;

/// <summary>
/// C03: one parse feeds HTML, search text, TOC, links, and assets.
/// </summary>
public sealed class SharedCompilationTests
{
    private static SiteSettings TestSite() => new()
    {
        Title = "Shared Site",
        Description = "One-parse tests.",
        BaseUrl = "https://example.test/",
        Language = "en",
        TimeZone = "UTC",
    };

    [Test]
    public async Task SingleAnalyze_ServesAllConsumersWithOneParse()
    {
        var compiler = new LithoMarkdownCompiler();
        const string body = "# Title\n\nBody with [link](https://example.org/a) and ![img](pic.png).\n";

        var analyzed = compiler.Analyze(body);

        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        await Assert.That(analyzed.Html).Contains("<h1");
        await Assert.That(analyzed.Semantics!.Headings.Count).IsEqualTo(1);
        await Assert.That(analyzed.Semantics.PlainText).Contains("Body with link and");
        await Assert.That(analyzed.Semantics.Links.Count).IsEqualTo(2);
        await Assert.That(analyzed.Semantics.Assets.Count).IsEqualTo(1);
        await Assert.That(compiler.ParseCount).IsEqualTo(1);

        // The pre-C03 split path parses once per consumer.
        _ = compiler.Compile(body);
        _ = compiler.RenderPlainText(body);
        await Assert.That(compiler.ParseCount).IsEqualTo(3);
    }

    [Test]
    [Arguments("# Top\n\nBody.\n")]
    [Arguments("## A & B\n\nBody.\n")]
    [Arguments("## `code` head\n\nBody.\n")]
    [Arguments("## [linked](https://example.org/x) head\n\nBody.\n")]
    [Arguments("## 日本語見出し\n\n本文。\n")]
    [Arguments("## Dup\n\n## Dup\n\nBody.\n")]
    public async Task CompiledToc_MatchesLegacyExtraction(string body)
    {
        var compiler = new LithoMarkdownCompiler();
        var analyzed = compiler.Analyze(body);
        var demoted = Demote(analyzed.Html);
        var legacy = SiteGenerator.ExtractTemplateHeadings(demoted);
        var compiled = analyzed.Semantics!.Headings
            .Where(heading => heading.OutputLevel is 2 or 3
                && !string.IsNullOrWhiteSpace(heading.Id)
                && !string.IsNullOrWhiteSpace(heading.Text))
            .Select(heading => new SiteTemplateHeading(heading.OutputLevel, heading.Id!, heading.Text))
            .ToArray();

        await Assert.That(compiled.Length).IsEqualTo(legacy.Count);
        for (var index = 0; index < compiled.Length; index++)
        {
            await Assert.That(compiled[index]).IsEqualTo(legacy[index]);
        }
    }

    [Test]
    public async Task CompiledToc_MatchesLegacyExtractionOnBaselineFixtures()
    {
        var compiler = new LithoMarkdownCompiler();
        foreach (var file in new[] { "body-basics.md", "gfm-extensions.md", "advanced-extensions.md" })
        {
            var body = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdigBaseline", file));
            var analyzed = compiler.Analyze(body);
            var legacy = SiteGenerator.ExtractTemplateHeadings(Demote(analyzed.Html));
            var compiled = analyzed.Semantics!.Headings
                .Where(heading => heading.OutputLevel is 2 or 3
                    && !string.IsNullOrWhiteSpace(heading.Id)
                    && !string.IsNullOrWhiteSpace(heading.Text))
                .Select(heading => new SiteTemplateHeading(heading.OutputLevel, heading.Id!, heading.Text))
                .ToArray();

            await Assert.That(string.Join("|", compiled.Select(heading => $"{heading.Level}:{heading.Id}:{heading.Text}")))
                .IsEqualTo(string.Join("|", legacy.Select(heading => $"{heading.Level}:{heading.Id}:{heading.Text}")));
        }
    }

    [Test]
    public async Task OnePostBuild_ParsesBodyOnceAndServesEveryConsumer()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "post.md"), """
            ---
            title: "Shared Post"
            date: "2026-01-02T03:04:05Z"
            summary: "shared summary"
            tags:
              - shared
            ---

            # Body Title

            Body text with [link](https://example.org/a).

            ## A &amp; B
            """);
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();
        await generator.GenerateAsync(
            TestSite(), posts, output, clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() });

        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(1);

        var post = await File.ReadAllTextAsync(Path.Combine(output, "posts", "post.html"));
        await Assert.That(post).Contains("<h2 id=\"body-title\">");
        await Assert.That(post).Contains("target=\"_blank\"");
        await Assert.That(post).Contains("class=\"toc-list\"");
        using var dom = new AngleSharp.Html.Parser.HtmlParser().ParseDocument(post);
        await Assert.That(dom.QuerySelector("a[data-toc-link][href='#a-b']")?.TextContent).IsEqualTo("A & B");

        await using var stream = File.OpenRead(Path.Combine(output, "search-index.json"));
        using var document = await JsonDocument.ParseAsync(stream);
        var bodyText = document.RootElement.GetProperty("documents")[0].GetProperty("body").GetString()!;
        await Assert.That(bodyText).Contains("Body text with link");
    }

    [Test]
    public async Task BlogAndDocs_ShareSearchAndTocFromTheSameModel()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "post.md"), """
            ---
            title: "Shared Post"
            date: "2026-01-02T03:04:05Z"
            summary: "shared summary"
            tags:
              - shared
            ---

            ## Section

            Body text.
            """);
        var posts = await new MarkdownPostReader().ReadAllAsync(content);

        var blogOutput = Path.Combine(workspace.Root, "blog");
        await new SiteGenerator().GenerateAsync(
            TestSite(), posts, blogOutput, clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() });
        var docsOutput = Path.Combine(workspace.Root, "docs");
        await new SiteGenerator().GenerateAsync(
            TestSite(), posts, docsOutput, clean: true,
            new SiteCustomization { Template = new DocsSiteTemplate() });

        var blogBody = await SearchBodyAsync(Path.Combine(blogOutput, "search-index.json"));
        await Assert.That(blogBody).Contains("Body text.");

        var blogPost = await File.ReadAllTextAsync(Path.Combine(blogOutput, "posts", "post.html"));
        var docsPost = await File.ReadAllTextAsync(Path.Combine(docsOutput, "posts", "post.html"));
        await Assert.That(blogPost).Contains("id=\"section\"");
        await Assert.That(docsPost).Contains("id=\"section\"");
    }

    [Test]
    public async Task FootnotePost_SurfacesWarningInBuildReport()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "post.md"), """
            ---
            title: "Footnote Post"
            date: "2026-01-02T03:04:05Z"
            summary: "footnote summary"
            tags:
              - footnote
            ---

            Note[^a]

            [^a]: footnote text
            """);
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        var output = Path.Combine(workspace.Root, "output");
        var generation = await new SiteGenerator().GenerateAsync(
            TestSite(), posts, output, clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() });

        await Assert.That(generation.BuildReport.Diagnostics.Any(diagnostic =>
            diagnostic.Id == LithoLimits.UnsupportedFootnoteDiagnosticId)).IsTrue();
        await Assert.That(generation.BuildReport.Diagnostics.Single(diagnostic =>
            diagnostic.Id == LithoLimits.UnsupportedFootnoteDiagnosticId).Location?.Line).IsEqualTo(9);
    }

    private static string Demote(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, "<h1(\\s[^>]*)?>", "<h2$1>")
            .Replace("</h1>", "</h2>");

    private static async Task<string> SearchBodyAsync(string indexPath)
    {
        await using var stream = File.OpenRead(indexPath);
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.GetProperty("documents")[0].GetProperty("body").GetString()!;
    }
}
