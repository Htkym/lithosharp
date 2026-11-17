using System.Security.Cryptography;
using System.Text;

namespace LithoSharp.Documentation;

internal sealed class ApiCatalogNavigation
{
    private readonly ApiMember[] members;
    private readonly Dictionary<string, ApiMember> types;
    private readonly ILookup<string?, ApiMember> children;
    private readonly Func<string, SiteUrl> url;
    private readonly SiteUrl indexUrl;
    private readonly string locale;

    internal ApiCatalogNavigation(ApiMember[] members, Func<string, SiteUrl> url, SiteUrl indexUrl, string locale)
    {
        this.members = members;
        types = members.Where(member => member.Id.StartsWith("T:", StringComparison.Ordinal)).ToDictionary(member => member.Id, StringComparer.Ordinal);
        this.url = url;
        this.indexUrl = indexUrl;
        this.locale = locale;
        children = members.ToLookup(member => ParentType(member.Id));
    }

    private string Text(string english, string japanese) => locale.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ? japanese : english;
    private string? ParentType(string id)
    {
        if (id.Length < 3 || id[1] != ':' || "TMPFE".IndexOf(id[0]) < 0)
            return null;
        var name = id[2..].Split('(')[0].Split('~')[0];
        while (name.LastIndexOf('.') is var dot && dot >= 0)
        {
            name = name[..dot];
            if (types.ContainsKey("T:" + name))
                return "T:" + name;
        }
        return null;
    }

    private string Category(ApiMember member)
    {
        var type = member.Id.StartsWith("T:", StringComparison.Ordinal) ? member.Id : ParentType(member.Id);
        if (type is null)
            return Text("Other APIs", "その他の API");
        while (ParentType(type) is { } parent)
            type = parent;
        var name = type[2..];
        var dot = name.LastIndexOf('.');
        return dot < 0 ? Text("Global namespace", "グローバル名前空間") : name[..dot];
    }

    private static string Anchor(string category) => "api-category-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(category)))[..12];
    private string Link(ApiMember member) => "<a href=\"" + url(member.Id).ToAttributeValue() + "\"><code>" + Html.Encode(DisplayName(member)) + "</code></a>";
    private string DisplayName(ApiMember member)
    {
        var prefix = ParentType(member.Id) is { } parent ? parent[2..] + "." : Category(member) + ".";
        var name = member.Title.StartsWith(prefix, StringComparison.Ordinal) ? member.Title[prefix.Length..] : member.Title;
        return name.StartsWith("#ctor", StringComparison.Ordinal) && ParentType(member.Id) is { } owner
            ? owner[(owner.LastIndexOf('.') + 1)..].Replace("T:", "", StringComparison.Ordinal) + name[5..] : name;
    }
    private string Table(IEnumerable<ApiMember> entries) => "<div class=\"api-table-scroll\"><table class=\"api-member-table\"><thead><tr><th scope=\"col\">"
        + Text("API", "API") + "</th><th scope=\"col\">" + Text("Description", "説明") + "</th></tr></thead><tbody>"
        + string.Concat(entries.Select(member => "<tr><th scope=\"row\">" + Link(member) + "</th><td>" + Html.Encode(member.Summary)
            + (member.Deprecated ? " <span class=\"api-deprecated\">" + Text("Deprecated", "非推奨") + "</span>" : "") + "</td></tr>"))
        + "</tbody></table></div>";

    internal string Sidebar(ApiMember? current = null)
    {
        var activeType = current is null ? null : types.ContainsKey(current.Id) ? current.Id : ParentType(current.Id);
        LithoSharp.Content.DocsPresentationNode Item(ApiMember entry)
        {
            var descendants = children[entry.Id].Where(child => child.Id.StartsWith("T:", StringComparison.Ordinal) || entry.Id == activeType)
                .Select(Item).ToArray();
            return new(DisplayName(entry), url(entry.Id).Value, null, descendants);
        }
        var groups = members.Where(member => ParentType(member.Id) is null).GroupBy(Category).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new LithoSharp.Content.DocsPresentationNode(group.Key, indexUrl.Value + "#" + Anchor(group.Key), null,
                group.OrderBy(member => member.Title, StringComparer.Ordinal).Select(Item).ToArray())).ToArray();
        var root = new LithoSharp.Content.DocsPresentationNode("API", null, null,
            [new(Text("API index", "API 一覧"), indexUrl.Value, null, []), .. groups]);
        return DocsNavigationComponent.RenderCore(root, [], current is null ? indexUrl.Value : url(current.Id).Value, collapseTopics: true, locale);
    }
    internal string Index()
    {
        var entries = members.Where(member => member.Id.StartsWith("T:", StringComparison.Ordinal) || ParentType(member.Id) is null);
        var groups = entries.GroupBy(Category).OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        return "<div class=\"api-category-grid\">"
            + string.Concat(groups.Select(group => "<details class=\"api-category-card\" id=\"" + Anchor(group.Key) + "\"><summary><h2>" + Html.Encode(group.Key)
                + "</h2><span class=\"api-category-count\">" + group.Count() + " " + Text("APIs", "件") + "</span></summary><ul class=\"api-type-list\">"
                + string.Concat(group.OrderBy(member => member.Title, StringComparer.Ordinal).Select(member => "<li>" + Link(member)
                    + (member.Summary.Length == 0 ? "" : "<p>" + Html.Encode(member.Summary) + "</p>") + "</li>")) + "</ul></details>")) + "</div>";
    }

    internal string Breadcrumb(ApiMember member)
    {
        var parent = ParentType(member.Id);
        var category = Category(member);
        return "<nav class=\"api-breadcrumbs\" aria-label=\"" + Text("Breadcrumb", "パンくずリスト") + "\"><ol><li><a href=\"" + indexUrl.ToAttributeValue() + "\">"
            + Text("API index", "API 一覧") + "</a></li><li><a href=\"" + indexUrl.ToAttributeValue() + "#" + Anchor(category) + "\">" + Html.Encode(category) + "</a></li>"
            + (parent is null ? "" : "<li>" + Link(types[parent]) + "</li>") + "<li aria-current=\"page\">" + Html.Encode(member.Title) + "</li></ol></nav>";
    }

    internal string Children(ApiMember member)
    {
        if (!types.ContainsKey(member.Id))
            return "";
        var childMembers = children[member.Id].ToArray();
        var kinds = new[] { ('T', "Nested types", "入れ子の型"), ('C', "Constructors", "コンストラクター"), ('P', "Properties", "プロパティ"),
            ('M', "Methods", "メソッド"), ('F', "Fields", "フィールド"), ('E', "Events", "イベント") };
        return string.Concat(kinds.Select(kind =>
        {
            var group = childMembers.Where(candidate => (candidate.Id.StartsWith("M:", StringComparison.Ordinal) && candidate.Id.Contains(".#ctor", StringComparison.Ordinal) ? 'C' : candidate.Id[0]) == kind.Item1).ToArray();
            return group.Length == 0 ? "" : "<section class=\"api-members\" id=\"api-members-" + kind.Item1 + "\"><h2>" + Text(kind.Item2, kind.Item3) + "</h2>" + Table(group) + "</section>";
        }));
    }
}
