using System.Text;
using LithoSharp;
using LithoSharp.Pages;

internal sealed class WebsiteApiLayout(ImageSource socialImage) : IPageLayout<PageLayoutContent>
{
    internal const string Fingerprint = "website-api-layout/1";
    internal static SiteGeneratedAsset Stylesheet { get; } = new("website-api-layout", "site-api.css", Encoding.UTF8.GetBytes("""
        :root{font-family:system-ui,sans-serif;color-scheme:light dark}body{max-width:80rem;margin:auto;padding:1rem;line-height:1.65}header,footer nav{display:flex;flex-wrap:wrap;gap:1rem;align-items:center}header{border-bottom:1px solid;padding-bottom:1rem}main{min-height:65vh;overflow-wrap:anywhere}pre{overflow:auto;padding:1rem;background:light-dark(#f1f5f9,#1e293b)}code{font-size:.95em}a{color:light-dark(#075985,#7dd3fc)}a:focus-visible{outline:3px solid #0ea5e9;outline-offset:3px}.skip-link{position:absolute;left:-10000px}.skip-link:focus{position:static}footer{border-top:1px solid;padding-top:1rem}dt{font-weight:700}dd{margin-bottom:1rem}
        """));

    public IHtmlContent Render(SitePage<PageLayoutContent> page, PageRenderingContext context)
    {
        var language = page.Metadata.Language ?? context.Site.Language;
        var japanese = language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        var locale = japanese ? "ja" : "en";
        var title = page.Metadata.Title ?? context.Site.Title;
        var description = page.Metadata.Description ?? (japanese ? "C# と Markdown による型付きの静的サイト。" : context.Site.Description);
        var canonical = SiteUrl.FromAbsolute(new Uri(new Uri(context.Site.BaseUrl), page.Route.PublicPath).AbsoluteUri);
        var image = SiteUrl.FromAbsolute(new Uri(new Uri(context.Site.BaseUrl), socialImage.GetUrl(context.Assets)).AbsoluteUri);
        var seo = new SeoComponentProps(title + " - " + context.Site.Title, description, canonical, "website", image, context.Site.Title);
        var head = new HeadComponent().Render(new(seo, SiteUrl.ForFile("site-api.css", context.Site.BaseUrl), Html.UnsafeRaw(""), Html.UnsafeRaw("")), context).ToHtmlString();
        var metadata = socialImage.GetMetadata(context.Assets);
        var extra = (page.Metadata.NoIndex ? "<meta name=\"robots\" content=\"noindex,follow\">" : "")
            + (metadata is null ? "" : "<meta property=\"og:image:width\" content=\"" + metadata.Width + "\"><meta property=\"og:image:height\" content=\"" + metadata.Height + "\"><meta property=\"og:image:type\" content=\"" + Html.Encode(metadata.MediaType) + "\">")
            + (page.Content.Head?.ToHtmlString() ?? "");
        var links = Link(japanese ? "ガイド" : "Guide", "ja" == locale ? "ja/introduction" : "en/introduction")
            + Link(japanese ? "詳細ガイド" : "Detailed guides", "reference/" + locale + "/README")
            + Link(japanese ? "実行例" : "Examples", "examples/" + locale + "/typed-content");
        return Html.UnsafeRaw("<!doctype html><html lang=\"" + Html.Encode(language) + "\"" + (page.Metadata.RightToLeft ? " dir=\"rtl\"" : "") + ">"
            + head.Replace("</head>", extra + "</head>", StringComparison.Ordinal)
            + "<body><a class=\"skip-link\" href=\"#main\">" + (japanese ? "本文へ移動" : "Skip to main content") + "</a>"
            + "<header><a href=\"" + SiteUrl.ForDirectory("", context.Site.BaseUrl).ToAttributeValue() + "\">LithoSharp</a><nav aria-label=\""
            + (japanese ? "サイト内の案内" : "Site navigation") + "\">" + links + "</nav></header><main id=\"main\" tabindex=\"-1\">"
            + page.Content.Body.ToHtmlString() + "</main><footer><p>" + (japanese ? "LithoSharp で生成" : "Generated with LithoSharp") + "</p></footer></body></html>");

        string Link(string label, string route) => "<a href=\"" + SiteUrl.ForDirectory(route, context.Site.BaseUrl).ToAttributeValue() + "\">" + Html.Encode(label) + "</a> ";
    }
}
