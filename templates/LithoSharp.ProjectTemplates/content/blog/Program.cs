using LithoSharp;

var context = new SiteFactoryContext(Environment.CurrentDirectory);
var definition = await new BlogSiteFactory().CreateAsync(context);
var output = Path.GetFullPath(definition.OutputDirectory, context.ProjectDirectory);
await new SiteGenerator().GenerateWithOptionsAsync(
    definition.Site,
    definition.Posts,
    output,
    clean: false,
    definition.Customization,
    definition.Options,
    CancellationToken.None);
