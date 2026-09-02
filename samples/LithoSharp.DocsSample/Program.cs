using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

var output = GetArgument(args, "--output") ?? Path.Combine(Environment.CurrentDirectory, "_site");
var content = GetArgument(args, "--content") ?? Path.Combine(AppContext.BaseDirectory, "content");
var typedContent = Path.Combine(AppContext.BaseDirectory, "typed-content");

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
var articleLoader = new MarkdownContentCollectionLoader<ArticleFrontMatter>(
    new ContentCollectionId("articles"),
    typedContent,
    entry => SiteRoute.ForDirectoryIndex($"articles/{Path.GetFileNameWithoutExtension(entry.Id.Value)}"),
    entry => new PageMetadata(
        entry.FrontMatter.Title,
        entry.FrontMatter.Summary,
        entry.FrontMatter.Draft,
        entry.FrontMatter.PublishedFrom,
        environments: entry.FrontMatter.Environments),
    transformationId: new ContentTransformationId("docs-sample-articles:v1"),
    isCacheable: true);
var articleResult = await articleLoader.LoadAsync();
if (!articleResult.IsSuccess)
{
    foreach (var diagnostic in articleResult.Diagnostics)
    {
        Console.Error.WriteLine($"{diagnostic.Id}: {diagnostic.Message}");
    }

    return 1;
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
                $"<li><a href=\"{SiteRoute.ForDirectoryIndex($"articles/{Path.GetFileNameWithoutExtension(entry.Id.Value)}").PublicPath}\">{System.Net.WebUtility.HtmlEncode(entry.FrontMatter.Title)}</a></li>"));
        return context.RenderDocument(
            $"<h1>{System.Net.WebUtility.HtmlEncode(page.Content.Topic)}</h1><ul>{items}</ul>");
    },
    transformationId: new ContentTransformationId("docs-sample-topics:v1"),
    isCacheable: true,
    derivedSurfaces: GeneratedPageDerivedSurfaces.Default | GeneratedPageDerivedSurfaces.Navigation);

var options = new SiteGenerationOptions
{
    BuildTimestamp = DateTimeOffset.Parse("2026-09-02T00:00:00Z"),
    EnvironmentName = "Production",
    ContentCollections = [new SiteContentCollection<ArticleFrontMatter, string>(
        articles,
        (entry, context) => context.RenderDocument(context.RenderMarkdown(entry.Body))), topicPages]
};
var generator = new SiteGenerator();
var firstResult = await generator.GenerateWithOptionsAsync(
    site, posts, output, clean: true, customization, options, CancellationToken.None);
var result = await generator.GenerateWithOptionsAsync(
    site,
    posts,
    output,
    clean: false,
    customization,
    options with { PreviousBuildPlan = firstResult.BuildPlan },
    CancellationToken.None);

Console.WriteLine(
    $"Generated {result.PostCount} Markdown post(s), {articles.Entries.Count} typed article(s), " +
    $"{result.BuildReport.GeneratedArtifacts.Count} artifact(s) into {result.OutputDirectory}.");
Console.WriteLine($"Build invalidations: {result.BuildReport.Invalidations.Count}.");
return 0;

static string? GetArgument(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
