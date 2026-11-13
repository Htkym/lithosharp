using LithoSharp;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Quality;

public sealed class DocsSiteFactory : ISiteFactory
{
    public async Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        var loaded = await MarkdownContentCollectionLoader<GuideFrontMatter>.FromCatalog(
            Guides.Catalog,
            context.ProjectDirectory,
            entry => new PageMetadata(entry.FrontMatter.Title, entry.FrontMatter.Description),
            frontMatterBinder: Guides.Binder,
            transformationId: new ContentTransformationId("docs-content:v1"),
            isCacheable: true).LoadAsync(cancellationToken);
        if (!loaded.IsSuccess)
            throw new SiteBuildExtensionException(loaded.Diagnostics);

        var docs = loaded.Collection!.RenderDocs(
            entry => new DocsPagePresentation
            {
                Title = entry.FrontMatter.Title,
                Description = entry.FrontMatter.Description,
                Order = entry.FrontMatter.Order
            },
            (entry, rendering) => rendering.RenderMarkdown(entry.Body) +
                $"<p><a href=\"{Guides.Entries.Content_Getting_Started.GetUrl(rendering.Site.BaseUrl).ToAttributeValue()}\">Getting started</a></p>",
            rendererFingerprint: "docs-layout:v1",
            isThreadSafe: true);
        return new SiteDefinition(new SiteSettings
        {
            Title = "LithoSharp site",
            Description = "Documentation managed with C# types and builds.",
            BaseUrl = "https://example.com/",
            Language = "en",
            Author = "LithoSharp",
            TimeZone = "UTC"
        }, [])
        {
            OutputDirectory = "dist",
            Customization = new SiteCustomization
            {
                Template = new DocsSiteTemplate(),
                Theme = new SiteThemeOptions { UseExternalThemeRestoration = true }
            },
            Options = new SiteGenerationOptions
            {
                ContentCollections = [docs],
                Quality = new SiteQualityOptions(
                    failureThreshold: SiteDiagnosticSeverity.Error,
                    externalLinks: null)
            }
        };
    }
}
