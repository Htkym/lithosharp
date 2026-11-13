namespace LithoSharp.Content;

/// <summary>Selects Markdown interpretation independently of front matter binding and publication policy.</summary>
public sealed record MarkdownOptions
{
    /// <summary>The compatibility profile retained by existing sites.</summary>
    public const string LegacyProfile = "lithosharp-markdown/1";

    /// <summary>The CommonMark 0.31.2 profile without documentation extensions.</summary>
    public const string CommonMarkProfile = "commonmark/0.31.2";

    /// <summary>The documentation profile with footnotes and structured directives.</summary>
    public const string DocumentationProfile = "syntamark-docs/2";

    /// <summary>The exact supported parser profile identifier. The default preserves existing output.</summary>
    public string ProfileId { get; init; } = LegacyProfile;

    /// <summary>Explicitly registered typed components. Only the documentation profile enables their syntax.</summary>
    public MarkdownComponentRegistry? Components { get; init; }

    internal string Fingerprint => ProfileId + "/components=" + (Components?.Fingerprint ?? "none");

    internal void Validate()
    {
        if (ProfileId is not (LegacyProfile or CommonMarkProfile or DocumentationProfile))
            throw new ArgumentException("Select a supported Markdown profile identifier.", nameof(ProfileId));
        if (Components is not null && ProfileId != DocumentationProfile)
            throw new ArgumentException("Registered Markdown components require the documentation profile.", nameof(Components));
    }
}
