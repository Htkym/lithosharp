namespace PageSharp.Content;

/// <summary>
/// A source that a post references.
/// </summary>
public sealed record PostSourceReference
{
    /// <summary>Kind of source (for example <c>feed</c>, <c>article</c>, or <c>repo</c>).</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Name of the source.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Source URL.</summary>
    public string? Url { get; init; }
}
