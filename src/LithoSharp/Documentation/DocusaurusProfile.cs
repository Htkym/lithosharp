namespace LithoSharp.Documentation;

/// <summary>The compatibility judgment for one Docusaurus construct.</summary>
internal enum DocusaurusSupportLevel
{
    /// <summary>Renders with the documented behavior and limits.</summary>
    Supported,
    /// <summary>Only a documented subset renders.</summary>
    Partial,
    /// <summary>Not available; the build or migration report says so explicitly.</summary>
    Unsupported,
}

/// <summary>One judged Docusaurus component or hook.</summary>
/// <param name="Name">The Docusaurus name, for example <c>Tabs</c>.</param>
/// <param name="Level">The verified judgment. Never mark an unverified construct as supported.</param>
/// <param name="Import">The canonical import path, or null when only bare usage or no usage exists.</param>
/// <param name="Note">The verified behavior, limit, or replacement.</param>
internal sealed record DocusaurusComponentSupport(string Name, DocusaurusSupportLevel Level, string? Import, string Note);

/// <summary>
/// The single source of truth for Docusaurus compatibility decisions.
/// The migration report and the worker alias handling must agree with this
/// profile instead of keeping separate lists. Native conversion targets are
/// <see cref="DocumentFrontMatter"/> (front matter), <see cref="DocumentCatalog"/>
/// with <see cref="SidebarItem"/> and <see cref="DocumentCategory"/> (sidebar and
/// category), and <see cref="DocumentVariant"/> (version and locale).
/// Arbitrary plugins and themes, theme overrides outside the import list, and
/// JavaScript configuration execution are unsupported and stay out of this profile.
/// </summary>
internal static class DocusaurusProfile
{
    /// <summary>The compared Docusaurus release, pinned by tests/fixtures/mdx-baseline.</summary>
    public const string DocusaurusVersion = "3.10.2";
    /// <summary>The compared MDX release.</summary>
    public const string MdxVersion = "3.1.1";
    /// <summary>The compared React release, also enforced by the worker.</summary>
    public const string ReactVersion = "19.2.4";
    /// <summary>The compared esbuild release.</summary>
    public const string EsbuildVersion = "0.25.12";
    /// <summary>The compared Node.js release.</summary>
    public const string NodeVersion = "24.13.0";
    /// <summary>The compared npm release.</summary>
    public const string NpmVersion = "11.6.2";

    /// <summary>Component names importable from <c>@theme/</c>. Mirrors the worker allowlist.</summary>
    public static IReadOnlyList<string> SupportedThemeImports { get; } =
        ["Tabs", "TabItem", "Admonition", "Details", "CodeBlock", "TOCInline", "Card", "DocCardList", "MDXComponents", "BrowserOnly", "IdealImage", "ThemedImage", "Heading"];

    /// <summary>JSX components that render without requesting page hydration. The worker extends this with request.StaticComponents.</summary>
    public static IReadOnlyList<string> BuiltInStaticComponents { get; } =
        ["Admonition", "Details", "Card", "TOCInline", "Translate", "FormattedDate"];

    /// <summary>Decides whether a <c>@docusaurus/</c> or <c>@theme/</c> import is supported. Other scopes are out of scope and unsupported here.</summary>
    public static bool IsSupportedImport(string? name) =>
        name is "@docusaurus/BrowserOnly" || (name is not null && name.StartsWith("@theme/", StringComparison.Ordinal)
            && SupportedThemeImports.Contains(name[7..], StringComparer.Ordinal));

    /// <summary>The judged components and hooks. Every item named by the plan appears here.</summary>
    public static IReadOnlyList<DocusaurusComponentSupport> Components { get; } =
    [
        new("Tabs", DocusaurusSupportLevel.Supported, "@theme/Tabs",
            "Stateful tabs with a server-rendered initial tab and a noscript fallback. Bare usage resolves from the runtime component map. Using tabs requests page hydration unless enclosed in an explicit Island."),
        new("TabItem", DocusaurusSupportLevel.Supported, "@theme/TabItem",
            "Used inside Tabs with the same hydration note."),
        new("Admonition", DocusaurusSupportLevel.Supported, "@theme/Admonition",
            "Static-safe. Markdown ::: directives render the same aside shape."),
        new("Details", DocusaurusSupportLevel.Supported, "@theme/Details",
            "Static-safe."),
        new("CodeBlock", DocusaurusSupportLevel.Supported, "@theme/CodeBlock",
            "Fenced code renders statically with shared title, highlight, and line-number metadata. Copy controls hydrate in the browser. Bare <CodeBlock> usage requests page hydration."),
        new("Card", DocusaurusSupportLevel.Supported, "@theme/Card",
            "Static-safe card with a linked title. Renders without hydration."),
        new("DocCardList", DocusaurusSupportLevel.Supported, "@theme/DocCardList",
            "Bare usage renders sibling directory cards at compile time with titles and descriptions. Variants with props stay explicit failures."),
        new("MDXComponents", DocusaurusSupportLevel.Partial, "@theme/MDXComponents",
            "The import resolves to the runtime component map (default export) for custom MDX providers. It is not a renderable component."),
        new("TOCInline", DocusaurusSupportLevel.Supported, "@theme/TOCInline",
            "Static-safe. Receives the page toc export built from worker headings, levels 2-3 by default."),
        new("Link", DocusaurusSupportLevel.Supported, null,
            "Bare component only; no @docusaurus/Link import path exists. In MDX, `a` elements and bare usage resolve to it and unsafe schemes fail the build; a `to` prop aliases `href` for unmigrated content. Markdown-pipeline links render CommonMark targets unchanged."),
        new("Zoom", DocusaurusSupportLevel.Supported, null,
            "Bare image-zoom wrapper with no import path; renders children without zoom interaction."),
        new("BrowserOnly", DocusaurusSupportLevel.Supported, "@docusaurus/BrowserOnly",
            "Also importable from @theme/BrowserOnly. Renders its fallback statically and hydrates content in the browser."),
        new("IdealImage", DocusaurusSupportLevel.Supported, "@theme/IdealImage",
            "Static image passthrough; responsive variants render the base image."),
        new("ThemedImage", DocusaurusSupportLevel.Supported, "@theme/ThemedImage",
            "Renders the light source statically; color-mode switching needs hydration."),
        new("Heading", DocusaurusSupportLevel.Supported, "@theme/Heading",
            "Renders the requested heading level statically without anchor automation."),
        new("Translate", DocusaurusSupportLevel.Supported, null,
            "Bare component only; no @docusaurus/Translate import path exists. Renders the translation catalog message or its children as fallback."),
        new("useBaseUrl", DocusaurusSupportLevel.Unsupported, null,
            "No such export. Read basePath from usePageContext in @lithosharp/runtime."),
        new("useDocusaurusContext", DocusaurusSupportLevel.Unsupported, null,
            "No such export. Use usePageContext from @lithosharp/runtime."),
    ];
}
