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
    string Body);
