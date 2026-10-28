using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Testing;

namespace LithoSharp.Tests;

public sealed class DocsNavigationEnhancementTests
{
    [Test]
    public async Task CollapsedTopicsKeepOverviewLinksAndOpenOnlyCurrentAncestors()
    {
        var current = Page("/sub/guides/advanced/install.html", "Install");
        var root = Node("Root", null,
            Node("First", Page("/sub/first.html", "First")),
            Node("<Guides>", Page("/sub/guides.html", "Overview"),
                Node("Advanced", null, Node("Install", current))),
            Node("Other", null, Node("Last", Page("/sub/last.html", "Last"))));
        var context = ComponentRenderingContext.Create(new SiteSettings());
        var props = new DocsNavigationComponentProps(root, [], SiteUrl.ForFile("guides/advanced/install.html", "https://example.test/sub/"));
        var component = new DocsNavigationComponent();
        using var original = SiteTestDocument.Parse(context.Render(component, props).ToHtmlString());
        await Assert.That(original.Snapshot.QueryAll("details").Count).IsEqualTo(0);
        var html = context.Render(component, props with { CollapseTopics = true }).ToHtmlString();
        using var document = SiteTestDocument.Parse(html);
        var snapshot = document.Snapshot;
        await Assert.That(snapshot.QueryAll("details").Count).IsEqualTo(3);
        await Assert.That(snapshot.QueryAll("details[open] > summary").Select(element => element.TextContent))
            .IsEquivalentTo(["<Guides>", "Advanced"]);
        await Assert.That(snapshot.QueryAll("summary a").Count).IsEqualTo(0);
        await Assert.That(snapshot.Query("details > a[href='/sub/guides.html']")?.TextContent).IsEqualTo("<Guides>");
        await Assert.That(snapshot.QueryAll("[aria-current=page]").Count).IsEqualTo(1);
        await Assert.That(snapshot.Query("[aria-current=page]")?.GetAttribute("href")).IsEqualTo(current.Url);
        await Assert.That(string.Join(',', snapshot.QueryAll("a").Select(element => element.GetAttribute("href"))))
            .IsEqualTo("/sub/first.html,/sub/guides.html,/sub/guides/advanced/install.html,/sub/last.html");
        await Assert.That(snapshot.QueryAll("script").Count).IsEqualTo(0);
        await Assert.That(html).Contains("<summary>&lt;Guides&gt;</summary>");
    }

    [Test]
    public async Task TopicCardsUseNavigationOrderAndEscapeSummariesWithMissingOverviewFallback()
    {
        var root = Node("Root", null,
            Node("<Overview>", Page("/sub/overview.html?a=1&b=2", "Overview", "<script>Summary & detail</script>"),
                Node("Child", Page("/sub/child.html", "Child"))),
            Node("No overview", null,
                Node("Nested", null, Node("Leaf", Page("/sub/leaf.html", "Leaf")))),
            Node("No summary", Page("/sub/empty.html", "Empty")));
        var html = DocsTopicCardsComponent.RenderCore(root);
        using var document = SiteTestDocument.Parse(html);
        var snapshot = document.Snapshot;
        await Assert.That(string.Join(',', snapshot.QueryAll("article > h2").Select(element => element.TextContent)))
            .IsEqualTo("<Overview>,No overview,No summary");
        await Assert.That(string.Join(',', snapshot.QueryAll("a").Select(element => element.GetAttribute("href"))))
            .IsEqualTo("/sub/overview.html?a=1&b=2,/sub/child.html,/sub/leaf.html,/sub/empty.html");
        await Assert.That(snapshot.QueryAll("article > p").Count).IsEqualTo(1);
        await Assert.That(snapshot.Query("article > p")?.TextContent).IsEqualTo("<script>Summary & detail</script>");
        await Assert.That(snapshot.QueryAll("script").Count).IsEqualTo(0);
        await Assert.That(html).Contains("href=\"/sub/overview.html?a=1&amp;b=2\"");
        await Assert.That(DocsTopicCardsComponent.RenderCore(Node("Empty", null))).IsEqualTo(string.Empty);
    }

    private static SiteTemplateNavigationNode Node(string label, SiteTemplatePage? page,
        params SiteTemplateNavigationNode[] children) => new() { Label = label, Page = page, Children = children };

    private static SiteTemplatePage Page(string url, string title, string summary = "") => new()
    {
        Post = new MarkdownPost(title + ".md", title,
            new PostFrontMatter { Title = title, Summary = summary }, string.Empty, title + ".html"),
        Url = url,
        ContentHtml = string.Empty,
        Headings = []
    };
}
