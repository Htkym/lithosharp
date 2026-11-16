using LithoSharp;
using LithoSharp.Pages;

internal sealed class WebsiteApiLayout(ImageSource socialImage) : IPageLayout<PageLayoutContent>
{
    internal const string Fingerprint = "website-api-layout/2";
    public IHtmlContent Render(SitePage<PageLayoutContent> page, PageRenderingContext context)
    {
        var language = page.Metadata.Language ?? context.Site.Language;
        var japanese = language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        var locale = japanese ? "ja" : "en";
        var title = page.Metadata.Title ?? context.Site.Title;
        var description = page.Metadata.Description ?? (japanese ? "C# の型と Markdown で静的サイトを構築します。" : context.Site.Description);
        var canonical = SiteUrl.FromAbsolute(new Uri(new Uri(context.Site.BaseUrl), page.Route.PublicPath).AbsoluteUri);
        var image = SiteUrl.FromAbsolute(new Uri(new Uri(context.Site.BaseUrl), socialImage.GetUrl(context.Assets)).AbsoluteUri);
        var seo = new SeoComponentProps(title + " - " + context.Site.Title, description, canonical, "website", image, context.Site.Title);
        var head = new HeadComponent().Render(new(seo, SiteUrl.ForFile("assets/site.css", context.Site.BaseUrl), Html.UnsafeRaw(""), Html.UnsafeRaw("")), context).ToHtmlString();
        var metadata = socialImage.GetMetadata(context.Assets);
        var extra = (page.Metadata.NoIndex ? "<meta name=\"robots\" content=\"noindex,follow\">" : "")
            + (metadata is null ? "" : "<meta property=\"og:image:width\" content=\"" + metadata.Width + "\"><meta property=\"og:image:height\" content=\"" + metadata.Height + "\"><meta property=\"og:image:type\" content=\"" + Html.Encode(metadata.MediaType) + "\">")
            + (page.Content.Head?.ToHtmlString() ?? "")
            + "<script defer src=\"" + SiteUrl.ForFile("assets/site.js", context.Site.BaseUrl).ToAttributeValue() + "\"></script>"
            + string.Concat(page.Metadata.Alternates.Select(pair => "<link rel=\"alternate\" hreflang=\"" + Html.Encode(pair.Key) + "\" href=\"" + pair.Value.ToAttributeValue() + "\">"))
            + "<link rel=\"stylesheet\" href=\"" + SiteUrl.ForFile("website.css", context.Site.BaseUrl).ToAttributeValue() + "\"><script type=\"module\" src=\"" + SiteUrl.ForFile("website.mjs", context.Site.BaseUrl).ToAttributeValue() + "\"></script>";
        var languages = page.Metadata.Alternates.Count < 2 ? "" : string.Concat(page.Metadata.Alternates.Select(pair => "<a href=\"" + pair.Value.ToAttributeValue() + "\" lang=\"" + Html.Encode(pair.Key) + "\"" + (pair.Key == locale ? " aria-current=\"true\"" : "") + ">" + Html.Encode(pair.Key == "ja" ? "日本語" : pair.Key == "en" ? "English" : pair.Key) + "</a>"));
        return Html.UnsafeRaw("<!doctype html><html lang=\"" + Html.Encode(language) + "\"" + (page.Metadata.RightToLeft ? " dir=\"rtl\"" : "") + ">"
            + head.Replace("</head>", extra + "</head>", StringComparison.Ordinal)
            + "<body class=\"website-body website-api\"><a class=\"skip-link\" href=\"#main\">" + (japanese ? "本文へ移動" : "Skip to main content") + "</a>"
            + WebsiteHeader.Render(context.Site.BaseUrl, locale, languages) + "<main id=\"main\" tabindex=\"-1\">"
            + page.Content.Body.ToHtmlString() + "</main><footer><p>" + (japanese ? "LithoSharp で生成" : "Generated with LithoSharp") + "</p></footer></body></html>");


    }
}
