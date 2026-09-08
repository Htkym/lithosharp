using LithoSharp;

var context = new SiteFactoryContext(Environment.CurrentDirectory);
var definition = await new MdxSiteFactory().CreateAsync(context);
try
{
    await new SiteGenerator().GenerateWithOptionsAsync(definition.Site, definition.Posts,
        Path.GetFullPath(definition.OutputDirectory, context.ProjectDirectory), false, definition.Customization, definition.Options, default);
}
finally
{
    foreach (var extension in definition.Options.Extensions.OfType<IAsyncDisposable>()) await extension.DisposeAsync();
}
