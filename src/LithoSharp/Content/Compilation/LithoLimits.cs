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

    /// <summary>Diagnostic when definition-list syntax stays literal (U02-definition-lists).</summary>
    public const string UnsupportedDefinitionListDiagnosticId = "LIT003";

    /// <summary>Diagnostic when generic-attribute syntax stays literal (U13-generic-attributes).</summary>
    public const string UnsupportedGenericAttributeDiagnosticId = "LIT004";

    /// <summary>Diagnostic when grid-table syntax stays literal (U12-grid-tables).</summary>
    public const string UnsupportedGridTableDiagnosticId = "LIT005";

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
    internal static Diagnostics.SiteDiagnostic? FindBrowserAssetWarning(string body, string? filePath, int bodyStartLine = 1)
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
                    return BrowserAsset(body, filePath, mathFenceOffset, bodyStartLine);
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
                        return BrowserAsset(body, filePath, offset, bodyStartLine);
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
                return BrowserAsset(body, filePath, offset + column, bodyStartLine);
            }
        }

        return null;
    }

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
    internal static Diagnostics.SiteDiagnostic? FindFootnoteWarning(string body, string? filePath, int bodyStartLine = 1)
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
                location = new Diagnostics.SiteSourceLocation(filePath, bodyStartLine + line - 1, column);
            }

            return new Diagnostics.SiteDiagnostic(
                UnsupportedFootnoteDiagnosticId,
                Diagnostics.SiteDiagnosticSeverity.Warning,
                "Footnote syntax stays literal text in the Litho compiler (U01-footnotes); previously it rendered footnote links. Use an explicit link or section instead.",
                location);
        }

        return null;
    }

    /// <summary>
    /// Finds opt-in compatibility advisories for definition lists (U02),
    /// generic attributes (U13), and grid tables (U12). Each category reports at
    /// most one informational at the first high-confidence occurrence, or nothing
    /// when absent. Code fences, inline code spans, and backslash-escaped markers
    /// are excluded. Returns diagnostics in document order.
    /// </summary>
    internal static IReadOnlyList<Diagnostics.SiteDiagnostic> FindCompatibilityAdvisories(
        string body, string? filePath, int bodyStartLine = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var found = new List<(int Offset, Diagnostics.SiteDiagnostic Diagnostic)>(3);
        if (FindDefinitionListAdvisory(body, filePath, bodyStartLine, out var definitionOffset, cancellationToken) is { } definition)
        {
            found.Add((definitionOffset, definition));
        }

        if (FindGenericAttributeAdvisory(body, filePath, bodyStartLine, out var attributeOffset, cancellationToken) is { } attribute)
        {
            found.Add((attributeOffset, attribute));
        }

        if (FindGridTableAdvisory(body, filePath, bodyStartLine, out var gridOffset, cancellationToken) is { } grid)
        {
            found.Add((gridOffset, grid));
        }

        found.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        return found.Select(static item => item.Diagnostic).ToArray();
    }

    /// <summary>
    /// Finds a high-confidence definition-list shape: a non-blank term line
    /// followed (after at most one blank line) by a <c>:</c> definition marker
    /// (<c>:</c> plus space/tab plus content, not <c>:::</c>). The Litho frontend
    /// keeps both lines as paragraphs.
    /// </summary>
    internal static Diagnostics.SiteDiagnostic? FindDefinitionListAdvisory(
        string body, string? filePath, int bodyStartLine = 1,
        CancellationToken cancellationToken = default) =>
        FindDefinitionListAdvisory(body, filePath, bodyStartLine, out _, cancellationToken);

    internal static Diagnostics.SiteDiagnostic? FindDefinitionListAdvisory(
        string body, string? filePath, int bodyStartLine, out int offset,
        CancellationToken cancellationToken = default)
    {
        offset = -1;
        ArgumentNullException.ThrowIfNull(body);
        var lines = SplitBodyLines(body);
        var inFence = false;
        var fenceChar = '\0';
        var fenceRun = 0;
        var fenceState = new bool[lines.Count];
        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = lines[index].Text;
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

                fenceState[index] = true;
                continue;
            }

            fenceState[index] = inFence;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fenceState[index])
            {
                continue;
            }

            var (markerOffset, markerColumn) = DefinitionMarkerOffset(lines[index].Text);
            if (markerOffset < 0)
            {
                continue;
            }

            if (!HasTermLine(lines, fenceState, index))
            {
                continue;
            }

            var visible = BlankCodeSpans(lines[index].Text);
            if (!IsDefinitionMarker(visible, markerColumn))
            {
                continue;
            }

            offset = lines[index].Offset + markerOffset;
            var (line, column) = new SourceText(body).GetLineAndColumn(offset);
            Diagnostics.SiteSourceLocation? location = null;
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                location = new Diagnostics.SiteSourceLocation(filePath, bodyStartLine + line - 1, column);
            }

            return new Diagnostics.SiteDiagnostic(
                UnsupportedDefinitionListDiagnosticId,
                Diagnostics.SiteDiagnosticSeverity.Info,
                "Definition list syntax stays literal paragraphs in the Litho compiler (U02-definition-lists). Use a bullet list or a GFM pipe table instead.",
                location);
        }

        return null;
    }

    private static (int Offset, int Column) DefinitionMarkerOffset(string text)
    {
        var index = 0;
        var columns = 0;
        while (index < text.Length && (text[index] is ' ' or '\t'))
        {
            columns = text[index] == '\t' ? columns + (4 - (columns % 4)) : columns + 1;
            index++;
        }

        if (columns >= 4 || index >= text.Length || text[index] != ':')
        {
            return (-1, -1);
        }

        if (index + 1 < text.Length && text[index + 1] == ':')
        {
            return (-1, -1);
        }

        if (index + 1 >= text.Length || (text[index + 1] is not (' ' or '\t')))
        {
            return (-1, -1);
        }

        var cursor = index + 2;
        while (cursor < text.Length && (text[cursor] is ' ' or '\t'))
        {
            cursor++;
        }

        return cursor < text.Length ? (index, index) : (-1, -1);
    }

    private static bool IsDefinitionMarker(string visible, int column)
    {
        if (column < 0 || column >= visible.Length || visible[column] != ':')
        {
            return false;
        }

        if (column + 1 < visible.Length && visible[column + 1] == ':')
        {
            return false;
        }

        return column + 1 < visible.Length && (visible[column + 1] is ' ' or '\t');
    }

    private static bool HasTermLine(
        List<(string Text, int Offset)> lines, bool[] fenceState, int markerIndex)
    {
        var cursor = markerIndex - 1;
        if (cursor >= 0 && lines[cursor].Text.Trim().Length == 0 && !fenceState[cursor])
        {
            cursor--;
        }

        if (cursor < 0 || fenceState[cursor])
        {
            return false;
        }

        var term = lines[cursor].Text;
        if (term.Trim().Length == 0)
        {
            return false;
        }

        var trimmed = term.TrimStart(' ', '\t');
        if (trimmed.Length == 0 || trimmed[0] == ':')
        {
            return false;
        }

        var indentColumns = 0;
        for (var i = 0; i < term.Length && (term[i] is ' ' or '\t'); i++)
        {
            indentColumns = term[i] == '\t' ? indentColumns + (4 - (indentColumns % 4)) : indentColumns + 1;
        }

        return indentColumns < 4;
    }

    /// <summary>
    /// Finds a high-confidence generic attribute (<c>{#id .class}</c>). The Litho
    /// frontend keeps it as literal text (for example, heading anchors stay
    /// visible). Escaped, fenced, and inline-code occurrences are excluded.
    /// </summary>
    internal static Diagnostics.SiteDiagnostic? FindGenericAttributeAdvisory(
        string body, string? filePath, int bodyStartLine = 1,
        CancellationToken cancellationToken = default) =>
        FindGenericAttributeAdvisory(body, filePath, bodyStartLine, out _, cancellationToken);

    internal static Diagnostics.SiteDiagnostic? FindGenericAttributeAdvisory(
        string body, string? filePath, int bodyStartLine, out int offset,
        CancellationToken cancellationToken = default)
    {
        offset = -1;
        ArgumentNullException.ThrowIfNull(body);
        var lines = SplitBodyLines(body);
        var inFence = false;
        var fenceChar = '\0';
        var fenceRun = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (text, lineOffset) = lines[index];
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

            var visible = BlankCodeSpans(text);
            for (var cursor = 0; cursor < visible.Length; cursor++)
            {
                if (visible[cursor] != '{')
                {
                    continue;
                }

                if (IsEscaped(text, cursor))
                {
                    continue;
                }

                if (!GenericAttributeAt(visible, cursor, out var length))
                {
                    continue;
                }

                offset = lineOffset + cursor;
                var (line, column) = new SourceText(body).GetLineAndColumn(offset);
                Diagnostics.SiteSourceLocation? location = null;
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    location = new Diagnostics.SiteSourceLocation(filePath, bodyStartLine + line - 1, column);
                }

                return new Diagnostics.SiteDiagnostic(
                    UnsupportedGenericAttributeDiagnosticId,
                    Diagnostics.SiteDiagnosticSeverity.Info,
                    "Generic attribute syntax stays literal text in the Litho compiler (U13-generic-attributes). Remove the attribute or use a supported heading and link form instead.",
                    location);
            }
        }

        return null;
    }

    private static bool GenericAttributeAt(string visible, int open, out int length)
    {
        length = 0;
        if (open + 2 >= visible.Length || (visible[open + 1] is not ('#' or '.')))
        {
            return false;
        }

        var close = visible.IndexOf('}', open + 2);
        if (close < 0 || close - open > 200)
        {
            return false;
        }

        var inner = visible[(open + 1)..close];
        if (inner.Contains('{') || inner.Contains('\n') || inner.Contains('\r'))
        {
            return false;
        }

        var trimmed = inner.Trim(' ', '\t');
        if (trimmed.Length == 0)
        {
            return false;
        }

        foreach (var ch in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '#' or '.' or '-' or '_' or ' ' or '\t' or '=' or '"' or '\'' or ':'))
            {
                return false;
            }
        }

        if (!trimmed.Any(char.IsAsciiLetterOrDigit))
        {
            return false;
        }

        var hasIdOrClass = false;
        for (var i = 0; i < trimmed.Length; i++)
        {
            if ((trimmed[i] == '#' || trimmed[i] == '.')
                && i + 1 < trimmed.Length
                && char.IsAsciiLetterOrDigit(trimmed[i + 1]))
            {
                hasIdOrClass = true;
                break;
            }
        }

        if (!hasIdOrClass)
        {
            return false;
        }

        length = close - open + 1;
        return true;
    }

    /// <summary>
    /// Finds a high-confidence grid table: a <c>+---+</c> border line plus a
    /// nearby border or <c>|</c> row. The Litho frontend keeps grid syntax as
    /// paragraphs; GFM pipe tables are the supported alternative.
    /// </summary>
    internal static Diagnostics.SiteDiagnostic? FindGridTableAdvisory(
        string body, string? filePath, int bodyStartLine = 1,
        CancellationToken cancellationToken = default) =>
        FindGridTableAdvisory(body, filePath, bodyStartLine, out _, cancellationToken);

    internal static Diagnostics.SiteDiagnostic? FindGridTableAdvisory(
        string body, string? filePath, int bodyStartLine, out int offset,
        CancellationToken cancellationToken = default)
    {
        offset = -1;
        ArgumentNullException.ThrowIfNull(body);
        var lines = SplitBodyLines(body);
        var inFence = false;
        var fenceChar = '\0';
        var fenceRun = 0;
        var fenceState = new bool[lines.Count];
        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = lines[index].Text;
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

                fenceState[index] = true;
                continue;
            }

            fenceState[index] = inFence;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fenceState[index])
            {
                continue;
            }

            var text = lines[index].Text;
            if (!IsGridBorder(BlankCodeSpans(text)))
            {
                continue;
            }

            if (IsEscaped(text, GridBorderStart(text)))
            {
                continue;
            }

            if (!HasGridContext(lines, fenceState, index, cancellationToken))
            {
                continue;
            }

            offset = lines[index].Offset + GridBorderStart(text);
            var (line, column) = new SourceText(body).GetLineAndColumn(offset);
            Diagnostics.SiteSourceLocation? location = null;
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                location = new Diagnostics.SiteSourceLocation(filePath, bodyStartLine + line - 1, column);
            }

            return new Diagnostics.SiteDiagnostic(
                UnsupportedGridTableDiagnosticId,
                Diagnostics.SiteDiagnosticSeverity.Info,
                "Grid table syntax stays literal paragraphs in the Litho compiler (U12-grid-tables). Use a GFM pipe table instead.",
                location);
        }

        return null;
    }

    private static int GridBorderStart(string text)
    {
        var index = 0;
        while (index < text.Length && (text[index] is ' ' or '\t'))
        {
            index++;
        }

        return index;
    }

    private static bool IsGridBorder(string visible)
    {
        var start = 0;
        while (start < visible.Length && (visible[start] is ' ' or '\t'))
        {
            start++;
        }

        var columns = 0;
        for (var i = 0; i < start; i++)
        {
            columns = visible[i] == '\t' ? columns + (4 - (columns % 4)) : columns + 1;
        }

        if (columns >= 4 || start >= visible.Length || visible[start] != '+')
        {
            return false;
        }

        var trimmed = visible.TrimEnd(' ', '\t');
        if (trimmed.Length == 0 || trimmed[^1] != '+')
        {
            return false;
        }

        var plusCount = 0;
        var dashOrEqual = false;
        for (var i = start; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '+')
            {
                plusCount++;
            }
            else if (ch is '-' or '=')
            {
                dashOrEqual = true;
            }
            else if (ch is ' ' or '\t' or ':' or '|')
            {
                continue;
            }
            else
            {
                return false;
            }
        }

        return plusCount >= 2 && dashOrEqual && trimmed.Length - start >= 3;
    }

    private static bool HasGridContext(
        List<(string Text, int Offset)> lines, bool[] fenceState, int borderIndex,
        CancellationToken cancellationToken)
    {
        for (var delta = -4; delta <= 4; delta++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delta == 0)
            {
                continue;
            }

            var neighbor = borderIndex + delta;
            if (neighbor < 0 || neighbor >= lines.Count || fenceState[neighbor])
            {
                continue;
            }

            var visible = BlankCodeSpans(lines[neighbor].Text);
            if (IsGridBorder(visible))
            {
                return true;
            }

            var trimmed = visible.TrimStart(' ', '\t');
            if (trimmed.StartsWith('|') && trimmed.TrimEnd(' ', '\t').EndsWith('|'))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEscaped(string text, int index)
    {
        var backslashes = 0;
        for (var cursor = index - 1; cursor >= 0 && text[cursor] == '\\'; cursor--)
        {
            backslashes++;
        }

        return (backslashes % 2) == 1;
    }

    private static string BlankCodeSpans(string text)
    {
        var result = text.ToCharArray();
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] != '`')
            {
                index++;
                continue;
            }

            var run = 0;
            while (index + run < text.Length && text[index + run] == '`')
            {
                run++;
            }

            var closing = text.IndexOf(
                new string('`', run), index + run, StringComparison.Ordinal);
            if (closing < 0)
            {
                break;
            }

            for (var cursor = index; cursor < closing + run; cursor++)
            {
                if (result[cursor] != '\n' && result[cursor] != '\r')
                {
                    result[cursor] = ' ';
                }
            }

            index = closing + run;
        }

        return new string(result);
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
