using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Documentation;
using LithoSharp.Mdx;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class ScopedDocumentationSearchTests
{
    [Test]
    public async Task StaticScopedBodiesUseDocumentIdsAndInvalidateWithoutChangingRenderedBodies()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var extension = new StaticBodyExtension(workspace.Root);
        var options = new SiteGenerationOptions { Extensions = [extension], BuildTimestamp = DateTimeOffset.UnixEpoch };
        var generator = new SiteGenerator();
        Task<SiteGenerationResult> Build() => generator.GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/base/" }, [], output, false,
            new() { Template = new DocsSiteTemplate { EnableSearch = true } }, options, CancellationToken.None);
        await Build();
        await Check("en", "Static worker text");
        await Check("ja", "Rendered ja");
        var coreBefore = await File.ReadAllBytesAsync(Path.Combine(output, "search-index.json"));
        var unchanged = await Build();
        await Assert.That(unchanged.BuildReport.Nodes.Where(static node => node.NodeId.StartsWith("asset:fixture-search-", StringComparison.Ordinal))
            .All(static node => node.CacheHit)).IsTrue();

        // An empty static body is authoritative, and only its scoped index changes.
        extension.OverrideText = string.Empty;
        var changed = await Build();
        await Check("en", string.Empty);
        await Check("ja", "Rendered ja");
        await Assert.That(changed.BuildReport.Nodes.Single(static node => node.NodeId == "asset:fixture-search-en").CacheHit).IsFalse();
        await Assert.That(changed.BuildReport.Nodes.Single(static node => node.NodeId == "asset:fixture-search-ja").CacheHit).IsTrue();
        await Assert.That(changed.BuildReport.Nodes.Single(static node => node.NodeId == "index:search").CacheHit).IsTrue();
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(output, "search-index.json"))).IsEquivalentTo(coreBefore);

        async Task Check(string locale, string expectedBody)
        {
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(output, locale, "_search.json")));
            var document = json.RootElement.EnumerateArray().Single();
            await Assert.That(document.GetProperty("body").GetString()).IsEqualTo(expectedBody);
            await Assert.That(document.GetProperty("locale").GetString()).IsEqualTo(locale);
            await Assert.That(document.GetProperty("sections")[0].GetProperty("title").GetString()).IsEqualTo("Rendered " + locale);
        }
    }

    [Test]
    public async Task VariantSearchUsesActualComponentHtmlAndVerifiedCachedBodiesWithoutNode()
    {
        using var workspace = new TemporaryWorkspace();
        var english = Path.Combine(workspace.Root, "en");
        var japanese = Path.Combine(workspace.Root, "ja");
        Directory.CreateDirectory(english);
        Directory.CreateDirectory(japanese);
        const string body = "---\nid: intro\ntitle: Intro\ntags: [catalogue]\n---\n# Intro\n\n:::component name=\"search-card\"\n:::";
        await File.WriteAllTextAsync(Path.Combine(english, "intro.md"), body);
        await File.WriteAllTextAsync(Path.Combine(japanese, "intro.md"), body.Replace("title: Intro", "title: Japanese intro", StringComparison.Ordinal));
        await using var docs = new DocumentationSite(new(workspace.Root) { NodeExecutable = "unavailable-node-for-markdown" })
        {
            Browser = new() { Search = true },
        };
        docs.AddCollection(new("guide", [new("v1", "en", english, "v1/en"), new("v1", "ja", japanese, "v1/ja")]));
        var renders = 0;
        var output = Path.Combine(workspace.Root, "output");
        var options = new SiteGenerationOptions
        {
            Extensions = [docs],
            Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.DocumentationProfile, Components = Registry("first") },
            BuildTimestamp = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero),
        };
        var generator = new SiteGenerator();
        async Task<SiteGenerationResult> Build() => await generator.GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/base/" }, [], output, false,
            new() { Template = new DocsSiteTemplate() }, options, CancellationToken.None);
        await Build();
        await Assert.That(renders).IsEqualTo(2);
        await Check("en", "first");
        await Check("ja", "first");
        var firstBytes = await File.ReadAllBytesAsync(Path.Combine(output, "v1", "en", "_search.json"));
        renders = 0;
        var unchanged = await Build();
        await Assert.That(renders).IsEqualTo(0);
        await Assert.That(unchanged.BuildReport.Nodes.Count(static node => node.NodeId.StartsWith("asset:docs:search:", StringComparison.Ordinal))).IsEqualTo(2);
        await Assert.That(unchanged.BuildReport.Nodes.Where(static node => node.NodeId.StartsWith("asset:docs:search:", StringComparison.Ordinal))
            .All(static node => node.CacheHit)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(Path.Combine(output, "v1", "en", "_search.json"))).SequenceEqual(firstBytes)).IsTrue();
        options = options with { Markdown = options.Markdown with { Components = Registry("second") } };
        var changed = await Build();
        await Assert.That(renders).IsEqualTo(2);
        await Assert.That(changed.BuildReport.Nodes.Where(static node => node.NodeId.StartsWith("asset:docs:search:", StringComparison.Ordinal))
            .All(static node => !node.CacheHit)).IsTrue();
        await Check("en", "second");
        await Check("ja", "second");

        MarkdownComponentRegistry Registry(string revision) => new([
            MarkdownComponentRegistration.Create("search-card", new SearchCard(revision, () => renders++),
                new ReflectionContentFrontMatterBinder<SearchCardProps>(), revision)
        ]);

        async Task Check(string locale, string marker)
        {
            var path = Path.Combine(output, "v1", locale, "_search.json");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            await Assert.That(json.RootElement.GetArrayLength()).IsEqualTo(1);
            var document = json.RootElement[0];
            await Assert.That(document.GetProperty("collection").GetString()).IsEqualTo("guide");
            await Assert.That(document.GetProperty("version").GetString()).IsEqualTo("v1");
            await Assert.That(document.GetProperty("locale").GetString()).IsEqualTo(locale);
            await Assert.That(document.GetProperty("body").GetString()!).Contains("Generated " + marker);
            await Assert.That(document.GetProperty("tags")[0].GetString()).IsEqualTo("catalogue");
            var section = document.GetProperty("sections").EnumerateArray().Single(item => item.GetProperty("anchor").GetString() == "component-heading");
            await Assert.That(section.GetProperty("title").GetString()).IsEqualTo("Generated " + marker);
            var html = await File.ReadAllTextAsync(Path.Combine(output, "v1", locale, "intro", "index.html"));
            await Assert.That(html).Contains("id=\"component-heading\"");
            await Assert.That(html).Contains("/base/v1/" + locale + "/_search.json");
        }
    }

    private sealed class StaticBodyExtension(string root) : ISiteBuildExtension
    {
        internal string OverrideText { get; set; } = "Static worker text";

        public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken)
        {
            var collections = new List<SiteContentCollection>();
            var projections = new List<ScopedSearchProjection>();
            foreach (var locale in new[] { "en", "ja" })
            {
                var collection = new ContentCollection<string, string>(new("fixture-" + locale), root,
                    [new(new("physical-entry"), "source.md", "unchanged", locale, "<h2 id=\"rendered\">Rendered " + locale + "</h2>")],
                    _ => SiteRoute.ForDirectoryIndex(locale + "/page", context.Site.BaseUrl),
                    _ => new PageMetadata("Page") { Document = new DocumentKey("guide", "v1", locale, "stable-page") },
                    transformationId: new("fixture-v1"), isCacheable: true);
                collections.Add(new SiteContentCollection<string, string>(collection,
                    static (entry, rendering) => rendering.RenderDocument(entry.Body))
                { RendererFingerprint = "fixture-v1" });
                projections.Add(new("fixture-search-" + locale, locale + "/_search.json", "guide", "v1", locale,
                    locale == "en" ? [new KeyValuePair<string, string>("stable-page", OverrideText)] : []));
            }
            return Task.FromResult(new SiteBuildContribution { ContentCollections = collections, ScopedSearch = projections });
        }
    }

    private sealed class SearchCardProps { }
    private sealed class SearchCard(string marker, Action rendered) : ISiteComponent<SearchCardProps>
    {
        public IHtmlContent Render(SearchCardProps props, ComponentRenderingContext context)
        {
            rendered();
            return Html.UnsafeRaw("<section><h2 id=\"component-heading\">Generated " + Html.Encode(marker)
                + "</h2><p>Searchable component body.</p></section>");
        }
    }
}
