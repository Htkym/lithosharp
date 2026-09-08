using LithoSharp;
using LithoSharp.Pages;
using LithoSharp.Routing;

public sealed class EmptyLayout : IPageLayout<string>
{
    public IHtmlContent Render(SitePage<string> page, PageRenderingContext context) =>
        Html.UnsafeRaw($"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{new HtmlText(page.Metadata.Title ?? context.Site.Title)}</title>
            <meta name="description" content="{new HtmlAttributeValue("A custom C# site.")}">
            <meta property="og:title" content="{new HtmlAttributeValue(page.Metadata.Title ?? context.Site.Title)}">
            <meta property="og:description" content="{new HtmlAttributeValue("A custom C# site.")}">
            <meta property="og:url" content="{SiteUrl.FromAbsolute(new Uri(new Uri(context.Site.BaseUrl), page.Route.PublicPath).AbsoluteUri).ToAttributeValue()}">
            <link rel="canonical" href="{SiteUrl.FromAbsolute(new Uri(new Uri(context.Site.BaseUrl), page.Route.PublicPath).AbsoluteUri).ToAttributeValue()}"></head>
            <body><main>{context.RenderMarkdown(page.Content).ToHtmlString()}</main></body>
            </html>
            """);
}

internal sealed class EmptyTemplate(string body) : ISiteTemplate
{
    public Task<SiteTemplateResult> RenderAsync(
        SiteTemplateContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = new SitePage<string>(new PageId("home"),
            SiteRoute.ForDirectoryIndex("", context.Site.BaseUrl), body, new PageMetadata(context.Site.Title));
        return Task.FromResult(new SiteTemplateResult([
            new SiteTemplateFile
            {
                RelativePath = page.Route.RelativeOutputPath,
                Content = context.RenderLayout(page, new EmptyLayout()).ToHtmlString()
            }
        ]));
    }
}
