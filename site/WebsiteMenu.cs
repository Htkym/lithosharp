using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LithoSharp;
using LithoSharp.Documentation;

internal sealed class WebsiteMenu
{
    private sealed record Item(string Label, string? Path, IReadOnlyList<Item> Children);
    private readonly Dictionary<string, IReadOnlyList<Item>> pages = [];
    internal ApiReferenceSite? Api { get; set; }
    internal WebsiteMenu(string root)
    {
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "documentation-inventory.json")));
        foreach (var locale in new[] { "en", "ja" })
        {
            var ja = locale == "ja";
            string Title(string path) => Regex.Match(File.ReadAllText(path), @"(?m)^title:\s*(.+)$").Groups[1].Value.Trim().Trim('"');
            Item Folder(string en, string jp, IEnumerable<Item> children) => new(ja ? jp : en, null, children.ToArray());
            var guidePages = Directory.EnumerateFiles(Path.Combine(root, "content", locale), "*.md")
                .OrderBy(path => Path.GetFileNameWithoutExtension(path) == "introduction" ? 0 : 1)
                .ThenBy(path => path, StringComparer.Ordinal)
                .Select(path => new Item(Title(path), locale + "/" + Path.GetFileNameWithoutExtension(path) + "/", []));
            var guide = WebsiteNavigation.Groups.Select(group => Folder(group.English, group.Japanese, group.Documents.Select(id => guidePages.Single(item => item.Path == locale + "/" + id + "/"))));
            var groups = inventory.RootElement.GetProperty("entries").EnumerateArray().GroupBy(entry => entry.GetProperty("collection").GetString()!);
            var reference = groups.Select(group => Folder(group.Key switch { "history" => "Archive", "samples" => "Samples", "packages" => "Packages", "overview" => "Overview", _ => "Reference" },
                group.Key switch { "history" => "過去の資料", "samples" => "サンプル", "packages" => "パッケージ", "overview" => "概要", _ => "リファレンス" },
                group.Select(entry => new Item(entry.GetProperty("source").GetProperty(locale).GetProperty("title").GetString()!,
                    group.Key + "/" + locale + "/" + entry.GetProperty("id").GetString() + "/", []))));
            var examples = Directory.EnumerateFiles(Path.Combine(root, "examples", locale), "*.md", SearchOption.AllDirectories)
                .Select(path => new Item(Title(path), "examples/" + locale + "/" + Path.ChangeExtension(Path.GetRelativePath(Path.Combine(root, "examples", locale), path), null).Replace('\\', '/') + "/", []));
            var knowledge = Directory.EnumerateFiles(Path.Combine(root, "knowledge", locale), "*.md", SearchOption.AllDirectories)
                .Select(path => new Item(Title(path), "knowledge/" + locale + "/" + Path.ChangeExtension(Path.GetRelativePath(Path.Combine(root, "knowledge", locale), path), ".html").Replace('\\', '/'), []));
            pages[locale] = [new(ja ? "ホーム" : "Home", "", []), Folder("Guide", "ガイド", guide), .. reference,
                Folder("Examples", "実例", examples), Folder("Knowledge", "ナレッジ", [new(ja ? "ナレッジ一覧" : "Knowledge index", "knowledge/index.html", []), .. knowledge])];
        }
    }

    internal IHtmlContent Render(string baseUrl, string? currentUrl, string? locale, string? apiSidebar = null)
    {
        locale = locale?.StartsWith("ja", StringComparison.OrdinalIgnoreCase) == true ? "ja" : "en";
        var ja = locale == "ja";
        var result = new StringBuilder("<aside id=\"docs-sidebar\" class=\"docs-sidebar\" data-docs-sidebar><nav aria-label=\"")
            .Append(ja ? "ドキュメントナビゲーション" : "Documentation navigation").Append("\"><ul class=\"docs-nav-list\">");
        string Url(string path) => (path.EndsWith(".html", StringComparison.Ordinal) ? SiteUrl.ForFile(path, baseUrl) : SiteUrl.ForDirectory(path, baseUrl)).Value;
        bool Active(Item item) => (item.Path is not null && Url(item.Path) == currentUrl) || item.Children.Any(Active);
        void Append(Item item)
        {
            var active = Active(item);
            result.Append("<li").Append(item.Children.Count > 0 ? " class=\"docs-nav-folder" + (active ? " is-ancestor" : "") + "\"" : "").Append('>');
            if (item.Children.Count > 0) result.Append("<details class=\"docs-nav-topic\"").Append(active ? " open" : "").Append("><summary>").Append(Html.Encode(item.Label)).Append("</summary>");
            if (item.Path is not null) result.Append("<a class=\"docs-nav-link").Append(Url(item.Path) == currentUrl ? " is-current\" aria-current=\"page" : "").Append("\" href=\"").Append(Html.Encode(Url(item.Path))).Append("\">").Append(Html.Encode(item.Label)).Append("</a>");
            if (item.Children.Count > 0) { result.Append("<ul>"); foreach (var child in item.Children) Append(child); result.Append("</ul></details>"); }
            result.Append("</li>");
        }
        foreach (var item in pages[locale]) Append(item);
        var apiActive = currentUrl?.Contains("/api/" + locale + "/", StringComparison.Ordinal) == true;
        result.Append("<li class=\"docs-nav-folder\"><details class=\"docs-nav-topic\"").Append(apiActive ? " open" : "").Append("><summary>API</summary><ul>");
        var catalogs = (Api?.Members.Keys.Where(key => key.Locale == locale).GroupBy(key => key.Collection)
            .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group =>
            {
                // Derive the catalog prefix from a resolved member route, never from its display name.
                var memberPath = Api!.Resolve(group.First()).Route.RelativeOutputPath;
                var memberDirectory = memberPath[..memberPath.LastIndexOf('/')];
                return (Collection: group.Key, Path: memberDirectory[..(memberDirectory.LastIndexOf('/') + 1)]);
            }).ToArray()) ?? [];
        // A root catalog may contain other catalog prefixes. The most specific route owns the sidebar.
        var activePath = catalogs.Where(catalog => currentUrl?.StartsWith(Url(catalog.Path), StringComparison.Ordinal) == true)
            .OrderByDescending(catalog => catalog.Path.Length).Select(catalog => catalog.Path).FirstOrDefault();
        foreach (var (collection, path) in catalogs)
        {
            var activeCatalog = apiSidebar is not null && path == activePath;            if (!activeCatalog)
                Append(new(collection, path, []));
            else
                result.Append("<li><span>").Append(Html.Encode(collection)).Append("</span></li>");
            if (activeCatalog)
            {
                var start = apiSidebar!.IndexOf("<ul class=\"docs-nav-list\">", StringComparison.Ordinal);
                var end = apiSidebar!.LastIndexOf("</ul>", StringComparison.Ordinal);
                if (start >= 0 && end > start) result.Append("<li><ul>").Append(apiSidebar!.AsSpan(start + "<ul class=\"docs-nav-list\">".Length, end - start - "<ul class=\"docs-nav-list\">".Length)).Append("</ul></li>");
            }
        }
        return Html.UnsafeRaw(result.Append("</ul></details></li></ul></nav></aside>").ToString());
    }
}
