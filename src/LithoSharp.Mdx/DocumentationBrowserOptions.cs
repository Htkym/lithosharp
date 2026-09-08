namespace LithoSharp.Mdx;

/// <summary>Progressive browser enhancements for documentation; no external service is enabled implicitly.</summary>
public sealed record DocumentationBrowserOptions
{
    /// <summary>Uses HTML fetches and the common root lifecycle for internal navigation.</summary>
    public bool Navigation { get; init; } = true;
    /// <summary>Offers light, dark and operating-system color modes.</summary>
    public bool Theme { get; init; } = true;
    /// <summary>An optional dismissible announcement.</summary>
    public string? Announcement { get; init; }
    /// <summary>Additional navbar links.</summary>
    public IReadOnlyList<NavigationLink> Navbar { get; init; } = [];
    /// <summary>Additional footer links.</summary>
    public IReadOnlyList<NavigationLink> Footer { get; init; } = [];
}
