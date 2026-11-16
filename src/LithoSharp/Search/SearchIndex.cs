namespace LithoSharp.Search;

/// <summary>
/// The complete search index used by the client-side fuzzy search.
/// </summary>
/// <param name="Site">Site name.</param>
/// <param name="Generated">When the index was generated (UTC, ISO 8601).</param>
/// <param name="Documents">The posts to search.</param>
public sealed record SearchIndex(string Site, string Generated, IReadOnlyList<SearchDocument> Documents);

/// <summary>
/// One post's worth of information in the search index.
/// </summary>
/// <param name="Title">Post title.</param>
/// <param name="Summary">Post summary.</param>
/// <param name="Tags">Post tags.</param>
/// <param name="Url">Link to the post page.</param>
/// <param name="Date">Publication date formatted for display.</param>
/// <param name="Body">Plain-text body (shortened for search).</param>
public sealed record SearchDocument(
    string Title,
    string Summary,
    IReadOnlyList<string> Tags,
    string Url,
    string Date,
    string Body)
{
    /// <summary>The document collection, when this entry is contextual documentation.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Collection { get; init; }
    /// <summary>The document version.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }
    /// <summary>The document language.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Locale { get; init; }
    /// <summary>Searchable sections with stable anchors.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SearchSection>? Sections { get; init; }
}

/// <summary>A searchable document section.</summary>
public sealed record SearchSection(string Title, string Anchor, string Body);

/// <summary>A ranked search result with a matching section and plain-text excerpt.</summary>
public sealed record SearchHit(SearchDocument Document, string Url, string Excerpt, int Score);

/// <summary>Local contextual search for Japanese text and API identifiers.</summary>
public static class LocalSearch
{
    internal static string BrowserScript { get; } = ReadBrowserScript();

    /// <summary>Searches a document set without a network service.</summary>
    public static IReadOnlyList<SearchHit> Query(IEnumerable<SearchDocument> documents, string query, string? collection = null,
        string? version = null, string? locale = null, int limit = 30)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var exact = Fold(query);
        var words = Words(query);
        var tokens = words.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        var scoped = documents.Where(document => (collection is null || document.Collection == collection) && (version is null || document.Version == version) && (locale is null || document.Locale == locale));
        if (tokens.Length == 0)
            return scoped.Select(document =>
            {
                var section = document.Sections?.FirstOrDefault();
                return new SearchHit(document, document.Url + (section is null ? "" : "#" + Uri.EscapeDataString(section.Anchor)),
                    Excerpt(section?.Body ?? document.Body, tokens), section is null ? 0 : 1);
            }).OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Url, StringComparer.Ordinal).Take(limit).ToArray();
        return scoped
            .Select(document =>
            {
                var title = Text(document.Title);
                var summary = Text(document.Summary);
                var tags = Text(string.Join(' ', document.Tags));
                var body = Text(document.Body);
                var sections = (document.Sections ?? []).Select(section => (Section: section, Title: Text(section.Title), Body: Text(section.Body))).ToArray();
                var fields = new[] { title, summary, tags, body }.Concat(sections.SelectMany(section => new[] { section.Title, section.Body })).ToArray();
                if (!tokens.All(token => fields.Any(field => Contains(field, token))))
                    return null;
                SearchSection? best = null;
                var sectionScore = 0;
                foreach (var section in sections)
                {
                    if (!tokens.Any(token => Contains(section.Title, token) || Contains(section.Body, token)) ||
                        !tokens.All(token => Contains(title, token) || Contains(summary, token) || Contains(tags, token) || Contains(section.Title, token) || Contains(section.Body, token)))
                        continue;
                    var score = (section.Title.Exact.Contains(exact, StringComparison.Ordinal) || section.Body.Exact.Contains(exact, StringComparison.Ordinal) ? 1000 : 0)
                        + Score(section.Title, exact, words, tokens) * 3 + Score(section.Body, exact, words, tokens);
                    if (best is null || score > sectionScore)
                    {
                        best = section.Section;
                        sectionScore = score;
                    }
                }
                var total = 1000 + (fields.Any(field => field.Exact.Contains(exact, StringComparison.Ordinal)) ? 10000 : 0)
                    + Score(title, exact, words, tokens) * 4 + Score(summary, exact, words, tokens) * 2
                    + Score(tags, exact, words, tokens) + Score(body, exact, words, tokens) + sectionScore;
                return new SearchHit(document, document.Url + (best is null ? "" : "#" + Uri.EscapeDataString(best.Anchor)),
                    Excerpt(best?.Body ?? document.Body, tokens), total);
            }).OfType<SearchHit>().OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Url, StringComparer.Ordinal).Take(limit).ToArray();
    }

    private static string ReadBrowserScript()
    {
        using var reader = new StreamReader(typeof(LocalSearch).Assembly.GetManifestResourceStream("LithoSharp.Search.query.mjs")!);
        return reader.ReadToEnd();
    }

    private static string Fold(string text) => System.Text.RegularExpressions.Regex.Replace(
        text.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant(), @"\s+", " ").Trim();

    private static string Words(string text)
    {
        var normalized = text.Normalize(System.Text.NormalizationForm.FormKC);
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"(\p{Lu})(\p{Lu}\p{Ll})", "$1 $2");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"([\p{Ll}\p{Nd}])(\p{Lu})", "$1 $2");
        return System.Text.RegularExpressions.Regex.Replace(normalized.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    }

    private static (string Exact, string Words) Text(string value) => (Fold(value), Words(value));

    private static bool Contains((string Exact, string Words) text, string token) =>
        text.Exact.Contains(token, StringComparison.Ordinal) || text.Words.Contains(token, StringComparison.Ordinal);

    private static int Score((string Exact, string Words) text, string exact, string words, string[] tokens) =>
        (text.Exact == exact ? 120 : text.Exact.Contains(exact, StringComparison.Ordinal) ? 60 : text.Words == words ? 30 : text.Words.Contains(words, StringComparison.Ordinal) ? 15 : 0)
        + tokens.Count(token => Contains(text, token));

    private static string Excerpt(string body, string[] tokens)
    {
        var first = tokens.Select(token => body.IndexOf(token, StringComparison.OrdinalIgnoreCase)).Where(index => index >= 0).DefaultIfEmpty(0).Min();
        var position = Math.Max(0, first - 40);
        return body.Substring(position, Math.Min(180, body.Length - position));
    }
}
