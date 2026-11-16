using LithoSharp;
using LithoSharp.AgentReady;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Images;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;

public sealed class DocsSampleFactory : ISiteFactory
{
    public string? ContentDirectory { get; init; }
    public string OutputDirectory { get; init; } = "dist";
    public bool Check { get; init; }
    public bool RedirectDemo { get; init; }
    public bool AssetDemo { get; init; }

    public async Task<SiteDefinition> CreateAsync(
        SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        var content = ContentDirectory ?? Path.Combine(context.ProjectDirectory, "content");
        var typedContent = Path.Combine(context.ProjectDirectory, "typed-content");
        var output = Path.GetFullPath(OutputDirectory, context.ProjectDirectory);
        var check = Check;
        var redirectDemo = RedirectDemo;
        var assetDemo = AssetDemo;
        var imagePath = Path.Combine(context.ProjectDirectory, "image-source.png");
        if (!File.Exists(imagePath))
            imagePath = Path.GetFullPath("../../src/LithoSharp/icon.png", context.ProjectDirectory);

        var site = new SiteSettings
        {
            Title = "LithoSharp Docs Sample",
            Description = "A documentation site generated with LithoSharp.",
            BaseUrl = "https://example.com/",
            Language = "en",
            Author = "LithoSharp",
            TimeZone = "UTC"
        };

        var customization = new SiteCustomization
        {
            Theme = new SiteThemeOptions
            {
                BrandPrefix = "lithosharp / ",
            },
            AgentReady = new() { Enabled = true, StructuredData = new() { Mode = AgentReadyStructuredDataMode.Automatic } },
            GenerateLlmsTxt = true
        };

        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        SiteGenerator.Validate(site, content, posts, customization);
        var articleLoader = new MarkdownContentCollectionLoader<ArticleFrontMatter>(
            new ContentCollectionId("articles"),
            typedContent,
            entry => GeneratedArticles.Catalog.GetRoute(entry.Id),
            entry => new PageMetadata(
                entry.FrontMatter.Title,
                entry.FrontMatter.Summary,
                entry.FrontMatter.Draft,
                entry.FrontMatter.PublishedFrom,
                environments: entry.FrontMatter.Environments),
            frontMatterBinder: GeneratedArticles.Binder,
            transformationId: new ContentTransformationId("docs-sample-articles:v1"),
            isCacheable: true);
        var articleResult = await articleLoader.LoadAsync(cancellationToken);
        if (!articleResult.IsSuccess)
        {
            throw new SiteBuildExtensionException(articleResult.Diagnostics);
        }
        var articles = articleResult.Collection!;
        var topicPages = articles.GeneratePagesWithMarkdown(
            new ContentCollectionId("article-topics"),
            entry => entry.FrontMatter.Tags,
            group => new SitePage<TopicIndex>(
                new PageId($"topic:{group.Key}"),
                SiteRoute.ForDirectoryIndex($"topics/{group.Key}"),
                new TopicIndex(group.Key, group.Entries),
                new PageMetadata($"Articles tagged {group.Key}", $"Articles tagged {group.Key}")),
            (page, context) =>
            {
                var items = string.Join(
                    string.Empty,
                    page.Content.Entries.Select(entry =>
                        $"<li><a href=\"{new ContentRef<ContentEntry<ArticleFrontMatter, string>>(GeneratedArticles.Catalog.CollectionId, entry.Id, GeneratedArticles.Catalog.GetRoute(entry.Id)).GetUrl(context.Site.BaseUrl).ToAttributeValue()}\">{new HtmlText(entry.FrontMatter.Title)}</a></li>"));
                return context.RenderDocument(
                    $"<h1>{System.Net.WebUtility.HtmlEncode(page.Content.Topic)}</h1><ul>{items}</ul>");
            },
            (page, rendering) => "# " + MarkdownText(page.Content.Topic) + "\n\n" + string.Concat(page.Content.Entries.Select(entry =>
                "- " + MarkdownLink(entry.FrontMatter.Title, new ContentRef<ContentEntry<ArticleFrontMatter, string>>(GeneratedArticles.Catalog.CollectionId, entry.Id, GeneratedArticles.Catalog.GetRoute(entry.Id)).GetUrl(rendering.Site.BaseUrl).Value) + "\n")),
            rendererFingerprint: "docs-sample-topics-public:v1",
            transformationId: new ContentTransformationId("docs-sample-topics:v1"),
            isCacheable: true,
            derivedSurfaces: GeneratedPageDerivedSurfaces.Default | GeneratedPageDerivedSurfaces.Navigation);

        var articleSource = new SiteAsset("article-source", typedContent, "first.md", "downloads/first.md");
        var imageFallback = new ImageVariant("sample-png", "images/sample.png", 128, System.IO.Compression.CompressionLevel.Optimal);
        var image = assetDemo ? new ImageAsset(
            new SiteAsset("sample-image", Path.GetDirectoryName(imagePath)!, Path.GetFileName(imagePath), "images/original.png"),
            [imageFallback], imageFallback) : null;
        var options = new SiteGenerationOptions
        {
            Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.DocumentationProfile, Components = DocsSampleComponents.Registry },
            Assets = image is null ? [articleSource] : [articleSource, image.Source],
            AssetTransforms = image is null ? [] : [image.Transform],
            AssetCacheDirectory = assetDemo ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, ".lithosharp", "assets") : null,
            PublicDirectory = assetDemo ? Path.Combine(context.ProjectDirectory, "public") : null,
            BuildTimestamp = DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
            EnvironmentName = "Production",
            Quality = check ? new SiteQualityOptions() : null,
            Redirects = redirectDemo
                ? [new SiteRedirect(SiteRoute.ForFile("old-home.html"), SiteRoute.ForDirectoryIndex(""))]
                : [],
            ContentCollections = [articles.RenderDocsWithMarkdown(
                entry => new DocsPagePresentation
                {
                    Title = entry.FrontMatter.Title,
                    Description = entry.FrontMatter.Summary,
                    Order = entry.FrontMatter.Order
                },
                (entry, rendering) => rendering.RenderMarkdown(entry.Body) +
                    $"<p><a href=\"{GeneratedArticles.Entries.Typed_Content_Second.GetUrl(rendering.Site.BaseUrl).ToAttributeValue()}\">Read the related typed article</a></p>" +
                    $"<p><a href=\"{rendering.Assets.GetUrl(articleSource).ToAttributeValue()}\">{new HtmlText("Download sample source")}</a></p>" +
                    (image is null ? string.Empty : ResponsiveImage.Render(rendering.Assets, image.Descriptor, "LithoSharp sample icon").ToHtmlString() +
                        $"<p><a href=\"{rendering.Assets.GetUrl(image.Source).ToAttributeValue()}\">Original image</a> · <a href=\"/asset-demo.txt\">Public file</a></p>"),
                (entry, rendering) => entry.Body + "\n\n"
                    + MarkdownLink("Read the related typed article", GeneratedArticles.Entries.Typed_Content_Second.GetUrl(rendering.Site.BaseUrl).ToString()) + "\n\n"
                    + MarkdownLink("Download sample source", rendering.Assets.GetUrl(articleSource).ToString())
                    + (image is null ? string.Empty : "\n\n!" + MarkdownLink("LithoSharp sample icon", image.Descriptor.Fallback.GetUrl(rendering.Assets))
                        + "\n\n" + MarkdownLink("Original image", rendering.Assets.GetUrl(image.Source).ToString()) + " · " + MarkdownLink("Public file", "/asset-demo.txt")),
                rendererFingerprint: "article-docs-layout-public:v1",
                isThreadSafe: true), topicPages]
        };
        return new SiteDefinition(site, posts)
        {
            OutputDirectory = output,
            Customization = customization,
            Options = options
        };
    }

    // These helpers format newly authored sample content, not source-span edits.
    private static string MarkdownLink(string text, string url) => "[" + MarkdownText(text) + "](<"
        + url.Replace("&", "&#38;", StringComparison.Ordinal).Replace("<", "%3C", StringComparison.Ordinal)
            .Replace(">", "%3E", StringComparison.Ordinal).Replace(" ", "%20", StringComparison.Ordinal) + ">)";

    internal static string MarkdownText(string text) => string.Concat(text.Select(character =>
        char.IsControl(character) ? " " : "\\`*_{}[]()#+-.!|<>&".Contains(character) ? "\\" + character : character.ToString()));
}
