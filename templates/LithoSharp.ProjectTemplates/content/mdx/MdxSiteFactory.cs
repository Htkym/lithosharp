using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;

public sealed class MdxSiteFactory : ISiteFactory
{
    public Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        var docs = new DocumentationSite(new(context.ProjectDirectory) { Cacheable = true, Hydration = "selective" }) { Browser = new() };
        docs.AddCollection(new("guide", [new("current", "en", Path.Combine(context.ProjectDirectory, "content"), "guide")]) { UseMdx = true });
        return Task.FromResult(new SiteDefinition(new SiteSettings { Title = "LithoSharp site", BaseUrl = "https://example.com/", Language = "en" }, [])
        {
            OutputDirectory = "dist", Customization = new() { Template = new DocsSiteTemplate { EnableSearch = true } },
            Options = new() { Extensions = [docs] }
        });
    }
}
