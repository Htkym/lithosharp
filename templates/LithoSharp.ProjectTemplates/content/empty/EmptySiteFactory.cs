using LithoSharp;
using LithoSharp.Configuration;

public sealed class EmptySiteFactory : ISiteFactory
{
    public async Task<SiteDefinition> CreateAsync(
        SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        var body = await File.ReadAllTextAsync(
            Path.Combine(context.ProjectDirectory, "content", "index.md"), cancellationToken);
        return new SiteDefinition(new SiteSettings
        {
            Title = "My site", Description = "A custom C# site.",
            BaseUrl = "https://example.com/", Language = "en", TimeZone = "UTC"
        }, [])
        {
            Customization = new SiteCustomization { Template = new EmptyTemplate(body) }
        };
    }
}
