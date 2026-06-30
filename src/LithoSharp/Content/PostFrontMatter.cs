namespace LithoSharp.Content;

/// <summary>
/// YAML front matter for a Markdown post.
/// </summary>
public sealed record PostFrontMatter
{
    /// <summary>Post title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Publication date and time.</summary>
    public DateTimeOffset Date { get; init; }

    /// <summary>Short description of the post.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>Post tags.</summary>
    public List<string> Tags { get; init; } = [];

    /// <summary>Sources the post references.</summary>
    public List<PostSourceReference> Sources { get; init; } = [];
}
