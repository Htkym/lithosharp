namespace LithoSharp.Content;

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
    [ContentName("sidebar_position")]
    public int? SidebarPosition { get; init; }

    /// <summary>Optional sidebar label. Defaults to <see cref="Title"/>.</summary>
    [System.ComponentModel.Description("Optional sidebar label. Defaults to the title.")]
    [ContentName("sidebar_label")]
    public string? SidebarLabel { get; init; }

    [System.ComponentModel.Description("Whether the post is a draft and stays unpublished.")]
    [ContentName("draft")]
    public bool Draft { get; init; }

    [System.ComponentModel.Description("The publication start time. This time is included in the range.")]
    [ContentName("publish_from")]
    public DateTimeOffset? PublishFrom { get; init; }

    [System.ComponentModel.Description("The publication end time. This time is excluded from the range.")]
    [ContentName("publish_until")]
    public DateTimeOffset? PublishUntil { get; init; }

    [System.ComponentModel.Description("Environment names allowed to publish. Empty allows all environments.")]
    [ContentName("environments")]
    public List<string> Environments { get; init; } = [];
}
