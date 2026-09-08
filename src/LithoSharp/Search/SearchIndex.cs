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
    /// <summary>Searches a document set without a network service.</summary>
    public static IReadOnlyList<SearchHit> Query(IEnumerable<SearchDocument> documents, string query, string? collection = null,
        string? version = null, string? locale = null, int limit = 30)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        string Normalize(string text) => System.Text.RegularExpressions.Regex.Replace(text.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
        var tokens = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return documents.Where(document => (collection is null || document.Collection == collection) && (version is null || document.Version == version) && (locale is null || document.Locale == locale))
            .Select(document =>
            {
                var sections = document.Sections ?? [];
                var section = sections.FirstOrDefault(section => tokens.All(token => Normalize(section.Title + " " + section.Body).Contains(token, StringComparison.Ordinal)));
                var text = Normalize(document.Title + " " + document.Summary + " " + document.Body);
                if (!tokens.All(token => text.Contains(token, StringComparison.Ordinal))) return null;
                var body = section?.Body ?? document.Body;
                var position = tokens.Length == 0 ? 0 : Math.Max(0, body.IndexOf(tokens[0], StringComparison.OrdinalIgnoreCase) - 40);
                return new SearchHit(document, document.Url + (section is null ? "" : "#" + Uri.EscapeDataString(section.Anchor)),
                    body.Substring(position, Math.Min(180, body.Length - position)), tokens.Sum(token => Normalize(document.Title).Contains(token, StringComparison.Ordinal) ? 3 : 1) + (section is null ? 0 : 1));
            }).OfType<SearchHit>().OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Url, StringComparer.Ordinal).Take(limit).ToArray();
    }
}
