using LithoSharp.Content;

[StaticContentCollection(typeof(ArticleFrontMatter), typeof(ContentEntry<ArticleFrontMatter, string>), "articles")]
public static partial class GeneratedArticles
{
}

public sealed class ArticleFrontMatter
{
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public DateTimeOffset PublishedFrom { get; init; }
    public bool Draft { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<string> Environments { get; init; } = [];
}

public sealed record TopicIndex(
    string Topic,
    IReadOnlyList<ContentEntry<ArticleFrontMatter, string>> Entries);
