using YamlDotNet.Serialization;

namespace LithoSharp.Content;

/// <summary>
/// Markdown 投稿の YAML front matter です。公開条件のキーは
/// <c>draft</c>、<c>publish_from</c>、<c>publish_until</c>、
/// <c>environments</c> です。
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

    /// <summary>Optional sort order in the documentation sidebar.</summary>
    [YamlMember(Alias = "sidebar_position")]
    public int? SidebarPosition { get; init; }

    /// <summary>Optional sidebar label. Defaults to <see cref="Title"/>.</summary>
    [YamlMember(Alias = "sidebar_label")]
    public string? SidebarLabel { get; init; }

    /// <summary>下書きとして公開しないかどうか。</summary>
    [YamlMember(Alias = "draft")]
    public bool Draft { get; init; }

    /// <summary>公開を開始する時刻。この時刻は公開範囲に含まれます。</summary>
    [YamlMember(Alias = "publish_from")]
    public DateTimeOffset? PublishFrom { get; init; }

    /// <summary>公開を終了する時刻。この時刻は公開範囲に含まれません。</summary>
    [YamlMember(Alias = "publish_until")]
    public DateTimeOffset? PublishUntil { get; init; }

    /// <summary>公開を許可する環境名。空の場合はすべての環境を許可します。</summary>
    [YamlMember(Alias = "environments")]
    public List<string> Environments { get; init; } = [];
}
