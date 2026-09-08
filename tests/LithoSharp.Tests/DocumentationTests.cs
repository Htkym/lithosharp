using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;
using LithoSharp.Routing;
using LithoSharp.Search;

namespace LithoSharp.Tests;

public sealed class DocumentationTests
{
    [Test]
    public async Task TwoCollectionsThreeVersionsTwoLanguagesShareNavigationAndPublicationWithoutNode()
    {
        using var workspace = new TemporaryWorkspace();
        await using var docs = new DocumentationSite(new(workspace.Root) { NodeExecutable = "must-not-start-node" });
        foreach (var collection in new[] { "guide", "api" })
        {
            var variants = new List<DocumentVariant>();
            foreach (var version in new[] { "current", "v1", "v2" })
            foreach (var locale in new[] { "en", "ja" })
            {
                var input = Path.Combine(workspace.Root, "input", collection, version, locale);
                Directory.CreateDirectory(input);
                await File.WriteAllTextAsync(Path.Combine(input, "02-next.md"), "---\ntitle: Next\ntags: [topic]\n---\n# Next\n");
                await File.WriteAllTextAsync(Path.Combine(input, "01-intro.md"), "---\nid: introduction\ntitle: Introduction\n---\n# Intro\n");
                await File.WriteAllTextAsync(Path.Combine(input, "03-hidden.md"), "---\ntitle: Hidden\nunlisted: true\n---\nPrivate discovery canary\n");
                variants.Add(new(version, locale, input, $"{collection}/{version}/{locale}") { FallbackDocumentId = "introduction" });
            }
            docs.AddCollection(new(collection, variants));
        }
        var output = Path.Combine(workspace.Root, "out");
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.com/project/" }, [], output, true, new() { Template = new DocsSiteTemplate { EnableSearch = true } },
            new() { Extensions = [docs], BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
        await Assert.That(docs.Catalog.Pages.Count).IsEqualTo(36);
        var key = new DocumentKey("guide", "v1", "ja", "introduction");
        var navigation = docs.Catalog.Sidebar("guide", "v1", "ja");
        await Assert.That(navigation.Count).IsEqualTo(2);
        await Assert.That(docs.Catalog.Adjacent(key, navigation).Next!.Key.Id).IsEqualTo("next");
        await Assert.That(docs.Catalog.Switch(key, "v2", "en").Key.Id).IsEqualTo("introduction");
        var html = await File.ReadAllTextAsync(Path.Combine(output, "guide/v1/ja/introduction/index.html"));
        await Assert.That(html).Contains("<html lang=\"ja\">");
        await Assert.That(html).Contains("hreflang=\"en\"");
        await Assert.That(html).Contains("/project/guide/v1/ja/next/");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "guide/v1/ja/hidden/index.html"))).Contains("noindex");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "search-index.json"))).DoesNotContain("Private discovery canary");
        await Assert.That(docs.MdxMetrics.RenderedPages).IsEqualTo(0);
        var otherPage = Path.Combine(output, "api/v1/en/introduction/index.html");
        var otherBefore = await File.ReadAllBytesAsync(otherPage);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "input/api/v1/en/01-intro.md"), "---\nid: introduction\ntitle: Not selected\n---\nChanged outside the scope\n");
        File.Delete(Path.Combine(workspace.Root, "input/guide/v1/ja/02-next.md"));
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.com/project/" }, [], output, false,
            new() { Template = new DocsSiteTemplate { EnableSearch = true } }, new() { Extensions = [docs], BuildTimestamp = DateTimeOffset.UnixEpoch, OutputScope = ["guide/v1/ja"] }, default);
        await Assert.That(File.Exists(Path.Combine(output, "guide/v1/ja/next/index.html"))).IsFalse();
        await Assert.That(await File.ReadAllBytesAsync(otherPage)).IsEquivalentTo(otherBefore);
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.com/project/" }, [], output, false,
            new() { Template = new DocsSiteTemplate { EnableSearch = true } }, new() { Extensions = [docs], BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
        await Assert.That(await File.ReadAllTextAsync(otherPage)).Contains("Not selected");
    }

    [Test]
    public async Task SnapshotsPreserveRelativeDependenciesAndTranslationsRequireExplicitFallback()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), "import Counter from './Counter.jsx'\n\n<Counter />");
        await File.WriteAllTextAsync(Path.Combine(source, "Counter.jsx"), "export default () => 'counter';");
        var snapshot = Path.Combine(workspace.Root, "v1");
        await DocumentSnapshot.CreateAsync(source, snapshot, "v1");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(snapshot, "Counter.jsx"))).Contains("counter");
        await Assert.That(async () => await DocumentSnapshot.CreateAsync(source, snapshot, "v1")).Throws<IOException>();
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), "import Outside from '../outside.jsx'");
        await Assert.That(async () => await DocumentSnapshot.CreateAsync(source, Path.Combine(workspace.Root, "v2"), "v2")).Throws<IOException>();
        var translations = new TranslationCatalog("en", new Dictionary<string, IReadOnlyDictionary<string, string>>
        { ["en"] = new Dictionary<string, string> { ["copy"] = "Copy", ["next"] = "Next" }, ["ja"] = new Dictionary<string, string> { ["copy"] = "コピー" } });
        await Assert.That(translations.Get("ja", "copy")).IsEqualTo("コピー");
        await Assert.That(translations.Get("ja", "next", MissingTranslationPolicy.Source)).IsEqualTo("Next");
        await Assert.That(() => translations.Get("ja", "next")).Throws<KeyNotFoundException>();
        await Assert.That(TranslationCatalog.ExtractKeys("t('copy'); Translate(\"next\"); t(dynamicKey)")).IsEquivalentTo(new[] { "copy", "next" });
    }

    [Test]
    public async Task CategoryNavigationAndJapaneseApiSearchResolveTheRequestedContext()
    {
        var intro = new DocumentPage(new("guide", "v1", "ja", "intro"), "01-basics/01-intro.md", new() { Title = "初期化", SidebarPosition = 2 }, SiteRoute.ForDirectoryIndex("guide/v1/ja/intro"));
        var api = new DocumentPage(new("guide", "v1", "ja", "api"), "01-basics/02-api.md", new() { Title = "API", SidebarPosition = 1 }, SiteRoute.ForDirectoryIndex("guide/v1/ja/api"));
        var catalog = new DocumentCatalog([intro, api]);
        var tree = catalog.Sidebar("guide", "v1", "ja", categories: new Dictionary<string, DocumentCategory> { ["01-basics"] = new() { Label = "基本操作", Collapsed = false } });
        await Assert.That(tree[0].Label).IsEqualTo("基本操作");
        await Assert.That(tree[0].Children[0].Document).IsEqualTo(api.Key);
        var document = new SearchDocument("非同期 API", "ジェネリック型", [], "/guide/v1/ja/api/", "", "Task<T>.ConfigureAwait(false) で非同期処理を設定します。")
        { Collection = "guide", Version = "v1", Locale = "ja", Sections = [new("ConfigureAwait", "configure-await", "Task<T>.ConfigureAwait(false) で非同期処理を設定します。")] };
        var other = document with { Version = "v2", Url = "/guide/v2/ja/api/" };
        foreach (var query in new[] { "非同期", "Task<T>", "ConfigureAwait(false)", "Task 非同期" })
        {
            var results = LocalSearch.Query([document, other], query, "guide", "v1", "ja");
            await Assert.That(results.Count).IsEqualTo(1);
            await Assert.That(results[0].Url).IsEqualTo("/guide/v1/ja/api/#configure-await");
        }
    }
}
