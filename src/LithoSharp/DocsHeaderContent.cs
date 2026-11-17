namespace LithoSharp;

/// <summary>Optional additions to the built-in documentation header.</summary>
public sealed class DocsHeaderContent
{
    /// <summary>Content following the site brand, such as a version badge or navigation links.</summary>
    public IHtmlContent? Navigation { get; init; }

    /// <summary>Content following search and preceding the built-in theme and menu controls.</summary>
    public IHtmlContent? Actions { get; init; }
}
