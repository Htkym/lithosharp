namespace PageSharp;

/// <summary>
/// The result of validating Markdown posts.
/// </summary>
/// <param name="PostCount">Number of posts that were validated.</param>
public sealed record ContentValidationResult(int PostCount);
