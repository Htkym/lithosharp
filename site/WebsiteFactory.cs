using System.Text.Json;
using LithoSharp;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Documentation;
using LithoSharp.Images;
using LithoSharp.Mdx;
using LithoSharp.OpenKnowledge;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Website.Examples;

public sealed class WebsiteFactory : ISiteFactory
{
    public string OutputDirectory { get; init; } = "dist";
    public string BaseUrl { get; init; } = "https://example.invalid/";
    public string? GeneratedInputDirectory { get; init; }

    public async Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        var root = context.ProjectDirectory;
        var generatedInputs = GeneratedInputDirectory ?? Path.Combine(Path.GetTempPath(), "ls-api", Guid.NewGuid().ToString("N"));
        var site = new SiteSettings { Title = "LithoSharp", Description = "Typed static sites with C# and Markdown.",
            BaseUrl = BaseUrl, Language = "en", Author = "LithoSharp", TimeZone = "UTC" };
        var social = new SiteAssetOutput("website-social", "images/social.png", "image/png");
        var docs = new DocumentationSite(new MdxOptions(root) { Cacheable = true })
        {
            Layout = new WebsiteDocumentationLayout(),
            Browser = new() { Search = true, Navigation = true, Theme = true },
            Translations = new TranslationCatalog("en", JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
                File.ReadAllBytes(Path.Combine(root, "ui-messages.json")))!
                .ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value, StringComparer.Ordinal)),
            TranslationPolicy = MissingTranslationPolicy.Error
        };
        var referenceAssets = PublicDocumentation.Register(docs, root, Path.Combine(generatedInputs, "reference"), BaseUrl);
        docs.AddCollection(new DocumentationCollection("handbook", [
            new("3.0", "en", Path.Combine(root, "content", "en"), "en"),
            new("3.0", "ja", Path.Combine(root, "content", "ja"), "ja")])
        {
            SourceLocale = "en", MissingDocuments = MissingDocumentPolicy.Error,
            SemanticRules = DocumentSemanticRuleSet.Documentation,
            TrackTranslationReviews = true, TranslationReviews = ReadReviews(root)
        });
        var loaded = await MarkdownContentCollectionLoader<ExampleFrontMatter>.FromCatalog(
            GeneratedExamples.Catalog, root,
            entry => new PageMetadata(entry.FrontMatter.Title, draft: entry.FrontMatter.Status == ExampleStatus.Draft)
                { Language = entry.FrontMatter.Locale },
            GeneratedExamples.Binder, transformationId: new("website-examples/1"), isCacheable: true)
            .LoadAsync(cancellationToken);
        if (!loaded.IsSuccess)
            throw new SiteBuildExtensionException(loaded.Diagnostics);
        var knowledge = await OkfBundle.ReadAsync(Path.Combine(root, "knowledge"),
            new OkfReadOptions { AsOf = DateTimeOffset.Parse("2026-11-13T00:00:00Z"), BaseUrl = BaseUrl }, cancellationToken);
        if (!knowledge.IsSuccess)
            throw new SiteBuildExtensionException(knowledge.Diagnostics);
        var api = PublicApiDocumentation.Create(root, Path.Combine(generatedInputs, "api"), new WebsiteApiLayout(new(social)));
        const string exampleId = "M:LithoSharp.Website.Examples.WebsiteExamples.HasPublishedVersion(System.Boolean,System.String)";
        var symbols = new ApiAssemblySource("LithoSharp.Website", [
            new ApiSymbol("T:LithoSharp.Website.Examples.WebsiteExamples", "public static class WebsiteExamples", ApiVisibility.Public),
            new ApiSymbol(exampleId, "public static bool HasPublishedVersion(bool draft, string? supportedVersion)", ApiVisibility.Public)
            { Parameters = [new("draft", "bool"), new("supportedVersion", "string?")], ReturnType = "bool" }
        ], new Dictionary<string, DocumentKey>());
        foreach (var locale in new[] { "en", "ja" })
            api.AddXml(new("examples", new("3.0", locale, root, $"api/{locale}"),
                locale == "ja" ? Path.Combine(root, "api", "ja", "WebsiteExamples.xml")
                    : Path.Combine(AppContext.BaseDirectory, "LithoSharp.Website.xml")), symbols);
        var socialTransform = new SiteAssetTransform("website-social", "website-pixels/1;" + RgbaImage.ProcessingProfile,
            [], [social], async (output, token) =>
            {
                var canvas = RgbaImage.Create(1200, 630, 20, 30, 48);
                canvas.FillRectangle(80, 80, 1040, 24, 83, 211, 187);
                await output.WriteAsync(social, PngCodec.Encode(canvas, cancellationToken: token),
                    new ImageMetadata(canvas.Width, canvas.Height, "image/png", ImageMetadataProvenance.Generated), token);
            });
        return new SiteDefinition(site, [])
        {
            OutputDirectory = Path.GetFullPath(OutputDirectory, root),
            Customization = new()
            {
                Template = new WebsiteTemplate(), GenerateLlmsTxt = true,
                Theme = new() { UseExternalThemeRestoration = true },
            },
            Options = new()
            {
                Markdown = new() { ProfileId = MarkdownOptions.DocumentationProfile,
                    Components = new([MarkdownComponentRegistration.Create("version-badge", new VersionBadge(),
                        GeneratedBadges.Binder, "website-version-badge/1")]) },
                Extensions = [docs, api], BuildTimestamp = DateTimeOffset.Parse("2026-11-13T00:00:00Z"),
                Quality = new SiteQualityOptions(), CommonSocialImage = new ImageSource(social),
                AssetTransforms = [socialTransform], GeneratedAssets = [.. knowledge.CreateAuxiliaryAssets("knowledge"), .. referenceAssets, WebsiteApiLayout.Stylesheet],
                ContentCollections = [loaded.Collection!.RenderDocs(entry => new()
                    { Title = entry.FrontMatter.Title, Version = "3.0", Locale = entry.FrontMatter.Locale },
                    (entry, rendering) =>
                    {
                        var reference = entry.FrontMatter.Locale == "ja"
                            ? GeneratedExamples.Entries.Examples_En_Typed_Content
                            : GeneratedExamples.Entries.Examples_Ja_Typed_Content;
                        return rendering.RenderMarkdown(entry.Body) + "<p><a href=\"" + reference.GetUrl(rendering.Site.BaseUrl).ToAttributeValue()
                            + "\">" + Html.Encode(entry.FrontMatter.Locale == "ja" ? "English" : "日本語") + "</a></p>";
                    }, "website-examples/1", isThreadSafe: true),
                    knowledge.RenderDocs(new ContentCollectionId("knowledge"), document => new PageMetadata(
                        document.Metadata?.Title ?? document.ConceptId ?? document.Path, document.Metadata?.Description)
                        { Language = document.Path.StartsWith("ja/", StringComparison.Ordinal) ? "ja" : "en" },
                        policyFingerprint: "website-knowledge-locales/1", outputPrefix: "knowledge")]
            }
        };
    }

    private static IReadOnlyList<DocumentTranslationReview> ReadReviews(string root)
    {
        var path = Path.Combine(root, "translation-reviews.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("reviews").EnumerateArray().Select(review =>
        {
            var id = review.GetProperty("id").GetString()!;
            return new DocumentTranslationReview(
                new(new("handbook", "3.0", "en", id), $"content/en/{id}.md", review.GetProperty("sourceSha256").GetString()!),
                new(new("handbook", "3.0", "ja", id), $"content/ja/{id}.md", review.GetProperty("translationSha256").GetString()!));
        }).ToArray();
    }
}
