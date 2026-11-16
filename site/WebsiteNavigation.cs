using LithoSharp.Documentation;

internal static class WebsiteNavigation
{
    private static readonly (string English, string Japanese, string[] Documents)[] Groups =
    [
        ("Start here", "まずはここから", ["introduction", "quickstart", "features"]),
        ("Write content", "コンテンツ作成", ["typed-content", "profiles", "documentation"]),
        ("Reference and extensions", "リファレンスと拡張", ["api", "images", "knowledge", "extended"]),
        ("Develop and operate", "開発と運用", ["tooling", "operations", "migration"])
    ];

    internal static IReadOnlyDictionary<string, IReadOnlyList<SidebarItem>> Sidebars { get; } =
        new Dictionary<string, IReadOnlyList<SidebarItem>>
        {
            ["handbook"] = Groups.Select(group => new SidebarItem("category", Label: group.English)
            {
                Collapsed = true,
                Items = group.Documents.Select(id => new SidebarItem("doc", Id: id)).ToArray()
            }).ToArray()
        };

    internal static string Localize(string html, bool japanese)
    {
        if (!japanese) return html;
        foreach (var group in Groups)
        {
            html = html.Replace(">" + group.English + "</summary>", ">" + group.Japanese + "</summary>", StringComparison.Ordinal)
                .Replace("<li>" + group.English + "</li>", "<li>" + group.Japanese + "</li>", StringComparison.Ordinal);
        }
        return html;
    }
}
