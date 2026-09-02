using LithoSharp;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;

namespace LithoSharp.Tests;

public sealed class BuiltInBuildPlanTests
{
    private static readonly DateTimeOffset BuildTimestamp =
        new(2026, 8, 30, 10, 11, 12, TimeSpan.Zero);

    [Test]
    public async Task BuiltInPlans_UseDistinctDeterministicArtifactNodes()
    {
        using var workspace = new TemporaryWorkspace();
        var posts = new[] { Post("alpha", "Alpha"), Post("beta", "Beta") };
        var extraPages = new[]
        {
            new SiteExtraPage
            {
                RelativePath = "about.html",
                Title = "About",
                BodyHtml = "<p>About</p>",
                NavLabel = "About",
            },
        };

        var docs = await GenerateAsync(
            workspace,
            "docs",
            new DocsSiteTemplate(),
            posts,
            extraPages,
            generateLlmsTxt: true);
        var blog = await GenerateAsync(
            workspace,
            "blog",
            new BlogSiteTemplate(),
            posts,
            extraPages,
            generateLlmsTxt: true);

        await Assert.That(NodeIds(docs.BuildPlan)).IsEquivalentTo(
        [
            "asset:site-css",
            "asset:site-script",
            "navigation:docs",
            "page:extra:about.html",
            "page:index",
            "page:markdown:posts/alpha.html",
            "page:markdown:posts/beta.html",
            "text:llms",
        ]);
        await Assert.That(NodeIds(blog.BuildPlan)).IsEquivalentTo(
        [
            "asset:search-script",
            "asset:site-css",
            "asset:site-script",
            "feed:rss",
            "index:search",
            "index:sitemap",
            "navigation:blog",
            "page:archives",
            "page:extra:about.html",
            "page:index",
            "page:markdown:posts/alpha.html",
            "page:markdown:posts/beta.html",
            "page:search",
            "page:tags",
            "text:llms",
        ]);
        await Assert.That(blog.BuildPlan.Artifacts.Select(artifact => artifact.RelativeOutputPath))
            .IsEquivalentTo(blog.GeneratedFiles.Select(path =>
                Path.GetRelativePath(blog.OutputDirectory, path).Replace('\\', '/')));
    }

    [Test]
    public async Task BlogPlan_NoChangeHasNoInvalidations()
    {
        using var workspace = new TemporaryWorkspace();
        var posts = new[] { Post("alpha", "Alpha") };
        var previous = await GenerateAsync(workspace, "before", new BlogSiteTemplate(), posts);
        var current = await GenerateAsync(workspace, "after", new BlogSiteTemplate(), posts);

        await Assert.That(current.BuildPlan.GetInvalidatedNodes(previous.BuildPlan)).IsEmpty();
    }

    [Test]
    public async Task TemplateSwitch_DocsToBlogInvalidatesSharedAssetsAndBlogNodes()
    {
        using var workspace = new TemporaryWorkspace();
        var posts = new[] { Post("alpha", "Alpha") };
        var docs = await GenerateAsync(workspace, "docs", new DocsSiteTemplate(), posts);
        var blog = await GenerateAsync(workspace, "blog", new BlogSiteTemplate(), posts);

        var invalidations = blog.BuildPlan.GetInvalidatedNodes(docs.BuildPlan);

        await Assert.That(InvalidatedNodeIds(invalidations)).IsEquivalentTo(
        [
            "asset:search-script",
            "asset:site-css",
            "asset:site-script",
            "feed:rss",
            "index:search",
            "index:sitemap",
            "navigation:blog",
            "page:archives",
            "page:index",
            "page:markdown:posts/alpha.html",
            "page:search",
            "page:tags",
        ]);
        await Assert.That(invalidations.Single(item => item.NodeId.Value == "asset:site-css").Reasons)
            .IsEquivalentTo(["入力 'Configuration:template.builtIn' が変更されました。"]);
        await Assert.That(invalidations.Single(item => item.NodeId.Value == "asset:site-script").Reasons)
            .IsEquivalentTo(["入力 'Configuration:template.builtIn' が変更されました。"]);
    }

