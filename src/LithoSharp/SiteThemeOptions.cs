namespace LithoSharp;

/// <summary>
/// Theme-related constants. They let each site swap in its own brand text and colors.
/// </summary>
public sealed record SiteThemeOptions
{
    /// <summary>Prefix shown before the brand name (used in the CSS <c>.brand::before</c>).</summary>
    public string BrandPrefix { get; init; } = string.Empty;

    /// <summary>Theme color (used for the <c>meta theme-color</c> and the PWA manifest).</summary>
    public string ThemeColor { get; init; } = "#0d1117";

    /// <summary>Subtitle placed on the default social image.</summary>
    public string DefaultSocialSubtitle { get; init; } = "A static site built with LithoSharp";

    /// <summary>
    /// Optional CSS appended to the end of the default theme stylesheet.
    /// Because it wins the cascade, it can override <c>:root</c> variables and selectors,
    /// so colors and fonts can be swapped without touching the default markup.
    /// Nothing is appended when it is empty (the default).
    /// </summary>
    public string AdditionalCss { get; init; } = string.Empty;
}
