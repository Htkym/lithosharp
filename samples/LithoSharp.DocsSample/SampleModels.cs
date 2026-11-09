using LithoSharp.Content;

[StaticContentCollection(typeof(ArticleFrontMatter), typeof(ContentEntry<ArticleFrontMatter, string>), "articles", EmitStaticSiteManifest = true)]
public static partial class GeneratedArticles
{
}

public sealed class ArticleFrontMatter
{
    public required string Title { get; init; }
    public string Summary { get; init; } = string.Empty;
    public int Order { get; init; }
    public DateTimeOffset PublishedFrom { get; init; }
    public bool Draft { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<string> Environments { get; init; } = [];
}

public sealed record TopicIndex(
    string Topic,
    IReadOnlyList<ContentEntry<ArticleFrontMatter, string>> Entries);
