namespace LithoSharp;

public sealed partial class SiteGenerator
{
    internal const string ThemeRestorationScript = "try{const t=localStorage.getItem('lithosharp-theme');if(t==='light'||t==='dark')document.documentElement.dataset.siteTheme=t;}catch{}";

    private static bool HasExternalThemeRestoration(RenderContext configuration) =>
        configuration.Theme.EnableThemeSwitching && configuration.Theme.UseExternalThemeRestoration;

    private static string RenderNotFound(RenderContext configuration, SiteTemplateNavigationNode? navigation = null)
    {
        var text = configuration.Text;
        var body = $"<h1>{Html.Encode(text.NotFoundTitle)}</h1>\n<p>{Html.Encode(text.NotFoundDescription)}</p>\n"
            + $"<p><a href=\"{Html.Encode(configuration.Routes.PublicPath(configuration.Routes.Home))}\">{Html.Encode(text.NotFoundHomeLinkLabel)}</a></p>";
        var metadata = new Pages.PageMetadata(text.NotFoundTitle, text.NotFoundDescription) { NoIndex = true };
        return navigation is null
            ? Layout(configuration, text.NotFoundTitle, body, configuration.Routes.NotFound.RelativeOutputPath,
                description: text.NotFoundDescription, metadata: metadata)
            : DocsLayout(configuration, navigation, text.NotFoundTitle, body, configuration.Routes.NotFound.RelativeOutputPath,
                currentPagePath: null, tableOfContents: null, description: text.NotFoundDescription, metadata: metadata);
    }
}
