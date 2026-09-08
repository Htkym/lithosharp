using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;
public sealed class BlogSiteFactory : ISiteFactory
{
    public async Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        var posts = await new MarkdownPostReader().ReadAllAsync(Path.Combine(context.ProjectDirectory, "content"));
        return new SiteDefinition(new SiteSettings { Title = "LithoSharp blog", Description = "A LithoSharp blog site.", BaseUrl = "https://example.com/", Language = "en", Author = "LithoSharp", TimeZone = "UTC" }, posts) { OutputDirectory = "dist" };
    }
}
