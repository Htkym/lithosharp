using LithoSharp;
using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using LithoSharp.Routing;

try
{
    var context = new SiteFactoryContext(Environment.CurrentDirectory);
    var definition = await new DocsSiteFactory().CreateAsync(context);
    var output = Path.GetFullPath(definition.OutputDirectory, context.ProjectDirectory);
    var result = await new SiteGenerator().GenerateWithOptionsAsync(
        definition.Site,
        definition.Posts,
        output,
        clean: false,
        definition.Customization,
        definition.Options,
        CancellationToken.None);
    Console.WriteLine(result.QualityReport.Format(SiteDiagnosticFormat.Text));
    return 0;
}
catch (SiteBuildExtensionException exception)
{
    return Fail(exception.Diagnostics);
}
catch (SiteQualityValidationException exception)
{
    return Fail(exception.Diagnostics);
}
catch (SiteRouteValidationException exception)
{
    return Fail(exception.Diagnostics);
}
catch (SiteBuildPlanValidationException exception)
{
    return Fail(exception.Diagnostics);
}

static int Fail(IReadOnlyList<SiteDiagnostic> diagnostics)
{
    Console.Error.WriteLine(new SiteQualityReport(diagnostics).Format(SiteDiagnosticFormat.Text));
    return 1;
}
