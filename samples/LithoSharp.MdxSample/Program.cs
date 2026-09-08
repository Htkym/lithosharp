using LithoSharp;

var root = Path.GetFullPath("../../..", AppContext.BaseDirectory);
var definition = await new MdxSampleFactory { Hydration = args.Contains("--page-hydration") ? "page" : "selective",
    Offline = !args.Contains("--offline-disabled"), Title = args.Contains("--next-revision") ? "Next revision" : "LithoSharp MDX" }.CreateAsync(new(root));
var outputArgument = Array.IndexOf(args, "--output");
var outputDirectory = outputArgument >= 0 && outputArgument + 1 < args.Length ? args[outputArgument + 1] : definition.OutputDirectory;
try
{
    var result = await new SiteGenerator().GenerateWithOptionsAsync(definition.Site, definition.Posts,
        Path.GetFullPath(outputDirectory, root), args.Contains("--clean"), definition.Customization, definition.Options, default);
    Console.WriteLine($"Generated {result.GeneratedFiles.Count} files in {result.OutputDirectory}");
    if (args.Contains("--inspect")) foreach (var extension in definition.Options.Extensions) Console.WriteLine(extension.GetInspection());
}
finally
{
    foreach (var extension in definition.Options.Extensions.OfType<IAsyncDisposable>()) await extension.DisposeAsync();
}
