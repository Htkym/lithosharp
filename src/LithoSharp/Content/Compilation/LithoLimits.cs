using System.Text;
using System.Text.RegularExpressions;

namespace LithoSharp.Content.Compilation;

/// <summary>Litho frontend scope boundaries.</summary>
internal static class LithoLimits
{
    /// <summary>Maximum nested container depth (blockquotes/lists). Deeper input degrades to paragraphs.</summary>
    public const int MaxNestingDepth = 200;

    /// <summary>Maximum reference label length, per CommonMark.</summary>
    public const int MaxReferenceLabelLength = 999;

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

    /// <summary>Replaces unpaired surrogates with U+FFFD so normalization never throws on document text.</summary>
    internal static string SanitizeUnpairedSurrogates(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if ((char.IsHighSurrogate(ch) && (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])))
                || (char.IsLowSurrogate(ch) && (i == 0 || !char.IsHighSurrogate(text[i - 1]))))
            {
                var builder = new StringBuilder(text.Length + 8);
                builder.Append(text, 0, i);
                for (; i < text.Length; i++)
                {
                    ch = text[i];
                    builder.Append((char.IsHighSurrogate(ch) && (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])))
                        || (char.IsLowSurrogate(ch) && (i == 0 || !char.IsHighSurrogate(text[i - 1])))
                        ? '\uFFFD' : ch);
                }

                return builder.ToString();
            }
        }

        return text;
    }

    /// <summary>Diagnostic when footnote syntax stays literal (U01-footnotes).</summary>
    public const string UnsupportedFootnoteDiagnosticId = "LIT001";

    /// <summary>Diagnostic when math or diagram output needs unbundled browser assets.</summary>
    public const string BrowserAssetDiagnosticId = "LIT002";

    /// <summary>
    /// Finds math or diagram content whose HTML needs browser assets the Markdown
    /// pipeline does not bundle: <c>mermaid</c>/<c>nomnoml</c> fenced diagrams
    /// (<c>pre.mermaid</c>/<c>div.nomnoml</c>), <c>$$</c> display math, and
    /// <c>$...$</c> inline math (<c>div/span.math</c> for KaTeX). Templates must
    /// provide the scripts; Markdown-only builds never fetch them implicitly.
    /// Detection mirrors the parser fence and delimiter rules; unmatched dollars
    /// stay literal like the parser. Returns one informational at the first
    /// occurrence, or null when absent.
    /// </summary>
    internal static Diagnostics.SiteDiagnostic? FindBrowserAssetWarning(string body, string? filePath)
    {
        ArgumentNullException.ThrowIfNull(body);
        var lines = SplitBodyLines(body);
        var inFence = false;
        var fenceChar = '\0';
        var fenceRun = 0;
        var inMathFence = false;
        var mathFenceOffset = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var (text, offset) = lines[index];
            if (!inFence && IsMathFenceLine(text))
            {
                if (!inMathFence)
                {
                    // Unclosed $$ fences stay literal (parser parity), so the
                    // warning fires only once the closing fence is seen.
                    inMathFence = true;
                    mathFenceOffset = offset;
                }
                else
                {
                    inMathFence = false;
                    return BrowserAsset(body, filePath, mathFenceOffset);
                }

                continue;
            }

            if (inMathFence)
            {
                continue;
            }

            if (TryFenceMarker(text, out var marker, out var run))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = marker;
                    fenceRun = run;
                    if (IsDiagramInfo(text, run))
                    {
                        return BrowserAsset(body, filePath, offset);
                    }
                }
                else if (marker == fenceChar && run >= fenceRun)
                {
                    inFence = false;
                }

                continue;
            }

            if (inFence)
            {
                continue;
            }

            if (FindInlineMath(text) is { } column)
            {
                return BrowserAsset(body, filePath, offset + column);
            }
        }

        return null;
    }

    private static Diagnostics.SiteDiagnostic BrowserAsset(string body, string? filePath, int offset)
    {
        var (line, column) = new SourceText(body).GetLineAndColumn(offset);
        Diagnostics.SiteSourceLocation? location = null;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            location = new Diagnostics.SiteSourceLocation(filePath, line, column);
        }

        return new Diagnostics.SiteDiagnostic(
            BrowserAssetDiagnosticId,
            Diagnostics.SiteDiagnosticSeverity.Info,
            "Math or diagram content renders static HTML that needs browser assets the Markdown pipeline does not bundle: provide KaTeX for math and mermaid.js or nomnoml.js for diagrams in the site template.",
            location);
    }

    private static bool IsMathFenceLine(string text)
    {
        var columns = 0;
        var index = 0;
        while (index < text.Length && (text[index] is ' ' or '\t'))
        {
            columns = text[index] == '\t' ? columns + (4 - (columns % 4)) : columns + 1;
            index++;
        }

        if (columns >= 4)
        {
            return false;
        }

        var rest = text[index..];
        return rest.StartsWith("$$", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(rest[2..]);
    }

    private static bool IsDiagramInfo(string text, int run)
    {
        var index = 0;
        while (index < text.Length && (text[index] is ' ' or '\t'))
        {
            index++;
        }

        index += run;
        while (index < text.Length && (text[index] is ' ' or '\t'))
        {
            index++;
        }

        var start = index;
        while (index < text.Length && text[index] is not (' ' or '\t'))
        {
            index++;
        }

        return text[start..index] is "mermaid" or "nomnoml";
    }

    private static int? FindInlineMath(string text)
    {
        // Mirrors ScanMath opener/closer shapes: an opener needs a non-letter,
        // non-digit before it and non-whitespace, non-digit after it; a closer
        // needs non-whitespace before it. Code spans are blanked by the caller.
        var visible = InlineCodeSpans.Replace(text, match => new string(' ', match.Length));
        for (var open = 0; open < visible.Length; open++)
        {
            if (visible[open] != '$' || (open > 0 && visible[open - 1] == '$'))
            {
                continue;
            }

            var before = open == 0 ? '\n' : visible[open - 1];
            var after = open + 1 >= visible.Length ? '\n' : visible[open + 1];
            if (IsAsciiLetterOrDigit(before) || IsBlankChar(after) || IsAsciiDigit(after))
            {
                continue;
            }

            for (var close = open + 1; close < visible.Length; close++)
            {
                if (visible[close] != '$' || visible[close - 1] == '$')
                {
                    continue;
                }

                if (!IsBlankChar(visible[close - 1]))
                {
                    return open;
                }
            }
        }

        return null;
    }

    private static bool IsAsciiLetterOrDigit(char ch) =>
        (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');

    private static bool IsAsciiDigit(char ch) => ch >= '0' && ch <= '9';

    private static bool IsBlankChar(char ch) => ch is ' ' or '\t' or '\n' or '\r' or '\0';

    /// <summary>
    /// Finds footnote syntax (<c>[^label]</c> references and definitions) outside
    /// fenced code. The Litho frontend keeps it as literal text, unlike the
    /// previous Markdig pipeline, so callers report it instead of staying silent.
    /// Returns one warning at the first occurrence, or null when absent.
    /// </summary>
    internal static Diagnostics.SiteDiagnostic? FindFootnoteWarning(string body, string? filePath)
    {
        ArgumentNullException.ThrowIfNull(body);
        var lines = SplitBodyLines(body);
        var inFence = false;
        var fenceChar = '\0';
        var fenceRun = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var (text, offset) = lines[index];
            if (TryFenceMarker(text, out var marker, out var run))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = marker;
                    fenceRun = run;
                }
                else if (marker == fenceChar && run >= fenceRun)
                {
                    inFence = false;
                }

                continue;
            }

            if (inFence)
            {
                continue;
            }

            var visible = InlineCodeSpans.Replace(text, match => new string(' ', match.Length));
            var match = FootnotePattern.Match(visible);
            if (!match.Success)
            {
                continue;
            }

            var (line, column) = new SourceText(body).GetLineAndColumn(offset + match.Index);
            Diagnostics.SiteSourceLocation? location = null;
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                location = new Diagnostics.SiteSourceLocation(filePath, line, column);
            }

            return new Diagnostics.SiteDiagnostic(
                UnsupportedFootnoteDiagnosticId,
                Diagnostics.SiteDiagnosticSeverity.Warning,
                "Footnote syntax stays literal text in the Litho compiler (U01-footnotes); previously it rendered footnote links. Use an explicit link or section instead.",
                location);
        }

        return null;
    }

    private static List<(string Text, int Offset)> SplitBodyLines(string body)
    {
        var result = new List<(string Text, int Offset)>();
        var start = 0;
        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] == '\r')
            {
                var next = index + 1 < body.Length && body[index + 1] == '\n' ? index + 2 : index + 1;
                result.Add((body[start..index], start));
                start = next;
                index = next - 1;
            }
            else if (body[index] == '\n')
            {
                result.Add((body[start..index], start));
                start = index + 1;
            }
        }

        result.Add((body[start..], start));
        return result;
    }

    private static bool TryFenceMarker(string text, out char marker, out int run)
    {
        marker = '\0';
        run = 0;
        var index = 0;
        var columns = 0;
        while (index < text.Length && (text[index] is ' ' or '\t'))
        {
            columns = text[index] == '\t' ? columns + (4 - (columns % 4)) : columns + 1;
            index++;
        }

        if (columns >= 4 || index >= text.Length || (text[index] is not ('`' or '~')))
        {
            return false;
        }

        marker = text[index];
        while (index + run < text.Length && text[index + run] == marker)
        {
            run++;
        }

        return run >= 3 && (marker != '`' || !text[(index + run)..].Contains('`'));
    }

    private static readonly Regex FootnotePattern = new(@"\[\^([^\[\]\s]+)\]", RegexOptions.Compiled);
    private static readonly Regex InlineCodeSpans = new("`[^`\n]*`", RegexOptions.Compiled);

    /// <summary>Lowercases with diacritic removal for slug comparison helpers.</summary>
    public static string RemoveDiacritics(string text)
    {
        var normalized = SanitizeUnpairedSurrogates(text).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
