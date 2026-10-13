using System.Text.Json;
using LithoSharp.Configuration;
using LithoSharp.Build;
using LithoSharp.Content.Compilation;
using LithoSharp.Mdx;

namespace LithoSharp.Tests;

/// <summary>
/// C08: MDX information mapped into the common semantic model, and docs
/// search/TOC wired to it. Dynamic React output stays out of static
/// inferences; final-DOM validation remains responsible for it.
/// </summary>
public sealed class MdxSemanticsTests
{
    private static JsonElement Element<T>(T value) =>
        JsonSerializer.SerializeToElement(value);

    private static MdxDocument Body(string text, int bodyStartLine = 1, int bodyStartOffset = 0) =>
        new(text, bodyStartLine, bodyStartOffset);

    [Test]
    public async Task FromWorker_MapsHeadingsLinksAndTitle()
    {
        var body = Body("intro\n\n## Section\n\nText.\n", bodyStartLine: 4);
        var semantics = MdxSemantics.FromWorker(
            "guide/intro.mdx",
            body,
            Element(new[] { new { depth = 2, text = "Section", line = 6, id = "section" } }),
            Element(new[]
            {
                new { url = "https://example.org/a", line = 8, image = false },
                new { url = "pic.png", line = 8, image = true },
            }),
            Element(Array.Empty<object>()),
            "Section\nText.");

        await Assert.That(semantics.Title).IsNull();
        await Assert.That(semantics.PlainText).IsEqualTo("Section\nText.");
        var heading = semantics.Headings.Single();
        await Assert.That(heading.Text).IsEqualTo("Section");
        await Assert.That(heading.Id).IsEqualTo("section");
        await Assert.That(heading.RawLevel).IsEqualTo(2);
        await Assert.That(heading.OutputLevel).IsEqualTo(2);
        // Worker line 6 is a file line; body starts at file line 4.
        await Assert.That(heading.Span.Start).IsEqualTo("intro\n\n".Length);
        await Assert.That(semantics.Links.Count).IsEqualTo(2);
        await Assert.That(semantics.Links[0].IsImage).IsFalse();
        await Assert.That(semantics.Links[1].IsImage).IsTrue();
        await Assert.That(semantics.Assets.Single().Url).IsEqualTo("pic.png");
        await Assert.That(semantics.Components.Count).IsEqualTo(0);
        await Assert.That(semantics.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(semantics.Source.FilePath).IsEqualTo("guide/intro.mdx");
    }

    [Test]
    public async Task FromWorker_TitleIsFirstH1WithoutDemotion()
    {
        var semantics = MdxSemantics.FromWorker(
            "doc.mdx",
            Body("# Title\n"),
            Element(new[] { new { depth = 1, text = "Title", line = 1 } }),
            Element(Array.Empty<object>()),
            Element(Array.Empty<object>()),
            "Title");

        await Assert.That(semantics.Title).IsEqualTo("Title");
        // MDX headings are not demoted: raw and output levels match.
        await Assert.That(semantics.Headings.Single().OutputLevel).IsEqualTo(1);
    }

    [Test]
    public async Task FromWorker_MapsIslandsAsComponents()
    {
        var semantics = MdxSemantics.FromWorker(
            "doc.mdx",
            Body("text\n"),
            Element(Array.Empty<object>()),
            Element(Array.Empty<object>()),
            Element(new[]
            {
                new { module = "./Counter.jsx", exportName = "default" },
                new { module = "", exportName = "broken" },
            }),
            "text");

        var component = semantics.Components.Single();
        await Assert.That(component.Name).IsEqualTo("./Counter.jsx#default");
    }

    [Test]
    public async Task FromWorker_IgnoresMalformedEntries()
    {
        var semantics = MdxSemantics.FromWorker(
            "doc.mdx",
            Body("a\nb\n"),
            Element(new object?[]
            {
                null,
                new { depth = 0, text = "shallow", line = 1 },
                new { depth = 9, text = "deep", line = 2 },
                new { depth = 2, text = "gone", line = 99 },
            }),
            Element(new object?[] { null, new { url = "https://example.org/a", line = 2 } }),
            Element(Array.Empty<object>()),
            null);

        // Invalid depths and nulls never surface; unknown lines keep their data
        // with an empty span instead of being dropped.
        await Assert.That(semantics.Headings.Count).IsEqualTo(1);
        await Assert.That(semantics.Headings[0].Text).IsEqualTo("gone");
        await Assert.That(semantics.Headings[0].Span.IsEmpty).IsTrue();
        await Assert.That(semantics.Links.Single().Url).IsEqualTo("https://example.org/a");
        await Assert.That(semantics.PlainText).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task FromWorker_SpanUsesOriginalFileOffset()
    {
        // Front matter "---\ntitle: T\n---\n" is 18 chars; body starts at offset 18.
        const string frontMatter = "---\ntitle: T\n---\n";
        var body = Body("intro\n\n## Section\n", bodyStartLine: 4, bodyStartOffset: frontMatter.Length);
        var semantics = MdxSemantics.FromWorker(
            "guide/intro.mdx",
            body,
            Element(new[] { new { depth = 2, text = "Section", line = 6, id = "section" } }),
            Element(Array.Empty<object>()),
            Element(Array.Empty<object>()),
            "intro\nSection");

        await Assert.That(semantics.Headings.Single().Span.Start).IsEqualTo(frontMatter.Length + "intro\n\n".Length);
    }

    [Test]
    public async Task DocsSearchAndToc_UseStaticWorkerSemantics()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "intro.mdx"),
            "---\ntitle: Guide\n---\nimport Counter from './Counter.jsx'\n\n## Guide <Counter />\n\nStatic text here.\n\n<Counter />\n");
        await File.WriteAllTextAsync(Path.Combine(source, "Counter.jsx"),
            "import {useState} from 'react';export default function Counter(){const [n,set]=useState(0);return <button onClick={()=>set(n+1)}>Count {n}</button>}");
        await using var docs = new DocumentationSite(new(workspace.Root, WorkerDirectory()) { Cacheable = true })
        {
            Browser = new DocumentationBrowserOptions(),
        };
        docs.AddCollection(new("guide", [new("current", "en", source, "guide")]) { UseMdx = true });
        var output = Path.Combine(workspace.Root, "out");
        var observer = new FactsLifetimeObserver();
        var options = new SiteGenerationOptions
        {
            Extensions = [observer, docs], BuildTimestamp = DateTimeOffset.UnixEpoch,
            Quality = new(checkOrphans: false)
        };
        var generator = new SiteGenerator();
        var first = await generator.GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/" }, [], output, clean: true,
            new() { Template = new DocsSiteTemplate { EnableSearch = true } },
            options, CancellationToken.None);
        await Assert.That(first.HtmlFactsSharing.ParseCount).IsEqualTo(first.QualityReport.HtmlParseCount + 1);
        await Assert.That(first.HtmlFactsSharing.ReuseCount > 0).IsTrue(); // MDX article -> Core search; final layout is a distinct input.
        await Assert.That(first.HtmlFactsSharing.EntryCount + first.HtmlFactsSharing.RetainedBytes).IsEqualTo(0); // Released before result/hooks, not only at Dispose.
        await Assert.That(observer.Owner!.Counters.EntryCount).IsEqualTo(0); // Actual Generate return has awaited owner disposal.

        var page = Directory.EnumerateFiles(Path.Combine(output, "guide"), "*.html", SearchOption.AllDirectories)
            .Select(path => File.ReadAllText(path)).Single(text => text.Contains("Static text here."));
        // The rendered button exists in final HTML (final-DOM territory) ...
        await Assert.That(page).Contains("<button");
        await Assert.That(page).Contains(">Guide </a>");
        await Assert.That(page.Contains(">Guide Count 0</a>")).IsFalse();
        // ... but the indexed body carries worker text only. Snippets stay
        // HTML-derived by design (final-DOM territory, like link validation).
        await using var stream = File.OpenRead(Path.Combine(output, "guide", "_search.json"));
        using var search = await JsonDocument.ParseAsync(stream);
        var body = search.RootElement.EnumerateArray().Single().GetProperty("body").GetString()!;
        await Assert.That(body).Contains("Static text here.");
        await Assert.That(body.Contains("Count")).IsFalse();
        var section = search.RootElement.EnumerateArray().Single().GetProperty("sections").EnumerateArray().Single();
        await Assert.That(section.GetProperty("title").GetString()).IsEqualTo("Guide Count 0");
        await Assert.That(section.GetProperty("body").GetString()).Contains("Static text here.");
        await Assert.That(section.GetProperty("body").GetString()).Contains("Count 0");
        await Assert.That(page).Contains("id=\"" + section.GetProperty("anchor").GetString() + "\"");
        var coreBefore = await File.ReadAllBytesAsync(Path.Combine(output, "search-index.json"));
        var docsBefore = await File.ReadAllBytesAsync(Path.Combine(output, "guide", "_search.json"));
        using var coreIndex = JsonDocument.Parse(coreBefore);
        var coreDocument = coreIndex.RootElement.GetProperty("documents").EnumerateArray()
            .Single(document => document.TryGetProperty("collection", out var collection) && collection.GetString() == "guide");
        await Assert.That(coreDocument.GetProperty("body").GetString()!).Contains("Count 0");
        var coreSection = coreDocument.GetProperty("sections").EnumerateArray().Single();
        foreach (var field in new[] { "title", "anchor", "body" })
            await Assert.That(coreSection.GetProperty(field).GetString()).IsEqualTo(section.GetProperty(field).GetString());
        var second = await generator.GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/" }, [], output, clean: false,
            new() { Template = new DocsSiteTemplate { EnableSearch = true } }, options, CancellationToken.None);
        await Assert.That(second.QualityReport.HtmlParseCount).IsEqualTo(0);
        await Assert.That(second.QualityReport.HtmlFactsCacheHitCount > 0).IsTrue();
        await Assert.That(second.HtmlFactsSharing.ParseCount).IsEqualTo(1);
        await Assert.That(observer.Owner!.Counters.RetainedBytes).IsEqualTo(0);
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(output, "search-index.json"))).IsEquivalentTo(coreBefore);
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(output, "guide", "_search.json"))).IsEquivalentTo(docsBefore);
    }

    [Test]
    public async Task MarkdownDocs_SearchRenderAndWarningsNeedNoNode()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "intro.md"),
            "---\ntitle: Guide\n---\n## Section\n\nShared body text. Note[^a]\n\n[^a]: footnote text\n");
        await using var docs = new DocumentationSite(new(workspace.Root, "must-not-start-node"))
        {
            Browser = new DocumentationBrowserOptions(),
        };
        docs.AddCollection(new("guide", [new("current", "en", source, "guide")])
        {
            UseMdx = false,
        });
        var output = Path.Combine(workspace.Root, "out");
        var generation = await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/" }, [], output, clean: true,
            new() { Template = new DocsSiteTemplate() },
            new() { Extensions = [docs], BuildTimestamp = DateTimeOffset.UnixEpoch }, CancellationToken.None);

        await Assert.That(docs.MdxMetrics.WorkerStarts).IsEqualTo(0);
        await Assert.That(generation.BuildReport.Diagnostics.Any(diagnostic =>
            diagnostic.Id == LithoLimits.UnsupportedFootnoteDiagnosticId)).IsTrue();
        var page = Directory.EnumerateFiles(Path.Combine(output, "guide"), "*.html", SearchOption.AllDirectories)
            .Select(path => File.ReadAllText(path)).Single(text => text.Contains("Shared body text."));
        await Assert.That(page).Contains("href=\"#section\"");
        await using var stream = File.OpenRead(Path.Combine(output, "guide", "_search.json"));
        using var search = await JsonDocument.ParseAsync(stream);
        var body = search.RootElement.EnumerateArray().Single().GetProperty("body").GetString()!;
        await Assert.That(body).Contains("Shared body text.");
    }

    private sealed class FactsLifetimeObserver : ISiteBuildExtension
    {
        internal BuildHtmlFacts? Owner { get; private set; }
        public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
        {
            Owner = context.HtmlFacts;
            return Task.FromResult(new SiteBuildContribution());
        }
    }

    private static string WorkerDirectory([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(sourcePath)!, AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        for (var path = new DirectoryInfo(start); path is not null; path = path.Parent)
        {
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
            {
                return Path.Combine(path.FullName, "src", "LithoSharp.Mdx", "worker");
            }
        }

        throw new DirectoryNotFoundException("The MDX integration fixture requires the repository's restored worker.");
    }
}
