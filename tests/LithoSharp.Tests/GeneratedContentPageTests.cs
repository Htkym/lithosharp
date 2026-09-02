using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class GeneratedContentPageTests
{
    private static readonly DateTimeOffset BuildTimestamp =
        new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task GeneratePages_CreatesTagsYearsCategoriesAndCustomGroupsDeterministically()
    {
        using var workspace = new TemporaryWorkspace();
        var entries = new[]
        {
            Entry("z", "Zulu", 2025, "news", ["dotnet", "csharp"], published: true),
            Entry("a", "Alpha", 2024, "guide", ["csharp"], published: true),
            Entry("draft", "Draft", 2023, "hidden", ["private"], published: false),
        };
        var firstCollection = Collection(workspace.Root, entries);
        var secondCollection = Collection(workspace.Root, entries.Reverse());
        var firstRegistrations = Registrations(firstCollection).ToArray();
        var secondRegistrations = Registrations(secondCollection).Reverse().ToArray();

        var first = Path.Combine(workspace.Root, "first");
        var second = Path.Combine(workspace.Root, "second");
        await GenerateAsync(first, firstRegistrations);
        await GenerateAsync(second, secondRegistrations);

        await Assert.That(File.Exists(Path.Combine(first, "generated", "tags", "csharp.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(first, "generated", "tags", "dotnet.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(first, "generated", "years", "2024.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(first, "generated", "years", "2025.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(first, "generated", "categories", "guide.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(first, "generated", "custom", "a.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(first, "generated", "custom", "empty.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(first, "generated", "tags", "private.html"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(
            Path.Combine(first, "generated", "tags", "csharp.html"))).Contains("a,z");
        await Assert.That(await File.ReadAllTextAsync(
            Path.Combine(first, "generated", "custom", "empty.html"))).Contains("empty:");

        var firstFiles = await ReadTextFilesAsync(first);
        var secondFiles = await ReadTextFilesAsync(second);
        await Assert.That(firstFiles.SequenceEqual(secondFiles)).IsTrue();
    }

    [Test]
    public async Task GeneratePages_NormalizesUnicodeAndKeepsCaseDistinct()
    {
        using var workspace = new TemporaryWorkspace();
        var collection = Collection(
            workspace.Root,
            [
                Entry("one", "One", 2025, "news", ["cafe\u0301", "Tag", "Tag"], published: true),
                Entry("two", "Two", 2025, "news", ["caf\u00E9", "tag"], published: true),
            ]);
        var generated = collection.GeneratePages(
            new ContentCollectionId("unicode-groups"),
            static entry => entry.FrontMatter.Tags,
            static group => new SitePage<GroupContent>(
                new PageId($"group:{group.Key}"),
                SiteRoute.ForFile(group.Key switch
                {
                    "Tag" => "unicode/upper.html",
                    "tag" => "unicode/lower.html",
                    _ => "unicode/cafe.html",
                }),
                new GroupContent(group.Key, group.Entries.Select(entry => entry.Id.Value).ToArray()),
                new PageMetadata(group.Key)),
            RenderGroup,
            transformationId: new ContentTransformationId("unicode:v1"));

        var output = Path.Combine(workspace.Root, "output");
        await GenerateAsync(output, [generated]);

        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "unicode", "cafe.html")))
            .Contains("café:one,two");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "unicode", "upper.html")))
            .Contains("Tag:one");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "unicode", "lower.html")))
            .Contains("tag:two");
    }

    [Test]
    [Arguments("draft")]
    [Arguments("expired")]
    [Arguments("environment")]
    public async Task GeneratePages_ReportsStableIdentityWhenGeneratedPageIsUnpublished(
        string transition)
    {
        using var workspace = new TemporaryWorkspace();
        var collection = Collection(
            workspace.Root,
            [Entry("one", "One", 2025, "news", ["dotnet"], published: true)]);
        var hiddenMetadata = transition switch
        {
            "draft" => new PageMetadata("Hidden", draft: true),
            "expired" => new PageMetadata("Hidden", publishUntil: BuildTimestamp),
            "environment" => new PageMetadata("Hidden", environments: ["Staging"]),
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };
        var generated = collection.GeneratePages(
            new ContentCollectionId("publication-groups"),
            static entry => entry.FrontMatter.Tags,
            group => new SitePage<GroupContent>(
                new PageId($"publication-groups:{group.Key}"),
                SiteRoute.ForFile($"hidden/{group.Key}.html"),
                new GroupContent(group.Key, []),
                hiddenMetadata),
            RenderGroup);

        var result = await GenerateAsync(Path.Combine(workspace.Root, "output"), [generated]);

        await Assert.That(result.BuildReport.UnpublishedPages)
            .IsEquivalentTo(
                ["page:collection:18:publication-groups:25:publication-groups:dotnet"]);
    }

    [Test]
    public async Task GeneratePages_UsesSafeDefaultDerivedSurfaces()
    {
        using var workspace = new TemporaryWorkspace();
        var faviconRoot = Path.Combine(workspace.Root, "favicon");
        Directory.CreateDirectory(faviconRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(faviconRoot, "android-chrome-192x192.png"),
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        var collection = Collection(
            workspace.Root,
            [Entry("one", "One", 2025, "news", ["dotnet"], published: true)]);
        var generated = GroupPages(
            collection,
            "surface-groups",
            "surface",
            static entry => entry.FrontMatter.Tags);
        var output = Path.Combine(workspace.Root, "output");

        await GenerateAsync(
            output,
            [generated],
            faviconRoot,
            generateLlmsTxt: true);

        const string publicPath = "/base/surface/dotnet.html";
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "index.html")))
            .DoesNotContain(publicPath);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "search-index.json")))
            .Contains(publicPath);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "feed.xml")))
            .DoesNotContain(publicPath);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "sitemap.xml")))
            .Contains(publicPath);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "llms.txt")))
            .Contains(publicPath);
        await Assert.That(Directory.EnumerateFiles(
            Path.Combine(output, "assets", "social", "content"), "*.png").Count()).IsEqualTo(1);
    }

    [Test]
    public async Task GeneratePages_CanOptIntoAllDerivedSurfaces()
    {
        using var workspace = new TemporaryWorkspace();
        var faviconRoot = await CreateSocialImageSourceAsync(workspace.Root);
        var collection = Collection(
            workspace.Root,
            [Entry("one", "One", 2025, "news", ["dotnet"], published: true)]);
        var generated = GroupPages(
            collection,
            "surface-groups",
            "surface",
            static entry => entry.FrontMatter.Tags,
            derivedSurfaces: GeneratedPageDerivedSurfaces.All);
        var output = Path.Combine(workspace.Root, "output");

        await GenerateAsync(
            output,
            [generated],
            faviconRoot,
            generateLlmsTxt: true);

        const string publicPath = "/base/surface/dotnet.html";
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "index.html")))
            .Contains(publicPath);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "search-index.json")))
            .Contains(publicPath);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "feed.xml")))
            .Contains(publicPath);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "sitemap.xml")))
            .Contains(publicPath);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "llms.txt")))
            .Contains(publicPath);
        await Assert.That(Directory.EnumerateFiles(
            Path.Combine(output, "assets", "social", "content"), "*.png").Count()).IsEqualTo(1);
    }

    [Test]
    public async Task GeneratePages_CanOptOutOfAllDerivedSurfaces()
    {
        using var workspace = new TemporaryWorkspace();
        var faviconRoot = await CreateSocialImageSourceAsync(workspace.Root);
        var collection = Collection(
            workspace.Root,
            [Entry("one", "One", 2025, "news", ["dotnet"], published: true)]);
        var generated = GroupPages(
            collection,
            "surface-groups",
            "surface",
            static entry => entry.FrontMatter.Tags,
            derivedSurfaces: GeneratedPageDerivedSurfaces.None);
        var output = Path.Combine(workspace.Root, "output");

        await GenerateAsync(
            output,
            [generated],
            faviconRoot,
            generateLlmsTxt: true);

        const string publicPath = "/base/surface/dotnet.html";
        foreach (var relativePath in new[]
                 {
                     "index.html",
                     "search-index.json",
                     "feed.xml",
                     "sitemap.xml",
                     "llms.txt",
                 })
        {
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, relativePath)))
                .DoesNotContain(publicPath);
        }

        await Assert.That(Directory.Exists(
            Path.Combine(output, "assets", "social", "content"))).IsFalse();
    }

    [Test]
    public async Task GeneratePages_DefaultRssExclusionDoesNotConsumeFeedBudget()
    {
        using var workspace = new TemporaryWorkspace();
        var aggregateSource = Collection(
            workspace.Root,
            Enumerable.Range(1, 20)
                .Select(index => Entry(
                    $"aggregate-{index:D2}",
                    $"Aggregate {index:D2}",
                    2025,
                    "news",
                    [$"tag-{index:D2}"],
                    published: true)),
            id: "aggregate-source");
        var generated = GroupPages(
            aggregateSource,
            "a-aggregates",
            "aggregates",
            static entry => entry.FrontMatter.Tags);
        var ordinaryCollection = Collection(
            workspace.Root,
            [Entry("ordinary", "Ordinary", 2025, "news", [], published: true)],
            id: "z-ordinary");
        var ordinary = new SiteContentCollection<Article, string>(
            ordinaryCollection,
            static (entry, context) => context.RenderDocument(entry.Body));
        var output = Path.Combine(workspace.Root, "output");

        await GenerateAsync(output, [generated, ordinary]);

        var feed = await File.ReadAllTextAsync(Path.Combine(output, "feed.xml"));
        await Assert.That(feed).Contains("/base/entries/ordinary.html");
        await Assert.That(feed).DoesNotContain("/base/aggregates/");
    }

    [Test]
    public async Task GeneratePages_RemovesStaleOwnedGroupArtifactsWithCleanFalse()
    {
        using var workspace = new TemporaryWorkspace();
        var faviconRoot = Path.Combine(workspace.Root, "favicon");
        Directory.CreateDirectory(faviconRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(faviconRoot, "android-chrome-192x192.png"),
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        var output = Path.Combine(workspace.Root, "output");
        var published = Collection(
            workspace.Root,
            [Entry("one", "One", 2025, "news", ["dotnet"], published: true)]);
        await GenerateAsync(
            output,
            [GroupPages(published, "owned-groups", "owned", static entry => entry.FrontMatter.Tags)],
            faviconRoot);
        var pagePath = Path.Combine(output, "owned", "dotnet.html");
        var socialPath = Directory.EnumerateFiles(
            Path.Combine(output, "assets", "social", "content"), "*.png").Single();
        var userPath = Path.Combine(output, "user.txt");
        await File.WriteAllTextAsync(userPath, "keep");

        var unpublished = Collection(
            workspace.Root,
            [Entry("one", "One", 2025, "news", ["dotnet"], published: false)]);
        await GenerateAsync(
            output,
            [GroupPages(unpublished, "owned-groups", "owned", static entry => entry.FrontMatter.Tags)],
            faviconRoot,
            clean: false);

        await Assert.That(File.Exists(pagePath)).IsFalse();
        await Assert.That(File.Exists(socialPath)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(userPath)).IsEqualTo("keep");
    }

    [Test]
    public async Task GeneratePages_ReportsDuplicateIdsRoutesAndInvalidKeysBeforeOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var collection = Collection(
            workspace.Root,
            [
                Entry("one", "One", 2025, "news", ["valid", " "], published: true),
                Entry("two", "Two", 2025, "news", ["other"], published: true),
            ]);
        var duplicateIds = collection.GeneratePages(
            new ContentCollectionId("duplicate-ids"),
            static entry => entry.FrontMatter.Tags,
            static group => new SitePage<GroupContent>(
                new PageId("same"),
                SiteRoute.ForFile($"ids/{group.Key}.html"),
                new GroupContent(group.Key, []),
                new PageMetadata(group.Key)),
            RenderGroup,
            transformationId: new ContentTransformationId("duplicate-ids:v1"));
        var duplicateRoutes = collection.GeneratePages(
            new ContentCollectionId("duplicate-routes"),
            static entry => entry.FrontMatter.Tags.Where(static tag => !string.IsNullOrWhiteSpace(tag)),
            static group => new SitePage<GroupContent>(
                new PageId(group.Key),
                SiteRoute.ForFile("routes/same.html"),
                new GroupContent(group.Key, []),
                new PageMetadata(group.Key)),
            RenderGroup,
            transformationId: new ContentTransformationId("duplicate-routes:v1"));
        var output = Path.Combine(workspace.Root, "output");

        SiteRouteValidationException? failure = null;
        try
        {
            await GenerateAsync(output, [duplicateIds, duplicateRoutes]);
        }
        catch (SiteRouteValidationException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Diagnostics.Select(diagnostic => diagnostic.Id))
            .Contains(GeneratedPageDiagnosticIds.InvalidGroupKey);
        await Assert.That(failure.Diagnostics.Select(diagnostic => diagnostic.Id))
            .Contains(GeneratedPageDiagnosticIds.DuplicatePageId);
        await Assert.That(failure.Diagnostics.Select(diagnostic => diagnostic.Id))
            .Contains(SiteRouteDiagnosticIds.DuplicateOutputPath);
        await Assert.That(Directory.Exists(output)).IsFalse();
    }

    [Test]
    public async Task GeneratePages_DoesNotConvertFactoryExceptionsIntoRouteDiagnostics()
    {
        using var workspace = new TemporaryWorkspace();
        var collection = Collection(
            workspace.Root,
            [Entry("one", "One", 2025, "news", ["dotnet"], published: true)]);
        var generated = collection.GeneratePages<GroupContent>(
            new ContentCollectionId("factory-failure"),
            static entry => entry.FrontMatter.Tags,
            static _ => throw new ArgumentException("Factory context was preserved.", "group"),
            RenderGroup,
            transformationId: new ContentTransformationId("factory-failure:v1"));
        var output = Path.Combine(workspace.Root, "output");

        ArgumentException? failure = null;
        try
        {
            await GenerateAsync(output, [generated]);
        }
        catch (ArgumentException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.ParamName).IsEqualTo("group");
        await Assert.That(failure.Message).Contains("Factory context was preserved.");
        await Assert.That(Directory.Exists(output)).IsFalse();
    }

    [Test]
    public async Task GeneratePages_InvalidatesForMembershipContentRouteLayoutAndTransformations()
    {
        using var workspace = new TemporaryWorkspace();
        var baseline = await GeneratePlanAsync(
            workspace.Root,
            "baseline",
            [
                Entry("one", "One", 2025, "target", ["tag"], published: true, body: "before"),
                Entry("two", "Two", 2025, "other", ["tag"], published: true),
            ],
            routePrefix: "aggregates",
            layout: "layout:v1",
            sourceTransformation: "source:v1",
            generatedTransformation: "generated:v1");
        var membership = await GeneratePlanAsync(
            workspace.Root,
            "membership",
            [
                Entry("one", "One", 2025, "target", ["tag"], published: true, body: "before"),
                Entry("two", "Two", 2025, "target", ["tag"], published: true),
            ],
            routePrefix: "aggregates",
            layout: "layout:v1",
            sourceTransformation: "source:v1",
            generatedTransformation: "generated:v1");
        var content = await GeneratePlanAsync(
            workspace.Root,
            "content",
            [
                Entry("one", "One", 2025, "target", ["tag"], published: true, body: "after"),
                Entry("two", "Two", 2025, "other", ["tag"], published: true),
            ],
            routePrefix: "aggregates",
            layout: "layout:v1",
            sourceTransformation: "source:v1",
            generatedTransformation: "generated:v1");
        var route = await GeneratePlanAsync(
            workspace.Root,
            "route",
            [
                Entry("one", "One", 2025, "target", ["tag"], published: true, body: "before"),
                Entry("two", "Two", 2025, "other", ["tag"], published: true),
            ],
            routePrefix: "changed",
            layout: "layout:v1",
            sourceTransformation: "source:v1",
            generatedTransformation: "generated:v1");
        var layout = await GeneratePlanAsync(
            workspace.Root,
            "layout",
            [
                Entry("one", "One", 2025, "target", ["tag"], published: true, body: "before"),
                Entry("two", "Two", 2025, "other", ["tag"], published: true),
            ],
            routePrefix: "aggregates",
            layout: "layout:v2",
            sourceTransformation: "source:v1",
            generatedTransformation: "generated:v1");
        var transformations = await GeneratePlanAsync(
            workspace.Root,
            "transformations",
            [
                Entry("one", "One", 2025, "target", ["tag"], published: true, body: "before"),
                Entry("two", "Two", 2025, "other", ["tag"], published: true),
            ],
            routePrefix: "aggregates",
            layout: "layout:v1",
            sourceTransformation: "source:v2",
            generatedTransformation: "generated:v2");

        var targetNode = baseline.BuildPlan.Nodes.Single(node =>
            node.Artifacts.Any(artifact => artifact.RelativeOutputPath == "aggregates/target.html"));
        foreach (var current in new[] { membership, content, route, layout, transformations })
        {
            await Assert.That(current.BuildPlan.GetInvalidatedNodes(baseline.BuildPlan)
                .Select(invalidation => invalidation.NodeId)).Contains(targetNode.Id);
        }

        var transformedNode = transformations.BuildPlan.Nodes.Single(node => node.Id.Equals(targetNode.Id));
        await Assert.That(transformedNode.Inputs.Single(input =>
            input.Key == "content.sourceTransformation").Value).IsEqualTo("source:v2");
        await Assert.That(transformedNode.Inputs.Single(input =>
            input.Key == "content.transformation").Value).IsEqualTo("generated:v2");
        await Assert.That(transformedNode.Inputs.Any(input =>
            input.Key == "content.source:00000000")).IsTrue();
        await Assert.That(transformedNode.Inputs.Any(input =>
            input.Key == "content.dependency:locale")).IsTrue();
    }

    [Test]
    public async Task GeneratePages_DerivedSurfacePolicyInvalidatesOnlyAffectedSurfaceAndPage()
    {
        using var workspace = new TemporaryWorkspace();
        var collection = Collection(
            workspace.Root,
            [Entry("one", "One", 2025, "news", ["dotnet"], published: true)]);
        var baseline = await GenerateAsync(
            Path.Combine(workspace.Root, "baseline"),
            [
                GroupPages(
                    collection,
                    "policy-groups",
                    "policy",
                    static entry => entry.FrontMatter.Tags),
            ],
            generateLlmsTxt: true);
        var currentSurfaces =
            GeneratedPageDerivedSurfaces.Default | GeneratedPageDerivedSurfaces.Rss;
        var current = await GenerateAsync(
            Path.Combine(workspace.Root, "current"),
            [
                GroupPages(
                    collection,
                    "policy-groups",
                    "policy",
                    static entry => entry.FrontMatter.Tags,
                    derivedSurfaces: currentSurfaces),
            ],
            generateLlmsTxt: true);

        var pageNode = current.BuildPlan.Nodes.Single(node =>
            node.Artifacts.Any(artifact => artifact.RelativeOutputPath == "policy/dotnet.html"));
        await Assert.That(pageNode.Inputs.Single(input =>
            input.Key == "content.derivedSurfaces").Value)
            .IsEqualTo(((int)currentSurfaces).ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        var invalidated = current.BuildPlan.GetInvalidatedNodes(baseline.BuildPlan)
            .Select(item => item.NodeId.Value)
            .ToArray();
        await Assert.That(invalidated).Contains(pageNode.Id.Value);
        await Assert.That(invalidated).Contains("feed:rss");
        await Assert.That(invalidated).DoesNotContain("index:search");
        await Assert.That(invalidated).DoesNotContain("index:sitemap");
        await Assert.That(invalidated).DoesNotContain("text:llms");
    }

    [Test]
    public async Task GeneratePages_RssFallbackTimestampParticipatesInInvalidation()
    {
        using var workspace = new TemporaryWorkspace();
        var collection = Collection(
            workspace.Root,
            [Entry("one", "One", 2025, "news", ["dotnet"], published: true)]);
        var generated = GroupPages(
            collection,
            "rss-groups",
            "rss",
            static entry => entry.FrontMatter.Tags,
            derivedSurfaces: GeneratedPageDerivedSurfaces.Rss);
        var firstTimestamp =
            new DateTimeOffset(2026, 9, 2, 19, 0, 0, TimeSpan.FromHours(9));
        var secondTimestamp = firstTimestamp.AddHours(1);
        var baseline = await GenerateAsync(
            Path.Combine(workspace.Root, "baseline"),
            [generated],
            buildTimestamp: firstTimestamp);
        var current = await GenerateAsync(
            Path.Combine(workspace.Root, "current"),
            [generated],
            buildTimestamp: secondTimestamp);

        var feedNode = current.BuildPlan.Nodes.Single(node => node.Id.Value == "feed:rss");
        await Assert.That(feedNode.Inputs.Single(input =>
            input.Key == "build.timestamp").Value)
            .IsEqualTo(secondTimestamp.ToUniversalTime().ToString(
                "O",
                System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(current.BuildPlan.GetInvalidatedNodes(baseline.BuildPlan)
            .Single(item => item.NodeId.Value == "feed:rss").Reasons)
            .Contains("入力 'Configuration:build.timestamp' が変更されました。");
    }

    [Test]
    public async Task GeneratePages_PreservesTypedEntryAndLegacyGenerationBehavior()
    {
        using var workspace = new TemporaryWorkspace();
        var collection = Collection(
            workspace.Root,
            [Entry("one", "One", 2025, "news", ["dotnet"], published: true)]);
        var source = new SiteContentCollection<Article, string>(
            collection,
            static (entry, context) => context.RenderDocument(entry.Body));
        var generated = GroupPages(
            collection,
            "compat-groups",
            "groups",
            static entry => entry.FrontMatter.Tags);
        var output = Path.Combine(workspace.Root, "output");

        var result = await GenerateAsync(output, [source, generated]);

        await Assert.That(File.Exists(Path.Combine(output, "entries", "one.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, "groups", "dotnet.html"))).IsTrue();
        await Assert.That(result.PostCount).IsEqualTo(0);
        await Assert.That(result.BuildPlan.Nodes.Count(node =>
            node.Id.Value.StartsWith("page:collection:", StringComparison.Ordinal))).IsEqualTo(2);

        var legacyOutput = Path.Combine(workspace.Root, "legacy");
        var legacy = await new SiteGenerator().GenerateAsync(
            Site(),
            [],
            legacyOutput,
            clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() });
        await Assert.That(legacy.PostCount).IsEqualTo(0);
        await Assert.That(legacy.BuildPlan.Nodes.Any(node =>
            node.Id.Value.StartsWith("page:collection:", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task GeneratePages_RejectsAmbiguousOrUnsafeCacheableDefinitions()
    {
        using var workspace = new TemporaryWorkspace();
        var nonCacheable = Collection(
            workspace.Root,
            [Entry("one", "One", 2025, "news", ["dotnet"], published: true)],
            isCacheable: false);

        await Assert.That(() => GroupPages(
            nonCacheable,
            "generated",
            "groups",
            static entry => entry.FrontMatter.Tags,
            isCacheable: true)).Throws<ArgumentException>();
        await Assert.That(() => nonCacheable.GeneratePages(
            nonCacheable.Id,
            static entry => entry.FrontMatter.Tags,
            static group => new SitePage<GroupContent>(
                new PageId(group.Key),
                SiteRoute.ForFile($"groups/{group.Key}.html"),
                new GroupContent(group.Key, []),
                new PageMetadata(group.Key)),
            RenderGroup)).Throws<ArgumentException>();
    }

    private async Task<SiteGenerationResult> GeneratePlanAsync(
        string root,
        string directory,
        IReadOnlyList<ContentEntry<Article, string>> entries,
        string routePrefix,
        string layout,
        string sourceTransformation,
        string generatedTransformation)
    {
        var collection = Collection(
            root,
            entries,
            sourceTransformation: sourceTransformation);
        var generated = collection.GeneratePages(
            new ContentCollectionId("invalidation-groups"),
            static entry => [entry.FrontMatter.Category],
            group => new SitePage<GroupContent>(
                new PageId($"group:{group.Key}"),
                SiteRoute.ForFile($"{routePrefix}/{group.Key}.html"),
                new GroupContent(
                    group.Key,
                    group.Entries.Select(entry => $"{entry.Id.Value}:{entry.Body}").ToArray()),
                new PageMetadata(group.Key)),
            RenderGroup,
            layoutId: new ContentLayoutId(layout),
            declaredDependencies: [ContentDependency.FromValue("locale", "ja")],
            transformationId: new ContentTransformationId(generatedTransformation),
            isCacheable: true);
        return await GenerateAsync(
            Path.Combine(root, directory),
            [generated]);
    }

    private static IEnumerable<SiteContentCollection> Registrations(
        ContentCollection<Article, string> collection)
    {
        yield return GroupPages(
            collection,
            "aggregate-tags",
            "generated/tags",
            static entry => entry.FrontMatter.Tags);
        yield return GroupPages(
            collection,
            "aggregate-years",
            "generated/years",
            static entry => [entry.FrontMatter.Year.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        yield return GroupPages(
            collection,
            "aggregate-categories",
            "generated/categories",
            static entry => [entry.FrontMatter.Category]);
        yield return GroupPages(
            collection,
            "aggregate-custom",
            "generated/custom",
            static entry => [entry.FrontMatter.Title[..1].ToLowerInvariant()],
            groupKeys: ["empty"]);
    }

    private static SiteContentCollection GroupPages(
        ContentCollection<Article, string> collection,
        string id,
        string routePrefix,
        ContentPageGroupSelector<Article, string> selector,
        IEnumerable<string>? groupKeys = null,
        bool isCacheable = false,
        GeneratedPageDerivedSurfaces derivedSurfaces =
            GeneratedPageDerivedSurfaces.Default) =>
        collection.GeneratePages(
            new ContentCollectionId(id),
            selector,
            group => new SitePage<GroupContent>(
                new PageId($"{id}:{group.Key}"),
                SiteRoute.ForFile($"{routePrefix}/{group.Key}.html"),
                new GroupContent(
                    group.Key,
                    group.Entries.Select(entry => entry.Id.Value).ToArray()),
                new PageMetadata(group.Key, $"Entries in {group.Key}")),
            RenderGroup,
            groupKeys,
            new ContentLayoutId($"{id}:layout:v1"),
            declaredDependencies: [ContentDependency.FromValue("grouping", id)],
            transformationId: new ContentTransformationId($"{id}:v1"),
            isCacheable: isCacheable,
            derivedSurfaces: derivedSurfaces);

    private static string RenderGroup(
        SitePage<GroupContent> page,
        ContentPageRenderingContext context) =>
        context.RenderDocument(
            $"{page.Content.Key}:{string.Join(',', page.Content.EntryIds)}");

    private static ContentCollection<Article, string> Collection(
        string inputRoot,
        IEnumerable<ContentEntry<Article, string>> entries,
        bool isCacheable = true,
        string sourceTransformation = "articles:v1",
        string id = "articles") =>
        new(
            new ContentCollectionId(id),
            inputRoot,
            entries,
            static entry => SiteRoute.ForFile($"entries/{entry.Id.Value}.html"),
            static entry => new PageMetadata(entry.FrontMatter.Title, draft: !entry.FrontMatter.Published),
            transformationId: isCacheable
                ? new ContentTransformationId(sourceTransformation)
                : null,
            isCacheable: isCacheable);

    private static ContentEntry<Article, string> Entry(
        string id,
        string title,
        int year,
        string category,
        IReadOnlyList<string> tags,
        bool published,
        string? body = null) =>
        new(
            new ContentEntryId(id),
            $"{id}.data",
            $"sha256:{id}:{body ?? title}:{category}",
            new Article(title, year, category, tags, published),
            body ?? $"Body {id}");

    private static async Task<SiteGenerationResult> GenerateAsync(
        string output,
        IReadOnlyList<SiteContentCollection> collections,
        string? faviconRoot = null,
        bool generateLlmsTxt = false,
        bool clean = true,
        DateTimeOffset? buildTimestamp = null) =>
        await new SiteGenerator().GenerateWithOptionsAsync(
            Site(),
            [],
            output,
            clean,
            new SiteCustomization
            {
                Template = new BlogSiteTemplate(),
                FaviconSourceDirectory = faviconRoot,
                GenerateLlmsTxt = generateLlmsTxt,
            },
            new SiteGenerationOptions
            {
                BuildTimestamp = buildTimestamp ?? BuildTimestamp,
                ContentCollections = collections,
            },
            CancellationToken.None);

    private static async Task<string> CreateSocialImageSourceAsync(string root)
    {
        var faviconRoot = Path.Combine(root, "favicon");
        Directory.CreateDirectory(faviconRoot);
        await File.WriteAllBytesAsync(
            Path.Combine(faviconRoot, "android-chrome-192x192.png"),
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        return faviconRoot;
    }

    private static SiteSettings Site() => new()
    {
        Title = "Generated pages",
        Description = "Generated page tests.",
        BaseUrl = "https://example.test/base/",
        Language = "en",
        TimeZone = "UTC",
    };

    private static async Task<IReadOnlyList<(string Path, string Content)>> ReadTextFilesAsync(
        string root)
    {
        var files = await Task.WhenAll(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) != ".png")
            .Select(async path => (
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                await File.ReadAllTextAsync(path))));
        return files.OrderBy(item => item.Item1, StringComparer.Ordinal).ToArray();
    }

    private sealed record Article(
        string Title,
        int Year,
        string Category,
        IReadOnlyList<string> Tags,
        bool Published);

    private sealed record GroupContent(string Key, IReadOnlyList<string> EntryIds);
}
