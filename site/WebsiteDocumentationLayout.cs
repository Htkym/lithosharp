using LithoSharp;
using LithoSharp.Documentation;
using LithoSharp.Pages;
using LithoSharp.Testing;

internal sealed class WebsiteDocumentationLayout : IPageLayout<PageLayoutContent>
{
    private sealed record Binding(DocumentKey Document, string NativeId, string Heading, string Alias, string? MarkerSource = null);

    private static readonly Binding[] Bindings =
    [
        new(new("samples", "3.0", "ja", "samples-lithosharp-docssample"), "c", "C#の型定義から生成物の検査まで", "cの型定義から生成物の検査まで"),
        new(new("reference", "3.0", "ja", "Testing-v2-api"), "section-2", "移行", "移行"),
        new(new("history", "2.2", "ja", "migration-2.0"), "docs", "既存Docsを型付きコンテンツへ移す", "既存docsを型付きコンテンツへ移す"),
        new(new("reference", "3.0", "ja", "typed-markdown-collections"), "section-4", "コードの取り込みと本文解析", "コードの取り込みと本文解析"),
        new(new("reference", "3.0", "en", "layout-css-contract"), "theme-switching", "Theme switching", "theme-switching--テーマ切り替え", "docs/layout-css-contract.md"),
        new(new("reference", "3.0", "ja", "layout-css-contract"), "section", "テーマの切り替え", "テーマ切り替え", "docs/layout-css-contract.ja.md"),
        new(new("reference", "3.0", "ja", "quickstart"), "docsci", "このDocsプロジェクトのCI", "このdocsプロジェクトのci"),
        new(new("reference", "3.0", "ja", "quickstart"), "section-3", "使用例と次の手順", "使用例と次の手順"),
        new(new("reference", "3.0", "ja", "known-limitations"), "markdown", "Markdown の互換性", "markdown-の互換性"),
    ];

    static WebsiteDocumentationLayout()
    {
        var aliases = new HashSet<(DocumentKey, string)>();
        foreach (var binding in Bindings)
            if (string.IsNullOrWhiteSpace(binding.NativeId) || string.IsNullOrWhiteSpace(binding.Heading)
                || string.IsNullOrWhiteSpace(binding.Alias) || binding.NativeId == binding.Alias
                || !aliases.Add((binding.Document, binding.Alias)))
                throw new InvalidDataException("Invalid or duplicate website fragment binding: " + binding.Document + "#" + binding.Alias);
    }

    internal static string ProjectSource(string sourcePath, string text)
    {
        foreach (var binding in Bindings.Where(binding => binding.MarkerSource == sourcePath))
        {
            var marker = "<a id=\"" + binding.Alias + "\"></a>";
            var start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0 || text.IndexOf(marker, start + marker.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("Expected one declared website fragment marker: " + sourcePath + "#" + binding.Alias);
            text = text.Remove(start, marker.Length);
        }
        return text;
    }

    public IHtmlContent Render(SitePage<PageLayoutContent> page, PageRenderingContext context)
    {
        var bindings = Bindings.Where(binding => binding.Document == page.Metadata.Document).ToArray();
        if (bindings.Length == 0)
            return new DocsPageLayout().Render(page, context);
        var body = page.Content.Body.ToHtmlString();
        using var document = SiteTestDocument.Parse(body);
        var ids = document.Snapshot.QueryAll("[id]");
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in ids)
            if (!existing.Add(element.GetAttribute("id")!))
                throw new InvalidDataException("Duplicate website document ID: " + page.Metadata.Document + "#" + element.GetAttribute("id"));
        existing.UnionWith(document.Snapshot.QueryAll("a[name]").Select(element => element.GetAttribute("name")!));
        var insertions = new List<(int Start, string Html)>();
        foreach (var binding in bindings)
        {
            if (!existing.Add(binding.Alias))
                throw new InvalidDataException("Website fragment alias collides with an existing anchor: " + binding.Document + "#" + binding.Alias);
            var matches = ids.Where(element => element.GetAttribute("id") == binding.NativeId).ToArray();
            if (matches.Length != 1 || matches[0].TagName is not ("h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                || matches[0].TextContent != binding.Heading)
                throw new InvalidDataException("Website fragment heading changed or is ambiguous: " + binding.Document + "#" + binding.NativeId);
            var heading = matches[0];
            var opener = "<" + heading.TagName + " id=\"" + Html.Encode(binding.NativeId) + "\">";
            if (heading.SourceStart is not int start || heading.SourceLength != opener.Length
                || start < 0 || start > body.Length - opener.Length
                || !body.AsSpan(start, opener.Length).SequenceEqual(opener.AsSpan()))
                throw new InvalidDataException("Website fragment heading has no exact native opening tag: " + binding.Document + "#" + binding.NativeId);
            insertions.Add((start, "<span id=\"" + Html.Encode(binding.Alias) + "\" tabindex=\"-1\"></span>"));
        }
        foreach (var insertion in insertions.OrderByDescending(insertion => insertion.Start))
            body = body.Insert(insertion.Start, insertion.Html);
        return new DocsPageLayout().Render(new(page.Id, page.Route,
            page.Content with { Body = Html.UnsafeRaw(body) }, page.Metadata), context);
    }
}
