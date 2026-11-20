using LithoSharp;
using LithoSharp.Pages;
using LithoSharp.Routing;

internal sealed class WebsiteTemplate : DocsSiteTemplate
{
    internal WebsiteTemplate(WebsiteMenu menu, string baseUrl)
    {
        EnableHeaderSearch = true;
        CollapseTopics = true;
        HeaderContentRenderer = (url, locale) => url is null
            || url == SiteUrl.ForFile("index.html", baseUrl).Value
            || url == SiteUrl.ForFile("search.html", baseUrl).Value
                ? WebsiteHeader.Content(baseUrl, locale) : new DocsHeaderContent();
        NavigationRenderer = (url, locale) => menu.Render(baseUrl, url, locale);
    }

    public override async Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default)
    {
        var standard = await base.RenderAsync(context, cancellationToken);
        var home = new SiteTemplateFile
        {
            RelativePath = "index.html",
            Content = Decorate(RenderUtility(new()
            {
                RelativePath = "index.html",
                Title = "LithoSharp",
                BodyHtml = "<section class=\"website-introduction\"><h1>LithoSharp</h1>"
                    + "<p>A C# and .NET static site generator for technical documentation, API references, and Open Knowledge Format (OKF) bundles (preview).</p>"
                    + "<p lang=\"ja\">技術文書、API リファレンス、Open Knowledge Format（OKF）バンドルを扱う C# と .NET の静的サイトジェネレーターです。OKF 対応はプレビューです。</p>"
                    + "<nav class=\"website-language-choice\" aria-label=\"Documentation language\">"
                    + Link("English documentation", "en/introduction/", "en") + Link("日本語ドキュメント", "ja/introduction/", "ja") + "</nav>"
                    + "<p>" + Link("Open Knowledge Format guide", "en/knowledge/", "en") + " · "
                    + Link("Open Knowledge Format ガイド", "ja/knowledge/", "ja") + "</p>"
                    + "<pre><code>dotnet new lithosharp-docs -n MyDocs</code></pre>"
                    + "<a href=\"https://github.com/Htkym/lithosharp\">GitHub</a></section>"
            }))
        };
        var notFound = new SiteTemplateFile
        {
            RelativePath = "404.html",
            Content = Decorate(RenderUtility(new()
            {
                RelativePath = "404.html",
                Title = "Page not found / ページが見つかりません",
                BodyHtml = "<h1>Page not found</h1><p>The requested page is unavailable. Check the address or return to the guide.</p><p>"
                    + Link("English guide", "en/introduction/", "en") + "</p><section lang=\"ja\"><h2>ページが見つかりません</h2>"
                    + "<p>指定したページを利用できません。アドレスを確認するか、ガイドへ戻ってください。</p><p>"
                    + Link("日本語ガイド", "ja/introduction/", "ja") + "</p></section>"
            })).Replace("</head>", "<meta name=\"robots\" content=\"noindex,follow\"></head>", StringComparison.Ordinal)
        };
        var search = new SiteTemplateFile
        {
            RelativePath = "search.html",
            Content = Decorate(RenderUtility(new()
            {
                RelativePath = "search.html",
                Title = "Search this site",
                BodyHtml = "<h1>Search this site <span lang=\"ja\">サイト内検索</span></h1>"
                    + "<form role=\"search\" action=\"" + SiteUrl.ForFile("search.html", context.Site.BaseUrl).ToAttributeValue() + "\" method=\"get\">"
                    + "<input id=\"search-input\" name=\"q\" type=\"search\" aria-label=\"Search this site / このサイトを検索\" placeholder=\"Search / 検索\" autofocus></form>"
                    + "<p id=\"search-status\" role=\"status\" aria-live=\"polite\"></p>"
                    + "<section id=\"search-app\" data-index=\"" + SiteUrl.ForFile("search-index.json", context.Site.BaseUrl).ToAttributeValue() + "\"><div id=\"search-results\"></div></section>"
                    + "<noscript><p>JavaScript is required for search.</p></noscript>"
                    + "<script defer src=\"" + SiteUrl.ForFile("assets/search.js", context.Site.BaseUrl).ToAttributeValue() + "\"></script>"
            }))
        };
        return new([.. standard.Files.Where(file => file.RelativePath != home.RelativePath && file.RelativePath != notFound.RelativePath && file.RelativePath != search.RelativePath), home, notFound, search]);

        string RenderUtility(SiteTemplateDocument document)
        {
            var route = SiteRoute.ForFile(document.RelativePath, context.Site.BaseUrl);
            var content = new PageLayoutContent(Html.UnsafeRaw(document.BodyHtml));
            var page = new SitePage<PageLayoutContent>(new PageId("website:" + document.RelativePath),
                route, content, new PageMetadata(document.Title, document.Description));
            return context.RenderLayout(page, new DocsPageLayout()).ToHtmlString();
        }

        string Decorate(string html)
        {
            return html.Replace("</head>", "<script src=\"" + SiteUrl.ForFile("website-theme.js", context.Site.BaseUrl).ToAttributeValue() + "\" data-base-path=\"" + SiteUrl.ForDirectory("", context.Site.BaseUrl).ToAttributeValue() + "\"></script><link rel=\"stylesheet\" href=\"" + SiteUrl.ForFile("website.css", context.Site.BaseUrl).ToAttributeValue()
                + "\"><script type=\"module\" src=\"" + SiteUrl.ForFile("website.mjs", context.Site.BaseUrl).ToAttributeValue() + "\"></script></head>", StringComparison.Ordinal)
                .Replace("class=\"blog-body\"", "class=\"blog-body website-body website-utility\"", StringComparison.Ordinal)
                .Replace("class=\"docs-body\"", "class=\"docs-body website-body website-utility\"", StringComparison.Ordinal);
        }
        string Link(string label, string path, string language) => "<a href=\"" + SiteUrl.ForDirectory(path, context.Site.BaseUrl).ToAttributeValue()
            + "\" lang=\"" + language + "\">" + Html.Encode(label) + "</a>";
    }
}
