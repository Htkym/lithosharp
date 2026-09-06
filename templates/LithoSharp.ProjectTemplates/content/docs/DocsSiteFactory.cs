using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;

public sealed class DocsSiteFactory : ISiteFactory
{
    public async Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        var root = context.ProjectDirectory;
        var posts = await new MarkdownPostReader().ReadAllAsync(Path.Combine(root, "content"));
        return new SiteDefinition(new SiteSettings { Title = "LithoSharp site", Description = "A LithoSharp documentation site.", BaseUrl = "https://example.com/", Language = "en", Author = "LithoSharp", TimeZone = "UTC" }, posts)
        { OutputDirectory = "dist" };
    }
}
