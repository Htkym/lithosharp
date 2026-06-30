using PageSharp;
using PageSharp.Configuration;
using PageSharp.Content;

var output = GetArgument(args, "--output") ?? Path.Combine(Environment.CurrentDirectory, "_site");
var content = GetArgument(args, "--content") ?? Path.Combine(AppContext.BaseDirectory, "content");

var site = new SiteSettings
{
    Title = "PageSharp Sample",
    Description = "A minimal static site generated with PageSharp.",
    BaseUrl = "https://example.com/",
    Language = "en",
    Author = "PageSharp",
    TimeZone = "UTC"
};

var customization = new SiteCustomization
{
    Theme = new SiteThemeOptions
    {
        BrandPrefix = "pagesharp / ",
        DefaultSocialSubtitle = "Built with PageSharp",
        AdditionalCss = ":root { --accent: #7c9eff; }"
    },
    GenerateLlmsTxt = true
};

var posts = await new MarkdownPostReader().ReadAllAsync(content);
SiteGenerator.Validate(site, content, posts, customization);
var result = await new SiteGenerator().GenerateAsync(site, posts, output, clean: true, customization);

Console.WriteLine($"Generated {result.PostCount} post(s) into {result.OutputDirectory}.");

static string? GetArgument(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
