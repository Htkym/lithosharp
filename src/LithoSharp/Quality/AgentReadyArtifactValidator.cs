using System.Text.Json;
using LithoSharp.AgentReady;
using LithoSharp.Diagnostics;
using LithoSharp.HtmlParsing;
using LithoSharp.Routing;

namespace LithoSharp.Quality;

internal sealed record AgentReadyArtifactExpectation(SiteRoute HtmlRoute, AgentReadyHeadData Head,
    AgentReadyStructuredDataOptions StructuredData, SiteRoute? MarkdownRoute, SiteRoute? LlmsRoute);

// Generated-artifact inspection only. This does not make HTTP requests or claim host configuration.
internal static class AgentReadyArtifactValidator
{
    internal static async Task<IReadOnlyList<SiteDiagnostic>> ValidateAsync(
        IReadOnlyList<AgentReadyArtifactExpectation> expectations,
        IReadOnlyDictionary<string, SiteRoute> artifacts,
        Func<string, CancellationToken, Task<string>> readText,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<QualityHtmlFacts>>? getFacts = null,
        bool validateJson = true,
        Action? onDirectParse = null)
    {
        var diagnostics = new List<SiteDiagnostic>();
        foreach (var expected in expectations.OrderBy(value => value.HtmlRoute.RelativeOutputPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = expected.HtmlRoute.RelativeOutputPath;
            if (!artifacts.ContainsKey(path))
            {
                diagnostics.Add(new("LSQ014", SiteDiagnosticSeverity.Error, "The planned HTML artifact does not exist.", new(path), "AgentReady", null));
                continue;
            }
            var html = await readText(path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (validateJson || getFacts is null)
                onDirectParse?.Invoke();
            // Always read the current artifact. Reuse only local observations, never validity.
            var tree = validateJson ? HtmlFacts.Parse(html, cancellationToken) : null;
            var facts = tree is not null ? QualityHtmlFacts.From(tree, cancellationToken)
                : getFacts is not null ? await getFacts(html, cancellationToken).ConfigureAwait(false)
                : QualityHtmlFacts.Parse(html, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var canonicals = facts.Elements.Where(element => element.Name == "link" && HasRel(element, "canonical")).ToArray();
            if (canonicals.Length != 1 || !SameAddress(QualityHtmlFacts.Attribute(canonicals[0], "href"), expected.Head.CanonicalUrl, expected.Head.CanonicalUrl))
                Add("LSQ014", "The public HTML canonical must match the shared Agent Ready page plan.", canonicals.FirstOrDefault());
            var languageElement = facts.Elements.FirstOrDefault(element => element.Name == "html");
            if (!string.IsNullOrWhiteSpace(expected.Head.Language) && (languageElement is null || QualityHtmlFacts.Attribute(languageElement, "lang") != expected.Head.Language))
                Add("LSQ014", "The HTML language must match the shared Agent Ready page plan.", languageElement);
            var resolutionBase = expected.Head.CanonicalUrl;
            if (facts.BaseHref is { } baseHref && Uri.TryCreate(new Uri(resolutionBase, UriKind.Absolute), baseHref, out var documentBase))
                resolutionBase = documentBase.AbsoluteUri;
            var markdownLinks = facts.Elements.Where(element => element.Name == "link" && HasRel(element, "alternate")
                && string.Equals(QualityHtmlFacts.Attribute(element, "type")?.Trim(), "text/markdown", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (expected.MarkdownRoute is null)
            {
                if (markdownLinks.Length != 0)
                    Add("LSQ014", "A Markdown alternate was emitted without a complete planned Markdown artifact.", markdownLinks[0]);
            }
            else if (!artifacts.ContainsKey(expected.MarkdownRoute.RelativeOutputPath)
                || markdownLinks.Length != 1 || !SameAddress(QualityHtmlFacts.Attribute(markdownLinks[0], "href"), expected.Head.MarkdownUrl, resolutionBase))
                Add("LSQ014", "The Markdown alternate must identify the one complete planned Markdown artifact.", markdownLinks.FirstOrDefault());

            var describedBy = facts.Elements.Where(element => element.Name == "link" && HasRel(element, "describedby")
                && IsLlmsReference(QualityHtmlFacts.Attribute(element, "href"))).ToArray();
            if (expected.LlmsRoute is not null)
            {
                if (!artifacts.ContainsKey(expected.LlmsRoute.RelativeOutputPath) || describedBy.Length != 1
                    || !SameAddress(QualityHtmlFacts.Attribute(describedBy[0], "href"), expected.Head.LlmsUrl, resolutionBase))
                    Add("LSQ014", "The describedby link must identify an emitted llms.txt index that includes this page.", describedBy.FirstOrDefault());
            }
            else if (describedBy.Length != 0)
                Add("LSQ014", "An llms.txt describedby link was emitted for a page outside that index.", describedBy[0]);

            if (tree is not null)
                ValidateJson(tree, expected, (id, message, element) =>
                {
                    var position = tree.Position(element);
                    diagnostics.Add(new(id, SiteDiagnosticSeverity.Error, message,
                        new(path, position?.Line, position?.Column), "AgentReady", null));
                }, cancellationToken);

            void Add(string id, string message, QualityHtmlElement? element = null) =>
                diagnostics.Add(new(id, SiteDiagnosticSeverity.Error, message,
                    new(path, element?.Line, element?.Column), "AgentReady", null));
        }
        return diagnostics;
    }

    internal static IReadOnlyList<SiteDiagnostic> ValidateRobots(string baseUrl, string robots,
        IReadOnlyDictionary<string, SiteRoute> artifacts)
    {
        var diagnostics = new List<SiteDiagnostic>();
        var root = new Uri(baseUrl, UriKind.Absolute);
        if (root.AbsolutePath != "/")
            diagnostics.Add(new("LSQ015", SiteDiagnosticSeverity.Error, "robots.txt is effective only at the origin root; use External management for this subpath site.", new("robots.txt"), "AgentReady", null));
        foreach (var line in robots.Split('\n'))
        {
            if (!line.TrimStart().StartsWith("Sitemap:", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = line[(line.IndexOf(':') + 1)..].Trim();
            var exists = Uri.TryCreate(value, UriKind.Absolute, out var sitemap) && sitemap.Scheme is "http" or "https"
                && sitemap.Fragment.Length == 0 && sitemap.Query.Length == 0 && artifacts.Values.Where(route => route.RelativeOutputPath == "sitemap.xml").Any(route =>
                    SameAddress(root.GetLeftPart(UriPartial.Authority) + route.PublicPath, value, baseUrl));
            if (!exists)
                diagnostics.Add(new("LSQ015", SiteDiagnosticSeverity.Error, "The robots Sitemap directive must identify an existing absolute sitemap artifact.", new("robots.txt"), "AgentReady", null));
        }
        return diagnostics;
    }

    private static void ValidateJson(HtmlFacts facts, AgentReadyArtifactExpectation expected,
        Action<string, string, HtmlTreeNode?> add, CancellationToken cancellationToken)
    {
        var mode = expected.Head.Page.Mode ?? expected.StructuredData.Mode;
        if (mode == AgentReadyStructuredDataMode.Disabled)
            return;
        var scripts = facts.Elements.Where(element => element.Name == "script"
            && string.Equals(HtmlFacts.Attribute(element, "type")?.Trim(), "application/ld+json", StringComparison.OrdinalIgnoreCase)).ToArray();
        var pageSubjects = 0;
        var managedScripts = 0;
        HtmlTreeNode? mismatchedPage = null;
        foreach (var script in scripts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HtmlFacts.Attribute(script, "data-lithosharp-agent-ready") == "automatic")
                managedScripts++;
            try
            {
                using var document = JsonDocument.Parse(facts.TextContent(script.Id));
                if (HasDuplicateProperties(document.RootElement))
                    add("LSQ013", "A JSON-LD object repeats a property name and has ambiguous public facts.", script);
                if (mode == AgentReadyStructuredDataMode.Automatic)
                {
                    using var planned = JsonDocument.Parse(AgentReadyMetadata.BuildJson(expected.Head, expected.StructuredData));
                    if (!Equivalent(document.RootElement, planned.RootElement))
                        add("LSQ013", "Automatic JSON-LD does not match the typed public facts, including type, URL, language, author, dates and image.", script);
                }
                foreach (var node in Nodes(document.RootElement))
                {
                    if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("@type", out var type) || !PageType(type))
                        continue;
                    if (node.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String
                        && SameAddress(url.GetString(), expected.Head.CanonicalUrl, expected.Head.CanonicalUrl))
                        pageSubjects++;
                    else
                        mismatchedPage ??= script;

                }
            }
            catch (JsonException)
            {
                add("LSQ013", "A JSON-LD script does not contain valid JSON.", script);
            }
        }
        if (pageSubjects == 0 && mode == AgentReadyStructuredDataMode.UserManaged)
            add("LSQ013", "User-managed JSON-LD must provide a verifiable page type and URL matching the HTML canonical URL.", mismatchedPage ?? scripts.FirstOrDefault());
        if (pageSubjects > 1)
            add("LSQ013", "Multiple structured-data page subjects duplicate or contradict the same HTML page.", scripts.FirstOrDefault());
        if (mode == AgentReadyStructuredDataMode.Automatic && (managedScripts != 1 || scripts.Length != 1 || pageSubjects != 1))
            add("LSQ013", "Automatic and user-managed JSON-LD must not own the same page. Select UserManaged for a custom head.", scripts.FirstOrDefault());

    }

    private static bool HasDuplicateProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1)
            || element.EnumerateObject().Any(property => HasDuplicateProperties(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(HasDuplicateProperties),
        _ => false,
    };

    private static bool Equivalent(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind)
            return false;
        if (expected.ValueKind == JsonValueKind.Object)
            return actual.EnumerateObject().Count() == expected.EnumerateObject().Count()
                && expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var value) && Equivalent(value, property.Value));
        if (expected.ValueKind == JsonValueKind.Array)
            return actual.GetArrayLength() == expected.GetArrayLength()
                && actual.EnumerateArray().Zip(expected.EnumerateArray()).All(pair => Equivalent(pair.First, pair.Second));
        return expected.ValueKind == JsonValueKind.String ? actual.GetString() == expected.GetString()
            : actual.GetRawText() == expected.GetRawText();
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var node in Nodes(item))
                    yield return node;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            yield return element;
            if (element.TryGetProperty("@graph", out var graph))
                foreach (var node in Nodes(graph))
                    yield return node;
        }
    }

    private static bool PageType(JsonElement type) => type.ValueKind == JsonValueKind.String
        ? type.GetString() is "WebPage" or "TechArticle" or "BlogPosting"
        : type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(PageType);
    private static bool HasRel(QualityHtmlElement element, string value) => (QualityHtmlFacts.Attribute(element, "rel") ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(value, StringComparer.OrdinalIgnoreCase);
    private static bool IsLlmsReference(string? value) => value is not null && value.Split('?', '#')[0].EndsWith("llms.txt", StringComparison.OrdinalIgnoreCase);
    private static bool SameAddress(string? value, string? expected, string canonical) => value is not null && expected is not null
        && Uri.TryCreate(new Uri(canonical, UriKind.Absolute), value, out var address)
        && Uri.TryCreate(expected, UriKind.Absolute, out var target) && address == target
        && address.Fragment == target.Fragment && address.UserInfo == target.UserInfo;
}
