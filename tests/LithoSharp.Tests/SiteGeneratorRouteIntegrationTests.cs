using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Routing;
using System.Text.Json;
using System.Xml.Linq;

namespace LithoSharp.Tests;

public sealed class SiteGeneratorRouteIntegrationTests
{
    private static readonly DateTimeOffset BuildTimestamp =
        new(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task GenerateAsync_FiltersUnpublishedPostsFromEveryBlogSurface()
    {
        using var workspace = new TemporaryWorkspace();
        var faviconSource = Path.Combine(workspace.Root, "favicon");
        Directory.CreateDirectory(faviconSource);
        await File.WriteAllBytesAsync(
            Path.Combine(faviconSource, "android-chrome-192x192.png"),
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        var output = Path.Combine(workspace.Root, "output");
        var posts = new[]
        {
            Post(
                "published",
                "Published Boundary",
                publishFrom: BuildTimestamp,
                publishUntil: BuildTimestamp.AddTicks(1),
                environments: ["Production"]),
            Post("draft", "Hidden Draft", draft: true),
            Post("future", "Hidden Future", publishFrom: BuildTimestamp.AddTicks(1)),
            Post("expired", "Hidden Expired", publishUntil: BuildTimestamp),
            Post("staging", "Hidden Staging", environments: ["Staging"])
        };
        var result = await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            posts,
            output,
            clean: true,
            new SiteCustomization
            {
                Template = new BlogSiteTemplate(),
                FaviconSourceDirectory = faviconSource,
                GenerateLlmsTxt = true
            },
            new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
            CancellationToken.None);

        await Assert.That(result.PostCount).IsEqualTo(1);
        await Assert.That(result.BuildReport.UnpublishedPages.SequenceEqual(
        [
            "markdown:posts/draft.html",
            "markdown:posts/expired.html",
            "markdown:posts/future.html",
            "markdown:posts/staging.html",
        ])).IsTrue();
        await Assert.That(result.GeneratedFiles.SequenceEqual(
            result.GeneratedFiles.Order(StringComparer.Ordinal))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, "posts", "published.html"))).IsTrue();
        foreach (var slug in new[] { "draft", "future", "expired", "staging" })
        {
            await Assert.That(File.Exists(Path.Combine(output, "posts", $"{slug}.html"))).IsFalse();
        }

        await Assert.That(Directory.EnumerateFiles(
            Path.Combine(output, "assets", "social", "posts"), "*.png").Count()).IsEqualTo(1);
        var generatedText = string.Join(
            "\n",
            await Task.WhenAll(
                Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                    .Where(path => Path.GetExtension(path) is not ".png")
                    .Select(path => File.ReadAllTextAsync(path))));
        await Assert.That(generatedText).Contains("Published Boundary");
        await Assert.That(generatedText).DoesNotContain("Hidden Draft");
        await Assert.That(generatedText).DoesNotContain("Hidden Future");
        await Assert.That(generatedText).DoesNotContain("Hidden Expired");
        await Assert.That(generatedText).DoesNotContain("Hidden Staging");

        var searchIndex = await File.ReadAllTextAsync(Path.Combine(output, "search-index.json"));
        await Assert.That(searchIndex).DoesNotContain("Hidden");
        var feed = await File.ReadAllTextAsync(Path.Combine(output, "feed.xml"));
        await Assert.That(feed).DoesNotContain("Hidden");
        var sitemap = await File.ReadAllTextAsync(Path.Combine(output, "sitemap.xml"));
        await Assert.That(sitemap).DoesNotContain("/posts/draft.html");
        await Assert.That(sitemap).DoesNotContain("/posts/future.html");
        await Assert.That(sitemap).DoesNotContain("/posts/expired.html");
        await Assert.That(sitemap).DoesNotContain("/posts/staging.html");
        var llms = await File.ReadAllTextAsync(Path.Combine(output, "llms.txt"));
        await Assert.That(llms).DoesNotContain("Hidden");

