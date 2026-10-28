namespace LithoSharp;

/// <summary>Optional plain text and validated links appended to the standard site footer.</summary>
public sealed record SiteFooterOptions
{
    /// <summary>Additional plain text. HTML is escaped.</summary>
    public string? Text { get; init; }

    /// <summary>Links in display order. Only each link's label and validated URL are used.</summary>
    public IReadOnlyList<NavigationLink> Links { get; init; } = [];
}
