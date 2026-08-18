using LithoSharp.Validation;

namespace LithoSharp;

/// <summary>
/// The single extension point for the generator. Inject text, theme, validation, and
/// extra pages to drive site-specific behavior.
/// </summary>
public sealed record SiteCustomization
{
    /// <summary>UI text.</summary>
    public SiteText Text { get; init; } = SiteText.English;

    /// <summary>Theme settings.</summary>
    public SiteThemeOptions Theme { get; init; } = new();

    /// <summary>Template used to render the site. Defaults to <see cref="DocsSiteTemplate"/>.</summary>
    public ISiteTemplate Template { get; init; } = new DocsSiteTemplate();

    /// <summary>Content validators. When empty, only the default required-summary check runs.</summary>
    public IReadOnlyList<IContentValidator> Validators { get; init; } = [];

    /// <summary>Pages emitted in addition to the standard ones.</summary>
    public IReadOnlyList<SiteExtraPage> ExtraPages { get; init; } = [];

    /// <summary>
    /// Directory that holds the bundled favicon assets.
    /// When omitted, a <c>favicon</c> directory next to the executable is used.
    /// </summary>
    public string? FaviconSourceDirectory { get; init; }

    /// <summary>
    /// Whether to emit an <c>llms.txt</c> (<see href="https://llmstxt.org/"/>) for language models.
    /// Off by default. It is built from the site title, description, and post list.
    /// </summary>
    public bool GenerateLlmsTxt { get; init; }
}
