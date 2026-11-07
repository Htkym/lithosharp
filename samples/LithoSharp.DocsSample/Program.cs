using LithoSharp;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Images;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;

var output = GetArgument(args, "--output") ?? Path.Combine(Environment.CurrentDirectory, "_site");
var check = args.Contains("--check", StringComparer.Ordinal);
SiteDefinition definition;
try
{
    definition = await new DocsSampleFactory
    {
        ContentDirectory = GetArgument(args, "--content"),
        OutputDirectory = output,
        Check = check,
        RedirectDemo = args.Contains("--redirect-demo", StringComparer.Ordinal),
        AssetDemo = args.Contains("--asset-demo", StringComparer.Ordinal)
    }.CreateAsync(new SiteFactoryContext(FindProjectDirectory()));
}
catch (SiteBuildExtensionException exception)
{
    Console.Error.WriteLine(new SiteQualityReport(exception.Diagnostics).Format(SiteDiagnosticFormat.Text));
    return 1;
}
var site = definition.Site;
var posts = definition.Posts;
var customization = definition.Customization;
var options = definition.Options;

var generator = new SiteGenerator();
SiteGenerationResult result;
try
{
    var firstResult = await generator.GenerateWithOptionsAsync(
        site, posts, output, clean: true, customization, options, CancellationToken.None);
    result = await generator.GenerateWithOptionsAsync(
        site,
        posts,
        output,
        clean: false,
        customization,
        options with { PreviousBuildPlan = firstResult.BuildPlan },
        CancellationToken.None);
}
catch (SiteQualityValidationException exception)
{
    Console.Error.WriteLine(exception.Report.Format(SiteDiagnosticFormat.Text));
    return 1;
}

Console.WriteLine(
    $"Generated {result.PostCount} Markdown post(s), {options.ContentCollections.OfType<SiteContentCollection<ArticleFrontMatter, string>>().Single().Collection.Entries.Count} typed article(s), " +
    $"{result.BuildReport.GeneratedArtifacts.Count} artifact(s) into {result.OutputDirectory}.");
Console.WriteLine($"Build invalidations: {result.BuildReport.Invalidations.Count}.");
Console.WriteLine($"Cache hits: {result.BuildReport.CacheHitCount}; misses: {result.BuildReport.CacheMissCount}.");
if (check)
    Console.WriteLine(result.QualityReport.Format(SiteDiagnosticFormat.Text));
return 0;

static string? GetArgument(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string FindProjectDirectory()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "LithoSharp.DocsSample.csproj")))
            return directory.FullName;
    }
    return AppContext.BaseDirectory;
}