    [Test]
    public async Task TemplateSwitch_BlogToDocsInvalidatesSharedAssetsAndDocsNodes()
    {
        using var workspace = new TemporaryWorkspace();
        var posts = new[] { Post("alpha", "Alpha") };
        var blog = await GenerateAsync(workspace, "blog", new BlogSiteTemplate(), posts);
        var docs = await GenerateAsync(workspace, "docs", new DocsSiteTemplate(), posts);

        var invalidations = docs.BuildPlan.GetInvalidatedNodes(blog.BuildPlan);

        await Assert.That(InvalidatedNodeIds(invalidations)).IsEquivalentTo(
        [
            "asset:site-css",
            "asset:site-script",
            "navigation:docs",
            "page:index",
            "page:markdown:posts/alpha.html",
        ]);
        await Assert.That(invalidations.Single(item => item.NodeId.Value == "asset:site-css").Reasons)
            .IsEquivalentTo(["入力 'Configuration:template.builtIn' が変更されました。"]);
        await Assert.That(invalidations.Single(item => item.NodeId.Value == "asset:site-script").Reasons)
            .IsEquivalentTo(["入力 'Configuration:template.builtIn' が変更されました。"]);
    }

    [Test]
    public async Task BlogPlan_BodyChangeInvalidatesPageSearchIndexAndSearchPage()
    {
        using var workspace = new TemporaryWorkspace();
        var previousPost = Post("alpha", "Alpha");
        var currentPost = previousPost with { MarkdownBody = "# Alpha\n\nChanged body." };
        var previous = await GenerateAsync(workspace, "before", new BlogSiteTemplate(), [previousPost]);
        var current = await GenerateAsync(workspace, "after", new BlogSiteTemplate(), [currentPost]);

        var invalidations = current.BuildPlan.GetInvalidatedNodes(previous.BuildPlan);

        await Assert.That(InvalidatedNodeIds(invalidations)).IsEquivalentTo(
        [
            "index:search",
            "page:markdown:posts/alpha.html",
            "page:search",
        ]);
        await Assert.That(invalidations.Single(item =>
                item.NodeId.Value == "page:markdown:posts/alpha.html").Reasons)
            .IsEquivalentTo(["入力 'Value:page.body' が変更されました。"]);
        await Assert.That(invalidations.Single(item => item.NodeId.Value == "index:search").Reasons)
            .IsEquivalentTo(["入力 'Collection:pages.blog.search' が変更されました。"]);
        await Assert.That(invalidations.Single(item => item.NodeId.Value == "page:search").Reasons)
            .IsEquivalentTo(
            [
                "依存ノード 'index:search' が無効化されました。",
                "入力 'File:search-index.json' が変更されました。",
            ]);
    }

    [Test]
    public async Task BlogPlan_MetadataAndRouteChangesInvalidateExactDependents()
    {
        using var workspace = new TemporaryWorkspace();
        var original = Post("alpha", "Alpha");
        var cases = new[]
        {
            new ChangeCase(
                "title",
                original with
                {
                    FrontMatter = original.FrontMatter with { Title = "Changed title" },
                },
                [
                    "feed:rss",
                    "index:search",
                    "page:archives",
                    "page:index",
                    "page:markdown:posts/alpha.html",
                    "page:search",
                ]),
            new ChangeCase(
                "route",
                original with { RelativeOutputPath = "posts/renamed.html" },
                [
                    "feed:rss",
                    "index:search",
                    "index:sitemap",
                    "page:archives",
                    "page:index",
                    "page:markdown:posts/renamed.html",
                    "page:search",
                    "page:tags",
                ]),
            new ChangeCase(
                "tags",
                original with
                {
                    FrontMatter = original.FrontMatter with { Tags = ["changed"] },
                },
                [
                    "index:search",
                    "page:markdown:posts/alpha.html",
                    "page:search",
                    "page:tags",
                ]),
            new ChangeCase(
                "date",
                original with
                {
                    FrontMatter = original.FrontMatter with
                    {
                        Date = original.FrontMatter.Date.AddDays(1),
                    },
                },
                [
                    "feed:rss",
                    "index:search",
                    "index:sitemap",
                    "page:archives",
                    "page:index",
                    "page:markdown:posts/alpha.html",
                    "page:search",
                ]),
        };

        var previous = await GenerateAsync(workspace, "before", new BlogSiteTemplate(), [original]);
        foreach (var change in cases)
        {
            var current = await GenerateAsync(
                workspace,
                $"after-{change.Name}",
                new BlogSiteTemplate(),
                [change.Post]);

            await Assert.That(InvalidatedNodeIds(
                    current.BuildPlan.GetInvalidatedNodes(previous.BuildPlan)))
                .IsEquivalentTo(change.ExpectedNodeIds);
        }
    }

