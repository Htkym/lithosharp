using LithoSharp;

internal sealed class WebsiteTemplate : ISiteTemplate
{
    public async Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default)
    {
        var standard = await new DocsSiteTemplate { EnableSearch = true }.RenderAsync(context, cancellationToken);
        var home = new SiteTemplateFile
        {
            RelativePath = "index.html",
            Content = context.RenderDocument(new()
            {
                RelativePath = "index.html", Title = "LithoSharp",
                BodyHtml = "<h1>LithoSharp</h1><nav aria-label=\"Documentation language\">"
                    + Link("English", "en/introduction/", "en") + " · " + Link("日本語", "ja/introduction/", "ja") + "</nav>"
            })
        };
        var notFound = new SiteTemplateFile
        {
            RelativePath = "404.html",
            Content = context.RenderDocument(new()
            {
                RelativePath = "404.html", Title = "Page not found / ページが見つかりません",
                BodyHtml = "<h1>Page not found</h1><p>The requested page is unavailable. Check the address or return to the guide.</p><p>"
                    + Link("English guide", "en/introduction/", "en") + "</p><section lang=\"ja\"><h2>ページが見つかりません</h2>"
                    + "<p>指定したページを利用できません。アドレスを確認するか、ガイドへ戻ってください。</p><p>"
                    + Link("日本語ガイド", "ja/introduction/", "ja") + "</p></section>"
            }).Replace("</head>", "<meta name=\"robots\" content=\"noindex,follow\"></head>", StringComparison.Ordinal)
        };
        return new([.. standard.Files.Where(file => file.RelativePath != home.RelativePath && file.RelativePath != notFound.RelativePath), home, notFound]);

        string Link(string label, string path, string language) => "<a href=\"" + Html.Encode(SiteUrl.ForDirectory(path, context.Site.BaseUrl).Value)
            + "\" lang=\"" + language + "\">" + Html.Encode(label) + "</a>";
    }
}
