using LithoSharp;

internal sealed class WebsiteTemplate : DocsSiteTemplate
{
    internal WebsiteTemplate(WebsiteMenu menu, string baseUrl)
    {
        EnableSearch = true;
        CollapseTopics = true;
        NavigationRenderer = (url, locale) => menu.Render(baseUrl, url, locale);
    }

    public override async Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default)
    {
        var standard = await base.RenderAsync(context, cancellationToken);
        var home = new SiteTemplateFile
        {
            RelativePath = "index.html",
            Content = Decorate(context.RenderDocument(new()
            {
                RelativePath = "index.html",
                Title = "LithoSharp",
                BodyHtml = "<section class=\"website-introduction\"><h1>LithoSharp</h1>"
                    + "<p>Build static sites with C# types and Markdown.</p><p lang=\"ja\">C# の型と Markdown で静的サイトを構築します。</p>"
                    + "<nav class=\"website-language-choice\" aria-label=\"Documentation language\">"
                    + Link("English documentation", "en/introduction/", "en") + Link("日本語ドキュメント", "ja/introduction/", "ja") + "</nav>"
                    + "<pre><code>dotnet new lithosharp-docs -n MyDocs</code></pre>"
                    + "<a href=\"https://github.com/Htkym/lithosharp\">GitHub</a></section>"
            }))
        };
        var notFound = new SiteTemplateFile
        {
            RelativePath = "404.html",
            Content = Decorate(context.RenderDocument(new()
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
            Content = Decorate(context.RenderDocument(new()
            {
                RelativePath = "search.html",
                Title = "Search this site",
                BodyHtml = "<h1>Search this site <span lang=\"ja\">サイト内検索</span></h1>"
                    + "<form role=\"search\" action=\"" + SiteUrl.ForFile("search.html", context.Site.BaseUrl).ToAttributeValue() + "\" method=\"get\">"
                    + "<input id=\"search-input\" name=\"q\" type=\"search\" aria-label=\"Search this site / このサイトを検索\" placeholder=\"Search / 検索\" autofocus></form>"
                    + "<p id=\"search-status\" role=\"status\" aria-live=\"polite\"></p>"
                    + "<section id=\"search-app\" data-index=\"" + SiteUrl.ForFile("search-index.json", context.Site.BaseUrl).ToAttributeValue() + "\"><div id=\"search-results\"></div></section>"
                    + "<noscript><p>JavaScript is required for search.</p></noscript>"
            }))
        };
        return new([.. standard.Files.Where(file => file.RelativePath != home.RelativePath && file.RelativePath != notFound.RelativePath && file.RelativePath != search.RelativePath), home, notFound, search]);

        string Decorate(string html)
        {
            var header = System.Text.RegularExpressions.Regex.Match(html, "<header\\b[^>]*>[\\s\\S]*?</header>");
            if (!header.Success) throw new InvalidDataException("The utility document requires a site header.");
            var controls = string.Concat(System.Text.RegularExpressions.Regex.Matches(header.Value, "<button\\b[^>]*>[\\s\\S]*?</button>").Where(match => !match.Value.Contains("data-site-theme-toggle", StringComparison.Ordinal)).Select(match => match.Value));
            var replacement = WebsiteHeader.Render(context.Site.BaseUrl).Replace("</div></header>", controls + "</div></header>", StringComparison.Ordinal);
            html = html.Remove(header.Index, header.Length).Insert(header.Index, replacement);
            return html.Replace("</head>", "<script src=\"" + SiteUrl.ForFile("website-theme.js", context.Site.BaseUrl).ToAttributeValue() + "\" data-base-path=\"" + SiteUrl.ForDirectory("", context.Site.BaseUrl).ToAttributeValue() + "\"></script><link rel=\"stylesheet\" href=\"" + SiteUrl.ForFile("website.css", context.Site.BaseUrl).ToAttributeValue()
                + "\"><script defer src=\"" + SiteUrl.ForFile("assets/site.js", context.Site.BaseUrl).ToAttributeValue() + "\"></script><script type=\"module\" src=\"" + SiteUrl.ForFile("website.mjs", context.Site.BaseUrl).ToAttributeValue() + "\"></script></head>", StringComparison.Ordinal)
                .Replace("class=\"blog-body\"", "class=\"blog-body website-body website-utility\"", StringComparison.Ordinal)
                .Replace("class=\"docs-body\"", "class=\"docs-body website-body website-utility\"", StringComparison.Ordinal);
        }
        string Link(string label, string path, string language) => "<a href=\"" + SiteUrl.ForDirectory(path, context.Site.BaseUrl).ToAttributeValue()
            + "\" lang=\"" + language + "\">" + Html.Encode(label) + "</a>";
    }
}