    [Test]
    public async Task BlogPlan_PageAdditionAndRemovalInvalidateCollectionArtifacts()
    {
        using var workspace = new TemporaryWorkspace();
        var alpha = Post("alpha", "Alpha");
        var beta = Post("beta", "Beta") with
        {
            FrontMatter = Post("beta", "Beta").FrontMatter with { Tags = ["new-tag"] },
        };
        var onePage = await GenerateAsync(
            workspace,
            "one",
            new BlogSiteTemplate(),
            [alpha],
            generateLlmsTxt: true);
        var twoPages = await GenerateAsync(
            workspace,
            "two",
            new BlogSiteTemplate(),
            [alpha, beta],
            generateLlmsTxt: true);

        await Assert.That(InvalidatedNodeIds(
                twoPages.BuildPlan.GetInvalidatedNodes(onePage.BuildPlan)))
            .IsEquivalentTo(
            [
                "feed:rss",
                "index:search",
                "index:sitemap",
                "page:archives",
                "page:index",
                "page:markdown:posts/beta.html",
                "page:search",
                "page:tags",
                "text:llms",
            ]);
        await Assert.That(InvalidatedNodeIds(
                onePage.BuildPlan.GetInvalidatedNodes(twoPages.BuildPlan)))
            .IsEquivalentTo(
            [
                "feed:rss",
                "index:search",
                "index:sitemap",
                "page:archives",
                "page:index",
                "page:search",
                "page:tags",
                "text:llms",
            ]);
    }

    [Test]
    public async Task DocsPlan_TitleChangeInvalidatesNavigationPagesAndLlms()
    {
        using var workspace = new TemporaryWorkspace();
        var alpha = Post("alpha", "Alpha");
        var beta = Post("beta", "Beta");
        var previous = await GenerateAsync(
            workspace,
            "before",
            new DocsSiteTemplate(),
            [alpha, beta],
            generateLlmsTxt: true);
        var current = await GenerateAsync(
            workspace,
            "after",
            new DocsSiteTemplate(),
            [
                alpha with
                {
                    FrontMatter = alpha.FrontMatter with { Title = "Changed title" },
                },
                beta,
            ],
            generateLlmsTxt: true);

        await Assert.That(InvalidatedNodeIds(
                current.BuildPlan.GetInvalidatedNodes(previous.BuildPlan)))
            .IsEquivalentTo(
            [
                "navigation:docs",
                "page:index",
                "page:markdown:posts/alpha.html",
                "page:markdown:posts/beta.html",
                "text:llms",
            ]);
    }

