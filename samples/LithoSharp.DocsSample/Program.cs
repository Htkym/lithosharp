using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;

var output = GetArgument(args, "--output") ?? Path.Combine(Environment.CurrentDirectory, "_site");
var content = GetArgument(args, "--content") ?? Path.Combine(AppContext.BaseDirectory, "content");

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
var result = await new SiteGenerator().GenerateAsync(site, posts, output, clean: true, customization);

Console.WriteLine($"Generated {result.PostCount} post(s) into {result.OutputDirectory}.");

static string? GetArgument(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
