namespace LithoSharp.Content;

/// <summary>
/// A Markdown post that has been read from disk.
/// </summary>
/// <param name="FilePath">Path to the post file.</param>
/// <param name="Slug">Slug used in the URL.</param>
/// <param name="FrontMatter">Front matter.</param>
/// <param name="MarkdownBody">Markdown body.</param>
/// <param name="RelativeOutputPath">Relative path of the generated HTML.</param>
public sealed record MarkdownPost(
    string FilePath,
    string Slug,
    PostFrontMatter FrontMatter,
    string MarkdownBody,
    string RelativeOutputPath);