    [Test]
    public async Task DocsPlan_PageAdditionInvalidatesNavigationAndEveryPageUsingIt()
    {
        using var workspace = new TemporaryWorkspace();
        var alpha = Post("alpha", "Alpha");
        var beta = Post("beta", "Beta");
        var previous = await GenerateAsync(
            workspace,
            "before",
            new DocsSiteTemplate(),
            [alpha]);
        var current = await GenerateAsync(
            workspace,
            "after",
            new DocsSiteTemplate(),
            [alpha, beta]);

        await Assert.That(InvalidatedNodeIds(
                current.BuildPlan.GetInvalidatedNodes(previous.BuildPlan)))
            .IsEquivalentTo(
            [
                "navigation:docs",
                "page:index",
                "page:markdown:posts/alpha.html",
                "page:markdown:posts/beta.html",
            ]);
    }

    [Test]
    public async Task ThemeAndFaviconChangesInvalidateOnlyTheirConsumers()
    {
        using var workspace = new TemporaryWorkspace();
        var faviconDirectory = Path.Combine(workspace.Root, "favicon");
        await WriteFaviconAssetsAsync(faviconDirectory, FirstPng);
        var post = Post("alpha", "Alpha");
        var previous = await GenerateAsync(
            workspace,
            "before",
            new BlogSiteTemplate(),
            [post],
            theme: new SiteThemeOptions { ThemeColor = "#111111" },
            faviconSourceDirectory: faviconDirectory);
        var changedTheme = await GenerateAsync(
            workspace,
            "theme",
            new BlogSiteTemplate(),
            [post],
            theme: new SiteThemeOptions { ThemeColor = "#222222" },
            faviconSourceDirectory: faviconDirectory);

        await Assert.That(InvalidatedNodeIds(
                changedTheme.BuildPlan.GetInvalidatedNodes(previous.BuildPlan)))
            .IsEquivalentTo(
            [
                "asset:web-manifest",
                "page:archives",
                "page:index",
                "page:markdown:posts/alpha.html",
                "page:search",
                "page:tags",
            ]);

        var changedTitlePost = post with
        {
            FrontMatter = post.FrontMatter with { Title = "Changed title" },
        };
        var changedTitle = await GenerateAsync(
            workspace,
            "title-with-social",
            new BlogSiteTemplate(),
            [changedTitlePost],
            theme: new SiteThemeOptions { ThemeColor = "#111111" },
            faviconSourceDirectory: faviconDirectory);
        await Assert.That(InvalidatedNodeIds(
                changedTitle.BuildPlan.GetInvalidatedNodes(previous.BuildPlan)))
            .IsEquivalentTo(
            [
                "feed:rss",
                "index:search",
                "page:archives",
                "page:index",
                "page:markdown:posts/alpha.html",
                "page:search",
                "social:post:posts/alpha.html",
            ]);

        await File.WriteAllBytesAsync(
            Path.Combine(faviconDirectory, "android-chrome-192x192.png"),
            [.. Convert.FromBase64String(FirstPng), 0]);
        var changedFavicon = await GenerateAsync(
            workspace,
            "changed-favicon-output",
            new BlogSiteTemplate(),
            [post],
            theme: new SiteThemeOptions { ThemeColor = "#111111" },
            faviconSourceDirectory: faviconDirectory);

        var invalidations = changedFavicon.BuildPlan.GetInvalidatedNodes(previous.BuildPlan);
        await Assert.That(InvalidatedNodeIds(invalidations)).IsEquivalentTo(
        [
            "asset:favicon:android-chrome-192x192.png",
            "social:default",
            "social:post:posts/alpha.html",
        ]);
        await Assert.That(invalidations.All(item => item.Reasons.SequenceEqual(
            ["入力 'File:favicon/android-chrome-192x192.png' が変更されました。"]))).IsTrue();
    }

    [Test]
    public async Task CustomTemplate_RemainsOpaqueAndAlwaysInvalidatesGeneratorCommonNode()
    {
        using var workspace = new TemporaryWorkspace();
        var previous = await GenerateAsync(
            workspace,
            "before",
            new EchoTemplate(),
            [Post("alpha", "Alpha")],
            generateLlmsTxt: true);
        var current = await GenerateAsync(
            workspace,
            "after",
            new EchoTemplate(),
            [Post("alpha", "Alpha")],
            generateLlmsTxt: true);

        await Assert.That(NodeIds(current.BuildPlan)).IsEquivalentTo(
        [
            "generator:common",
            "template:legacy-opaque",
        ]);
        await Assert.That(InvalidatedNodeIds(
                current.BuildPlan.GetInvalidatedNodes(previous.BuildPlan)))
            .IsEquivalentTo(
            [
                "generator:common",
                "template:legacy-opaque",
            ]);
    }

