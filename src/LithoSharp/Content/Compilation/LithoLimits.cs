using Syntamark.Compilation;

namespace LithoSharp.Content.Compilation;

/// <summary>Litho frontend scope boundaries.</summary>
internal static partial class LithoLimits
{
    /// <summary>
    /// Constructs deliberately unsupported by the Litho frontend, with stable IDs.
    /// The IDs map mechanically to <c>tests/LithoSharp.Tests/Fixtures/LithoParser/Unsupported.md</c>;
    /// unsupported input must stay literal text, never silent success.
    /// C07 implemented math (U03), diagrams (U04), alert blocks (U05), and custom
    /// containers (U06); their IDs are retired (never reused).
    /// </summary>
    public static IReadOnlyList<string> UnsupportedIds { get; } =
    [
        "U01-footnotes",
        "U02-definition-lists",
        "U07-abbreviations",
        "U08-citations",
        "U09-figures",
        "U10-footers",
        "U11-media-links",
        "U12-grid-tables",
        "U13-generic-attributes",
        "U14-list-extras",
        "U15-subscript-superscript",
        "U16-inserted-marked-text",
        "U17-emoji-smarty-pants",
    ];

    /// <summary>Diagnostic when footnote syntax stays literal (U01-footnotes).</summary>
    public const string UnsupportedFootnoteDiagnosticId = "LIT001";

    /// <summary>Diagnostic when definition-list syntax stays literal (U02-definition-lists).</summary>
    public const string UnsupportedDefinitionListDiagnosticId = "LIT003";

    /// <summary>Diagnostic when generic-attribute syntax stays literal (U13-generic-attributes).</summary>
    public const string UnsupportedGenericAttributeDiagnosticId = "LIT004";

    /// <summary>Diagnostic when grid-table syntax stays literal (U12-grid-tables).</summary>
    public const string UnsupportedGridTableDiagnosticId = "LIT005";

    /// <summary>Diagnostic when math or diagram output needs unbundled browser assets.</summary>
    public const string BrowserAssetDiagnosticId = "LIT002";

    private static Diagnostics.SiteDiagnostic BrowserAsset(string body, string? filePath, int offset, int bodyStartLine)
    {
        var (line, column) = new SourceText(body).GetLineAndColumn(offset);
        Diagnostics.SiteSourceLocation? location = null;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            location = new Diagnostics.SiteSourceLocation(filePath, bodyStartLine + line - 1, column);
        }

        return new Diagnostics.SiteDiagnostic(
            BrowserAssetDiagnosticId,
            Diagnostics.SiteDiagnosticSeverity.Info,
            "Math or diagram content renders static HTML that needs browser assets the Markdown pipeline does not bundle: provide KaTeX for math and mermaid.js or nomnoml.js for diagrams in the site template.",
            location);
    }
}
