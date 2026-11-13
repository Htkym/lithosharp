namespace LithoSharp;

/// <summary>
/// Theme-related constants. They let each site swap in its own brand text and colors.
/// </summary>
public sealed record SiteThemeOptions
{
    /// <summary>
    /// Enables the built-in light/dark toggle, system preference, and saved theme selection.
    /// When false, the built-in theme uses light colors; override AdditionalCss for a fixed custom scheme.
    /// Custom templates must honor this option when implementing their own theme controls.
    /// </summary>
    public bool EnableThemeSwitching { get; init; } = true;

    /// <summary>
    /// Restores the saved theme through a blocking external script instead of inline JavaScript.
    /// Built-in templates emit assets/theme.js; allow same-origin scripts and styles in the host CSP.
    /// Custom templates must provide the corresponding asset when using HeadComponent.
    /// </summary>
    public bool UseExternalThemeRestoration { get; init; }

    /// <summary>Prefix shown before the brand name (used in the CSS <c>.brand::before</c>).</summary>
    public string BrandPrefix { get; init; } = string.Empty;

    /// <summary>Theme color (used for the <c>meta theme-color</c> and the PWA manifest).</summary>
    public string ThemeColor { get; init; } = "#0d1117";

    /// <summary>Unsupported legacy text generation setting. Supply an explicit existing social image instead.</summary>
    [Obsolete("Automatic social image text rendering is no longer supported. Use SiteGenerationOptions.CommonSocialImage or SocialImages with a pre-generated image.", true)]
    public string DefaultSocialSubtitle
    {
        get => "A static site built with LithoSharp";
        init => throw new NotSupportedException("Automatic social image text rendering is no longer supported. Supply an explicit existing social image through SiteGenerationOptions.CommonSocialImage or SocialImages.");
    }

    /// <summary>
    /// Optional CSS appended to the end of the default theme stylesheet.
    /// Because it wins the cascade, it can override <c>:root</c> variables and selectors,
    /// so colors and fonts can be swapped without touching the default markup.
    /// Nothing is appended when it is empty (the default).
    /// </summary>
    public string AdditionalCss { get; init; } = string.Empty;
}
