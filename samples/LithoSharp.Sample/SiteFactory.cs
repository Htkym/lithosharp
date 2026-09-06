using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using LithoSharp.Routing;

public sealed class BlogSampleFactory : ISiteFactory
{
    public string? ContentDirectory { get; init; }
    public bool Check { get; init; }
    public bool RedirectDemo { get; init; }

    public async Task<SiteDefinition> CreateAsync(
        SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        var content = ContentDirectory ?? Path.Combine(context.ProjectDirectory, "content");
        var check = Check;
        var redirectDemo = RedirectDemo;
        var site = new SiteSettings
        {
            Title = "LithoSharp Blog Sample",
            Description = "A minimal blog generated with LithoSharp.",
            BaseUrl = "https://example.com/",
            Language = "en",
            Author = "LithoSharp",
            TimeZone = "UTC"
        };

        var customization = new SiteCustomization
        {
            Template = new BlogSiteTemplate(),
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
        var options = new SiteGenerationOptions
        {
            Quality = check ? new SiteQualityOptions() : null,
            Redirects = redirectDemo
                ? [new SiteRedirect(SiteRoute.ForFile("old-home.html"), SiteRoute.ForDirectoryIndex(""))]
                : []
        };

        return new SiteDefinition(site, posts) { Customization = customization, Options = options };
    }
}
