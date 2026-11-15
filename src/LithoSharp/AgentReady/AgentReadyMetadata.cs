using System.Globalization;
using System.Text;
using System.Text.Json;
using LithoSharp.HtmlParsing;

namespace LithoSharp.AgentReady;

/// <summary>Head additions for custom layouts, using the same explicit public facts as built-in layouts.</summary>
public static class AgentReadyMetadata
{
    public static IHtmlContent RenderHead(AgentReadyHeadData data, AgentReadyStructuredDataOptions options)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);
        var builder = new StringBuilder();
        if (data.MarkdownUrl is { } markdown)
            builder.Append("<link rel=\"alternate\" type=\"text/markdown\" href=\"").Append(Html.Encode(RequireHttpUrl(markdown))).Append("\">\n");
        if (data.LlmsUrl is { } llms)
            builder.Append("<link rel=\"describedby\" href=\"").Append(Html.Encode(RequireHttpUrl(llms))).Append("\">\n");
        var mode = data.Page.Mode ?? options.Mode;
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown structured-data mode.");
        if (mode == AgentReadyStructuredDataMode.Automatic && !HasJsonLd(data.ExistingHead))
            builder.Append("<script type=\"application/ld+json\" data-lithosharp-agent-ready=\"automatic\">")
                .Append(BuildJson(data, options)).Append("</script>\n");
        return Html.UnsafeRaw(builder.ToString());
    }

    internal static bool HasJsonLd(IHtmlContent? head) => head is not null && HtmlFacts.Parse(head.ToHtmlString()).Elements
        .Any(element => element.Name == "script" && string.Equals(HtmlFacts.Attribute(element, "type")?.Trim(), "application/ld+json", StringComparison.OrdinalIgnoreCase));

    internal static string BuildJson(AgentReadyHeadData data, AgentReadyStructuredDataOptions options)
    {
        var canonical = RequireHttpUrl(data.CanonicalUrl);
        var site = RequireHttpUrl(data.SiteUrl);
        if (!Enum.IsDefined(data.Page.Kind))
            throw new ArgumentOutOfRangeException(nameof(data), "Unknown structured-data page kind.");
        var webSite = new Dictionary<string, object?>
        {
            ["@type"] = "WebSite",
            ["@id"] = site + "#website",
            ["url"] = site,
        };
        Put(webSite, "name", data.SiteTitle);
        Put(webSite, "inLanguage", data.SiteLanguage);
        var page = new Dictionary<string, object?>
        {
            ["@type"] = data.Page.Kind.ToString(),
            ["@id"] = canonical + "#webpage",
            ["url"] = canonical,
            ["isPartOf"] = new Dictionary<string, object?> { ["@id"] = site + "#website" },
        };
        Put(page, "name", data.Title);
        if (data.Page.Kind != AgentReadyPageKind.WebPage)
            Put(page, "headline", data.Title);
        Put(page, "description", data.Description);
        Put(page, "inLanguage", data.Language);
        if (data.Page.Published is { } published)
            page["datePublished"] = published.ToString("O", CultureInfo.InvariantCulture);
        if (data.Page.Modified is { } modified)
            page["dateModified"] = modified.ToString("O", CultureInfo.InvariantCulture);
        if (data.Page.ImageUrl is { } image)
            page["image"] = RequireHttpUrl(image);
        if ((data.Page.Author ?? options.Author) is { } author)
            page["author"] = Identity(author);
        if ((data.Page.Publisher ?? options.Publisher) is { } publisher)
            page["publisher"] = Identity(publisher);
        // The default encoder escapes HTML-sensitive characters, including a closing script tag.
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@graph"] = new[] { webSite, page },
        });
    }

    internal static string RequireHttpUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")
            || address.UserInfo.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("A public URL must be an absolute HTTP(S) URL without credentials or a fragment.", nameof(value));
        return value;
    }

    private static object Identity(AgentReadyIdentity identity)
    {
        if (!Enum.IsDefined(identity.Kind) || string.IsNullOrWhiteSpace(identity.Name))
            throw new ArgumentException("A public identity requires a known kind and a non-empty name.", nameof(identity));
        var result = new Dictionary<string, object?> { ["@type"] = identity.Kind.ToString(), ["name"] = identity.Name };
        if (identity.Url is { } url)
            result["url"] = RequireHttpUrl(url);
        if (identity.SameAs.Count != 0)
            result["sameAs"] = identity.SameAs.Select(RequireHttpUrl).ToArray();
        return result;
    }

    private static void Put(Dictionary<string, object?> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            target[key] = value;
    }
}

internal sealed record AgentReadyIndexEntry(string HtmlUrl, string? MarkdownUrl, string Title, string? Description);

internal static class AgentReadyLlmsFormatting
{
    internal static string Heading(string value) => EscapeText(Plain(value));
    internal static string Description(string value) => EscapeText(Plain(value));

    internal static string Entry(AgentReadyIndexEntry entry)
    {
        var url = AgentReadyMetadata.RequireHttpUrl(entry.MarkdownUrl ?? entry.HtmlUrl);
        var destination = url.Replace("&", "&#38;", StringComparison.Ordinal).Replace("\\", "%5C", StringComparison.Ordinal).Replace("<", "%3C", StringComparison.Ordinal)
            .Replace(">", "%3E", StringComparison.Ordinal).Replace(" ", "%20", StringComparison.Ordinal);
        var title = EscapeText(Plain(entry.Title));
        var description = EscapeText(Plain(entry.Description ?? string.Empty));
        return "- [" + title + "](<" + destination + ">)" + (description.Length == 0 ? string.Empty : ": " + description) + "\n";
    }

    private static string EscapeText(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if ("\\`*_{}[]()#+-.!|<>&".Contains(character))
                escaped.Append('\\');
            escaped.Append(character);
        }
        return escaped.ToString();
    }

    private static string Plain(string value) => string.Join(" ", new string(value.Select(character => char.IsControl(character) ? ' ' : character).ToArray()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
