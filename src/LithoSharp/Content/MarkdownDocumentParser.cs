namespace LithoSharp.Content;

using LithoSharp.Content.Compilation;

/// <summary>
/// Splits a Markdown document that has front matter.
/// </summary>
public static class MarkdownDocumentParser
{
    /// <summary>Splits the document into front matter and body.</summary>
    /// <param name="text">Full Markdown text.</param>
    /// <param name="path">Path used in error messages.</param>
    /// <returns>The YAML front matter and the body.</returns>
    public static (string Yaml, string Body) SplitFrontMatter(string text, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var split = FrontMatterSplitter.TrySplit(text);
        return split.Status switch
        {
            // A leading BOM is skipped by the shared scanner, matching the
            // collection loader and MDX handling. Previously a BOM-prefixed
            // document failed the opening marker check here.
            FrontMatterSplitStatus.Ok => (split.Yaml, split.Body),
            // Empty front matter still yields the body after the closing
            // marker, matching the previous StringReader implementation.
            FrontMatterSplitStatus.EmptyFrontMatter => (split.Yaml, split.Body),
            FrontMatterSplitStatus.UnterminatedFrontMatter =>
                throw new InvalidOperationException($"Markdown document '{path}' has no closing front matter marker."),
            _ => throw new InvalidOperationException($"Markdown document '{path}' must start with YAML front matter."),
        };
    }
}
