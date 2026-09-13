namespace LithoSharp.Content;

/// <summary>
/// A source that a post references.
/// </summary>
public sealed record PostSourceReference
{
    /// <summary>Kind of source (for example <c>feed</c>, <c>article</c>, or <c>repo</c>).</summary>
    [System.ComponentModel.Description("Kind of source (for example feed, article, or repo).")]
    public string Type { get; init; } = string.Empty;

    /// <summary>Name of the source.</summary>
    [System.ComponentModel.Description("Name of the source.")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Source URL.</summary>
    [System.ComponentModel.Description("Source URL.")]
    public string? Url { get; init; }
}
