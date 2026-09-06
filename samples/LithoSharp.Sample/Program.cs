using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using LithoSharp.Routing;

var output = GetArgument(args, "--output") ?? Path.Combine(Environment.CurrentDirectory, "_site");
var check = args.Contains("--check", StringComparer.Ordinal);
var definition = await new BlogSampleFactory
{
    ContentDirectory = GetArgument(args, "--content"),
    Check = check,
    RedirectDemo = args.Contains("--redirect-demo", StringComparer.Ordinal)
}.CreateAsync(new SiteFactoryContext(AppContext.BaseDirectory));
var site = definition.Site;
var posts = definition.Posts;
var customization = definition.Customization;
var options = definition.Options;

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
