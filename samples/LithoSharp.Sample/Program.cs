using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using LithoSharp.Routing;

var output = GetArgument(args, "--output") ?? Path.Combine(Environment.CurrentDirectory, "_site");
var content = GetArgument(args, "--content") ?? Path.Combine(AppContext.BaseDirectory, "content");
var check = args.Contains("--check", StringComparer.Ordinal);
var redirectDemo = args.Contains("--redirect-demo", StringComparer.Ordinal);

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

SiteGenerationResult result;
try
{
    result = await new SiteGenerator().GenerateWithOptionsAsync(
        site, posts, output, clean: true, customization, options, CancellationToken.None);
}
catch (SiteQualityValidationException exception)
{
    Console.Error.WriteLine(exception.Report.Format(SiteDiagnosticFormat.Text));
    return 1;
}

Console.WriteLine($"Generated {result.PostCount} post(s) into {result.OutputDirectory}.");
if (check) Console.WriteLine(result.QualityReport.Format(SiteDiagnosticFormat.Text));
return 0;

static string? GetArgument(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
