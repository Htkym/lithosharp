using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;
using LithoSharp.Pages;

namespace LithoSharp.Tests;

public sealed class DocumentationChromeTests
{
    [Test]
    [Arguments(DocumentationSearchScope.Collection)]
    [Arguments(DocumentationSearchScope.Site)]
    public async Task SeparateRegionsPreserveLegacyBodyAndRespectHiddenContents(DocumentationSearchScope scope)
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "docs");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "intro.md"), "---\ntitle: Intro\n---\nBody text.\n\n## Details\n\nMore text.");
        await File.WriteAllTextAsync(Path.Combine(input, "hidden.md"), "---\ntitle: Hidden\nhide_table_of_contents: true\n---\n## Hidden heading\n\nBody.");
        await File.WriteAllTextAsync(Path.Combine(input, "plain.md"), "---\ntitle: Plain\n---\nBody without subheadings.");
        var layout = new CapturingLayout();
        await using var docs = new DocumentationSite(new(workspace.Root)) { Layout = layout, Browser = new() { Search = true, SearchScope = scope } };
        docs.AddCollection(new("guide", [new("v1", "en", input, "en")]) { EditUrl = "https://example.test/edit" });
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], Path.Combine(workspace.Root, "output"), false,
            new() { Template = new DocsSiteTemplate { EnableSearch = true } }, new() { Extensions = [docs] }, default);
        await Assert.That(layout.Contents.Count).IsEqualTo(3);
        foreach (var content in layout.Contents)
        {
            var rendered = layout.Outputs[layout.Contents.IndexOf(content)];
            await Assert.That(rendered.Contains("docs-shell-no-toc", StringComparison.Ordinal)).IsEqualTo(content.TableOfContents is null);
            if (content.TableOfContents is not null)
                await Assert.That(rendered.IndexOf(content.TableOfContents.ToHtmlString(), StringComparison.Ordinal) < rendered.IndexOf("<main id=\"docs-main\"", StringComparison.Ordinal)).IsTrue();
            var chrome = content.DocumentationChrome!;
            await Assert.That(chrome is not null).IsTrue();
            await Assert.That(chrome!.Breadcrumbs!.ToHtmlString().Split("aria-current=\"page\"", StringSplitOptions.None).Length).IsEqualTo(2);
            var search = chrome!.Search!.ToHtmlString();
            await Assert.That(search.Contains("data-collection=", StringComparison.Ordinal)).IsEqualTo(scope == DocumentationSearchScope.Collection);
            await Assert.That(search.Contains("data-version=", StringComparison.Ordinal)).IsEqualTo(scope == DocumentationSearchScope.Collection);
            await Assert.That(search).Contains("data-locale=\"en\"");
            await Assert.That(search).Contains(scope == DocumentationSearchScope.Site ? "search-index.json" : "_search.json");
            var legacy = content.Body.ToHtmlString();
            foreach (var region in new[] { chrome!.Search, chrome.Breadcrumbs, chrome.Adjacent, chrome.Variants })
            {
                await Assert.That(region is not null).IsTrue();
                await Assert.That(legacy).Contains(region!.ToHtmlString());
                legacy = legacy.Replace(region.ToHtmlString(), "", StringComparison.Ordinal);
            }
            await Assert.That(legacy).IsEqualTo(chrome.Article.ToHtmlString());
            await Assert.That(chrome.Article.ToHtmlString()).Contains("Edit this page");
            await Assert.That(chrome.Article.ToHtmlString()).Contains("data-ls-messages");
            await Assert.That(chrome.Headings.Count).IsEqualTo(content.TableOfContents is null ? 0 : 1);
        }
        var ordinary = new PageLayoutContent(Html.UnsafeRaw("legacy"));
        await Assert.That(ordinary.DocumentationChrome is null).IsTrue();
        await Assert.That(ordinary.Body.ToHtmlString()).IsEqualTo("legacy");
    }

    [Test]
    public async Task ExistingMarkdownHeadingDoesNotReceiveASecondTitle()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "docs");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "intro.md"), "---\ntitle: Package\n---\n# Package\n\nDescription.\n\n## Usage\n\nUse the package.");
        var layout = new CapturingLayout();
        await using var docs = new DocumentationSite(new(workspace.Root)) { Layout = layout };
        docs.AddCollection(new("guide", [new("v1", "en", input, "en")]));
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], Path.Combine(workspace.Root, "output"), false,
            new() { Template = new DocsSiteTemplate() }, new() { Extensions = [docs] }, default);
        await Assert.That(layout.Contents.Single().DocumentationChrome!.Article.ToHtmlString().Split("<h1", StringSplitOptions.None).Length).IsEqualTo(2);
    }
    private sealed class CapturingLayout : IPageLayout<PageLayoutContent>
    {
        public List<PageLayoutContent> Contents { get; } = [];
        public List<string> Outputs { get; } = [];
        public IHtmlContent Render(SitePage<PageLayoutContent> page, PageRenderingContext context)
        {
            Contents.Add(page.Content);
            var html = new DocsPageLayout().Render(page, context);
            Outputs.Add(html.ToHtmlString());
            return html;
        }
    }
}
