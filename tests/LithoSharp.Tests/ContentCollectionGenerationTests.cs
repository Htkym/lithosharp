using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class ContentCollectionGenerationTests
{
    private static readonly DateTimeOffset BuildTimestamp =
        new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task MarkdownCollection_PublishesAcrossBlogSurfacesAndFiltersDrafts()
    {
        using var workspace = new TemporaryWorkspace();
        var contentRoot = Path.Combine(workspace.Root, "content");
        var faviconRoot = Path.Combine(workspace.Root, "favicon");
        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory(faviconRoot);
        await File.WriteAllTextAsync(
            Path.Combine(contentRoot, "published.md"),
            """
            ---
            title: Published article
            description: Visible summary
            draft: false
            ---
            ## Visible body
            """);
        await File.WriteAllTextAsync(
            Path.Combine(contentRoot, "draft.md"),
            """
            ---
            title: Hidden article
            description: Hidden summary
            draft: true
            ---
            ## Hidden body
            """);
        await WriteSocialSourceAsync(faviconRoot);

        var load = await new MarkdownContentCollectionLoader<ArticleFrontMatter>(
            new ContentCollectionId("articles"),
            contentRoot,
            static entry => SiteRoute.ForDirectoryIndex(
                $"articles/{Path.GetFileNameWithoutExtension(entry.SourcePath)}"),
            static entry => new PageMetadata(
                entry.FrontMatter.Title,
                entry.FrontMatter.Description,
                entry.FrontMatter.Draft),
            layoutId: new ContentLayoutId("article:v1"),
            transformationId: new ContentTransformationId("article-render:v1"),
            isCacheable: true).LoadAsync();
        await Assert.That(load.IsSuccess).IsTrue();

        var output = Path.Combine(workspace.Root, "output");
        ContentLayoutId? renderedLayout = null;
        var result = await GenerateAsync(
            output,
            [],
            [
                new SiteContentCollection<ArticleFrontMatter, string>(
                    load.Collection!,
                    (entry, context) =>
                    {
                        renderedLayout = context.LayoutId;
                        return context.RenderDocument(
                            $"<h1>{Html.Encode(entry.FrontMatter.Title)}</h1>{context.RenderMarkdown(entry.Body)}");
                    }),
            ],
            faviconRoot,
            generateLlmsTxt: true);

        await Assert.That(File.Exists(
            Path.Combine(output, "articles", "published", "index.html"))).IsTrue();
        await Assert.That(File.Exists(
            Path.Combine(output, "articles", "draft", "index.html"))).IsFalse();
        await Assert.That(Directory.EnumerateFiles(
            Path.Combine(output, "assets", "social", "content"), "*.png").Count()).IsEqualTo(1);

        var generatedText = string.Join(
            "\n",
            await Task.WhenAll(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path) != ".png")
                .Select(path => File.ReadAllTextAsync(path))));
        await Assert.That(generatedText).Contains("Published article");
        await Assert.That(generatedText).Contains("Visible body");
        await Assert.That(generatedText).DoesNotContain("Hidden article");
        await Assert.That(generatedText).DoesNotContain("Hidden body");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "search-index.json")))
            .Contains("/articles/published/");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "feed.xml")))
            .Contains("/articles/published/");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "sitemap.xml")))
            .Contains("/articles/published/");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "llms.txt")))
            .Contains("/articles/published/");
        await Assert.That(result.BuildPlan.Nodes.Any(node =>
            node.Id.Value.StartsWith("page:collection:", StringComparison.Ordinal))).IsTrue();
        await Assert.That(renderedLayout!.Value).IsEqualTo("article:v1");
    }

    [Test]
    [Arguments("draft")]
    [Arguments("expired")]
    [Arguments("environment")]
    public async Task CleanFalse_RemovesTypedPageAndSocialImageWhenPublicationEnds(
        string transition)
    {
        using var workspace = new TemporaryWorkspace();
        var faviconRoot = Path.Combine(workspace.Root, "favicon");
        Directory.CreateDirectory(faviconRoot);
        await WriteSocialSourceAsync(faviconRoot);
        var output = Path.Combine(workspace.Root, "output");

        await GenerateAsync(
            output,
            [],
            [Registration(Collection(new PageMetadata("Published", "Visible")))],
            faviconRoot,
            clean: true);
        var pagePath = Path.Combine(output, "typed", "entry.html");
        var socialPath = Directory.EnumerateFiles(
                Path.Combine(output, "assets", "social", "content"),
                "*.png")
            .Single();
        var userPath = Path.Combine(output, "user-file.txt");
        await File.WriteAllTextAsync(userPath, "unchanged");

        var hiddenMetadata = transition switch
        {
            "draft" => new PageMetadata("Hidden", draft: true),
            "expired" => new PageMetadata("Hidden", publishUntil: BuildTimestamp),
            "environment" => new PageMetadata("Hidden", environments: ["Staging"]),
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };
        var result = await GenerateAsync(
            output,
            [],
            [Registration(Collection(hiddenMetadata))],
            faviconRoot,
            clean: false);

        await Assert.That(File.Exists(pagePath)).IsFalse();
        await Assert.That(File.Exists(socialPath)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(userPath)).IsEqualTo("unchanged");
        await Assert.That(result.BuildReport.UnpublishedPages)
            .IsEquivalentTo(["page:collection:5:typed:5:entry"]);

        ContentCollection<Row, string> Collection(PageMetadata metadata) =>
            new(
                new ContentCollectionId("typed"),
                workspace.Root,
                [Entry("entry", "sha256:entry")],
                static _ => SiteRoute.ForFile("typed/entry.html"),
                _ => metadata,
                transformationId: new ContentTransformationId("typed:v1"),
                isCacheable: true);
    }

    [Test]
    public async Task JsonAndCsvCollections_GenerateDeterministicallyRegardlessOfRegistrationOrder()
    {
        using var workspace = new TemporaryWorkspace();
        var jsonRoot = Path.Combine(workspace.Root, "json");
        var csvRoot = Path.Combine(workspace.Root, "csv");
        Directory.CreateDirectory(jsonRoot);
        Directory.CreateDirectory(csvRoot);
        await File.WriteAllTextAsync(
            Path.Combine(jsonRoot, "items.json"),
            """[{"id":"b","title":"JSON B"},{"id":"a","title":"JSON A"}]""");
        await File.WriteAllTextAsync(
            Path.Combine(csvRoot, "items.csv"),
            "id,title\r\nb,CSV B\r\na,CSV A\r\n");

        var json = await new JsonContentCollectionLoader<Row, string>(
            jsonRoot,
            new ContentCollectionId("json"),
            new RowBinder(),
            element => element.GetProperty("title").GetString()!,
            static entry => SiteRoute.ForFile($"data/json-{entry.Id.Value}.html"),
            static entry => new PageMetadata(entry.FrontMatter.Title)).LoadAsync();
        var csv = await new CsvContentCollectionLoader<Row, string>(
            csvRoot,
            new ContentCollectionId("csv"),
            new RowBinder(),
            values => (string)values["title"]!,
            static entry => SiteRoute.ForFile($"data/csv-{entry.Id.Value}.html"),
            static entry => new PageMetadata(entry.FrontMatter.Title)).LoadAsync();
        await Assert.That(json.IsSuccess).IsTrue();
        await Assert.That(csv.IsSuccess).IsTrue();

        var jsonRegistration = Registration(json.Collection!);
        var csvRegistration = Registration(csv.Collection!);
        var first = Path.Combine(workspace.Root, "first");
        var second = Path.Combine(workspace.Root, "second");
        var firstResult = await GenerateAsync(first, [], [jsonRegistration, csvRegistration]);
        var secondResult = await GenerateAsync(second, [], [csvRegistration, jsonRegistration]);

        var firstManifest = await ReadManifestAsync(first);
        var secondManifest = await ReadManifestAsync(second);
        await Assert.That(firstManifest.SequenceEqual(secondManifest)).IsTrue();
        await Assert.That(firstResult.GeneratedFiles.Select(path => Path.GetRelativePath(first, path))
            .SequenceEqual(secondResult.GeneratedFiles.Select(path => Path.GetRelativePath(second, path))))
            .IsTrue();
        await Assert.That(firstManifest.Select(item => item.Path)).Contains("data/json-a.html");
        await Assert.That(firstManifest.Select(item => item.Path)).Contains("data/csv-b.html");
    }

    [Test]
    public async Task CollectionRouteCollision_FailsBeforeOutputMutation()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        var collection = Collection(
            "articles",
            Entry("entry", "sha256:entry"),
            static _ => SiteRoute.ForFile("posts/legacy.html"));

        SiteRouteValidationException? failure = null;
        try
        {
            await GenerateAsync(
                output,
                [Post("legacy")],
                [Registration(collection)]);
        }
        catch (SiteRouteValidationException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Diagnostics.Select(diagnostic => diagnostic.Id))
            .Contains(SiteRouteDiagnosticIds.DuplicateOutputPath);
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("unchanged");
        await Assert.That(Directory.EnumerateFiles(output).Count()).IsEqualTo(1);
    }

    [Test]
    public async Task DuplicateCollectionIdentity_IsRejectedBeforeRenderingRegardlessOfOrder()
    {
        using var workspace = new TemporaryWorkspace();
        var first = Collection(
            "same",
            Entry("first", "sha256:first"),
            static _ => SiteRoute.ForFile("first.html"));
        var second = Collection(
            "same",
            Entry("second", "sha256:second"),
            static _ => SiteRoute.ForFile("second.html"));
        var renderCount = 0;
        var registrations = new[]
        {
            new SiteContentCollection<Row, string>(
                first,
                (_, _) =>
                {
                    renderCount++;
                    return "first";
                }),
            new SiteContentCollection<Row, string>(
                second,
                (_, _) =>
                {
                    renderCount++;
                    return "second";
                }),
        };

        var messages = new List<string>();
        foreach (var order in new[]
                 {
                     registrations,
                     registrations.Reverse().ToArray(),
                 })
        {
            try
            {
                await GenerateAsync(
                    Path.Combine(workspace.Root, $"duplicate-{messages.Count}"),
                    [],
                    order);
            }
            catch (ArgumentException exception)
            {
                messages.Add(exception.Message);
            }
        }

        await Assert.That(messages.Count).IsEqualTo(2);
        await Assert.That(messages.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(1);
        await Assert.That(messages[0]).Contains("'same'");
        await Assert.That(renderCount).IsEqualTo(0);
        await Assert.That(Directory.EnumerateDirectories(workspace.Root, "duplicate-*")).IsEmpty();
    }

    [Test]
    public async Task MissingDependency_IsExplicit()
    {
        using var workspace = new TemporaryWorkspace();
        var missing = new ContentCollection<Row, string>(
            new ContentCollectionId("missing"),
            workspace.Root,
            [Entry("entry", "sha256:missing")],
            static _ => SiteRoute.ForFile("missing.html"),
            static entry => new PageMetadata(entry.FrontMatter.Title),
            declaredDependencies: [ContentDependency.FromFile("does-not-exist.json")],
            transformationId: new ContentTransformationId("missing:v1"),
            isCacheable: true);
        await Assert.That(async () => await GenerateAsync(
                Path.Combine(workspace.Root, "missing-output"),
                [],
                [Registration(missing)]))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task DeclaredFileDependency_RejectsSymlinkEscapeBeforeHashing()
    {
        using var workspace = new TemporaryWorkspace();
        var inputRoot = Path.Combine(workspace.Root, "content");
        var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(inputRoot);
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "shared.json"), """{"value":1}""");
        if (!TryCreateDirectorySymbolicLink(Path.Combine(inputRoot, "linked"), outside))
        {
            return;
        }

        var collection = new ContentCollection<Row, string>(
            new ContentCollectionId("linked"),
            inputRoot,
            [Entry("entry", "sha256:entry")],
            static _ => SiteRoute.ForFile("linked.html"),
            static entry => new PageMetadata(entry.FrontMatter.Title),
            declaredDependencies: [ContentDependency.FromFile("linked/shared.json")],
            transformationId: new ContentTransformationId("linked:v1"),
            isCacheable: true);
        var output = Path.Combine(workspace.Root, "output");

        await Assert.That(async () => await GenerateAsync(
                output,
                [],
                [Registration(collection)]))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("symbolic link or name-surrogate reparse point");
        await Assert.That(Directory.Exists(output)).IsFalse();
    }

    [Test]
    public async Task DeclaredFileDependency_RejectsFileSymlinkBeforeHashing()
    {
        using var workspace = new TemporaryWorkspace();
        var inputRoot = Path.Combine(workspace.Root, "content");
        var outside = Path.Combine(workspace.Root, "outside.json");
        Directory.CreateDirectory(inputRoot);
        await File.WriteAllTextAsync(outside, """{"value":1}""");
        if (!TryCreateFileSymbolicLink(
                Path.Combine(inputRoot, "linked.json"),
                outside))
        {
            return;
        }

        var collection = new ContentCollection<Row, string>(
            new ContentCollectionId("linked-file"),
            inputRoot,
            [Entry("entry", "sha256:entry")],
            static _ => SiteRoute.ForFile("linked.html"),
            static entry => new PageMetadata(entry.FrontMatter.Title),
            declaredDependencies: [ContentDependency.FromFile("linked.json")],
            transformationId: new ContentTransformationId("linked-file:v1"),
            isCacheable: true);
        var output = Path.Combine(workspace.Root, "output");

        await Assert.That(async () => await GenerateAsync(
                output,
                [],
                [Registration(collection)]))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("symbolic link or name-surrogate reparse point");
        await Assert.That(Directory.Exists(output)).IsFalse();
    }

    [Test]
    public async Task BuildInputsAndSocialPath_TrackDependenciesWithoutRouteInstability()
    {
        using var workspace = new TemporaryWorkspace();
        var dependencyPath = Path.Combine(workspace.Root, "shared.json");
        await File.WriteAllTextAsync(dependencyPath, """{"version":1}""");
        var faviconRoot = Path.Combine(workspace.Root, "favicon");
        Directory.CreateDirectory(faviconRoot);
        await WriteSocialSourceAsync(faviconRoot);

        var previousCollection = CacheableCollection("sha256:before", "ja");
        var previous = await GenerateAsync(
            Path.Combine(workspace.Root, "before"),
            [],
            [Registration(previousCollection)],
            faviconRoot,
            generateLlmsTxt: true);
        await File.WriteAllTextAsync(dependencyPath, """{"version":2}""");
        var currentCollection = CacheableCollection("sha256:after", "en");
        var current = await GenerateAsync(
            Path.Combine(workspace.Root, "after"),
            [],
            [Registration(currentCollection)],
            faviconRoot,
            generateLlmsTxt: true);

        var pageNode = current.BuildPlan.Nodes.Single(node =>
            node.Id.Value.StartsWith("page:collection:", StringComparison.Ordinal));
        await Assert.That(pageNode.Inputs.Select(input => $"{input.Kind}:{input.Key}"))
            .Contains("Value:content.sourceFingerprint");
        await Assert.That(pageNode.Inputs.Select(input => $"{input.Kind}:{input.Key}"))
            .Contains("Value:content.transformation");
        await Assert.That(pageNode.Inputs.Select(input => $"{input.Kind}:{input.Key}"))
            .Contains("Value:content.route.outputPath");
        await Assert.That(pageNode.Inputs.Select(input => $"{input.Kind}:{input.Key}"))
            .Contains("Value:content.metadata");
        await Assert.That(pageNode.Inputs.Select(input => $"{input.Kind}:{input.Key}"))
            .Contains("Value:content.layout");
        await Assert.That(pageNode.Inputs.Select(input => $"{input.Kind}:{input.Key}"))
            .Contains("Value:content.dependency:locale");
        await Assert.That(pageNode.Inputs.Select(input => $"{input.Kind}:{input.Key}"))
            .Contains("File:shared.json");

        var invalidated = current.BuildPlan.GetInvalidatedNodes(previous.BuildPlan)
            .Select(item => item.NodeId.Value)
            .ToArray();
        await Assert.That(invalidated).Contains(pageNode.Id.Value);
        await Assert.That(invalidated).Contains("index:search");
        await Assert.That(invalidated).Contains("feed:rss");
        await Assert.That(invalidated).Contains("index:sitemap");
        await Assert.That(invalidated).Contains("text:llms");
        await Assert.That(invalidated.Any(id =>
            id.StartsWith("social:collection:", StringComparison.Ordinal))).IsTrue();

        var previousSocial = previous.BuildPlan.Artifacts.Single(artifact =>
            artifact.RelativeOutputPath.StartsWith("assets/social/content/", StringComparison.Ordinal));
        var currentSocial = current.BuildPlan.Artifacts.Single(artifact =>
            artifact.RelativeOutputPath.StartsWith("assets/social/content/", StringComparison.Ordinal));
        await Assert.That(currentSocial.RelativeOutputPath)
            .IsEqualTo(previousSocial.RelativeOutputPath);

        ContentCollection<Row, string> CacheableCollection(string fingerprint, string locale) =>
            new(
                new ContentCollectionId("cacheable"),
                workspace.Root,
                [Entry("entry", fingerprint)],
                static _ => SiteRoute.ForFile("stable/entry.html"),
                static entry => new PageMetadata(entry.FrontMatter.Title, "Summary"),
                new ContentLayoutId("layout:v1"),
                declaredDependencies:
                [
                    ContentDependency.FromFile("shared.json"),
                    ContentDependency.FromValue("locale", locale),
                ],
                transformationId: new ContentTransformationId("render:v1"),
                isCacheable: true);
    }

    [Test]
    public async Task LegacyGenerateAsync_RemainsCompatibleWithoutCollections()
    {
        using var workspace = new TemporaryWorkspace();
        var generator = new SiteGenerator();
        var legacyOutput = Path.Combine(workspace.Root, "legacy");
        var posts = new[] { Post("legacy") };
        var customization = new SiteCustomization { Template = new BlogSiteTemplate() };

        var legacy = await generator.GenerateAsync(
            TestSite(),
            posts,
            legacyOutput,
            clean: true,
            customization);
        await Assert.That(legacy.PostCount).IsEqualTo(1);
        await Assert.That(File.Exists(Path.Combine(legacyOutput, "posts", "legacy.html"))).IsTrue();
        await Assert.That(legacy.BuildPlan.Nodes.Any(node =>
            node.Id.Value.StartsWith("page:collection:", StringComparison.Ordinal))).IsFalse();
    }

    private static SiteContentCollection<Row, string> Registration(
        ContentCollection<Row, string> collection) =>
        new(
            collection,
            static (entry, context) => context.RenderDocument(
                $"<h1>{Html.Encode(entry.FrontMatter.Title)}</h1><p>{Html.Encode(entry.Body)}</p>"));

    private static ContentCollection<Row, string> Collection(
        string id,
        ContentEntry<Row, string> entry,
        ContentRouteConvention<Row, string> routeConvention) =>
        new(
            new ContentCollectionId(id),
            "content",
            [entry],
            routeConvention,
            static item => new PageMetadata(item.FrontMatter.Title, item.Body),
            transformationId: new ContentTransformationId("render:v1"),
            isCacheable: true);

    private static ContentEntry<Row, string> Entry(string id, string fingerprint) =>
        new(
            new ContentEntryId(id),
            $"{id}.data",
            fingerprint,
            new Row(id, $"Title {id}"),
            $"Body {id}");

    private static MarkdownPost Post(string slug) =>
        new(
            $"{slug}.md",
            slug,
            new PostFrontMatter
            {
                Title = $"Post {slug}",
                Date = BuildTimestamp.AddDays(-1),
                Summary = $"Summary {slug}",
                Tags = [],
            },
            $"Body {slug}",
            $"posts/{slug}.html");

    private static async Task<SiteGenerationResult> GenerateAsync(
        string output,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<SiteContentCollection> collections,
        string? faviconRoot = null,
        bool generateLlmsTxt = false,
        bool clean = true) =>
        await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            posts,
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
                BuildTimestamp = BuildTimestamp,
                ContentCollections = collections,
            },
            CancellationToken.None);

    private static bool TryCreateDirectorySymbolicLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileSymbolicLink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static SiteSettings TestSite() => new()
    {
        Title = "Collection Site",
        Description = "Collection integration tests.",
        BaseUrl = "https://example.test/base/",
        Language = "en",
        TimeZone = "UTC",
    };

    private static async Task<IReadOnlyList<(string Path, string Content)>> ReadManifestAsync(
        string root)
    {
        var files = await Task.WhenAll(
            Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) != ".png")
            .Select(async path => (
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                await File.ReadAllTextAsync(path))));
        return files
            .OrderBy(item => item.Item1, StringComparer.Ordinal)
            .ToArray();
    }

    private static Task WriteSocialSourceAsync(string directory) =>
        File.WriteAllBytesAsync(
            Path.Combine(directory, "android-chrome-192x192.png"),
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

    private sealed class ArticleFrontMatter
    {
        public string Title { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public bool Draft { get; init; }
    }

    private sealed record Row(string Id, string Title);

    private sealed class RowBinder : IContentFrontMatterBinder<Row>
    {
        public ContentParseResult<Row> Bind(
            IReadOnlyDictionary<string, object?> values,
            LithoSharp.Diagnostics.SiteSourceLocation? sourceLocation = null) =>
            ContentParseResult<Row>.Success(new Row(
                (string)values["id"]!,
                (string)values["title"]!));
    }
}
