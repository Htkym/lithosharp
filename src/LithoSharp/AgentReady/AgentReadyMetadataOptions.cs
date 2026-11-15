namespace LithoSharp.AgentReady;

public enum AgentReadyStructuredDataMode
{
    Disabled,
    Automatic,
    UserManaged,
}

public enum AgentReadyPageKind
{
    WebPage,
    TechArticle,
    BlogPosting,
}

public enum AgentReadyIdentityKind
{
    Person,
    Organization,
}

/// <summary>An explicitly supplied public author or publisher.</summary>
public sealed record AgentReadyIdentity
{
    public AgentReadyIdentityKind Kind { get; init; } = AgentReadyIdentityKind.Person;
    public string Name { get; init; } = string.Empty;
    public string? Url { get; init; }
    public IReadOnlyList<string> SameAs { get; init; } = [];
}

public sealed record AgentReadyStructuredDataOptions
{
    public AgentReadyStructuredDataMode Mode { get; init; }
    public AgentReadyIdentity? Author { get; init; }
    public AgentReadyIdentity? Publisher { get; init; }
}

public sealed record AgentReadyPageStructuredDataOptions
{
    /// <summary>Null inherits the site policy.</summary>
    public AgentReadyStructuredDataMode? Mode { get; init; }
    public AgentReadyPageKind Kind { get; init; }
    public AgentReadyIdentity? Author { get; init; }
    public AgentReadyIdentity? Publisher { get; init; }
    public DateTimeOffset? Published { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string? ImageUrl { get; init; }
}

public sealed partial record AgentReadyOptions
{
    public AgentReadyStructuredDataOptions StructuredData { get; init; } = new();
    public AgentReadyRobotsOptions Robots { get; init; } = new();
}

public sealed partial record AgentReadyPageOptions
{
    public AgentReadyPageStructuredDataOptions StructuredData { get; init; } = new();
}

/// <summary>Public facts shared by built-in and custom document heads. URLs are absolute HTTP(S) URLs.</summary>
public sealed record AgentReadyHeadData
{
    public string SiteTitle { get; init; } = string.Empty;
    public string SiteUrl { get; init; } = string.Empty;
    public string? SiteLanguage { get; init; }
    public string CanonicalUrl { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Language { get; init; }
    public AgentReadyPageStructuredDataOptions Page { get; init; } = new();
    /// <summary>A complete, registered Markdown artifact, or null.</summary>
    public string? MarkdownUrl { get; init; }
    /// <summary>An emitted index that includes this page, or null.</summary>
    public string? LlmsUrl { get; init; }
    /// <summary>Existing custom head additions, inspected for JSON-LD ownership; not emitted by this helper.</summary>
    public IHtmlContent? ExistingHead { get; init; }
}
