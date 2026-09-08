using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;

public sealed class MdxSampleFactory : ISiteFactory
{
    public string Hydration { get; init; } = "selective";
    public bool Offline { get; init; } = true;
    public string Title { get; init; } = "LithoSharp MDX";
    public Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        var worker = Path.GetFullPath("../../src/LithoSharp.Mdx/worker", context.ProjectDirectory);
        var docs = new DocumentationSite(new(context.ProjectDirectory, worker) { Cacheable = true, Hydration = Hydration }) { Browser = new() { Offline = Offline } };
        docs.AddCollection(new("guide", [new("current", "en", Path.Combine(context.ProjectDirectory, "content"), "guide")]) { UseMdx = true });
        return Task.FromResult(new SiteDefinition(new SiteSettings { Title = Title, BaseUrl = "https://example.com/", Language = "en" }, [])
        {
            OutputDirectory = ".artifacts/site", Customization = new() { Template = new DocsSiteTemplate { EnableSearch = true }, GenerateLlmsTxt = true },
            Options = new() { Extensions = [docs], BuildTimestamp = DateTimeOffset.Parse("2026-09-08T00:00:00Z") }
        });
    }
}
