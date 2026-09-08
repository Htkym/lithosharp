using LithoSharp;

var root = Path.GetFullPath("../../..", AppContext.BaseDirectory);
var definition = await new MdxSampleFactory().CreateAsync(new(root));
try
{
    var result = await new SiteGenerator().GenerateWithOptionsAsync(definition.Site, definition.Posts,
        Path.GetFullPath(definition.OutputDirectory, root), args.Contains("--clean"), definition.Customization, definition.Options, default);
    Console.WriteLine($"Generated {result.GeneratedFiles.Count} files in {result.OutputDirectory}");
    foreach (var extension in definition.Options.Extensions) Console.WriteLine(extension.GetInspection());
}
finally
{
    foreach (var extension in definition.Options.Extensions.OfType<IAsyncDisposable>()) await extension.DisposeAsync();
}
