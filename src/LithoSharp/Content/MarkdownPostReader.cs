namespace LithoSharp.Content;

/// <summary>
/// A reader that discovers Markdown posts and parses their front matter.
/// </summary>
public sealed class MarkdownPostReader
{
    /// <summary>Reads the Markdown posts under the given directory.</summary>
    /// <param name="contentDirectory">The content directory.</param>
    /// <returns>The posts ordered newest first.</returns>
    public async Task<IReadOnlyList<MarkdownPost>> ReadAllAsync(string contentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentDirectory);

        if (!Directory.Exists(contentDirectory))
        {
            return [];
        }

        var posts = new List<MarkdownPost>();
        foreach (var path in Directory.EnumerateFiles(contentDirectory, "*.md", SearchOption.AllDirectories))
        {
            posts.Add(await ReadAsync(path, contentDirectory).ConfigureAwait(false));
        }

        return posts
            .OrderByDescending(post => post.FrontMatter.Date)
            .ThenBy(post => post.Slug, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Reads a single Markdown post.</summary>
    /// <param name="path">Path to the Markdown file.</param>
    /// <param name="contentRoot">Root of the content directory.</param>
    /// <returns>The post that was read.</returns>
    public async Task<MarkdownPost> ReadAsync(string path, string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        var (yaml, body) = MarkdownDocumentParser.SplitFrontMatter(text, path);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new InvalidOperationException($"Post '{path}' has empty front matter.");
        }

        var frontMatter = MarkdownFrontMatterYaml.Deserialize(yaml)
            ?? throw new InvalidOperationException($"Post '{path}' has empty front matter.");

        Validate(frontMatter, path);

        var relative = Path.GetRelativePath(contentRoot, path);
        var withoutExtension = Path.ChangeExtension(relative, ".html");
        var normalizedOutput = withoutExtension.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        var slug = SlugHelper.ToSlug(Path.GetFileNameWithoutExtension(path));

        return new MarkdownPost(path, slug, frontMatter, body.Trim(), $"posts/{normalizedOutput}");
    }
    private static void Validate(PostFrontMatter frontMatter, string path)
    {
        if (string.IsNullOrWhiteSpace(frontMatter.Title))
        {
            throw new InvalidOperationException($"Post '{path}' is missing title.");
        }

        if (frontMatter.Date == default)
        {
            throw new InvalidOperationException($"Post '{path}' is missing date.");
        }
    }
}
