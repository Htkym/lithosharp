using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;
using LithoSharp.Routing;
using LithoSharp.Search;

namespace LithoSharp.Tests;

public sealed class DocumentationTests
{
    [Test]
    public async Task GitUpdatesAndMissingTranslationsAreExplicitAndPublicationSafe()
    {
        using var workspace = new TemporaryWorkspace();
        var en = Path.Combine(workspace.Root, "en");
        var ja = Path.Combine(workspace.Root, "ja");
        Directory.CreateDirectory(en); Directory.CreateDirectory(ja);
        await File.WriteAllTextAsync(Path.Combine(en, "intro.md"), "---\ntitle: Intro\n---\n# Intro\n");
        async Task Git(params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = workspace.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await output;
            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        try
        {
        await Git("init"); await Git("add", "en/intro.md");
        await Git("-c", "user.name=Fixture Author", "-c", "user.email=fixture@example.test", "commit", "-m", "Document fixture");
        var collection = new DocumentationCollection("guide", [new("current", "en", en, "en"), new("current", "ja", ja, "ja")]) { GitMetadata = true };
        await using var docs = new DocumentationSite(new(workspace.Root));
        docs.AddCollection(collection);
        var outputRoot = Path.Combine(workspace.Root, "out");
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], outputRoot, true, new() { Template = new DocsSiteTemplate() }, new() { Extensions = [docs] }, default);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(outputRoot, "en/intro/index.html"))).Contains("Fixture Author");
        await Assert.That(docs.GetInspection()!.Value.GetProperty("missingTranslations").GetArrayLength()).IsEqualTo(1);
        await using var strict = new DocumentationSite(new(workspace.Root));
        strict.AddCollection(collection with { MissingDocuments = MissingDocumentPolicy.Error });
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], outputRoot, false, null, new() { Extensions = [strict] }, default)).Throws<LithoSharp.Build.SiteBuildExtensionException>();
        await Assert.That(File.Exists(Path.Combine(outputRoot, "en/intro/index.html"))).IsTrue();
        }
        finally
        {
            var gitRoot = Path.Combine(workspace.Root, ".git");
            if (Directory.Exists(gitRoot))
                foreach (var file in Directory.EnumerateFiles(gitRoot, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Test]
    public async Task ApiReferencesResolveOverloadsAndGenerateVersionDifferencesAndOpenApi()
    {
        using var workspace = new TemporaryWorkspace();
        var before = Path.Combine(workspace.Root, "v1.xml");
        var after = Path.Combine(workspace.Root, "v2.xml");
        await File.WriteAllTextAsync(before, "<doc><members><member name=\"T:Example.Box`1\"><summary>A generic box.</summary></member><member name=\"M:Example.Box`1.Get(System.Int32)\"><summary>See <see cref=\"T:Example.Box`1\"/>.</summary></member></members></doc>");
        await File.WriteAllTextAsync(after, "<doc><members><member name=\"T:Example.Box`1\"><summary>A generic box.</summary><deprecated>Use NewBox.</deprecated></member><member name=\"M:Example.Box`1.Get(System.String)\"><summary>String overload.</summary></member></members></doc>");
        var openApi = Path.Combine(workspace.Root, "openapi.json");
        await File.WriteAllTextAsync(openApi, "{\"openapi\":\"3.1.0\",\"paths\":{\"/items\":{\"get\":{\"summary\":\"List items\",\"deprecated\":true,\"responses\":{\"200\":{\"description\":\"OK\"}}}}}}");
        var api = new ApiReferenceSite();
        api.AddXml(new("library", new("v1", "en", workspace.Root, "api/v1"), before));
        api.AddXml(new("library", new("v2", "en", workspace.Root, "api/v2"), after));
        api.AddOpenApi(new("http", new("v1", "en", workspace.Root, "http"), openApi));
        api.AddComparison("library", "v1", "v2", "en", "api/changes");
        var output = Path.Combine(workspace.Root, "out");
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.com/project/" }, [], output, true, new() { Template = new DocsSiteTemplate { EnableSearch = true } },
            new() { Extensions = [api], BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
        var target = api.Resolve(new("library", "v1", "en", "M:Example.Box`1.Get(System.Int32)"));
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, target.Route.RelativeOutputPath))).Contains("/project/api/v1/");
        var changes = await File.ReadAllTextAsync(Path.Combine(output, "api/changes/index.html"));
        await Assert.That(changes).Contains("deprecated");
        await Assert.That(changes).Contains("removed");
        await Assert.That(changes).Contains("added");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "search-index.json"))).Contains("List items");
        var searchPage = await File.ReadAllTextAsync(Path.Combine(output, "search.html"));
        await Assert.That(searchPage).DoesNotContain("/feed.xml");
        await Assert.That(searchPage).DoesNotContain("/archives.html");
        await File.WriteAllTextAsync(openApi, "{\"openapi\":\"3.1.0\",\"paths\":{},\"components\":{\"$ref\":\"https://example.com/remote.json\"}}");
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], output, false, null, new() { Extensions = [api] }, default)).Throws<ArgumentException>();
        await Assert.That(File.Exists(Path.Combine(output, "api/changes/index.html"))).IsTrue();
    }
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
