using LithoSharp;
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
                DefaultSocialSubtitle = "Built with LithoSharp",
                AdditionalCss = ":root { --accent: #7c9eff; }"
            },
            GenerateLlmsTxt = true
        };

        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        SiteGenerator.Validate(site, content, posts, customization);
        var articleRoutes = new[] { GeneratedArticles.Entries.Typed_Content_First, GeneratedArticles.Entries.Typed_Content_Second }
            .ToDictionary(reference => reference.Id, reference => reference.Route);
        var articleLoader = new MarkdownContentCollectionLoader<ArticleFrontMatter>(
            new ContentCollectionId("articles"),
            typedContent,
            entry => articleRoutes[entry.Id],
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
            throw new InvalidOperationException(string.Join(Environment.NewLine,
                articleResult.Diagnostics.Select(diagnostic => $"{diagnostic.Id}: {diagnostic.Message}")));
        }
        var articles = articleResult.Collection!;
        var topicPages = articles.GeneratePages(
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
                        $"<li><a href=\"{articleRoutes[entry.Id].PublicPath}\">{System.Net.WebUtility.HtmlEncode(entry.FrontMatter.Title)}</a></li>"));
                return context.RenderDocument(
                    $"<h1>{System.Net.WebUtility.HtmlEncode(page.Content.Topic)}</h1><ul>{items}</ul>");
            },
            transformationId: new ContentTransformationId("docs-sample-topics:v1"),
            isCacheable: true,
            derivedSurfaces: GeneratedPageDerivedSurfaces.Default | GeneratedPageDerivedSurfaces.Navigation);

        var articleSource = new SiteAsset("article-source", typedContent, "first.md", "downloads/first.md");
        var image = assetDemo ? new ImageAsset(
            new SiteAsset("sample-image", Path.GetDirectoryName(imagePath)!, Path.GetFileName(imagePath), "images/original.png"),
            [new ImageVariant("sample-png", "images/sample.png", 128, ImageFormat.Png),
             new ImageVariant("sample-webp", "images/sample.webp", 128, ImageFormat.WebP)]) : null;
        var options = new SiteGenerationOptions
        {
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
            ContentCollections = [new SiteContentCollection<ArticleFrontMatter, string>(
                articles, new ArticleLayout(articleSource, image)) { RendererFingerprint = "article-layout:v1", IsThreadSafe = true }, topicPages]
        };
        return new SiteDefinition(site, posts)
        {
            OutputDirectory = output, Customization = customization, Options = options
        };
    }
}

sealed class ArticleLayout(SiteAsset source, ImageAsset? image) : IPageLayout<ContentEntry<ArticleFrontMatter, string>>
{
    public IHtmlContent Render(SitePage<ContentEntry<ArticleFrontMatter, string>> page, PageRenderingContext context) =>
        context.RenderDocument(page, Html.UnsafeRaw(
            context.RenderMarkdown(page.Content.Body).ToHtmlString() +
            $"<p><a href=\"{context.Assets.GetUrl(source).ToAttributeValue()}\">{new HtmlText("Download sample source")}</a></p>" +
            (image is null ? string.Empty : ResponsiveImage.Render(context.Assets, image, "LithoSharp sample icon").ToHtmlString() +
                $"<p><a href=\"{context.Assets.GetUrl(image.Source).ToAttributeValue()}\">Original image</a> · <a href=\"/asset-demo.txt\">Public file</a></p>")));
}
