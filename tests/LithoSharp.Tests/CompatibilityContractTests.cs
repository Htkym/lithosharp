using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;

namespace LithoSharp.Tests;

public sealed class CompatibilityContractTests
{
    private static readonly DateTimeOffset FixedBuildTimestamp =
        new(2026, 1, 10, 11, 12, 13, TimeSpan.Zero);

    private static readonly string[] FaviconFiles =
    [
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
    ];

    private static SiteSettings TestSite() => new()
    {
        Title = "Contract Site",
        Description = "Compatibility baseline.",
        BaseUrl = "https://example.test/product/",
        Language = "en",
        TimeZone = "UTC"
    };

    [Test]
    public async Task GenerateAsync_DocsTemplate_PreservesArtifactPathsAndPublicUrls()
    {
        using var workspace = new TemporaryWorkspace();
        var (posts, faviconSource) = await CreateInputsAsync(workspace);
        var output = Path.Combine(workspace.Root, "docs-output");
        var customization = ContractCustomization(faviconSource, new DocsSiteTemplate());

        await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(), posts, output, clean: true, customization,
            new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp },
            CancellationToken.None);

        await AssertManifestAsync(output, "docs-manifest.txt");

        var post = await File.ReadAllTextAsync(Path.Combine(output, "posts", "guides", "install.html"));
        await Assert.That(post).Contains("<link rel=\"canonical\" href=\"https://example.test/product/posts/guides/install.html\">");
        await Assert.That(post).Contains("<meta property=\"og:image\" content=\"https://example.test/product/assets/social/posts/efbbd97218682f5d9fb8a2b08c2e37bcb24ec6e32fb6a5ffb57b0cbadd1f9e8f.png\">");
        await Assert.That(post).Contains("href=\"/product/index.html\"");
        await Assert.That(post).Contains("href=\"/product/about/team.html\"");
        await Assert.That(post).Contains("href=\"/product/assets/favicon/favicon.ico\"");
        await Assert.That(post).Contains("href=\"/product/site.webmanifest\"");
        await Assert.That(post).Contains("<meta property=\"og:title\" content=\"Install - Contract Site\">");
        await Assert.That(post).Contains("<meta name=\"description\" content=\"Installation guide.\">");
        await Assert.That(post).Contains("日本語");
        await Assert.That(ReadPngDimensions(Path.Combine(output, "assets", "social", "og-default.png")))
            .IsEqualTo((1200, 630));

        var extra = await File.ReadAllTextAsync(Path.Combine(output, "about", "team.html"));
        await Assert.That(extra).Contains("<link rel=\"canonical\" href=\"https://example.test/product/about/team.html\">");

        var llms = await File.ReadAllTextAsync(Path.Combine(output, "llms.txt"));
        await Assert.That(llms).Contains("[Install](https://example.test/product/posts/guides/install.html)");
        await Assert.That(llms).Contains("[日本語 Install](https://example.test/product/posts/日本語/install.html)");
        await Assert.That(File.Exists(Path.Combine(output, "feed.xml"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(output, "sitemap.xml"))).IsFalse();
    }

    [Test]
    public async Task GenerateAsync_BlogTemplate_PreservesArtifactPathsAndPublicUrls()
    {
        using var workspace = new TemporaryWorkspace();
        var (posts, faviconSource) = await CreateInputsAsync(workspace);
        var output = Path.Combine(workspace.Root, "blog-output");
        var customization = ContractCustomization(faviconSource, new BlogSiteTemplate());

        await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(), posts, output, clean: true, customization,
            new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp },
            CancellationToken.None);

        await AssertManifestAsync(output, "blog-manifest.txt");

        var post = await File.ReadAllTextAsync(Path.Combine(output, "posts", "guides", "install.html"));
        await Assert.That(post).Contains("<link rel=\"canonical\" href=\"https://example.test/product/posts/guides/install.html\">");
        await Assert.That(post).Contains("<link rel=\"alternate\" type=\"application/rss+xml\" title=\"Contract Site\" href=\"/product/feed.xml\">");
        await Assert.That(post).Contains("href=\"/product/archives.html\"");
        await Assert.That(post).Contains("href=\"/product/tags.html\"");
        await Assert.That(post).Contains("href=\"/product/about/team.html\"");
        await Assert.That(post).Contains("href=\"/product/search.html\"");
        await Assert.That(post).Contains("href=\"/product/feed.xml\"");
        await Assert.That(post).Contains("<meta property=\"article:published_time\" content=\"2026-01-02T03:04:05.0000000+00:00\">");

        var feed = XDocument.Load(Path.Combine(output, "feed.xml"));
        var channel = feed.Root!.Element("channel")!;
        await Assert.That(channel.Element("link")!.Value).IsEqualTo("https://example.test/product/");
        await Assert.That(channel.Elements("item").Count()).IsEqualTo(4);
        await Assert.That(channel.Elements("item").First().Element("link")!.Value)
            .IsEqualTo("https://example.test/product/posts/deep/nested/install.html");
        await Assert.That(channel.Elements("item").First().Element("guid")!.Value)
            .IsEqualTo("https://example.test/product/posts/deep/nested/install.html");

        var sitemap = XDocument.Load(Path.Combine(output, "sitemap.xml"));
        var sitemapNamespace = sitemap.Root!.GetDefaultNamespace();
        var sitemapUrls = sitemap.Root.Elements(sitemapNamespace + "url")
            .Select(url => url.Element(sitemapNamespace + "loc")!.Value)
            .ToArray();
        await Assert.That(string.Join("|", sitemapUrls)).IsEqualTo(string.Join("|",
        [
            "https://example.test/product/index.html",
            "https://example.test/product/archives.html",
            "https://example.test/product/tags.html",
            "https://example.test/product/about/team.html",
            "https://example.test/product/search.html",
            "https://example.test/product/posts/deep/nested/install.html",
            "https://example.test/product/posts/日本語/install.html",
            "https://example.test/product/posts/guides/install.html",
            "https://example.test/product/posts/intro.html"
        ]));
        await Assert.That(sitemap.ToString(SaveOptions.DisableFormatting)).Contains("<lastmod>2026-01-04</lastmod>");

        await using var stream = File.OpenRead(Path.Combine(output, "search-index.json"));
        using var searchIndex = await JsonDocument.ParseAsync(stream);
        await Assert.That(searchIndex.RootElement.TryGetProperty("generated", out _)).IsTrue();
        await Assert.That(searchIndex.RootElement.GetProperty("site").GetString())
            .IsEqualTo("Contract Site");
        var documents = searchIndex.RootElement.GetProperty("documents");
        await Assert.That(documents.GetArrayLength()).IsEqualTo(4);
        await Assert.That(documents.EnumerateArray().Select(document => document.GetProperty("url").GetString()!))
            .IsEquivalentTo(
            [
                "/product/posts/deep/nested/install.html",
                "/product/posts/guides/install.html",
                "/product/posts/intro.html",
                "/product/posts/日本語/install.html"
            ]);
        var installDocument = documents.EnumerateArray()
            .Single(document => document.GetProperty("url").GetString() == "/product/posts/guides/install.html");
        await Assert.That(installDocument.GetProperty("title").GetString()).IsEqualTo("Install");
        await Assert.That(installDocument.GetProperty("summary").GetString()).IsEqualTo("Installation guide.");
        await Assert.That(installDocument.GetProperty("tags").EnumerateArray().Single().GetString())
            .IsEqualTo("setup");
        await Assert.That(installDocument.GetProperty("body").GetString()).Contains("Install the package.");
        await Assert.That(searchIndex.RootElement.GetProperty("generated").GetString())
            .IsEqualTo(FixedBuildTimestamp.ToString("O"));
    }

    [Test]
    public async Task GenerateAsync_BuiltInTemplates_PreserveMajorDomStructureAndOrdering()
    {
        using var workspace = new TemporaryWorkspace();
        var (posts, faviconSource) = await CreateInputsAsync(workspace);
        var docsOutput = Path.Combine(workspace.Root, "docs-output");
        var blogOutput = Path.Combine(workspace.Root, "blog-output");

        await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            posts,
            docsOutput,
            clean: true,
            ContractCustomization(faviconSource, new DocsSiteTemplate()),
            new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp },
            CancellationToken.None);
        await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            posts,
            blogOutput,
            clean: true,
            ContractCustomization(faviconSource, new BlogSiteTemplate()),
            new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp },
            CancellationToken.None);

        var docs = await File.ReadAllTextAsync(Path.Combine(docsOutput, "posts", "guides", "install.html"));
        await AssertInOrderAsync(
            docs,
            "<header class=\"docs-header\">",
            "<div class=\"docs-shell\">",
            "<aside id=\"docs-sidebar\" class=\"docs-sidebar\"",
            "<main class=\"docs-main\">",
            "<article class=\"docs-content\">",
            "<h1>Install</h1>",
            "<nav class=\"docs-pagination\"",
            "<aside class=\"post-toc docs-toc\"",
            "<footer class=\"docs-footer\">");
        await Assert.That(docs).Contains("class=\"docs-nav-link is-current\"");
        await Assert.That(docs).Contains("aria-current=\"page\"");
        await Assert.That(docs).Contains("class=\"toc-list\"");

        var unicode = await File.ReadAllTextAsync(Path.Combine(docsOutput, "posts", "日本語", "install.html"));
        await Assert.That(unicode).Contains("<h1>日本語 Install</h1>");
        await Assert.That(unicode).Contains("同じ名前のファイルを別のディレクトリに置きます。");

        var blog = await File.ReadAllTextAsync(Path.Combine(blogOutput, "posts", "guides", "install.html"));
        await AssertInOrderAsync(
            blog,
            "<header class=\"site-header\">",
            "class=\"site-nav-home\"",
            ">Archives</a>",
            ">Tags</a>",
            ">Team</a>",
            ">Search</a>",
            "class=\"rss-nav-link\"",
            "<main>",
            "<div class=\"post-layout\">",
            "<article class=\"post\">",
            "<h1 class=\"post-title\">Install</h1>",
            "<aside class=\"post-toc\"",
            "<footer class=\"site-footer\">");
        await Assert.That(blog).Contains("<meta property=\"article:published_time\"");
        await Assert.That(blog).Contains("class=\"toc-nav\"");
        await Assert.That(blog).Contains("class=\"toc-list\"");
    }

    [Test]
    public async Task AdapterRetainedEntryPoints_PreservePublicSignatures()
    {
        var generate = typeof(SiteGenerator).GetMethod(
            nameof(SiteGenerator.GenerateAsync),
            BindingFlags.Instance | BindingFlags.Public,
            [
                typeof(SiteSettings),
                typeof(IReadOnlyList<MarkdownPost>),
                typeof(string),
                typeof(bool),
                typeof(SiteCustomization),
                typeof(CancellationToken)
            ]);
        await Assert.That(generate).IsNotNull();
        await Assert.That(generate!.ReturnType).IsEqualTo(typeof(Task<SiteGenerationResult>));
        await Assert.That(string.Join(",", generate.GetParameters().Select(parameter => parameter.Name)))
            .IsEqualTo("site,posts,outputDirectory,clean,customization,cancellationToken");
        await Assert.That(generate.GetParameters()[4].HasDefaultValue).IsTrue();
        await Assert.That(generate.GetParameters()[4].DefaultValue).IsNull();
        await Assert.That(generate.GetParameters()[5].IsOptional).IsTrue();

        var readAll = typeof(MarkdownPostReader).GetMethod(
            nameof(MarkdownPostReader.ReadAllAsync),
            BindingFlags.Instance | BindingFlags.Public,
            [typeof(string)]);
        var readOne = typeof(MarkdownPostReader).GetMethod(
            nameof(MarkdownPostReader.ReadAsync),
            BindingFlags.Instance | BindingFlags.Public,
            [typeof(string), typeof(string)]);
        await Assert.That(readAll).IsNotNull();
        await Assert.That(readAll!.ReturnType).IsEqualTo(typeof(Task<IReadOnlyList<MarkdownPost>>));
        await Assert.That(readOne).IsNotNull();
        await Assert.That(readOne!.ReturnType).IsEqualTo(typeof(Task<MarkdownPost>));
        await Assert.That(typeof(MarkdownPostReader).GetConstructor(Type.EmptyTypes)).IsNotNull();

        var customization = new SiteCustomization();
        await Assert.That(typeof(SiteCustomization).GetConstructor(Type.EmptyTypes)).IsNotNull();
        await Assert.That(customization.Template.GetType()).IsEqualTo(typeof(DocsSiteTemplate));
        await Assert.That(typeof(SiteCustomization).GetProperty(nameof(SiteCustomization.Template))!.PropertyType)
            .IsEqualTo(typeof(ISiteTemplate));

        var render = typeof(ISiteTemplate).GetMethod(
            nameof(ISiteTemplate.RenderAsync),
            BindingFlags.Instance | BindingFlags.Public,
            [typeof(SiteTemplateContext), typeof(CancellationToken)]);
        await Assert.That(render).IsNotNull();
        await Assert.That(render!.ReturnType).IsEqualTo(typeof(Task<SiteTemplateResult>));
        await Assert.That(render.GetParameters()[1].IsOptional).IsTrue();
        await Assert.That(typeof(ISiteTemplate).IsAssignableFrom(typeof(DocsSiteTemplate))).IsTrue();
        await Assert.That(typeof(ISiteTemplate).IsAssignableFrom(typeof(BlogSiteTemplate))).IsTrue();
    }

    private static SiteCustomization ContractCustomization(string faviconSource, ISiteTemplate template) =>
        new()
        {
            Template = template,
            FaviconSourceDirectory = faviconSource,
            GenerateLlmsTxt = true,
            ExtraPages =
            [
                new SiteExtraPage
                {
                    RelativePath = "about/team.html",
                    Title = "Team",
                    NavLabel = "Team",
                    BodyHtml = "<section class=\"team\"><h1>Team</h1></section>"
                }
            ]
        };

    private static async Task<(IReadOnlyList<MarkdownPost> Posts, string FaviconSource)> CreateInputsAsync(
        TemporaryWorkspace workspace)
    {
        var content = Path.Combine(workspace.Root, "content");
        CopyDirectory(FixturePath("content"), content);

        var faviconSource = Path.Combine(workspace.Root, "favicon");
        Directory.CreateDirectory(faviconSource);
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        foreach (var file in FaviconFiles)
        {
            await File.WriteAllBytesAsync(Path.Combine(faviconSource, file), png);
        }

        return (await new MarkdownPostReader().ReadAllAsync(content), faviconSource);
    }

    private static string FixturePath(string relativePath) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", relativePath);

    private static async Task AssertManifestAsync(string output, string manifestName)
    {
        var expected = (await File.ReadAllLinesAsync(FixturePath(manifestName)))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Order(StringComparer.Ordinal)
            .ToArray();
        await AssertPathsAsync(output, expected);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static async Task AssertPathsAsync(string output, IReadOnlyList<string> expected)
    {
        var actual = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(output, path).Replace('\\', '/'))
            .Where(path => path != ".lithosharp-output-manifest.json")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var normalizedExpected = expected.Order(StringComparer.Ordinal).ToArray();

        await Assert.That(string.Join(Environment.NewLine, actual))
            .IsEqualTo(string.Join(Environment.NewLine, normalizedExpected));
    }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 24 ||
            !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new InvalidOperationException($"'{path}' is not a PNG with an IHDR chunk.");
        }

        return (
            ReadBigEndianInt32(bytes, 16),
            ReadBigEndianInt32(bytes, 20));
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset) =>
        (bytes[offset] << 24) |
        (bytes[offset + 1] << 16) |
        (bytes[offset + 2] << 8) |
        bytes[offset + 3];

    private static async Task AssertInOrderAsync(string text, params string[] values)
    {
        var previous = -1;
        foreach (var value in values)
        {
            var current = text.IndexOf(value, StringComparison.Ordinal);
            await Assert.That(current).IsGreaterThan(previous);
            previous = current;
        }
    }
}
