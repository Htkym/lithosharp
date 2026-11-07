using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;
using LithoSharp.Testing;

namespace LithoSharp.Tests;

public sealed class TypedDocsPresentationTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task CanonicalRoutesAndPublicationDriveHomeSidebarPaginationAndSameParseToc()
    {
        using var workspace = new TemporaryWorkspace();
        var entries = new[]
        {
            Entry("nested/first.md", "First", "reference/start/", 1, body: "# Body title\n\n## Repeated & heading\n\n## Repeated & heading\n"),
            Entry("second.md", "Second", "different/second.html", 2),
            Entry("unlisted.md", "Unlisted", "private/direct/", 3, unlisted: true),
            Entry("excluded.md", "Excluded", "direct/excluded/", 4, navigation: false),
            Entry("draft.md", "Draft", "draft/", 0, draft: true),
        };
        var projected = new List<string>();
        var collection = Collection(workspace.Root, "guides", entries).RenderDocs(entry =>
        {
            projected.Add(entry.Id.Value);
            return Presentation(entry);
        }, static (entry, context) => context.RenderMarkdown(entry.Body), "docs:v1");
        var generator = new SiteGenerator();
        var output = Path.Combine(workspace.Root, "output");
        var result = await Generate(generator, output, [collection]);

        await Assert.That(projected).DoesNotContain("draft.md");
        await Assert.That(File.Exists(Path.Combine(output, "draft", "index.html"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(output, "private", "direct", "index.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, "direct", "excluded", "index.html"))).IsTrue();
        await Assert.That(result.GeneratedFiles.Count(path => path == Path.Combine(output, "index.html"))).IsEqualTo(1);
        using var home = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "index.html")));
        await Assert.That(home.Snapshot.Query(".docs-primary-link")?.GetAttribute("href"))
            .IsEqualTo("/base/reference/start/");
        await Assert.That(home.Snapshot.Query(".docs-topic-cards")?.TextContent).Contains("Summary Second");
        using var first = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "reference", "start", "index.html")));
        var nav = first.Snapshot.Query("#docs-sidebar")!;
        await Assert.That(nav.TextContent).Contains("First");
        await Assert.That(nav.TextContent).Contains("Second");
        await Assert.That(nav.TextContent).DoesNotContain("Unlisted");
        await Assert.That(nav.TextContent).DoesNotContain("Excluded");
        await Assert.That(first.Snapshot.Query("[aria-current=page]")?.GetAttribute("href"))
            .IsEqualTo("/base/reference/start/");
        await Assert.That(first.Snapshot.Query(".docs-pagination a")?.GetAttribute("href"))
            .IsEqualTo("/base/different/second.html");
        var tocTargets = first.Snapshot.QueryAll(".docs-toc a").Select(anchor => anchor.GetAttribute("href")![1..]).ToArray();
        await Assert.That(tocTargets.Length).IsEqualTo(3);
        await Assert.That(tocTargets.Distinct().Count()).IsEqualTo(3);
        foreach (var id in tocTargets)
            await Assert.That(first.Snapshot.Query($"[id='{id}']")).IsNotNull();
        await Assert.That(first.Snapshot.QueryAll("h1").Count).IsEqualTo(1);
        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(4);
    }

    [Test]
    public async Task ExplicitParentsAndVersionLocaleCollectionBoundariesControlAdjacentLinks()
    {
        using var workspace = new TemporaryWorkspace();
        var parent = Entry("parent.md", "Parent", "v1/en/parent/", 1, version: "v1", locale: "en");
        var child = Entry("other/child.md", "Child", "v1/en/child/", 2, version: "v1", locale: "en", parentId: "parent.md");
        var otherVersion = Entry("v2.md", "Version two", "v2/en/parent/", 1, version: "v2", locale: "en");
        var otherLocale = Entry("ja.md", "Japanese", "v1/ja/parent/", 1, version: "v1", locale: "ja");
        var otherCollection = Entry("other.md", "Other collection", "other/", 1, version: "v1", locale: "en");
        var output = Path.Combine(workspace.Root, "output");
        await Generate(new SiteGenerator(), output,
            [Docs(Collection(workspace.Root, "guides", [parent, child, otherVersion, otherLocale])),
             Docs(Collection(workspace.Root, "other", [otherCollection]))]);
        using var document = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "v1", "en", "child", "index.html")));
        var sidebar = document.Snapshot.Query("#docs-sidebar")!.TextContent;
        await Assert.That(sidebar).Contains("Parent");
        await Assert.That(sidebar).Contains("Child");
        await Assert.That(sidebar).DoesNotContain("Version two");
        await Assert.That(sidebar).DoesNotContain("Japanese");
        await Assert.That(sidebar).DoesNotContain("Other collection");
        await Assert.That(document.Snapshot.Query("details[open] > summary")?.TextContent).IsEqualTo("Parent");
        await Assert.That(document.Snapshot.Query(".docs-pagination a")?.GetAttribute("href"))
            .IsEqualTo("/base/v1/en/parent/");
        var canonical = new ContentCollection<Card, string>(new ContentCollectionId("canonical"), workspace.Root,
            [parent, child, otherVersion, otherLocale],
            static entry => SiteRoute.ForDirectoryIndex(entry.FrontMatter.Route),
            static entry => new PageMetadata(entry.FrontMatter.Heading)
            {
                Document = new LithoSharp.Documentation.DocumentKey("guides", entry.FrontMatter.Version!,
                    entry.FrontMatter.Locale!, entry.Id.Value),
            });
        await Generate(new SiteGenerator(), output,
            [canonical.RenderDocs(entry => Presentation(entry) with { Version = null, Locale = null },
                static (entry, context) => context.RenderMarkdown(entry.Body))]);
        using var canonicalPage = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "v1", "en", "child", "index.html")));
        await Assert.That(canonicalPage.Snapshot.Query("#docs-sidebar")!.TextContent).DoesNotContain("Version two");
        await Assert.That(canonicalPage.Snapshot.Query("#docs-sidebar")!.TextContent).DoesNotContain("Japanese");
        await Assert.That(async () => await Generate(new SiteGenerator(), output,
            [canonical.RenderDocs(entry => Presentation(entry) with { Version = "combined" },
                static (entry, context) => context.RenderMarkdown(entry.Body))], clean: false)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task PresentationRouteAndMembershipChangesInvalidateHomeAndNeighborsWithCleanEquivalentOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "incremental");
        var initial = new[] { Entry("one.md", "One", "one/", 1), Entry("two.md", "Two", "two/", 2) };
        await Generate(new SiteGenerator(), output, [Docs(Collection(workspace.Root, "guides", initial))]);
        var warm = await Generate(new SiteGenerator(), output, [Docs(Collection(workspace.Root, "guides", initial))], clean: false);
        await Assert.That(warm.BuildReport.CacheMissCount).IsEqualTo(0);
        // Source fingerprints deliberately stay fixed: selectors and canonical routes are independent graph inputs.
        var changed = new[] { initial[0], Entry("two.md", "Renamed", "new-location/", 0),
            Entry("three.md", "New page", "three/", 3) };
        var incremental = await Generate(new SiteGenerator(), output, [Docs(Collection(workspace.Root, "guides", changed))], clean: false);
        await Assert.That(incremental.BuildReport.Nodes.Single(node => node.NodeId == "page:index").CacheHit).IsFalse();
        await Assert.That(incremental.BuildReport.Nodes.Single(node => node.NodeId.EndsWith(":6:one.md", StringComparison.Ordinal)).CacheHit).IsFalse();
        var cleanOutput = Path.Combine(workspace.Root, "clean");
        await Generate(new SiteGenerator(), cleanOutput, [Docs(Collection(workspace.Root, "guides", changed))]);
        await Assert.That(await Snapshot(output)).IsEquivalentTo(await Snapshot(cleanOutput));
        await Assert.That(File.Exists(Path.Combine(output, "two", "index.html"))).IsFalse();
        var hidden = changed.Select(entry => entry.Id.Value == "two.md"
            ? Entry("two.md", "Renamed", "new-location/", 0, unlisted: true) : entry).ToArray();
        await Generate(new SiteGenerator(), output, [Docs(Collection(workspace.Root, "guides", hidden))], clean: false);
        using var home = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "index.html")));
        await Assert.That(home.Snapshot.Query(".docs-primary-link")?.GetAttribute("href")).IsEqualTo("/base/one/");
        await Assert.That(home.Snapshot.Query("#docs-sidebar")!.TextContent).DoesNotContain("Renamed");
        var customization = new SiteCustomization
        {
            Template = new DocsSiteTemplate { ShowTopicCards = true, CollapseTopics = false, EnableHeaderSearch = true },
            Footer = new SiteFooterOptions { Text = "Updated footer" },
            ExtraPages = [new SiteExtraPage { RelativePath = "notes.html", Title = "Notes", NavLabel = "Release notes", BodyHtml = "<h1>Notes</h1>" }],
        };
        var layoutChanged = await Generate(new SiteGenerator(), output, [Docs(Collection(workspace.Root, "guides", hidden))],
            clean: false, customization);
        await Assert.That(layoutChanged.BuildReport.Nodes.Single(node => node.NodeId.EndsWith(":6:one.md", StringComparison.Ordinal)).CacheHit).IsFalse();
        await Generate(new SiteGenerator(), cleanOutput, [Docs(Collection(workspace.Root, "guides", hidden))],
            clean: true, customization);
        await Assert.That(await Snapshot(output)).IsEquivalentTo(await Snapshot(cleanOutput));
        using var changedPage = SiteTestDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "one", "index.html")));
        await Assert.That(changedPage.Snapshot.Query("#docs-sidebar")!.TextContent).Contains("Release notes");
        await Assert.That(changedPage.Snapshot.Query(".site-footer-additional")!.TextContent).Contains("Updated footer");
        await Assert.That(changedPage.Snapshot.Query(".site-header-search")).IsNotNull();
    }

    [Test]
    public async Task GenericTypedRenderingRemainsOptInAndParentCyclesFailWithoutPublishing()
    {
        using var workspace = new TemporaryWorkspace();
        var entry = Entry("one.md", "One", "one/", 1);
        var output = Path.Combine(workspace.Root, "output");
        var generic = new SiteContentCollection<Card, string>(Collection(workspace.Root, "guides", [entry]),
            static (row, context) => context.RenderDocument(context.RenderMarkdown(row.Body)));
        await Generate(new SiteGenerator(), output, [generic]);
        var original = await File.ReadAllTextAsync(Path.Combine(output, "one", "index.html"));
        await Assert.That(original).DoesNotContain("class=\"docs-pagination\"");
        var cycle = new[] { Entry("one.md", "One", "one/", 1, parentId: "two.md"),
            Entry("two.md", "Two", "two/", 2, parentId: "one.md") };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Generate(new SiteGenerator(), output, [Docs(Collection(workspace.Root, "guides", cycle))], clean: false));
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "one", "index.html"))).IsEqualTo(original);
    }

    private static SiteContentCollection<Card, string> Docs(ContentCollection<Card, string> collection) =>
        collection.RenderDocs(Presentation, static (entry, context) => context.RenderMarkdown(entry.Body), "docs:v1", true);

    private static DocsPagePresentation Presentation(ContentEntry<Card, string> entry) => new()
    {
        Title = entry.FrontMatter.Heading,
        Description = "Summary " + entry.FrontMatter.Heading,
        Order = entry.FrontMatter.Position,
        ParentId = entry.FrontMatter.ParentId is { } parent ? new ContentEntryId(parent) : null,
        Version = entry.FrontMatter.Version,
        Locale = entry.FrontMatter.Locale,
        Unlisted = entry.FrontMatter.Unlisted,
    };

    private static ContentCollection<Card, string> Collection(string root, string id, IEnumerable<ContentEntry<Card, string>> entries) =>
        new(new ContentCollectionId(id), root, entries,
            static entry => entry.FrontMatter.Route.EndsWith('/')
                ? SiteRoute.ForDirectoryIndex(entry.FrontMatter.Route)
                : SiteRoute.ForFile(entry.FrontMatter.Route),
            static entry => new PageMetadata(entry.FrontMatter.Heading, draft: entry.FrontMatter.Draft),
            transformationId: new ContentTransformationId("docs-source:v1"), isCacheable: true);

    private static ContentEntry<Card, string> Entry(string id, string title, string route, int order,
        bool draft = false, bool unlisted = false, bool navigation = true, string? version = null,
        string? locale = null, string? parentId = null, string body = "## Section\n\nBody.") =>
        new(new ContentEntryId(id), id, "source:" + id,
            new Card(title, route, order, draft, unlisted, version, locale, parentId), body)
        { DerivedSurfaces = navigation ? GeneratedPageDerivedSurfaces.All : GeneratedPageDerivedSurfaces.All & ~GeneratedPageDerivedSurfaces.Navigation };

    private static Task<SiteGenerationResult> Generate(SiteGenerator generator, string output,
        IReadOnlyList<SiteContentCollection> collections, bool clean = true, SiteCustomization? customization = null) =>
        generator.GenerateWithOptionsAsync(new SiteSettings
        { Title = "Typed Docs", Description = "A typed documentation site.", BaseUrl = "https://example.test/base/", TimeZone = "UTC" },
            [], output, clean, customization ?? new SiteCustomization
            { Template = new DocsSiteTemplate { ShowTopicCards = true, CollapseTopics = true }, FaviconSourceDirectory = Path.Combine(Path.GetDirectoryName(output)!, "no-favicon") },
            new SiteGenerationOptions
            {
                BuildTimestamp = Timestamp,
                ContentCollections = collections,
                Quality = new SiteQualityOptions(),
                MaxDegreeOfParallelism = 2
            }, CancellationToken.None);

    private static async Task<string[]> Snapshot(string root) =>
        await Task.WhenAll(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) != ".lithosharp-output-manifest.json")
            .Select(async file => Path.GetRelativePath(root, file).Replace('\\', '/') + ":"
                + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(file)))));

    private sealed record Card(string Heading, string Route, int Position, bool Draft, bool Unlisted,
        string? Version, string? Locale, string? ParentId);
}