        var publishedHtml = await File.ReadAllTextAsync(
            Path.Combine(output, "posts", "published.html"));
        await Assert.That(publishedHtml).DoesNotContain("Hidden");
        await Assert.That(publishedHtml).DoesNotContain("draft.html");
        await Assert.That(publishedHtml).DoesNotContain("future.html");
        await Assert.That(publishedHtml).DoesNotContain("expired.html");
        await Assert.That(publishedHtml).DoesNotContain("staging.html");
    }

    [Test]
    public async Task GenerateAsync_FiltersDocsNavigationAndPagination()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var first = Post("first", "First", sidebarPosition: 1);
        var hidden = Post("hidden", "Hidden", draft: true, sidebarPosition: 2);
        var last = Post("last", "Last", sidebarPosition: 3);

        await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            [first, hidden, last],
            output,
            clean: true,
            customization: null,
            new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
            CancellationToken.None);

        var firstHtml = await File.ReadAllTextAsync(Path.Combine(output, "posts", "first.html"));
        var lastHtml = await File.ReadAllTextAsync(Path.Combine(output, "posts", "last.html"));
        await Assert.That(firstHtml).Contains(">Last</a>");
        await Assert.That(lastHtml).Contains(">First</a>");
        await Assert.That(firstHtml).DoesNotContain(">Hidden</a>");
        await Assert.That(lastHtml).DoesNotContain(">Hidden</a>");
        await Assert.That(File.Exists(Path.Combine(output, "posts", "hidden.html"))).IsFalse();
    }

    [Test]
    public async Task GenerateAsync_RouteCollisionsFailBeforeOutputMutation()
    {
        var cases = new[]
        {
            new CollisionCase("INDEX.html", false, SiteRouteDiagnosticIds.OutputPathCaseCollision),
            new CollisionCase("assets", false, SiteRouteDiagnosticIds.OutputPathAncestorConflict),
            new CollisionCase("llms.txt", true, SiteRouteDiagnosticIds.DuplicateOutputPath),
            new CollisionCase("posts/post.html", false, SiteRouteDiagnosticIds.DuplicateOutputPath)
        };

        foreach (var collision in cases)
        {
            using var workspace = new TemporaryWorkspace();
            var output = Path.Combine(workspace.Root, "output");
            Directory.CreateDirectory(output);
            var sentinel = Path.Combine(output, "keep.txt");
            await File.WriteAllTextAsync(sentinel, "unchanged");
            var customization = new SiteCustomization
            {
                GenerateLlmsTxt = collision.GenerateLlmsTxt,
                ExtraPages =
                [
                    new SiteExtraPage
                    {
                        RelativePath = collision.RelativePath,
                        Title = "Collision",
                        BodyHtml = "<p>collision</p>"
                    }
                ]
            };

            SiteRouteValidationException? failure = null;
            try
            {
                await new SiteGenerator().GenerateWithOptionsAsync(
                    TestSite(),
                    [Post("post", "Post")],
                    output,
                    clean: true,
                    customization,
                    new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
                    CancellationToken.None);
            }
            catch (SiteRouteValidationException exception)
            {
                failure = exception;
            }

            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.Diagnostics.Select(diagnostic => diagnostic.Id))
                .Contains(collision.DiagnosticId);
            await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("unchanged");
            await Assert.That(Directory.EnumerateFiles(output).Count()).IsEqualTo(1);
        }
    }

    [Test]
    public async Task GenerateAsync_CustomTemplateUsesPublishedPagesAndSubpathRoutes()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var template = new PageEchoTemplate();
        var site = TestSite() with { BaseUrl = "https://example.test/product/" };
        var result = await new SiteGenerator().GenerateWithOptionsAsync(
            site,
            [
                Post("published", "Published", environments: ["Staging"]),
                Post("draft", "Draft", draft: true)
            ],
            output,
            clean: true,
            new SiteCustomization { Template = template },
            new SiteGenerationOptions
            {
                BuildTimestamp = BuildTimestamp,
                EnvironmentName = "staging"
            },
            CancellationToken.None);

        await Assert.That(template.PostTitles).IsEquivalentTo(["Published"]);
        await Assert.That(result.PostCount).IsEqualTo(1);
        var html = await File.ReadAllTextAsync(Path.Combine(output, "posts", "published.html"));
        await Assert.That(html).Contains("href=\"/product/posts/published.html\"");
        await Assert.That(html).Contains(
            "<link rel=\"canonical\" href=\"https://example.test/product/posts/published.html\">");
    }

    [Test]
    public async Task GenerateAsync_CleanFalseRemovesStaleFilteredFileAndPreservesUserFile()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();
        var options = new SiteGenerationOptions { BuildTimestamp = BuildTimestamp };
        await generator.GenerateWithOptionsAsync(
            TestSite(),
            [Post("post", "Published")],
            output,
            clean: true,
            customization: null,
            options,
            CancellationToken.None);
        var stalePath = Path.Combine(output, "posts", "post.html");
        var userPath = Path.Combine(output, "user-file.txt");
        await File.WriteAllTextAsync(userPath, "unchanged");

        var result = await generator.GenerateWithOptionsAsync(
            TestSite(),
            [Post("post", "Published", draft: true)],
            output,
            clean: false,
            customization: null,
            options,
            CancellationToken.None);

        await Assert.That(File.Exists(stalePath)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(userPath)).IsEqualTo("unchanged");
        await Assert.That(result.PostCount).IsEqualTo(0);
        await Assert.That(result.GeneratedFiles).DoesNotContain(stalePath);
    }

    [Test]
    public async Task GenerateAsync_RejectsNullOrWhitespaceEnvironmentName()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();

        await Assert.That(async () => await generator.GenerateWithOptionsAsync(
                TestSite(),
                [],
                output,
                clean: true,
                customization: null,
                new SiteGenerationOptions { EnvironmentName = null! },
                CancellationToken.None))
            .Throws<ArgumentNullException>();
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(
                TestSite(),
                [],
                output,
                clean: true,
                customization: null,
                new SiteGenerationOptions { EnvironmentName = " " },
                CancellationToken.None))
            .Throws<ArgumentException>();
        await Assert.That(Directory.Exists(output)).IsFalse();
    }

    [Test]
    public async Task GenerateAsync_InvalidPublicationMetadataFailsBeforeRouteAdaptation()
    {
        var cases = new[]
        {
            Post(
                "interval",
                "Invalid interval",
                publishFrom: BuildTimestamp,
                publishUntil: BuildTimestamp),
            Post(
                "environment",
                "Invalid environment",
                environments: [" "])
        };

        foreach (var post in cases)
        {
            using var workspace = new TemporaryWorkspace();
            var output = Path.Combine(workspace.Root, "output");
            Directory.CreateDirectory(output);
            var sentinel = Path.Combine(output, "keep.txt");
            await File.WriteAllTextAsync(sentinel, "unchanged");
            var invalidRoutePost = post with { RelativeOutputPath = "../outside.html" };

            await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(
                    TestSite(),
                    [invalidRoutePost],
                    output,
                    clean: true,
                    customization: null,
                    new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
                    CancellationToken.None))
                .Throws<ArgumentException>();
            await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("unchanged");
        }
    }

    [Test]
    public async Task GenerateAsync_PublishedInvalidRouteReportsOriginalFailureAndSource()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        var post = Post("invalid", "Invalid") with
        {
            FilePath = "content/invalid.md",
            RelativeOutputPath = "../outside.html"
        };

        var failure = await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(
                TestSite(),
                [post],
                output,
                clean: true,
                customization: null,
                new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
                CancellationToken.None))
            .Throws<SiteRouteValidationException>();

        var diagnostic = failure!.Diagnostics.Single(
            diagnostic => diagnostic.Id == SiteRouteDiagnosticIds.InvalidRoute);
        await Assert.That(diagnostic.Message).Contains("ArgumentException");
        await Assert.That(diagnostic.Message).Contains("'.' or '..'");
        await Assert.That(diagnostic.Location?.FilePath).IsEqualTo("content/invalid.md");
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("unchanged");
    }

    [Test]
    public async Task GenerateAsync_UnpublishedInvalidRouteDoesNotParticipateInRouting()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var post = Post("draft", "Draft", draft: true) with
        {
            RelativeOutputPath = "../outside.html"
        };

        var result = await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            [post],
            output,
            clean: true,
            customization: null,
            new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
            CancellationToken.None);

        await Assert.That(result.PostCount).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(workspace.Root, "outside.html"))).IsFalse();
    }

    [Test]
    [Arguments("page.html#fragment")]
    [Arguments("%ZZ.html")]
    [Arguments("../outside.html")]
    [Arguments("100%.html")]
    public async Task GenerateAsync_InvalidCustomTemplateRouteFailsBeforeOutputMutation(
        string relativePath)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");

        var failure = await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(
                TestSite(),
                [],
                output,
                clean: true,
                new SiteCustomization { Template = new SingleFileTemplate(relativePath) },
                new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
                CancellationToken.None))
            .Throws<SiteRouteValidationException>();

        await Assert.That(failure!.Diagnostics.Select(diagnostic => diagnostic.Id))
            .Contains(SiteRouteDiagnosticIds.InvalidRoute);
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("unchanged");
        await Assert.That(Directory.EnumerateFiles(output).Count()).IsEqualTo(1);
    }

    [Test]
    public async Task GenerateAsync_CustomTemplateUsesEncodedLiteralPercentRoute()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");

        var result = await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            [],
            output,
            clean: true,
            new SiteCustomization { Template = new SingleFileTemplate("100%25.html") },
            new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
            CancellationToken.None);

        var expected = Path.Combine(output, "100%.html");
        await Assert.That(File.Exists(expected)).IsTrue();
        await Assert.That(result.GeneratedFiles).Contains(expected);
    }

    [Test]
    public async Task GenerateAsync_CustomTemplateMayOmitPageAndEmitAncestorArtifact()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");

        var result = await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            [Post("post", "Post")],
            output,
            clean: true,
            new SiteCustomization { Template = new SingleFileTemplate("posts") },
            new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
            CancellationToken.None);

        await Assert.That(result.PostCount).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts")))
            .IsEqualTo("template");
        await Assert.That(File.Exists(Path.Combine(output, "posts", "post.html"))).IsFalse();
    }

    [Test]
    public async Task GenerateAsync_PreservesUnsafeAsciiEscapesAcrossRenderedUrls()
    {
        using var workspace = new TemporaryWorkspace();
        var faviconSource = Path.Combine(workspace.Root, "favicon");
        await WriteFaviconAssetsAsync(faviconSource);
        var output = Path.Combine(workspace.Root, "output");
        var site = TestSite() with
        {
            BaseUrl = "https://example.test/base%20space-%7Bbrace%7D-%7Cpipe-%5Ecaret-%60tick/"
        };
        var post = Post("encoded", "Encoded") with
        {
            RelativeOutputPath = "posts/日本語/space%20name-%7Bbrace%7D-%5Ecaret-%60tick-%26amp.html"
        };
        var expectedPath =
            "/base%20space-%7Bbrace%7D-%7Cpipe-%5Ecaret-%60tick/posts/日本語/space%20name-%7Bbrace%7D-%5Ecaret-%60tick-%26amp.html";
        var expectedUrl = $"https://example.test{expectedPath}";

        await new SiteGenerator().GenerateWithOptionsAsync(
            site,
            [post],
            output,
            clean: true,
            new SiteCustomization
            {
                Template = new BlogSiteTemplate(),
                FaviconSourceDirectory = faviconSource
            },
            new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
            CancellationToken.None);

        var physicalPost = Path.Combine(
            output,
            "posts",
            "日本語",
            "space name-{brace}-^caret-`tick-&amp.html");
        var html = await File.ReadAllTextAsync(physicalPost);
        var index = await File.ReadAllTextAsync(Path.Combine(output, "index.html"));
        await Assert.That(index).Contains($"href=\"{expectedPath}\"");
        await Assert.That(html).Contains($"<link rel=\"canonical\" href=\"{expectedUrl}\">");
        await Assert.That(html).Contains($"<meta property=\"og:url\" content=\"{expectedUrl}\">");
        await Assert.That(html).Contains(
            "content=\"https://example.test/base%20space-%7Bbrace%7D-%7Cpipe-%5Ecaret-%60tick/assets/social/posts/1d4672db027ab34666d998b285a35426f42eaf86faafbec6f9d0138b1116f825.png\"");
        await Assert.That(html).DoesNotContain("base space-{brace}-|pipe-^caret-`tick");

        await using (var stream = File.OpenRead(Path.Combine(output, "search-index.json")))
        {
            using var search = await JsonDocument.ParseAsync(stream);
            await Assert.That(search.RootElement
                    .GetProperty("documents")[0]
                    .GetProperty("url")
                    .GetString())
                .IsEqualTo(expectedPath);
        }

        var feed = XDocument.Load(Path.Combine(output, "feed.xml"));
        await Assert.That(feed.Root!.Element("channel")!.Element("item")!.Element("link")!.Value)
            .IsEqualTo(expectedUrl);
        var sitemap = XDocument.Load(Path.Combine(output, "sitemap.xml"));
        var ns = sitemap.Root!.GetDefaultNamespace();
        await Assert.That(sitemap.Root.Elements(ns + "url")
                .Select(element => element.Element(ns + "loc")!.Value))
            .Contains(expectedUrl);

        await using var manifestStream = File.OpenRead(Path.Combine(output, "site.webmanifest"));
        using var manifest = await JsonDocument.ParseAsync(manifestStream);
        await Assert.That(manifest.RootElement.GetProperty("start_url").GetString())
            .IsEqualTo("/base%20space-%7Bbrace%7D-%7Cpipe-%5Ecaret-%60tick/index.html");
        await Assert.That(manifest.RootElement.GetProperty("scope").GetString())
            .IsEqualTo("/base%20space-%7Bbrace%7D-%7Cpipe-%5Ecaret-%60tick/");
    }

    [Test]
    public async Task GenerateAsync_SameFileNameInDifferentRoutesGetsDistinctSocialImages()
    {
        using var workspace = new TemporaryWorkspace();
        var faviconSource = Path.Combine(workspace.Root, "favicon");
        await WriteFaviconAssetsAsync(faviconSource);
        var output = Path.Combine(workspace.Root, "output");
        var posts = new[]
        {
            Post("same", "First") with { RelativeOutputPath = "posts/a/same.html" },
            Post("same", "Second") with { RelativeOutputPath = "posts/b/same.html" },
        };

        var result = await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            posts,
            output,
            clean: true,
            new SiteCustomization
            {
                Template = new BlogSiteTemplate(),
                FaviconSourceDirectory = faviconSource,
            },
            new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
            CancellationToken.None);

        var firstPath =
            "assets/social/posts/0d64c079d0c3efefb9b961c5ca7aff497fbedcd2347d837bd296baebea30ec5a.png";
        var secondPath =
            "assets/social/posts/1c34211d7760e874e88598701f35c02a0d7db22d28eb82c855a644b69a95ba92.png";
        await Assert.That(File.Exists(Path.Combine(output, firstPath.Replace('/', Path.DirectorySeparatorChar)))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(output, secondPath.Replace('/', Path.DirectorySeparatorChar)))).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "a", "same.html")))
            .Contains($"https://example.test/{firstPath}");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "b", "same.html")))
            .Contains($"https://example.test/{secondPath}");
        await Assert.That(result.BuildPlan.Artifacts.Count(artifact =>
            artifact.RelativeOutputPath.StartsWith("assets/social/posts/", StringComparison.Ordinal)))
            .IsEqualTo(2);
    }

    private static SiteSettings TestSite() => new()
    {
        Title = "Test Site",
        Description = "A test site.",
        BaseUrl = "https://example.test/",
        Language = "en",
        TimeZone = "UTC"
    };

    private static MarkdownPost Post(
        string slug,
        string title,
        bool draft = false,
        DateTimeOffset? publishFrom = null,
        DateTimeOffset? publishUntil = null,
        IReadOnlyList<string>? environments = null,
        int? sidebarPosition = null) =>
        new(
            $"{slug}.md",
            slug,
            new PostFrontMatter
            {
                Title = title,
                Date = BuildTimestamp.AddDays(-1),
                Summary = $"{title} summary",
                Tags = [$"{slug}-tag"],
                Draft = draft,
                PublishFrom = publishFrom,
                PublishUntil = publishUntil,
                Environments = environments?.ToList() ?? [],
                SidebarPosition = sidebarPosition
            },
            $"## {title}\n\n{title} body.",
            $"posts/{slug}.html");

    private sealed record CollisionCase(
        string RelativePath,
        bool GenerateLlmsTxt,
        string DiagnosticId);

    private sealed class PageEchoTemplate : ISiteTemplate
    {
        public IReadOnlyList<string> PostTitles { get; private set; } = [];

        public Task<SiteTemplateResult> RenderAsync(
            SiteTemplateContext context,
            CancellationToken cancellationToken = default)
        {
            PostTitles = context.Posts.Select(post => post.FrontMatter.Title).ToArray();
            var files = context.Pages.Select(page => new SiteTemplateFile
            {
                RelativePath = page.Post.RelativeOutputPath,
                Content = context.RenderDocument(new SiteTemplateDocument
                {
                    Title = page.Post.FrontMatter.Title,
                    RelativePath = page.Post.RelativeOutputPath,
                    BodyHtml = $"<a href=\"{page.Url}\">{page.Post.FrontMatter.Title}</a>"
                })
            }).ToArray();
            return Task.FromResult(new SiteTemplateResult(files));
        }
    }

    private sealed class SingleFileTemplate(string relativePath) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(
            SiteTemplateContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult(
            [
                new SiteTemplateFile
                {
                    RelativePath = relativePath,
                    Content = "template"
                }
            ]));
    }

    private static async Task WriteFaviconAssetsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        var files = new[]
        {
            "favicon.ico",
            "apple-touch-icon.png",
            "android-chrome-192x192.png",
            "android-chrome-512x512.png",
            "favicon-16x16.png",
            "favicon-32x32.png",
            "favicon-48x48.png",
            "favicon-64x64.png",
            "favicon-96x96.png",
            "favicon-128x128.png",
            "favicon-180x180.png",
            "favicon-192x192.png",
            "favicon-256x256.png",
            "favicon-512x512.png"
        };
        await Task.WhenAll(files.Select(file => File.WriteAllBytesAsync(
            Path.Combine(directory, file),
            image)));
    }
}