    [Test]
    public async Task PlanAndInvalidationOrderIsDeterministic()
    {
        using var workspace = new TemporaryWorkspace();
        var previous = await GenerateAsync(
            workspace,
            "before",
            new BlogSiteTemplate(),
            [Post("beta", "Beta"), Post("alpha", "Alpha")]);
        var current = await GenerateAsync(
            workspace,
            "after",
            new BlogSiteTemplate(),
            [
                Post("beta", "Changed beta"),
                Post("alpha", "Changed alpha"),
            ]);

        await Assert.That(NodeIds(current.BuildPlan)
            .SequenceEqual(NodeIds(current.BuildPlan).Order(StringComparer.Ordinal))).IsTrue();
        var invalidations = current.BuildPlan.GetInvalidatedNodes(previous.BuildPlan);
        await Assert.That(InvalidatedNodeIds(invalidations)
            .SequenceEqual(InvalidatedNodeIds(invalidations).Order(StringComparer.Ordinal))).IsTrue();
        await Assert.That(invalidations.All(item =>
            item.Reasons.SequenceEqual(item.Reasons.Order(StringComparer.Ordinal)))).IsTrue();
    }

    private static async Task<SiteGenerationResult> GenerateAsync(
        TemporaryWorkspace workspace,
        string outputName,
        ISiteTemplate template,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<SiteExtraPage>? extraPages = null,
        SiteThemeOptions? theme = null,
        bool generateLlmsTxt = false,
        string? faviconSourceDirectory = null)
    {
        return await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings
            {
                Title = "Test Site",
                Description = "Build graph test.",
                BaseUrl = "https://example.test/",
                Language = "en",
                TimeZone = "UTC",
            },
            posts,
            Path.Combine(workspace.Root, outputName),
            clean: true,
            new SiteCustomization
            {
                Template = template,
                ExtraPages = extraPages ?? [],
                Theme = theme ?? new SiteThemeOptions(),
                GenerateLlmsTxt = generateLlmsTxt,
                FaviconSourceDirectory = faviconSourceDirectory,
            },
            new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
            CancellationToken.None);
    }

    private static MarkdownPost Post(string slug, string title) =>
        new(
            $"content/{slug}.md",
            slug,
            new PostFrontMatter
            {
                Title = title,
                Date = BuildTimestamp.AddDays(-1),
                Summary = $"{title} summary",
                Tags = ["test"],
            },
            $"# {title}\n\nBody for {slug}.",
            $"posts/{slug}.html");

    private static string[] NodeIds(SiteBuildPlan plan) =>
        plan.Nodes.Select(node => node.Id.Value).ToArray();

    private static string[] InvalidatedNodeIds(IReadOnlyList<BuildInvalidation> invalidations) =>
        invalidations.Select(item => item.NodeId.Value).ToArray();

    private static async Task WriteFaviconAssetsAsync(string directory, string pngBase64)
    {
        Directory.CreateDirectory(directory);
        var bytes = Convert.FromBase64String(pngBase64);
        foreach (var fileName in SiteGenerator.BundledFaviconAssetNames)
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), bytes);
        }
    }

    private const string FirstPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    private sealed record ChangeCase(
        string Name,
        MarkdownPost Post,
        IReadOnlyList<string> ExpectedNodeIds);

    private sealed class EchoTemplate : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(
            SiteTemplateContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult(
            [
                new SiteTemplateFile
                {
                    RelativePath = "custom.html",
                    Content = context.Posts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            ]));
    }
}
