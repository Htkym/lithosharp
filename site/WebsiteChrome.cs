using LithoSharp;
using LithoSharp.Pages;

internal static class WebsiteChrome
{
    internal const string SearchIcon = "<svg aria-hidden=\"true\" viewBox=\"0 0 24 24\"><circle cx=\"10.5\" cy=\"10.5\" r=\"6.5\"/><path d=\"m16 16 4 4\"/></svg>";
    internal const string LanguageIcon = "<svg aria-hidden=\"true\" viewBox=\"0 0 24 24\"><circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M3 12h18M12 3c5 5 5 13 0 18-5-5-5-13 0-18Z\"/></svg>";
    internal const string MenuIcon = "<svg aria-hidden=\"true\" viewBox=\"0 0 24 24\"><path d=\"M4 6h16M4 12h16M4 18h16\"/></svg>";

    public static IHtmlContent Render(SitePage<PageLayoutContent> page, PageRenderingContext context, ImageSource socialImage)
    {
        var chrome = page.Content.DocumentationChrome ?? throw new InvalidOperationException("The website requires structured documentation regions.");
        var language = page.Metadata.Language ?? context.Site.Language;
        var ja = language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        var locale = ja ? "ja" : "en";
        var title = page.Metadata.Title ?? context.Site.Title;
        var canonical = SiteUrl.FromAbsolute(new Uri(new Uri(context.Site.BaseUrl), page.Route.PublicPath).AbsoluteUri);
        var image = SiteUrl.FromAbsolute(new Uri(new Uri(context.Site.BaseUrl), socialImage.GetUrl(context.Assets)).AbsoluteUri);
        var seo = new SeoComponentProps(title + " - " + context.Site.Title, page.Metadata.Description ?? context.Site.Description, canonical, "article", image, context.Site.Title);
        var head = new HeadComponent().Render(new(seo, SiteUrl.ForFile("assets/site.css", context.Site.BaseUrl),
            Html.UnsafeRaw(""), Html.UnsafeRaw("")), context).ToHtmlString();
        var metadata = socialImage.GetMetadata(context.Assets);
        var extra = (page.Content.Head?.ToHtmlString() ?? "")
            + "<link rel=\"stylesheet\" href=\"" + Url("website.css") + "\"><script type=\"module\" src=\"" + Url("website.mjs") + "\"></script>"
            + (page.Metadata.NoIndex ? "<meta name=\"robots\" content=\"noindex,follow\">" : "")
            + (metadata is null ? "" : "<meta property=\"og:image:width\" content=\"" + metadata.Width + "\"><meta property=\"og:image:height\" content=\"" + metadata.Height + "\"><meta property=\"og:image:type\" content=\"" + Html.Encode(metadata.MediaType) + "\">")
            + string.Concat(page.Metadata.Alternates.Select(pair => "<link rel=\"alternate\" hreflang=\"" + Html.Encode(pair.Key) + "\" href=\"" + pair.Value.ToAttributeValue() + "\">"));
        var variants = string.Concat(page.Metadata.Alternates.Select(pair =>
            "<a href=\"" + pair.Value.ToAttributeValue() + "\" lang=\"" + Html.Encode(pair.Key) + "\" hreflang=\"" + Html.Encode(pair.Key) + "\""
            + (pair.Key == locale ? " aria-current=\"true\"" : "") + ">" + Html.Encode(pair.Key == "ja" ? "日本語" : pair.Key == "en" ? "English" : pair.Key) + "</a>"));
        var languageControl = variants.Length == 0 ? "" : "<details class=\"website-language\"><summary aria-label=\"" + (ja ? "言語: 日本語" : "Language: English") + "\">"
            + LanguageIcon + (ja ? "日本語" : "English") + "</summary><nav aria-label=\"" + (ja ? "言語: 日本語" : "Language: English") + "\">" + variants + "</nav></details>";
        var search = chrome.Search?.ToHtmlString() ?? "";
        var searchControl = search.Length == 0 ? "" : "<details class=\"website-search\"><summary>" + SearchIcon + (ja ? "検索" : "Search")
            + "</summary><div class=\"website-search-panel\">" + search + "<div class=\"website-search-actions\"><button type=\"button\" data-website-search-clear>" + (ja ? "クリア" : "Clear") + "</button><button type=\"button\" data-website-search-close>"
            + (ja ? "閉じる" : "Close") + "</button></div></div></details>";
        var toc = chrome.Headings.Count == 0 ? "" : "<details class=\"website-toc\"><summary>" + (ja ? "このページの内容" : "On this page")
            + "</summary><div><nav aria-label=\"" + (ja ? "このページの内容" : "On this page") + "\"><ul class=\"toc-list\">"
            + string.Concat(chrome.Headings.Select(heading => "<li class=\"toc-level-" + heading.Level + "\"><a href=\"#"
                + Html.Encode(heading.Id) + "\">" + Html.Encode(heading.Text) + "</a></li>")) + "</ul></nav></div></details>";
        var links = "<nav class=\"website-top-nav\" aria-label=\"" + (ja ? "サイト内の案内" : "Site navigation") + "\">"
            + "<a href=\"" + Directory(locale + "/introduction") + "\">" + (ja ? "ガイド" : "Guide") + "</a>"
            + "<a href=\"" + Directory("api/" + locale + "/lithosharp") + "\">API</a><a href=\"https://github.com/Htkym/lithosharp\">GitHub</a></nav>";
        return Html.UnsafeRaw("<!doctype html><html lang=\"" + Html.Encode(language) + "\"" + (page.Metadata.RightToLeft ? " dir=\"rtl\"" : "") + ">"
            + head.Replace("</head>", extra + "</head>", StringComparison.Ordinal)
            + "<body class=\"docs-body website-body" + (toc.Length == 0 ? " website-no-toc" : "") + "\">"
            + "<a class=\"docs-skip-link\" href=\"#docs-main\">" + (ja ? "本文へ移動" : "Skip to main content") + "</a>"
            + "<header class=\"docs-header\"><button class=\"docs-menu-toggle\" type=\"button\" aria-expanded=\"true\" aria-controls=\"docs-sidebar\" data-docs-menu-toggle aria-label=\"" + (ja ? "メニュー" : "Menu") + "\">" + MenuIcon + "</button>"
            + "<a class=\"docs-brand\" href=\"" + Directory(locale + "/introduction") + "\">LithoSharp</a>" + links
            + "<div class=\"docs-header-actions\">" + searchControl + languageControl + "<button type=\"button\" data-ls-theme>" + (ja ? "テーマ" : "Theme") + "</button></div></header>"
            + "<div class=\"docs-shell\">" + WebsiteNavigation.Localize(page.Content.Sidebar?.ToHtmlString() ?? "", ja) + toc
            + "<main id=\"docs-main\" class=\"docs-main\" tabindex=\"-1\"><article class=\"docs-content\">"
            + WebsiteNavigation.Localize(chrome.Breadcrumbs?.ToHtmlString() ?? "", ja)
            + chrome.Article.ToHtmlString() + (chrome.Adjacent?.ToHtmlString() ?? "")
            + "</article></main></div><footer class=\"docs-footer\"><a href=\"https://github.com/Htkym/lithosharp\">LithoSharp</a></footer></body></html>");

        string Url(string path) => SiteUrl.ForFile(path, context.Site.BaseUrl).ToAttributeValue().ToString();
        string Directory(string path) => SiteUrl.ForDirectory(path, context.Site.BaseUrl).ToAttributeValue().ToString();
    }
}
