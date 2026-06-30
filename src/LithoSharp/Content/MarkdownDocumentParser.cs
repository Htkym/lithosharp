namespace LithoSharp.Content;

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

        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Markdown document '{path}' must start with YAML front matter.");
        }

        using var reader = new StringReader(text);
        _ = reader.ReadLine();
        var yaml = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line == "---")
            {
                return (string.Join(Environment.NewLine, yaml), reader.ReadToEnd());
            }

            yaml.Add(line);
        }

        throw new InvalidOperationException($"Markdown document '{path}' has no closing front matter marker.");
    }
}
