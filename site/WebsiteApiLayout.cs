using LithoSharp;
using LithoSharp.Pages;

internal sealed class WebsiteApiLayout(ImageSource socialImage, WebsiteMenu menu) : IPageLayout<PageLayoutContent>
{
    internal const string Fingerprint = "website-api-layout/4";

    public IHtmlContent Render(SitePage<PageLayoutContent> page, PageRenderingContext context) =>
        Html.UnsafeRaw(WebsiteChrome.Render(page, context, socialImage, menu).ToHtmlString()
            .Replace("docs-body website-body", "docs-body website-body website-api", StringComparison.Ordinal));
}
