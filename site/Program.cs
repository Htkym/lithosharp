using System.Text.Json;
using System.Xml.Linq;
using System.Security.Cryptography;
using LithoSharp;
using LithoSharp.Content;
using LithoSharp.Documentation;
using LithoSharp.Quality;

var root = Argument("--project") ?? FindRoot();
if (args.Contains("--export-api-material", StringComparer.Ordinal))
{
    var directory = Argument("--generated-inputs") ?? throw new ArgumentException("API export requires --generated-inputs.");
    _ = PublicApiDocumentation.Create(root, directory, null, exportMaterialOnly: true);
    Console.WriteLine("Exported package public symbols and compiler documentation units into " + directory);
    return 0;
}
CheckLocalizedApi(root);
if (args.Contains("--verify-snippets", StringComparer.Ordinal))
{
    var snippets = new[] { "en", "ja" }.Select(locale => new DocumentSnippet(
        "published-version", $"examples/{locale}/typed-content.md", "checks/LithoSharp.Website.Checks.csproj"));
    var inputs = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or "dist"))
        .Select(path => ContentDependency.FromFile(Path.GetRelativePath(root, path))).ToList();
    inputs.Add(ContentDependency.FromValue("LithoSharpVersion", Argument("--version") ?? "3.0.0"));
    var results = await DocumentationVerification.VerifySnippetsAsync(root, snippets, inputs, "Release");
    Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    return results.All(result => result.State == DocumentSnippetVerificationState.Success) ? 0 : 1;
}
var definition = await new WebsiteFactory
{
    OutputDirectory = Argument("--output") ?? "dist",
    BaseUrl = Argument("--base-url") ?? "https://example.invalid/",
    GeneratedInputDirectory = Argument("--generated-inputs"),
}.CreateAsync(new SiteFactoryContext(root));
try
{
    var result = await new SiteGenerator().GenerateWithOptionsAsync(definition.Site, definition.Posts,
        definition.OutputDirectory, clean: false, definition.Customization, definition.Options, CancellationToken.None);
    Console.WriteLine($"Generated {result.BuildReport.GeneratedArtifacts.Count} artifacts into {result.OutputDirectory}.");
    return 0;
}
catch (SiteQualityValidationException exception)
{
    Console.Error.WriteLine(exception.Report.Format(SiteDiagnosticFormat.Text));
    return 1;
}

string? Argument(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string FindRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "LithoSharp.Website.csproj")))
            return directory.FullName;
    throw new DirectoryNotFoundException("Use --project to select the website source directory.");
}

static void CheckLocalizedApi(string root)
{
    using var reviews = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "api", "translation-reviews.json")));
    var example = reviews.RootElement.GetProperty("example");
    foreach (var (pathKey, hashKey) in new[] { ("sourcePath", "sourceSha256"), ("translationPath", "translationSha256") })
        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, example.GetProperty(pathKey).GetString()!)))) != example.GetProperty(hashKey).GetString())
            throw new InvalidDataException("The example API translation needs an explicit source/translation review.");
    var canonical = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "LithoSharp.Website.xml"));
    var localized = XDocument.Load(Path.Combine(root, "api", "ja", "WebsiteExamples.xml"));
    static Dictionary<string, XElement> Members(XDocument document) => document.Descendants("member")
        .Where(member => member.Attribute("name")!.Value.Contains("LithoSharp.Website.Examples.", StringComparison.Ordinal))
        .ToDictionary(member => member.Attribute("name")!.Value, StringComparer.Ordinal);
    var before = Members(canonical);
    var after = Members(localized);
    if (!before.Keys.Order(StringComparer.Ordinal).SequenceEqual(after.Keys.Order(StringComparer.Ordinal)))
        throw new InvalidDataException("Japanese API view must cover exactly the compiled example API IDs.");
    foreach (var (id, member) in before)
    {
        var translation = after[id];
        if (string.IsNullOrWhiteSpace(translation.Element("summary")?.Value)
            || !member.Elements("param").Select(parameter => parameter.Attribute("name")!.Value)
                .SequenceEqual(translation.Elements("param").Select(parameter => parameter.Attribute("name")!.Value))
            || translation.Elements("param").Any(parameter => string.IsNullOrWhiteSpace(parameter.Value))
            || member.Element("returns") is not null && string.IsNullOrWhiteSpace(translation.Element("returns")?.Value))
            throw new InvalidDataException("Incomplete Japanese API view: " + id);
    }
}
