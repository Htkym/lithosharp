namespace LithoSharp.Content.Compilation;

using System.Text.RegularExpressions;

/// <summary>Link reference definition collected during block parsing.</summary>
/// <param name="Url">Resolved destination.</param>
/// <param name="Title">Title, or null when absent.</param>
internal sealed record LithoReference(string Url, string? Title);

/// <summary>
/// Block parser for the Litho Markdown subset (CommonMark 0.31.2 core blocks +
/// GFM 0.29 tables and task lists). Raw HTML has no special meaning and flows
/// into paragraphs (the DisableHtml output policy escapes it at render time).
/// Tabs count toward indentation in 4-column stops; content tabs are preserved.
/// </summary>
internal static partial class LithoBlockParser
{
    /// <summary>A source line with its body-relative offset and line break.</summary>
    internal readonly record struct Line(string Text, int Offset, string Break);

    /// <summary>Splits text into lines, tracking body-relative offsets.</summary>
    public static List<Line> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<Line>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    lines.Add(new Line(text[start..index], start, "\r\n"));
                    index++;
                }
                else
                {
                    lines.Add(new Line(text[start..index], start, "\r"));
                }

                start = index + 1;
            }
            else if (text[index] == '\n')
            {
                lines.Add(new Line(text[start..index], start, "\n"));
                start = index + 1;
            }
        }

        lines.Add(new Line(text[start..], start, string.Empty));
        return lines;
    }

    /// <summary>Parses blocks and collects reference definitions.</summary>
    public static (IReadOnlyList<LithoBlock> Blocks, IReadOnlyDictionary<string, LithoReference> References) ParseBlocks(
        string body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        // NUL bytes become the replacement character, per CommonMark.
        var normalized = body.Replace('\0', '\uFFFD');
        var parser = new Parser(SplitLines(normalized), cancellationToken);
        var raw = parser.ParseRange(0, parser.Lines.Count, 0);
        return (ResolveInlines(raw, parser.References, cancellationToken), parser.References);
    }

    /// <summary>Parses block structure without inline content (for tail checks).</summary>
    internal static IReadOnlyList<LithoBlock> ParseStructure(
        List<Line> lines,
        int depth,
        CancellationToken cancellationToken)
    {
        var parser = new Parser(lines, cancellationToken, resolveInlines: false);
        return parser.ParseRange(0, lines.Count, depth);
    }

    private static List<LithoBlock> ResolveInlines(
        List<LithoBlock> blocks,
        Dictionary<string, LithoReference> references,
        CancellationToken cancellationToken)
    {
        var resolved = new List<LithoBlock>(blocks.Count);
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolved.Add(ResolveBlock(block, references, cancellationToken));
        }

        return resolved;
    }

    private static LithoBlock ResolveBlock(
        LithoBlock block,
        Dictionary<string, LithoReference> references,
        CancellationToken cancellationToken) => block switch
    {
        LithoParagraph paragraph => paragraph with
        {
            Inlines = ResolveRaw(paragraph.Inlines, references, cancellationToken),
        },
        LithoHeading heading => heading with
        {
            Inlines = ResolveRaw(heading.Inlines, references, cancellationToken),
        },
        LithoQuote quote => quote with
        {
            Children = ResolveInlines([.. quote.Children], references, cancellationToken),
        },
        LithoAdmonition admonition => admonition with
        {
            Children = ResolveInlines([.. admonition.Children], references, cancellationToken),
        },
        LithoDirective directive => directive with
        {
            Children = ResolveInlines([.. directive.Children], references, cancellationToken),
        },
        LithoList list => list with
        {
            Items = list.Items.Select(item => item with
            {
                Children = ResolveInlines([.. item.Children], references, cancellationToken),
            }).ToArray(),
        },
        LithoTable table => table with
        {
            Rows = table.Rows.Select(row => (IReadOnlyList<IReadOnlyList<LithoInline>>)row
                .Select(cell => (IReadOnlyList<LithoInline>)ResolveRaw(cell, references, cancellationToken))
                .ToArray()).ToArray(),
        },
        _ => block,
    };

    private static List<LithoInline> ResolveRaw(
        IReadOnlyList<LithoInline> inlines,
        Dictionary<string, LithoReference> references,
        CancellationToken cancellationToken)
    {
        var resolved = new List<LithoInline>(inlines.Count);
        foreach (var inline in inlines)
        {
            if (inline is LithoRawText raw)
            {
                resolved.AddRange(LithoInlineParser.Parse(
                    raw.Text, raw.Map, references, cancellationToken));
            }
            else
            {
                resolved.Add(inline);
            }
        }

        return resolved;
    }

    /// <summary>Normalizes a reference label (trim, collapse whitespace, case-fold).</summary>
    internal static string NormalizeLabel(string label)
    {
        var collapsed = WhitespaceRun().Replace(label.Trim(), " ");
        return collapsed.ToUpperInvariant();
    }

    private sealed partial class Parser(List<LithoBlockParser.Line> lines, CancellationToken cancellationToken,
        bool resolveInlines = true, Dictionary<string, LithoReference>? references = null)
    {
        public List<Line> Lines { get; } = lines;

        private bool Resolve => resolveInlines;

        public Dictionary<string, LithoReference> References { get; } =
            references ?? new(StringComparer.OrdinalIgnoreCase);

        public List<LithoBlock> ParseRange(int from, int to, int depth)
        {
            var blocks = new List<LithoBlock>();
            var index = from;
            while (index < to)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = Lines[index];
                if (IsBlank(line.Text))
                {
                    index++;
                    continue;
                }

                if (depth > LithoLimits.MaxNestingDepth)
                {
                    blocks.AddRange(ParseParagraph(index, to, out index));
                    continue;
                }

                if (TryParseFenced(index, to, out var fenced, out var next))
                {
                    blocks.Add(fenced!);
                    index = next;
                }
                else if (TryParseDirective(index, to, depth, out var directive, out next))
                {
                    blocks.Add(directive!);
                    index = next;
                }
                else if (TryParseMath(index, to, out var math, out next))
                {
                    blocks.Add(math!);
                    index = next;
                }
                else if (TryParseAtx(line, out var heading))
                {
                    blocks.Add(heading!);
                    index++;
                }
                else if (TryParseThematic(line))
                {
                    blocks.Add(new LithoBreak(SpanOf(line)));
                    index++;
                }
                else if (TryParseQuote(index, to, depth, out var quote, out next))
                {
                    blocks.Add(quote!);
                    index = next;
                }
                else if (TryParseList(index, to, depth, out var list, out next))
                {
                    blocks.Add(list!);
                    index = next;
                }
                else if (TryParseIndentedCode(index, to, out var code, out next))
                {
                    blocks.Add(code!);
                    index = next;
                }
                else if (TryParseReference(index, to, out next))
                {
                    index = next;
                }
                else
                {
                    blocks.AddRange(ParseParagraph(index, to, out index));
                }
            }

            return blocks;
        }

        private List<LithoBlock> ParseParagraph(int from, int to, out int next)
        {
            // Paragraph lines lose leading whitespace (CommonMark); trailing
            // whitespace is handled by the inline parser except on the final
            // line, where no hard break is possible and it is stripped here.
            var acc = new List<Line> { StripLeading(Lines[from]) };
            var index = from + 1;
            var closingKind = 0; // 0 none, 1 setext-h1, 2 setext-h2, 3 table
            while (index < to)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = Lines[index];
                if (IsBlank(line.Text))
                {
                    break;
                }

                if (IsSetextUnderline(line.Text))
                {
                    closingKind = line.Text.Trim().StartsWith('=') ? 1 : 2;
                    index++;
                    break;
                }

                if (IsTableDelimiter(line.Text, acc))
                {
                    closingKind = 3;
                    break;
                }

                if (TryParseFenced(index, to, out _, out _)
                    || TryParseAtx(line, out _)
                    || TryParseQuoteStart(line) >= 0
                    || IsDirectiveStart(line.Text)
                    || IsThematicInterrupting(line.Text)
                    || (TryParseListStart(line, out var marker) && CanInterruptParagraph(marker))
                    || IsReferenceStart(index, to)
                    || IsMathBlockStart(index, to))
                {
                    break;
                }

                acc.Add(StripLeading(line));
                index++;
            }

            next = index;
            if (closingKind is 1 or 2)
            {
                var start = acc[0].Offset;
                var end = Lines[index - 1].Offset + Lines[index - 1].Text.Length;
                return
                [
                    new LithoHeading(
                        closingKind,
                        InlineContent(acc),
                        new SourceSpan(start, end - start)),
                ];
            }

            if (closingKind == 3)
            {
                var result = new List<LithoBlock>();
                var header = acc[^1];
                if (acc.Count > 1)
                {
                    var init = acc.GetRange(0, acc.Count - 1);
                    var endInit = init[^1].Offset + init[^1].Text.Length;
                    result.Add(new LithoParagraph(
                        InlineContent(init),
                        new SourceSpan(init[0].Offset, endInit - init[0].Offset)));
                }

                if (TryParseTable([header], index, to, out var table, out var afterTable))
                {
                    result.Add(table!);
                    next = afterTable;
                    return result;
                }

                // Delimiter vanished (whitespace-only header edge): fall through as paragraph.
            }

            var paraStart = acc[0].Offset;
            var paraEnd = acc[^1].Offset + acc[^1].Text.Length;
            return
            [
                new LithoParagraph(
                    InlineContent(acc),
                    new SourceSpan(paraStart, paraEnd - paraStart)),
            ];
        }

        private List<LithoInline> InlineContent(List<Line> acc)
        {
            if (!Resolve)
            {
                return [];
            }

            var joined = JoinLines(acc);
            return [new LithoRawText(joined.Text, joined.Map)];
        }

        private static Line StripLeading(Line line)
        {
            var stripped = line.Text.TrimStart(' ', '\t');
            return stripped.Length == line.Text.Length
                ? line
                : new Line(stripped, line.Offset + (line.Text.Length - stripped.Length), line.Break);
        }

        private static (string Text, LithoLineMap Map) JoinLines(List<Line> acc)
        {
            // The final line cannot end in a hard break (no following line),
            // so trailing spaces/tabs are stripped per CommonMark.
            var lastText = acc[^1].Text.TrimEnd(' ', '\t');
            if (acc.Count == 1)
            {
                return (lastText, new LithoLineMap([0], [acc[0].Offset], lastText.Length, acc[0].Offset + lastText.Length));
            }

            var builder = new System.Text.StringBuilder();
            var localStarts = new List<int>(acc.Count);
            var bodyStarts = new List<int>(acc.Count);
            for (var i = 0; i < acc.Count; i++)
            {
                localStarts.Add(builder.Length);
                bodyStarts.Add(acc[i].Offset);
                builder.Append(i + 1 < acc.Count ? acc[i].Text : lastText);
                if (i + 1 < acc.Count)
                {
                    builder.Append(acc[i].Break);
                }
            }

            var text = builder.ToString();
            var last = acc[^1];
            return (text, new LithoLineMap([.. localStarts], [.. bodyStarts], text.Length, last.Offset + lastText.Length));
        }

        private bool TryParseFenced(int index, int to, out LithoCode? code, out int next)
        {
            code = null;
            next = index;
            var line = Lines[index].Text;
            var indent = CountIndent(line, 0);
            if (indent.Columns >= 4 || indent.Chars >= line.Length)
            {
                return false;
            }

            var fenceChar = line[indent.Chars];
            if (fenceChar is not ('`' or '~'))
            {
                return false;
            }

            var run = 0;
            while (indent.Chars + run < line.Length && line[indent.Chars + run] == fenceChar)
            {
                run++;
            }

            if (run < 3)
            {
                return false;
            }

            var info = line[(indent.Chars + run)..].Trim();
            if (fenceChar == '`' && info.Contains('`'))
            {
                return false;
            }

            var infoWord = info.Length == 0 ? null : info.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
            var meta = ParseCodeMeta(info);
            var content = new List<string>();
            var cursor = index + 1;
            while (cursor < to)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = Lines[cursor].Text;
                if (IsClosingFence(candidate, fenceChar, run))
                {
                    cursor++;
                    break;
                }

                content.Add(StripColumns(candidate, Math.Min(indent.Columns, CountIndent(candidate, 0).Columns)).Text);
                cursor++;
            }

            next = cursor;
            var start = Lines[index].Offset;
            var lastLine = cursor - 1;
            var end = Lines[lastLine].Offset + Lines[lastLine].Text.Length;
            var text = content.Count == 0 ? string.Empty : string.Join("\n", content) + "\n";
            code = new LithoCode(text, infoWord, Fenced: true, new SourceSpan(start, end - start), meta);
            return true;
        }

        /// <summary>
        /// Parses fence metadata with the MDX worker's expressions (title, highlight
        /// lines, line numbers, start). Returns null when only a language is present.
        /// </summary>
        private static LithoCodeMeta? ParseCodeMeta(string info)
        {
            var title = TitleAttribute().Match(info) is { Success: true } titleMatch
                ? titleMatch.Groups[1].Value
                : null;
            var highlight = HighlightAttribute().Match(info) is { Success: true } highlightMatch
                ? highlightMatch.Groups[1].Value
                : null;
            var numbered = LineNumbersAttribute().IsMatch(info);
            var start = StartAttribute().Match(info) is { Success: true } startMatch
                && int.TryParse(startMatch.Groups[1].Value, out var parsed)
                ? parsed
                : 1;
            return title is null && highlight is null && !numbered && start == 1
                ? null
                : new LithoCodeMeta(title, highlight, numbered, start);
        }

        [System.Text.RegularExpressions.GeneratedRegex("(?:^|\\s)title=\"([^\"]*)\"")]
        private static partial Regex TitleAttribute();

        [System.Text.RegularExpressions.GeneratedRegex("\\{([\\d, -]+)\\}")]
        private static partial Regex HighlightAttribute();

        [System.Text.RegularExpressions.GeneratedRegex("(?:^|\\s)showLineNumbers(?:\\s|$)")]
        private static partial Regex LineNumbersAttribute();

        [System.Text.RegularExpressions.GeneratedRegex("(?:^|\\s)start=(\\d+)")]
        private static partial Regex StartAttribute();

        private static bool IsClosingFence(string text, char fenceChar, int minRun)
        {
            var indent = CountIndent(text, 0);
            if (indent.Columns >= 4)
            {
                return false;
            }

            var run = 0;
            while (indent.Chars + run < text.Length && text[indent.Chars + run] == fenceChar)
            {
                run++;
            }

            if (run < Math.Max(3, minRun))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(text[(indent.Chars + run)..]);
        }

        private static bool TryParseDirectiveFence(string text, out int run, out string name, out string title)
        {
            run = 0;
            name = string.Empty;
            title = string.Empty;
            var indent = CountIndent(text, 0);
            if (indent.Columns >= 4)
            {
                return false;
            }

            while (indent.Chars + run < text.Length && text[indent.Chars + run] == ':')
            {
                run++;
            }

            if (run < 3)
            {
                return false;
            }

            var rest = text[(indent.Chars + run)..].Trim();
            if (rest.Length == 0)
            {
                return true;
            }

            var end = 0;
            while (end < rest.Length && (char.IsAsciiLetterOrDigit(rest[end]) || rest[end] is '-' or '_'))
            {
                end++;
            }

            if (end == 0)
            {
                return false;
            }

            name = rest[..end];
            title = rest[end..].Trim();
            return true;
        }

        private bool TryParseDirective(int index, int to, int depth, out LithoBlock? block, out int next)
        {
            block = null;
            next = index;
            if (!TryParseDirectiveFence(Lines[index].Text, out var openRun, out var name, out var title))
            {
                return false;
            }

            var content = new List<Line>();
            var cursor = index + 1;
            while (cursor < to)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = Lines[cursor];
                if (!IsBlank(line.Text)
                    && TryParseDirectiveFence(line.Text, out var closeRun, out var closeName, out var closeTitle)
                    && closeName.Length == 0 && closeTitle.Length == 0 && closeRun >= openRun)
                {
                    cursor++;
                    break;
                }

                content.Add(line);
                cursor++;
            }

            // Unclosed directives auto-close at the parent end (Markdig parity).
            next = cursor;
            var start = Lines[index].Offset;
            var endLine = cursor - 1;
            while (endLine > index && IsBlank(Lines[endLine].Text))
            {
                endLine--;
            }

            var end = Lines[endLine].Offset + Lines[endLine].Text.Length;
            var span = new SourceSpan(start, end - start);
            while (content.Count > 0 && IsBlank(content[^1].Text))
            {
                content.RemoveAt(content.Count - 1);
            }

            var children = new Parser(content, cancellationToken, references: References).ParseRange(0, content.Count, depth + 1);
            if (name.Length != 0 && LithoDirectives.IsAdmonitionName(name))
            {
                block = new LithoAdmonition(
                    name,
                    title.Length == 0 ? LithoDirectives.DefaultTitle(name) : title,
                    children,
                    span);
            }
            else
            {
                block = new LithoDirective(name, children, span);
            }

            return true;
        }

        private bool TryParseMath(int index, int to, out LithoMath? math, out int next)
        {
            math = null;
            next = index;
            if (!IsMathFence(Lines[index].Text))
            {
                return false;
            }

            var content = new List<string>();
            var cursor = index + 1;
            while (cursor < to)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsBlank(Lines[cursor].Text) && IsMathFence(Lines[cursor].Text))
                {
                    var start = Lines[index].Offset;
                    var end = Lines[cursor].Offset + Lines[cursor].Text.Length;
                    next = cursor + 1;
                    while (content.Count > 0 && content[^1].Length == 0)
                    {
                        content.RemoveAt(content.Count - 1);
                    }

                    math = new LithoMath(
                        string.Join("\n", content),
                        new SourceSpan(start, end - start));
                    return true;
                }

                content.Add(IsBlank(Lines[cursor].Text) ? string.Empty : Lines[cursor].Text);
                cursor++;
            }

            // Unclosed fences stay literal paragraphs (Markdig parity).
            return false;
        }

        private bool IsMathBlockStart(int index, int to)
        {
            if (!IsMathFence(Lines[index].Text))
            {
                return false;
            }

            for (var cursor = index + 1; cursor < to; cursor++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsBlank(Lines[cursor].Text) && IsMathFence(Lines[cursor].Text))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDirectiveStart(string text) =>
            TryParseDirectiveFence(text, out _, out _, out _);

        private static bool IsMathFence(string text)
        {
            var indent = CountIndent(text, 0);
            if (indent.Columns >= 4)
            {
                return false;
            }

            var rest = text[indent.Chars..];
            if (!rest.StartsWith("$$", StringComparison.Ordinal))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(rest[2..]);
        }

        private bool TryParseAtx(Line line, out LithoHeading? heading)
        {
            heading = null;
            var text = line.Text;
            var indent = CountIndent(text, 0);
            if (indent.Columns >= 4)
            {
                return false;
            }

            var level = 0;
            while (indent.Chars + level < text.Length && text[indent.Chars + level] == '#' && level < 6)
            {
                level++;
            }

            if (level == 0 || level > 6)
            {
                return false;
            }

            var rest = indent.Chars + level;
            if (rest < text.Length && text[rest] is not (' ' or '\t'))
            {
                return false;
            }

            var contentStart = rest;
            while (contentStart < text.Length && (text[contentStart] is ' ' or '\t'))
            {
                contentStart++;
            }

            var content = contentStart < text.Length ? text[contentStart..].Trim() : string.Empty;
            content = StripClosingRun(content);
            heading = new LithoHeading(
                level,
                Resolve ? [new LithoRawText(content, LithoLineMap.Contiguous(line.Offset + contentStart, content.Length))] : [],
                SpanOf(line));
            return true;
        }

        private static string StripClosingRun(string content)
        {
            var end = content.Length;
            while (end > 0 && (content[end - 1] is ' ' or '\t'))
            {
                end--;
            }

            var run = 0;
            while (end - run - 1 >= 0 && content[end - run - 1] == '#')
            {
                run++;
            }

            if (run > 0 && end - run - 1 >= 0 && (content[end - run - 1] is ' ' or '\t'))
            {
                while (end - run - 1 >= 0 && (content[end - run - 1] is ' ' or '\t'))
                {
                    run++;
                }

                return content[..(end - run)];
            }

            return content[..end];
        }

        private static bool TryParseThematic(Line line) => IsThematic(line.Text);

        private static bool IsThematic(string text)
        {
            var indent = CountIndent(text, 0);
            if (indent.Columns >= 4)
            {
                return false;
            }

            char? kind = null;
            var count = 0;
            for (var i = indent.Chars; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch is ' ' or '\t')
                {
                    continue;
                }

                if (ch is not ('*' or '-' or '_') || (kind is not null && kind != ch))
                {
                    return false;
                }

                kind = ch;
                count++;
            }

            return kind is not null && count >= 3;
        }

        private static bool IsThematicInterrupting(string text)
        {
            // "---" after a paragraph line is a Setext underline; only *** and ___ interrupt.
            var indent = CountIndent(text, 0);
            if (indent.Columns >= 4)
            {
                return false;
            }

            char? kind = null;
            var count = 0;
            for (var i = indent.Chars; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch is ' ' or '\t')
                {
                    continue;
                }

                if (ch is not ('*' or '-' or '_') || (kind is not null && kind != ch))
                {
                    return false;
                }

                kind = ch;
                count++;
            }

            return kind is '*' or '_' && count >= 3;
        }

        private static bool IsSetextUnderline(string text)
        {
            var indent = CountIndent(text, 0);
            if (indent.Columns >= 4)
            {
                return false;
            }

            // Span-based (no substring allocation on this hot path).
            var start = indent.Chars;
            var end = text.Length;
            while (end > start && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }

            if (end == start)
            {
                return false;
            }

            var marker = text[start];
            if (marker != '=' && marker != '-')
            {
                return false;
            }

            for (var i = start + 1; i < end; i++)
            {
                if (text[i] != marker)
                {
                    return false;
                }
            }

            return true;
        }

        private int TryParseQuoteStart(Line line)
        {
            var indent = CountIndent(line.Text, 0);
            if (indent.Columns >= 4)
            {
                return -1;
            }

            return line.Text.Length > indent.Chars && line.Text[indent.Chars] == '>' ? indent.Chars : -1;
        }

        private bool TryParseQuote(int index, int to, int depth, out LithoBlock? block, out int next)
        {
            block = null;
            next = index;
            if (TryParseQuoteStart(Lines[index]) < 0)
            {
                return false;
            }

            var content = new List<Line>();
            var cursor = index;
            // Tail probe cache: content only grows here, so a cached tail that
            // ends with a paragraph stays valid across appended plain lines.
            // Any quote-marker or blank append invalidates it. This turns the
            // per-lazy-line full re-parse from O(n^2) into one parse per group.
            IReadOnlyList<LithoBlock>? tailCache = null;
            while (cursor < to)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = Lines[cursor];
                if (IsBlank(line.Text))
                {
                    break;
                }

                var marker = TryParseQuoteStart(line);
                if (marker >= 0)
                {
                    var stripped = line.Text[(marker + 1)..];
                    if (stripped.Length > 0 && stripped[0] is ' ' or '\t')
                    {
                        stripped = stripped[1..];
                    }

                    content.Add(new Line(stripped, line.Offset + (line.Text.Length - stripped.Length), line.Break));
                    tailCache = null;
                    cursor++;
                    continue;
                }

                // Lazy continuation: only when the open tail is a paragraph and the
                // line cannot start a new block. The tail check parses structure
                // only (no inline content); the cache keeps it cheap on long quotes.
                tailCache ??= ParseStructure(content, depth + 1, cancellationToken);
                if (tailCache.Count > 0 && tailCache[^1] is LithoParagraph
                    && !TryParseFenced(cursor, to, out _, out _)
                    && !TryParseAtx(line, out _)
                    && !IsDirectiveStart(line.Text)
                    && !IsThematic(line.Text)
                    && !IsSetextUnderline(line.Text)
                    && !(TryParseListStart(line, out var lazy) && CanInterruptParagraph(lazy))
                    && !IsMathBlockStart(cursor, to))
                {
                    content.Add(new Line(line.Text.TrimStart(), line.Offset + (line.Text.Length - line.Text.TrimStart().Length), line.Break));
                    cursor++;
                    continue;
                }

                break;
            }

            next = cursor;
            var start = Lines[index].Offset;
            var endLine = cursor - 1;
            while (endLine > index && IsBlank(Lines[endLine].Text))
            {
                endLine--;
            }

            var end = Lines[endLine].Offset + Lines[endLine].Text.Length;
            var span = new SourceSpan(start, end - start);
            // GitHub alert: a quote whose first content line is exactly [!NAME].
            // The alert body ends at the first bare blank line (a `>`-blank keeps
            // it open); parsing resumes after the blank (reference parity).
            if (content.Count > 0 && TryParseAlertTag(content[0].Text, out var kind))
            {
                var rest = content.GetRange(1, content.Count - 1);
                while (rest.Count > 0 && rest[0].Text.Length == 0)
                {
                    rest.RemoveAt(0);
                }

                var children = new Parser(rest, cancellationToken, references: References).ParseRange(0, rest.Count, depth + 1);
                block = new LithoAdmonition(kind, LithoDirectives.DefaultTitle(kind), children, span);
                return true;
            }

            var inner = new Parser(content, cancellationToken, references: References).ParseRange(0, content.Count, depth + 1);
            block = new LithoQuote(inner, span);
            return true;
        }

        private static bool TryParseAlertTag(string text, out string kind)
        {
            kind = string.Empty;
            var trimmed = text.Trim();
            if (trimmed.Length < 4 || !trimmed.StartsWith("[!", StringComparison.Ordinal) || !trimmed.EndsWith(']'))
            {
                return false;
            }

            return LithoDirectives.TryParseAlertName(trimmed[2..^1], out kind);
        }

        internal sealed record ListMarker(bool Ordered, int Number, int IndentChars, int ContentChars, int ContentColumns);

        private static readonly ListMarker NoListMarker = new(false, 1, 0, 0, 0);

        private bool TryParseList(int index, int to, int depth, out LithoList? list, out int next)
        {
            list = null;
            next = index;
            if (!TryParseListStart(Lines[index], out var first))
            {
                return false;
            }

            var items = new List<LithoItem>();
            var ordered = first.Ordered;
            var startNumber = first.Number;
            var cursor = index;
            var loose = false;
            while (cursor < to)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseListStart(Lines[cursor], out var marker)
                    || marker.Ordered != ordered
                    || marker.IndentChars != first.IndentChars)
                {
                    break;
                }

                var itemLines = new List<Line>();
                var markerLine = Lines[cursor];
                var (taskState, afterMarker) = SplitTask(markerLine.Text, marker.ContentChars);
                itemLines.Add(new Line(afterMarker, markerLine.Offset + (markerLine.Text.Length - afterMarker.Length), markerLine.Break));
                cursor++;
                var blankInside = false;
                var pendingBlanks = 0;
                while (cursor < to)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = Lines[cursor];
                    if (IsBlank(line.Text))
                    {
                        pendingBlanks++;
                        cursor++;
                        continue;
                    }

                    var indent = CountIndent(line.Text, 0);
                    if (indent.Columns >= marker.ContentColumns)
                    {
                        if (pendingBlanks > 0)
                        {
                            blankInside = true;
                            for (var blank = cursor - pendingBlanks; blank < cursor; blank++)
                                itemLines.Add(new Line(string.Empty, Lines[blank].Offset, Lines[blank].Break));
                            pendingBlanks = 0;
                        }

                        var stripped = StripColumns(line.Text, marker.ContentColumns);
                        itemLines.Add(new Line(stripped.Text, line.Offset + stripped.Chars, line.Break));
                        cursor++;
                        continue;
                    }

                    if (TryParseListStart(line, out var following))
                    {
                        if (pendingBlanks > 0
                            && following.Ordered == ordered
                            && following.IndentChars == first.IndentChars)
                        {
                            // A blank line between sibling items makes the list loose.
                            blankInside = true;
                        }

                        break;
                    }

                    if (IsThematic(line.Text) || IsSetextUnderline(line.Text))
                    {
                        break;
                    }

                    // Lazy paragraph continuation inside the item. Lines that open
                    // a fenced code block, ATX heading, directive, math block, or
                    // blockquote cannot be lazy (mirrors the quote parser).
                    if (pendingBlanks == 0
                        && !TryParseFenced(cursor, to, out _, out _)
                        && !TryParseAtx(line, out _)
                        && !IsDirectiveStart(line.Text)
                        && TryParseQuoteStart(line) < 0
                        && !IsMathBlockStart(cursor, to))
                    {
                        itemLines.Add(new Line(line.Text.TrimStart(), line.Offset + (line.Text.Length - line.Text.TrimStart().Length), line.Break));
                        cursor++;
                        continue;
                    }

                    break;
                }

                while (itemLines.Count > 0 && itemLines[^1].Text.Length == 0)
                {
                    itemLines.RemoveAt(itemLines.Count - 1);
                }

                var children = new Parser(itemLines, cancellationToken, references: References).ParseRange(0, itemLines.Count, depth + 1);
                if (blankInside)
                {
                    loose = true;
                }

                var itemOffset = markerLine.Offset;
                var itemEnd = itemLines.Count == 0
                    ? markerLine.Offset + markerLine.Text.Length
                    : itemLines[^1].Offset + itemLines[^1].Text.Length;
                items.Add(new LithoItem(children, taskState, new SourceSpan(itemOffset, itemEnd - itemOffset)));
            }

            if (items.Count == 0)
            {
                return false;
            }

            next = cursor;
            var start = Lines[index].Offset;
            var last = items[^1];
            list = new LithoList(ordered, startNumber, items, !loose, new SourceSpan(start, last.Span.End - start));
            return true;
        }

        private static (bool? Checked, string Remainder) SplitTask(string text, int contentChars)
        {
            var rest = contentChars < text.Length ? text[contentChars..] : string.Empty;
            if (rest.Length >= 3 && rest[0] == '[' && rest[2] == ']'
                && (rest.Length == 3 || rest[3] is ' ' or '\t'))
            {
                if (rest[1] == ' ')
                {
                    return (false, rest.Length == 3 ? string.Empty : rest[4..]);
                }

                if (rest[1] is 'x' or 'X')
                {
                    return (true, rest.Length == 3 ? string.Empty : rest[4..]);
                }
            }

            return (null, rest);
        }

        private bool TryParseListStart(Line line, out ListMarker marker) =>
            TryParseListStart(line.Text, out marker);

        private static bool TryParseListStart(string text, out ListMarker marker)
        {
            // Failure must not allocate: this probe runs on every
            // paragraph-continuation line. A shared default stands in
            // (all call sites use the marker only on success).
            marker = NoListMarker;
            var indent = CountIndent(text, 0);
            if (indent.Columns >= 4 || indent.Chars >= text.Length)
            {
                return false;
            }

            var pos = indent.Chars;
            var ch = text[pos];
            if (ch is '-' or '+' or '*')
            {
                var after = pos + 1;
                if (after < text.Length && text[after] is not (' ' or '\t'))
                {
                    return false;
                }

                var (chars, cols) = ContentAfterMarker(text, indent.Chars, indent.Columns, after);
                marker = new ListMarker(false, 1, indent.Chars, chars, cols);
                return true;
            }

            if (ch is < '0' or > '9')
            {
                return false;
            }

            var digits = 0;
            var number = 0;
            while (pos + digits < text.Length && text[pos + digits] is >= '0' and <= '9' && digits < 9)
            {
                number = (number * 10) + (text[pos + digits] - '0');
                digits++;
            }

            if (digits == 0 || digits > 9 || pos + digits >= text.Length)
            {
                return false;
            }

            var delimiter = text[pos + digits];
            if (delimiter is not ('.' or ')'))
            {
                return false;
            }

            var afterMarker = pos + digits + 1;
            if (afterMarker < text.Length && text[afterMarker] is not (' ' or '\t'))
            {
                return false;
            }

            var (contentChars, contentCols) = ContentAfterMarker(text, indent.Chars, indent.Columns, afterMarker);
            marker = new ListMarker(true, number, indent.Chars, contentChars, contentCols);
            return true;
        }

        private static (int Chars, int Cols) ContentAfterMarker(string text, int indentChars, int indentColumns, int after)
        {
            var markerColumns = indentColumns + (after - indentChars);
            if (after >= text.Length)
            {
                return (after, markerColumns + 1);
            }

            if (text[after] == '\t')
            {
                return (after + 1, markerColumns + (4 - (markerColumns % 4)));
            }

            var spaces = 0;
            while (after + spaces < text.Length && text[after + spaces] == ' ' && spaces < 5)
            {
                spaces++;
            }

            if (spaces >= 5)
            {
                return (after + 1, markerColumns + 1);
            }

            return (after + spaces, markerColumns + spaces);
        }

        private static bool CanInterruptParagraph(ListMarker marker) =>
            !marker.Ordered || marker.Number == 1;

        private bool TryParseIndentedCode(int index, int to, out LithoCode? code, out int next)
        {
            code = null;
            next = index;
            if (CountIndent(Lines[index].Text, 0).Columns < 4)
            {
                return false;
            }

            var content = new List<string>();
            var cursor = index;
            var lastNonBlank = -1;
            while (cursor < to)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = Lines[cursor];
                if (IsBlank(line.Text))
                {
                    content.Add(string.Empty);
                    cursor++;
                    continue;
                }

                if (CountIndent(line.Text, 0).Columns < 4)
                {
                    break;
                }

                var stripped = StripColumns(line.Text, 4);
                content.Add(line.Text[stripped.Chars..]);
                lastNonBlank = content.Count - 1;
                cursor++;
            }

            while (content.Count > lastNonBlank + 1)
            {
                content.RemoveAt(content.Count - 1);
                cursor--;
            }

            if (content.Count == 0)
            {
                return false;
            }

            next = cursor;
            var start = Lines[index].Offset;
            var endLine = cursor - 1;
            var end = Lines[endLine].Offset + Lines[endLine].Text.Length;
            code = new LithoCode(string.Join("\n", content) + "\n", null, Fenced: false, new SourceSpan(start, end - start));
            return true;
        }

        private bool TryParseReference(int index, int to, out int next) =>
            TryParseReference(index, to, store: true, out next);

        private bool IsReferenceStart(int index, int to) =>
            TryParseReference(index, to, store: false, out _);

        private bool TryParseReference(int index, int to, bool store, out int next)
        {
            next = index;
            var line = Lines[index];
            var indent = CountIndent(line.Text, 0);
            if (indent.Columns >= 4)
            {
                return false;
            }

            var text = line.Text;
            var pos = indent.Chars;
            if (pos >= text.Length || text[pos] != '[')
            {
                return false;
            }

            var labelEnd = FindLabelEnd(text, pos);
            if (labelEnd < 0)
            {
                return false;
            }

            var rawLabel = text[(pos + 1)..labelEnd];
            if (rawLabel.Length == 0 || rawLabel.Length > LithoLimits.MaxReferenceLabelLength)
            {
                return false;
            }

            if (rawLabel[0] == '^')
            {
                // Footnote syntax (U01) is not a reference definition.
                return false;
            }

            var after = labelEnd + 1;
            if (after >= text.Length || text[after] != ':')
            {
                return false;
            }

            after++;
            var (url, title, consumedLines, ok) = ParseLinkTarget(text[(after)..], index, to);
            if (!ok || url is null)
            {
                return false;
            }

            var key = NormalizeLabel(rawLabel);
            if (store && key.Length != 0 && !References.ContainsKey(key))
            {
                References.Add(key, new LithoReference(LithoInlineParser.DecodeEscapesAndEntities(url),
                    title is null ? null : LithoInlineParser.DecodeEscapesAndEntities(title)));
            }

            next = index + consumedLines;
            return true;
        }

        private (string? Url, string? Title, int Lines, bool Ok) ParseLinkTarget(string rest, int index, int to)
        {
            var pos = 0;
            while (pos < rest.Length && rest[pos] is ' ' or '\t')
            {
                pos++;
            }

            string? url;
            if (pos < rest.Length && rest[pos] == '<')
            {
                var close = rest.IndexOf('>', pos + 1);
                if (close < 0)
                {
                    return (null, null, 1, false);
                }

                url = rest[(pos + 1)..close];
                if (url.Contains('\n') || url.Contains('\r'))
                {
                    return (null, null, 1, false);
                }

                pos = close + 1;
            }
            else
            {
                var end = pos;
                var depth = 0;
                while (end < rest.Length && rest[end] is not (' ' or '\t'))
                {
                    if (rest[end] == '(')
                    {
                        depth++;
                    }
                    else if (rest[end] == ')')
                    {
                        if (depth == 0)
                        {
                            break;
                        }

                        depth--;
                    }
                    else if (rest[end] == '\\' && end + 1 < rest.Length)
                    {
                        end++;
                    }

                    end++;
                }

                url = rest[pos..end];
                if (url.Length == 0)
                {
                    return (null, null, 1, false);
                }

                pos = end;
            }

            while (pos < rest.Length && rest[pos] is ' ' or '\t')
            {
                pos++;
            }

            if (pos >= rest.Length)
            {
                // Title may follow on the next line.
                if (index + 1 < to && TryParseTitleOnly(Lines[index + 1].Text, out var nextTitle))
                {
                    return (url, nextTitle, 2, true);
                }

                return (url, null, 1, true);
            }

            if (!TryParseTitle(rest, pos, out var title, out var titleEnd))
            {
                return (null, null, 1, false);
            }

            pos = titleEnd;
            while (pos < rest.Length && rest[pos] is ' ' or '\t')
            {
                pos++;
            }

            return pos >= rest.Length ? (url, title, 1, true) : (null, null, 1, false);
        }

        private static bool TryParseTitle(string text, int pos, out string? title, out int end)
        {
            title = null;
            end = pos;
            if (pos >= text.Length)
            {
                return false;
            }

            var quote = text[pos];
            var closer = quote switch
            {
                '"' => '"',
                '\'' => '\'',
                '(' => ')',
                _ => '\0',
            };
            if (closer == '\0')
            {
                return false;
            }

            var cursor = pos + 1;
            while (cursor < text.Length && text[cursor] != closer)
            {
                if (text[cursor] == '\\' && cursor + 1 < text.Length)
                {
                    cursor++;
                }

                cursor++;
            }

            if (cursor >= text.Length)
            {
                return false;
            }

            if (closer == ')' && text[cursor] != ')')
            {
                return false;
            }

            title = text[(pos + 1)..cursor];
            end = cursor + 1;
            return true;
        }

        private static bool TryParseTitleOnly(string text, out string? title)
        {
            title = null;
            var indent = CountIndent(text, 0);
            if (indent.Columns >= 4)
            {
                return false;
            }

            var trimmed = text.Trim();
            if (trimmed.Length < 2)
            {
                return false;
            }

            var ok = (trimmed[0] == '"' && trimmed[^1] == '"')
                || (trimmed[0] == '\'' && trimmed[^1] == '\'')
                || (trimmed[0] == '(' && trimmed[^1] == ')');
            if (!ok)
            {
                return false;
            }

            title = trimmed[1..^1];
            return true;
        }

        private static int FindLabelEnd(string text, int open)
        {
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                if (text[i] == '\\')
                {
                    i++;
                }
                else if (text[i] == '[')
                {
                    depth++;
                }
                else if (text[i] == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private bool IsTableDelimiter(string text, List<Line> acc)
        {
            if (acc.Count == 0 || !acc[^1].Text.Contains('|'))
            {
                return false;
            }

            var cells = SplitRow(text);
            if (cells is null || cells.Count == 0)
            {
                return false;
            }

            foreach (var cell in cells)
            {
                var trimmed = cell.Trim();
                if (trimmed.Length == 0)
                {
                    return false;
                }

                var core = trimmed.Trim(':');
                if (core.Length == 0 || core.Any(static ch => ch != '-'))
                {
                    return false;
                }
            }

            return SplitRow(acc[^1].Text)?.Count == cells.Count;
        }

        private static List<string>? SplitRow(string text)
        {
            var indent = CountIndent(text, 0);
            if (indent.Columns >= 4)
            {
                return null;
            }

            var content = text[indent.Chars..];
            if (!content.Contains('|'))
            {
                return null;
            }

            var cells = new List<string>();
            var current = new System.Text.StringBuilder();
            var index = 0;
            while (index < content.Length)
            {
                var ch = content[index];
                if (ch == '\\' && index + 1 < content.Length && content[index + 1] == '|')
                {
                    current.Append('|');
                    index += 2;
                }
                else if (ch == '|')
                {
                    cells.Add(current.ToString());
                    current.Clear();
                    index++;
                }
                else
                {
                    current.Append(ch);
                    index++;
                }
            }

            cells.Add(current.ToString());
            if (cells.Count > 0 && cells[0].Trim().Length == 0)
            {
                cells.RemoveAt(0);
            }

            if (cells.Count > 0 && cells[^1].Trim().Length == 0)
            {
                cells.RemoveAt(cells.Count - 1);
            }

            return cells;
        }

        private bool TryParseTable(List<Line> headerLines, int delimiterIndex, int to, out LithoTable? table, out int next)
        {
            table = null;
            next = delimiterIndex;
            var delimiterCells = SplitRow(Lines[delimiterIndex].Text);
            if (delimiterCells is null)
            {
                return false;
            }

            var headerCells = SplitRow(headerLines[^1].Text);
            if (headerCells is null || headerCells.Count != delimiterCells.Count)
            {
                return false;
            }

            var align = delimiterCells.Select(static cell =>
            {
                var trimmed = cell.Trim();
                var left = trimmed.StartsWith(':');
                var right = trimmed.EndsWith(':');
                return (left, right) switch
                {
                    (true, true) => "center",
                    (true, false) => "left",
                    (false, true) => "right",
                    _ => string.Empty,
                };
            }).ToArray();

            var rows = new List<IReadOnlyList<IReadOnlyList<LithoInline>>>
            {
                ParseRowCells(headerCells, headerLines[^1].Offset, headerLines[^1].Text),
            };
            var cursor = delimiterIndex + 1;
            while (cursor < to)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = Lines[cursor];
                if (IsBlank(line.Text))
                {
                    break;
                }

                var cells = SplitRow(line.Text);
                if (cells is null || !line.Text.Contains('|'))
                {
                    break;
                }

                while (cells.Count < headerCells.Count)
                {
                    cells.Add(string.Empty);
                }

                if (cells.Count > headerCells.Count)
                {
                    cells.RemoveRange(headerCells.Count, cells.Count - headerCells.Count);
                }

                rows.Add(ParseRowCells(cells, line.Offset, line.Text));
                cursor++;
            }

            next = cursor;
            var start = headerLines[^1].Offset;
            var end = Lines[cursor - 1].Offset + Lines[cursor - 1].Text.Length;
            table = new LithoTable(align, rows, new SourceSpan(start, end - start));
            return true;
        }

        private List<IReadOnlyList<LithoInline>> ParseRowCells(List<string> cells, int lineOffset, string lineText)
        {
            var result = new List<IReadOnlyList<LithoInline>>(cells.Count);
            var search = 0;
            foreach (var cell in cells)
            {
                var trimmed = cell.Trim();
                var relative = trimmed.Length == 0
                    ? -1
                    : lineText.IndexOf(trimmed, search, StringComparison.Ordinal);
                var offset = relative < 0 ? lineOffset : lineOffset + relative;
                result.Add(Resolve ? [new LithoRawText(trimmed, LithoLineMap.Contiguous(offset, trimmed.Length))] : []);
                if (relative >= 0)
                {
                    search = relative + trimmed.Length;
                }
            }

            return result;
        }
    }

    private readonly record struct Indent(int Chars, int Columns);

    private static Indent CountIndent(string text, int from)
    {
        var columns = 0;
        var index = from;
        while (index < text.Length && (text[index] is ' ' or '\t'))
        {
            columns = text[index] == '\t' ? columns + (4 - (columns % 4)) : columns + 1;
            index++;
        }

        return new Indent(index - from, columns);
    }

    private static bool IsBlank(string text)
    {
        foreach (var ch in text)
        {
            if (ch is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    private static (string Text, int Chars) StripColumns(string text, int columns)
    {
        var consumed = 0;
        var index = 0;
        while (index < text.Length && consumed < columns && (text[index] is ' ' or '\t'))
        {
            consumed = text[index] == '\t' ? consumed + (4 - (consumed % 4)) : consumed + 1;
            index++;
        }

        return (text[index..], index);
    }

    private static SourceSpan SpanOf(Line line) =>
        new(line.Offset, line.Text.Length);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
