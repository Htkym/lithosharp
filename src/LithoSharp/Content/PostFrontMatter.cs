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
    [System.ComponentModel.Description("Post title.")]
    public string Title { get; init; } = string.Empty;

    /// <summary>Publication date and time.</summary>
    [System.ComponentModel.Description("Publication date and time.")]
    public DateTimeOffset Date { get; init; }

    /// <summary>Short description of the post.</summary>
    [System.ComponentModel.Description("Short description of the post.")]
    public string Summary { get; init; } = string.Empty;

    /// <summary>Post tags.</summary>
    [System.ComponentModel.Description("Post tags.")]
    public List<string> Tags { get; init; } = [];

    /// <summary>Sources the post references.</summary>
    [System.ComponentModel.Description("Sources the post references.")]
    public List<PostSourceReference> Sources { get; init; } = [];

    /// <summary>Optional sort order in the documentation sidebar.</summary>
    [System.ComponentModel.Description("Optional sort order in the documentation sidebar.")]
    [YamlMember(Alias = "sidebar_position")]
    public int? SidebarPosition { get; init; }

    /// <summary>Optional sidebar label. Defaults to <see cref="Title"/>.</summary>
    [System.ComponentModel.Description("Optional sidebar label. Defaults to the title.")]
    [YamlMember(Alias = "sidebar_label")]
    public string? SidebarLabel { get; init; }

    /// <summary>下書きとして公開しないかどうか。</summary>
    [System.ComponentModel.Description("Whether the post is a draft and stays unpublished.")]
    [YamlMember(Alias = "draft")]
    public bool Draft { get; init; }

    /// <summary>公開を開始する時刻。この時刻は公開範囲に含まれます。</summary>
    [System.ComponentModel.Description("The publication start time. This time is included in the range.")]
    [YamlMember(Alias = "publish_from")]
    public DateTimeOffset? PublishFrom { get; init; }

    /// <summary>公開を終了する時刻。この時刻は公開範囲に含まれません。</summary>
    [System.ComponentModel.Description("The publication end time. This time is excluded from the range.")]
    [YamlMember(Alias = "publish_until")]
    public DateTimeOffset? PublishUntil { get; init; }

    /// <summary>公開を許可する環境名。空の場合はすべての環境を許可します。</summary>
    [System.ComponentModel.Description("Environment names allowed to publish. Empty allows all environments.")]
    [YamlMember(Alias = "environments")]
    public List<string> Environments { get; init; } = [];
}
