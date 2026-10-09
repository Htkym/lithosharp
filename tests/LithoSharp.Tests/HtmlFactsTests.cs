using System.Text.Json;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.HtmlParsing;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class HtmlFactsTests
{
    [Test]
    public async Task SearchFacts_KeepRenderedSectionsSiblingRulesAndExcludedSubtrees()
    {
        const string html = "<!doctype html><title> A\t&amp; B </title><body>"
            + "<nav><h2 id=nav>Hidden navigation</h2></nav>"
            + "<h1 id=rendered>React &amp; <em>Heading</em></h1>Loose text"
            + "<p>Body &amp; <b>bold</b><script>Hidden script</script> tail</p><aside>Aside</aside>"
            + "<h7>Stop</h7><p>Outside first section</p>"
            + "<div><h2 id=nested>Nested</h2><p>Inside</p></div><h2 id=''>Last</h2><p>Tail</p>"
            + "<template><h2 id=inert>Hidden template</h2></template>"
            + "<style>Hidden style</style><noscript>Hidden noscript</noscript></body>";
        var facts = HtmlFacts.Parse(html);
        await Assert.That(facts.Title).IsEqualTo("A & B");
        var sections = facts.SearchSections();
        await Assert.That(sections.Select(section => section.Anchor)).IsEquivalentTo(["rendered", "nested", ""]);
        await Assert.That(sections[0].Title).IsEqualTo("React & Heading");
        await Assert.That(sections[0].Body).IsEqualTo("Body & bold tail Aside");
        await Assert.That(sections[1].Body).IsEqualTo("Inside");
        await Assert.That(sections[2].Body).IsEqualTo("Tail");
        await Assert.That(facts.BodyText).Contains("Loose text");
        await Assert.That(facts.BodyText).DoesNotContain("Hidden");
        await Assert.That(SiteGenerator.NormalizeForIndex(facts.SearchText)).Contains("Body & bold tail");
        await Assert.That(SiteGenerator.ExtractSearchSections(html)).IsEquivalentTo(sections);
        var adjacent = HtmlFacts.Parse("<p>A<em>B</em>C<!--ignored-->D<script>Hidden</script>E</p>");
        await Assert.That(adjacent.BodyText).IsEqualTo("ABCDE");
        await Assert.That(SiteGenerator.NormalizeForIndex(adjacent.SearchText)).IsEqualTo("A B C D E");
    }

    [Test]
    public async Task SearchSections_HandleManyAdjacentHeadingsAndObserveCancellationAfterParsing()
    {
        const int headingCount = 12_000;
        var html = new System.Text.StringBuilder("<!doctype html><body>");
        for (var i = 0; i < headingCount; i++) html.Append($"<h2 id=h{i}>Title {i}</h2>");
        html.Append("<p>Final body</p>");
        using var cancel = new CancellationTokenSource();
        var facts = HtmlFacts.Parse(html.ToString(), cancel.Token);
        var sections = facts.SearchSections();
        await Assert.That(sections.Count).IsEqualTo(headingCount);
        await Assert.That(sections.Take(headingCount - 1).All(section => section.Body.Length == 0)).IsTrue();
        await Assert.That(sections[0].Anchor).IsEqualTo("h0");
        await Assert.That(sections[^1].Title).IsEqualTo($"Title {headingCount - 1}");
        await Assert.That(sections[^1].Body).IsEqualTo("Final body");
        cancel.Cancel();
        await Assert.That(() => facts.SearchSections()).Throws<OperationCanceledException>();
        await Assert.That(() => SiteGenerator.ExtractSearchSections(html.ToString(), cancel.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task GeneratedContentSearch_UsesTheSameFactsForBodyAndDocumentSections()
    {
        using var workspace = new TemporaryWorkspace();
        const string html = "<nav><h2 id=nav>Navigation canary</h2></nav>"
            + "<h2 id=section>Rendered &amp; title</h2><p>A<em>B</em>C</p>";
        var collection = new ContentCollection<string, string>(new("facts"), workspace.Root,
            [new(new("intro"), "intro.html", "source-v1", "Intro", html)],
            _ => SiteRoute.ForDirectoryIndex("guide/intro"),
            entry => new PageMetadata(entry.FrontMatter) { Document = new("guide", "current", "en", entry.Id.Value) });
        var output = Path.Combine(workspace.Root, "output");
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.test/sub/" }, [], output, true,
            new SiteCustomization { Template = new BlogSiteTemplate() },
            new() { ContentCollections = [new SiteContentCollection<string, string>(collection, (entry, rendering) => rendering.RenderDocument(entry.Body))],
                BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "search-index.json")));
        var document = index.RootElement.GetProperty("documents").EnumerateArray()
            .Single(item => item.TryGetProperty("collection", out var value) && value.GetString() == "guide");
        await Assert.That(document.GetProperty("body").GetString()).IsEqualTo("Rendered & title A B C");
        var section = document.GetProperty("sections").EnumerateArray().Single();
        await Assert.That(section.GetProperty("anchor").GetString()).IsEqualTo("section");
        await Assert.That(section.GetProperty("title").GetString()).IsEqualTo("Rendered & title");
        await Assert.That(section.GetProperty("body").GetString()).IsEqualTo("ABC");
    }

    [Test]
    public async Task RawPositions_KeepCrLfAndUtf16ColumnsAndSyntheticPositionsUnknown()
    {
        const string html = "<!doctype html>\r\n😀<p id=actual>A\0&amp;B</p>";
        var facts = HtmlFacts.Parse(html);
        var paragraph = facts.Elements.Single(element => element.Name == "p");
        await Assert.That(facts.Position(paragraph)).IsEqualTo(((int Line, int Column)?)(2, 3));
        await Assert.That(facts.Position(facts.Elements.Single(element => element.Name == "body"))).IsNull();
        await Assert.That(facts.TextContent(paragraph.Id)).IsEqualTo("A&B");
        await Assert.That(facts.Anchors).IsEquivalentTo(["actual"]);
    }

    [Test]
    public async Task IncompleteFacts_AreRejectedBeforeIndexingAndCancellationPropagates()
    {
        foreach (var html in new[]
        {
            "<!doctype html PUBLIC '-//W3C//DTD HTML 4.01 Transitional//EN'><p>X",
            "<template shadowrootmode=open><p>X</p></template>"
        })
        {
            await Assert.That(CompactHtmlTreeBuilder.Parse(html).Status).IsEqualTo(HtmlTokenizationStatus.Partial);
            await Assert.That(() => HtmlFacts.Parse(html)).Throws<HtmlFactsIncompleteException>();
            await Assert.That(() => SiteGenerator.ExtractSearchSections(html)).Throws<HtmlFactsIncompleteException>();
        }
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.That(() => HtmlFacts.Parse("<p>X", cancel.Token)).Throws<OperationCanceledException>();
    }
}
