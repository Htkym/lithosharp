using LithoSharp;

internal static class WebsiteHeader
{
    internal static string VersionBadge => "<span class=\"website-version\">" + Html.Encode(WebsiteVersion.Display) + "</span>";

    public static DocsHeaderContent Content(string baseUrl, string? locale = null, string? languageLinks = null)
    {
        var ja = locale == "ja";
        var current = locale ?? "en";
        string Directory(string path) => SiteUrl.ForDirectory(path, baseUrl).ToAttributeValue().ToString();
        if (languageLinks is null)
            languageLinks = "<a lang=\"en\" href=\"" + Directory("en/introduction") + "\">English</a><a lang=\"ja\" href=\"" + Directory("ja/introduction") + "\">日本語</a>";
        return new DocsHeaderContent
        {
            Navigation = Html.UnsafeRaw(VersionBadge
            + "<nav class=\"website-top-nav\" aria-label=\"" + (ja ? "サイト内の案内" : "Site navigation") + "\">"
            + "<a href=\"" + Directory(current + "/introduction") + "\">" + (ja ? "ガイド" : "Guide") + "</a>"
            + "<a href=\"" + Directory("api/" + current + "/lithosharp") + "\">API</a><a href=\"https://github.com/Htkym/lithosharp\">GitHub</a></nav>"),

            Actions = Html.UnsafeRaw(languageLinks.Length == 0 ? "" : "<details class=\"website-language\"><summary aria-label=\"" + (ja ? "言語: 日本語" : locale is null ? "Language / 言語" : "Language: English") + "\">"
            + WebsiteChrome.LanguageIcon + (ja ? "日本語" : locale is null ? "Language" : "English") + "</summary><nav aria-label=\"Language\">" + languageLinks + "</nav></details>")
        };
    }
    internal static string ThemeButton(bool ja) => "<button type=\"button\" data-ls-theme hidden aria-label=\"" + (ja ? "テーマ" : "Theme") + "\">" + (ja ? "テーマ" : "Theme") + "</button>";

    internal static string Search(string baseUrl, string? locale)
    {
        var ja = locale == "ja";
        var label = ja ? "サイト内を検索" : "Search this site";
        string Url(string path) => SiteUrl.ForFile(path, baseUrl).ToAttributeValue().ToString();
        return "<form class=\"website-search website-search-form\" role=\"search\" data-site-autocomplete data-search-scope=\"site\" data-locale=\"" + Html.Encode(locale ?? "") + "\" data-index=\"" + Url("search-index.json") + "\" action=\"" + Url("search.html") + "\" method=\"get\">"
            + "<label>" + WebsiteChrome.SearchIcon + "<input type=\"search\" name=\"q\" aria-label=\"" + label + "\" placeholder=\"" + label + "…\" autocomplete=\"off\"></label>"
            + "<div class=\"website-search-panel\"><p role=\"status\" aria-live=\"polite\"></p><div data-results></div>"
            + "<div class=\"website-search-actions\"><button type=\"button\" data-website-search-clear>" + (ja ? "クリア" : "Clear") + "</button><button type=\"button\" data-website-search-close>" + (ja ? "閉じる" : "Close") + "</button></div></div></form>"
            + "<script defer src=\"" + Url("assets/search.js") + "\"></script>";
    }
}
