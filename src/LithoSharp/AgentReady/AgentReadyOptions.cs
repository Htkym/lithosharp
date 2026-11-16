using LithoSharp.Routing;

namespace LithoSharp.AgentReady;

/// <summary>Explicitly enables public Markdown and its related publication metadata.</summary>
public sealed partial record AgentReadyOptions
{
    /// <summary>Disabled by default. Existing HTML and llms.txt behavior is unchanged when disabled.</summary>
    public bool Enabled { get; init; }
}

/// <summary>Selects Markdown provision independently from HTML publication and index listing.</summary>
public enum PublicMarkdownMode
{
    Auto = 0,
    Required = 1,
    Disabled = 2,
}

/// <summary>Selects index listing without changing HTML publication or Markdown provision.</summary>
public enum AgentIndexMode
{
    Auto = 0,
    Include = 1,
    Exclude = 2,
}

/// <summary>Agent-readable alternatives for one page that is otherwise eligible for HTML publication.</summary>
public sealed partial record AgentReadyPageOptions
{
    public PublicMarkdownMode MarkdownMode { get; init; } = PublicMarkdownMode.Auto;
    public AgentIndexMode Indexing { get; init; } = AgentIndexMode.Auto;
    /// <summary>A complete replacement body. Null requests automatic conversion; an empty string is an explicit body.</summary>
    public string? MarkdownReplacement { get; init; }
    /// <summary>An explicit Markdown output route. Null derives the route from the final HTML output path.</summary>
    public SiteRoute? MarkdownRoute { get; init; }
}
