using LithoSharp;

internal static class WebsiteHeader
{
    public static string Render(string baseUrl, string? locale = null, string? languageLinks = null)
    {
        var ja = locale == "ja";
        var current = locale ?? "en";
        string Directory(string path) => SiteUrl.ForDirectory(path, baseUrl).ToAttributeValue().ToString();
        var search = SiteUrl.ForFile("search.html", baseUrl).ToAttributeValue();
        if (languageLinks is null)
            languageLinks = "<a lang=\"en\" href=\"" + Directory("en/introduction") + "\">English</a><a lang=\"ja\" href=\"" + Directory("ja/introduction") + "\">日本語</a>";
        return "<header class=\"docs-header\"><a class=\"docs-brand\" href=\"" + Directory("") + "\">LithoSharp</a>"
            + "<nav class=\"website-top-nav\" aria-label=\"" + (ja ? "サイト内の案内" : "Site navigation") + "\">"
            + "<a href=\"" + Directory(current + "/introduction") + "\">" + (ja ? "ガイド" : "Guide") + "</a>"
            + "<a href=\"" + Directory("api/" + current + "/lithosharp") + "\">API</a><a href=\"https://github.com/Htkym/lithosharp\">GitHub</a></nav>"
            + "<div class=\"docs-header-actions\"><a class=\"website-search-link\" href=\"" + search + "\">" + WebsiteChrome.SearchIcon + (ja ? "検索" : "Search") + "</a>"
            + (languageLinks.Length == 0 ? "" : "<details class=\"website-language\"><summary aria-label=\"" + (ja ? "言語: 日本語" : locale is null ? "Language / 言語" : "Language: English") + "\">"
            + WebsiteChrome.LanguageIcon + (ja ? "日本語" : locale is null ? "Language" : "English") + "</summary><nav aria-label=\"Language\">" + languageLinks + "</nav></details>") + "</div></header>";
    }
}
