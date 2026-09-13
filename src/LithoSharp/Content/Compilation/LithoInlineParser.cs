using System.Text;

namespace LithoSharp.Content.Compilation;

/// <summary>
/// Inline parser for the Litho subset: text, escapes, entities, code spans,
/// emphasis/strong (delimiter algorithm with nesting), strikethrough, links
/// (inline, full/collapsed/shortcut reference), images, autolinks (including
/// www/http extended forms), and hard/soft breaks. Bare emails stay literal,
/// matching the reference output. Single tildes stay literal (U15).
/// </summary>
internal static class LithoInlineParser
{
    private sealed class DelimiterFrame
    {
        public char Marker;
        public int Length;
        public bool CanOpen;
        public bool CanClose;
        public List<LithoInline> Children = [];
        public SourceSpan Span;
    }

    private sealed class BracketFrame
    {
        public bool Image;
        public bool Active = true;
        public List<LithoInline> Children = [];
        public SourceSpan Span;
    }

    internal static string DecodeEscapesAndEntities(string value) => EscapesAndEntities.Replace(value,
        match => match.Groups[1].Success ? match.Groups[1].Value : System.Net.WebUtility.HtmlDecode(match.Value));

    private static readonly System.Text.RegularExpressions.Regex EscapesAndEntities = new(
        """\\([!"#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])|&(?:\#[xX][0-9a-fA-F]{1,6}|\#[0-9]{1,7}|[a-zA-Z][a-zA-Z0-9]{1,31});""");

    /// <summary>Parses inlines, emitting body-offset spans via the line map.</summary>
    public static List<LithoInline> Parse(
        string text,
        LithoLineMap map,
        IReadOnlyDictionary<string, LithoReference> references,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(references);
        return new Scanner(text, map, references, cancellationToken).Scan();
    }

    private sealed class Scanner(
        string text,
        LithoLineMap map,
        IReadOnlyDictionary<string, LithoReference> references,
        CancellationToken cancellationToken)
    {
        private readonly List<object> _stack = [new FrameList()];
        private int _position;
        private int _textStart = -1;

        private sealed class FrameList
        {
            public List<LithoInline> Children = [];
        }

        /// <summary>Converts a local span to body offsets. Frames stay local.</summary>
        private SourceSpan Glob(SourceSpan local)
        {
            if (local.IsEmpty)
            {
                return SourceSpan.Empty;
            }

            var start = map.Global(local.Start);
            var end = map.Global(local.End - 1) + 1;
            return end <= start ? new SourceSpan(start, 0) : new SourceSpan(start, end - start);
        }

        private List<LithoInline> Current
        {
            get
            {
                var top = _stack[^1];
                if (top is FrameList list)
                {
                    return list.Children;
                }

                if (top is DelimiterFrame delimiter)
                {
                    return delimiter.Children;
                }

                return ((BracketFrame)top).Children;
            }
        }

        public List<LithoInline> Scan()
        {
            while (_position < text.Length)
            {
                if ((_position & 127) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var ch = text[_position];
                switch (ch)
                {
                    case '\n':
                        ScanLineBreak();
                        break;
                    case '\r':
                        ScanLineBreak();
                        break;
                    case '\\':
                        ScanEscape();
                        break;
                    case '`':
                        ScanCodeSpan();
                        break;
                    case '&':
                        ScanEntity();
                        break;
                    case '<':
                        if (!ScanAutolink())
                        {
                            AppendTextChar();
                        }

                        break;
                    case '!':
                        if (Peek(1) == '[')
                        {
                            FlushText();
                            _stack.Add(new BracketFrame { Image = true, Span = SpanLocal(_position, 2) });
                            _position += 2;
                        }
                        else
                        {
                            AppendTextChar();
                        }

                        break;
                    case '[':
                        FlushText();
                        _stack.Add(new BracketFrame { Image = false, Span = SpanLocal(_position, 1) });
                        _position++;
                        break;
                    case ']':
                        ScanCloseBracket();
                        break;
                    case '*' or '_' or '~':
                        ScanDelimiterRun();
                        break;
                    case '$':
                        ScanMath();
                        break;
                    case 'w' when MatchAt("www.")
                        && IsExtendedAutolinkStart(_position):
                        ScanExtendedAutolink();
                        break;
                    case 'h' when (MatchAt("http://") || MatchAt("https://"))
                        && IsExtendedAutolinkStart(_position):
                        ScanExtendedAutolink();
                        break;
                    default:
                        AppendTextChar();
                        break;
                }
            }

            FlushText();
            UnwindTo(0);
            return ((FrameList)_stack[0]).Children;
        }

        private void ScanLineBreak()
        {
            // CRLF consumes both characters as a single break; the text slice
            // must end before the '\r' so it never leaks into output.
            var breakLength = text[_position] == '\r' && Peek(1) == '\n' ? 2 : 1;
            var pendingStart = _textStart < 0 ? _position : _textStart;
            var spaces = 0;
            while (spaces < _position - pendingStart && text[_position - spaces - 1] == ' ')
            {
                spaces++;
            }

            if (_textStart >= 0)
            {
                var contentEnd = _position - spaces;
                if (contentEnd > _textStart)
                {
                    Current.Add(new LithoText(
                        text[_textStart..contentEnd],
                        new SourceSpan(_textStart, contentEnd - _textStart)));
                }

                _textStart = -1;
            }

            if (spaces == _position - pendingStart && Current.Count == 0)
            {
                // Whitespace-only content: no break node.
                _position += breakLength;
                return;
            }

            Current.Add(new LithoLineBreak(spaces >= 2, SpanAt(_position, breakLength)));
            _position += breakLength;
        }

        private void AppendTextChar()
        {
            if (_textStart < 0)
            {
                _textStart = _position;
            }

            _position++;
        }

        private void FlushText()
        {
            if (_textStart < 0)
            {
                return;
            }

            Current.Add(new LithoText(
                text[_textStart.._position],
                new SourceSpan(_textStart, _position - _textStart)));
            _textStart = -1;
        }

        private char Peek(int ahead)
        {
            var index = _position + ahead;
            return index < text.Length ? text[index] : '\0';
        }

        private bool MatchAt(string value) =>
            _position + value.Length <= text.Length
            && text.AsSpan(_position, value.Length).SequenceEqual(value);

        private SourceSpan SpanAt(int local, int length) =>
            Glob(new SourceSpan(local, length));

        private static SourceSpan SpanLocal(int local, int length) =>
            new(local, length);

        private void ScanEscape()
        {
            var next = Peek(1);
            if (next == '\n')
            {
                FlushText();
                Current.Add(new LithoLineBreak(true, SpanAt(_position, 2)));
                _position += 2;
            }
            else if (next == '\r')
            {
                FlushText();
                var length = Peek(2) == '\n' ? 3 : 2;
                Current.Add(new LithoLineBreak(true, SpanAt(_position, length)));
                _position += length;
            }
            else if (next != '\0' && IsAsciiPunctuation(next))
            {
                FlushText();
                Current.Add(new LithoText(next.ToString(), SpanAt(_position + 1, 1)));
                _position += 2;
            }
            else
            {
                AppendTextChar();
            }
        }

        private void ScanCodeSpan()
        {
            var run = CountRun('`');
            var closer = FindCodeCloser(_position + run, run);
            if (closer < 0)
            {
                for (var i = 0; i < run; i++)
                {
                    AppendTextChar();
                }

                return;
            }

            FlushText();
            var content = text[(_position + run)..closer];
            content = content.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', ' ');
            if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim().Length != 0)
            {
                content = content[1..^1];
            }

            Current.Add(new LithoCodeSpan(content, SpanAt(_position, (closer + run) - _position)));
            _position = closer + run;
        }

        private int CountRun(char ch)
        {
            var run = 0;
            while (_position + run < text.Length && text[_position + run] == ch)
            {
                run++;
            }

            return run;
        }

        private int FindCodeCloser(int from, int run)
        {
            var cursor = from;
            while (cursor < text.Length)
            {
                if (text[cursor] == '`')
                {
                    var length = 0;
                    while (cursor + length < text.Length && text[cursor + length] == '`')
                    {
                        length++;
                    }

                    if (length == run)
                    {
                        return cursor;
                    }

                    cursor += length;
                }
                else
                {
                    cursor++;
                }
            }

            return -1;
        }

        private void ScanEntity()
        {
            var end = _position + 1;
            if (end < text.Length && text[end] == '#')
            {
                end++;
                var hex = end < text.Length && (text[end] == 'x' || text[end] == 'X');
                if (hex)
                {
                    end++;
                }

                var digits = 0;
                while (end < text.Length && IsHexDigit(text[end]) && digits < 8)
                {
                    end++;
                    digits++;
                }

                if (digits == 0 || end >= text.Length || text[end] != ';')
                {
                    AppendTextChar();
                    return;
                }

                EmitDecodedEntity(end + 1);
                return;
            }

            var nameEnd = end;
            while (nameEnd < text.Length && (char.IsAsciiLetterOrDigit(text[nameEnd])) && nameEnd - _position < 33)
            {
                nameEnd++;
            }

            if (nameEnd >= text.Length || text[nameEnd] != ';' || nameEnd == _position + 1)
            {
                AppendTextChar();
                return;
            }

            EmitDecodedEntity(nameEnd + 1);
        }

        private void EmitDecodedEntity(int end)
        {
            var candidate = text[_position..end];
            var decoded = System.Net.WebUtility.HtmlDecode(candidate);
            FlushText();
            Current.Add(new LithoText(
                string.Equals(decoded, candidate, StringComparison.Ordinal) ? candidate : decoded,
                SpanAt(_position, end - _position)));
            _position = end;
        }

        private static bool IsHexDigit(char ch) =>
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');

        private bool ScanAutolink()
        {
            var close = text.IndexOf('>', _position + 1);
            if (close < 0)
            {
                return false;
            }

            var inner = text[(_position + 1)..close];
            if (inner.Length == 0 || inner.Contains('<') || inner.Contains(' ') || inner.Contains('\n') || inner.Contains('\r'))
            {
                return false;
            }

            string? href = null;
            if (IsSchemeLink(inner))
            {
                href = inner;
            }
            else if (IsEmailLink(inner))
            {
                href = "mailto:" + inner;
            }

            if (href is null)
            {
                return false;
            }

            FlushText();
            Current.Add(new LithoAutolink(href, inner, SpanAt(_position, (close + 1) - _position)));
            _position = close + 1;
            return true;
        }

        private static bool IsSchemeLink(string inner)
        {
            var colon = inner.IndexOf(':');
            if (colon < 2 || colon > 32)
            {
                return false;
            }

            if (!char.IsAsciiLetter(inner[0]))
            {
                return false;
            }

            for (var i = 1; i < colon; i++)
            {
                if (!(char.IsAsciiLetterOrDigit(inner[i]) || inner[i] is '+' or '.' or '-'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsEmailLink(string inner)
        {
            var at = inner.IndexOf('@');
            return at > 0 && inner.IndexOf('@', at + 1) < 0 && inner[(at + 1)..].Contains('.');
        }

        private bool IsExtendedAutolinkStart(int local)
        {
            if (local == 0)
            {
                return true;
            }

            var prev = text[local - 1];
            return prev is ' ' or '\t' or '\n' or '(';
        }

        private void ScanExtendedAutolink()
        {
            var end = _position;
            while (end < text.Length && text[end] is not (' ' or '\t' or '\n' or '\r' or '<'))
            {
                end++;
            }

            var candidate = text[_position..end];
            candidate = TrimAutolinkEnd(candidate);
            if (candidate.Length == 0)
            {
                AppendTextChar();
                return;
            }

            FlushText();
            var href = candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? "http://" + candidate
                : candidate;
            Current.Add(new LithoAutolink(href, candidate, SpanAt(_position, candidate.Length)));
            _position += candidate.Length;
        }

        private static string TrimAutolinkEnd(string candidate)
        {
            var end = candidate.Length;
            while (end > 0 && candidate[end - 1] is '?' or '!' or '.' or ',' or ':' or ';' or '*' or '_' or '~')
            {
                end--;
            }

            var open = 0;
            foreach (var ch in candidate.AsSpan(0, end))
            {
                if (ch == '(')
                {
                    open++;
                }
                else if (ch == ')')
                {
                    open--;
                }
            }

            while (end > 0 && candidate[end - 1] == ')' && open < 0)
            {
                end--;
                open++;
            }

            return candidate[..end];
        }

        private void ScanCloseBracket()
        {
            var opener = -1;
            for (var i = _stack.Count - 1; i >= 1; i--)
            {
                if (_stack[i] is BracketFrame candidate && candidate.Active)
                {
                    opener = i;
                    break;
                }

                if (_stack[i] is BracketFrame)
                {
                    break;
                }
            }

            if (opener < 0)
            {
                AppendTextChar();
                return;
            }

            FlattenAbove(opener);
            FlushText();
            var frame = (BracketFrame)_stack[opener];
            var parent = ParentOf(opener);
            if (TryParseInlineTarget(out var url, out var title, out var end))
            {
                _stack.RemoveAt(opener);
                parent.Add(new LithoLink(frame.Children, url!, title, frame.Image, SpanAt(frame.Span.Start, end - frame.Span.Start)));
                if (!frame.Image)
                {
                    DeactivateBrackets();
                }

                _position = end;
                return;
            }

            var label = text[frame.Span.End.._position];
            string? reference = null;
            var after = _position + 1;
            if (after < text.Length && text[after] == '[')
            {
                var labelEnd = FindLabelEnd(after);
                if (labelEnd >= 0)
                {
                    var name = text[(after + 1)..labelEnd];
                    reference = name.Length == 0 ? label : name;
                    after = labelEnd + 1;
                }
            }
            else if (frame.Children.Count != 0)
            {
                reference = label;
            }

            // Footnote syntax (U01) never forms links.
            if (reference is not null && reference.StartsWith('^'))
            {
                reference = null;
            }

            if (reference is not null
                && references.TryGetValue(LithoBlockParser.NormalizeLabel(reference), out var definition))
            {
                _stack.RemoveAt(opener);
                parent.Add(new LithoLink(frame.Children, definition.Url, definition.Title, frame.Image, SpanAt(frame.Span.Start, after - frame.Span.Start)));
                if (!frame.Image)
                {
                    DeactivateBrackets();
                }

                _position = after;
                return;
            }

            // No link: the opener becomes literal text.
            frame.Active = false;
            _stack.RemoveAt(opener);
            parent.Add(new LithoText(frame.Image ? "![" : "[", Glob(frame.Span)));
            parent.AddRange(frame.Children);
            parent.Add(new LithoText("]", SpanAt(_position, 1)));
            _position++;
        }

        private void DeactivateBrackets()
        {
            foreach (var entry in _stack)
            {
                if (entry is BracketFrame { Image: false } bracket)
                {
                    bracket.Active = false;
                }
            }
        }

        private void FlattenAbove(int index)
        {
            while (_stack.Count - 1 > index)
            {
                var top = _stack[^1];
                _stack.RemoveAt(_stack.Count - 1);
                var parent = Current;
                if (top is DelimiterFrame delimiter)
                {
                    parent.Add(new LithoText(new string(delimiter.Marker, delimiter.Length), Glob(delimiter.Span)));
                    parent.AddRange(delimiter.Children);
                }
                else if (top is BracketFrame bracket)
                {
                    bracket.Active = false;
                    parent.Add(new LithoText(bracket.Image ? "![" : "[", Glob(bracket.Span)));
                    parent.AddRange(bracket.Children);
                }
            }
        }

        private void UnwindTo(int index)
        {
            while (_stack.Count - 1 > index)
            {
                var top = _stack[^1];
                _stack.RemoveAt(_stack.Count - 1);
                var parent = Current;
                if (top is DelimiterFrame delimiter)
                {
                    parent.Add(new LithoText(new string(delimiter.Marker, delimiter.Length), Glob(delimiter.Span)));
                    parent.AddRange(delimiter.Children);
                }
                else if (top is BracketFrame bracket)
                {
                    parent.Add(new LithoText(bracket.Image ? "![" : "[", Glob(bracket.Span)));
                    parent.AddRange(bracket.Children);
                }
            }
        }

        private int FindLabelEnd(int open)
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

        private bool TryParseInlineTarget(out string? url, out string? title, out int end)
        {
            url = null;
            title = null;
            end = _position + 1;
            if (end >= text.Length || text[end] != '(')
            {
                return false;
            }

            var cursor = end + 1;
            while (cursor < text.Length && text[cursor] is ' ' or '\t' or '\n' or '\r')
            {
                cursor++;
            }

            if (cursor < text.Length && text[cursor] == ')')
            {
                url = string.Empty;
                end = cursor + 1;
                return true;
            }

            string parsed;
            if (cursor < text.Length && text[cursor] == '<')
            {
                var close = cursor + 1;
                while (close < text.Length && text[close] != '>')
                {
                    if (text[close] is '\n' or '\r')
                    {
                        return false;
                    }

                    close++;
                }

                if (close >= text.Length)
                {
                    return false;
                }

                parsed = text[(cursor + 1)..close];
                cursor = close + 1;
            }
            else
            {
                var start = cursor;
                var depth = 0;
                while (cursor < text.Length && text[cursor] is not (' ' or '\t' or '\n' or '\r'))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (text[cursor] == '(')
                    {
                        depth++;
                    }
                    else if (text[cursor] == ')')
                    {
                        if (depth == 0) break;
                        depth--;
                    }
                    else if (text[cursor] == '\\' && cursor + 1 < text.Length)
                    {
                        cursor++;
                    }

                    cursor++;
                }

                parsed = text[start..cursor];
                if (parsed.Length == 0 || depth != 0)
                {
                    return false;
                }
            }

            url = DecodeEscapesAndEntities(parsed);
            var afterSpaces = cursor;
            while (afterSpaces < text.Length && text[afterSpaces] is ' ' or '\t' or '\n' or '\r')
            {
                afterSpaces++;
            }

            if (afterSpaces < text.Length && text[afterSpaces] == ')')
            {
                end = afterSpaces + 1;
                return true;
            }

            if (!TryParseInlineTitle(text, afterSpaces, out title, out var titleEnd))
            {
                return false;
            }

            cursor = titleEnd;
            while (cursor < text.Length && text[cursor] is ' ' or '\t' or '\n' or '\r')
            {
                cursor++;
            }

            if (cursor >= text.Length || text[cursor] != ')')
            {
                return false;
            }

            end = cursor + 1;
            return true;
        }

        private static bool TryParseInlineTitle(string full, int pos, out string? title, out int end)
        {
            title = null;
            end = pos;
            if (pos >= full.Length)
            {
                return false;
            }

            var closer = full[pos] switch
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
            while (cursor < full.Length && full[cursor] != closer)
            {
                if (full[cursor] == '\\' && cursor + 1 < full.Length)
                {
                    cursor++;
                }

                cursor++;
            }

            if (cursor >= full.Length)
            {
                return false;
            }

            title = DecodeEscapesAndEntities(full[(pos + 1)..cursor]);
            end = cursor + 1;
            return true;
        }

        private void ScanMath()
        {
            // Inline math rules pinned against the reference output: the delimiter
            // is one `$` (or two for a longer run); an opener needs a non-letter,
            // non-digit before it and non-whitespace after it. A closer needs
            // non-whitespace before it; what may follow differs by length
            // (single `$` tolerates letters, double `$$` does not), and a closer
            // is never adjacent to another `$` on its right. Length-2 content
            // must not contain `$$`. Unmatched dollars stay literal text.
            var run = 0;
            while (_position + run < text.Length && text[_position + run] == '$')
            {
                run++;
            }

            var length = run >= 2 ? 2 : 1;
            if (IsAsciiLetterOrDigit(Before(_position)) || IsBlankChar(After(_position + length)) || IsAsciiDigit(After(_position + length)))
            {
                AppendTextChar();
                return;
            }

            var contentStart = _position + length;
            var cursor = contentStart;
            while (cursor < text.Length)
            {
                if ((cursor & 127) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (text[cursor] != '$')
                {
                    cursor++;
                    continue;
                }

                var closeRun = 0;
                while (cursor + closeRun < text.Length && text[cursor + closeRun] == '$')
                {
                    closeRun++;
                }

                if (closeRun >= length
                    && !IsBlankChar(text[cursor - 1])
                    && !IsDollarBlockedAfter(cursor + length, length)
                    && (length == 1 || !ContainsDollarRun(text, contentStart, cursor)))
                {
                    FlushText();
                    Current.Add(new LithoMathInline(
                        text[contentStart..cursor],
                        SpanAt(_position, (cursor + length) - _position)));
                    _position = cursor + length;
                    return;
                }

                cursor += Math.Max(1, closeRun);
            }

            AppendTextChar();
        }

        private bool IsDollarBlockedAfter(int after, int length)
        {
            // The character after a closer is never `$` or a digit; length-2
            // closers additionally reject letters (pinned reference behavior).
            if (after >= text.Length)
            {
                return false;
            }

            var ch = text[after];
            return ch == '$'
                || (ch >= '0' && ch <= '9')
                || (length == 2 && IsAsciiLetterOrDigit(ch));
        }

        private static bool ContainsDollarRun(string source, int from, int to)
        {
            for (var i = from; i + 1 < to; i++)
            {
                if (source[i] == '$' && source[i + 1] == '$')
                {
                    return true;
                }
            }

            return false;
        }

        private char Before(int position) => position == 0 ? '\n' : text[position - 1];

        private char After(int position) => position >= text.Length ? '\n' : text[position];

        private static bool IsAsciiLetterOrDigit(char ch) =>
            (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');

        private static bool IsAsciiDigit(char ch) => ch >= '0' && ch <= '9';

        private void ScanDelimiterRun()
        {
            var marker = text[_position];
            if (marker == '~')
            {
                var tildeRun = 0;
                while (_position + tildeRun < text.Length && text[_position + tildeRun] == '~')
                {
                    tildeRun++;
                }

                if (tildeRun < 2)
                {
                    AppendTextChar();
                    return;
                }
            }

            var length = 0;
            while (_position + length < text.Length && text[_position + length] == marker)
            {
                length++;
            }

            var before = _position == 0 ? '\n' : text[_position - 1];
            var after = _position + length >= text.Length ? '\n' : text[_position + length];
            var (canOpen, canClose) = Flanking(marker, before, after);
            var runStart = _position;
            if (_textStart >= 0)
            {
                if (runStart > _textStart)
                {
                    Current.Add(new LithoText(
                        text[_textStart..runStart],
                        new SourceSpan(_textStart, runStart - _textStart)));
                }

                _textStart = -1;
            }

            _position += length;

            if (canClose)
            {
                length = MatchCloser(marker, length, canOpen, runStart);
            }

            if (length > 0 && canOpen)
            {
                FlushText();
                _stack.Add(new DelimiterFrame
                {
                    Marker = marker,
                    Length = length,
                    CanOpen = true,
                    CanClose = canClose,
                    Span = SpanLocal(runStart, _position - runStart),
                });
            }
            else if (length > 0)
            {
                FlushText();
                Current.Add(new LithoText(new string(marker, length), SpanAt(runStart, length)));
            }
        }

        private int MatchCloser(char marker, int length, bool canOpen, int runStart)
        {
            while (length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var opener = -1;
                for (var i = _stack.Count - 1; i >= 1; i--)
                {
                    if (_stack[i] is BracketFrame)
                    {
                        break;
                    }

                    if (_stack[i] is DelimiterFrame candidate
                        && candidate.Marker == marker
                        && candidate.Length > 0)
                    {
                        if (marker != '~' && (canOpen || candidate.CanClose)
                            && (candidate.Length + length) % 3 == 0
                            && (candidate.Length % 3 != 0 || length % 3 != 0))
                        {
                            continue;
                        }

                        opener = i;
                        break;
                    }
                }

                if (opener < 0)
                {
                    break;
                }

                var frame = (DelimiterFrame)_stack[opener];
                int use;
                if (marker == '~')
                {
                    if (frame.Length < 2 || length < 2)
                    {
                        break;
                    }

                    use = 2;
                }
                else
                {
                    use = frame.Length >= 2 && length >= 2 ? 2 : 1;
                }

                FlattenAbove(opener);
                FlushText();
                var match = (DelimiterFrame)_stack[opener];
                var parent = ParentOf(opener);
                var children = match.Children;
                _stack.RemoveAt(opener);
                LithoInline node = marker == '~'
                    ? new LithoStrike(children, SpanAt(match.Span.Start, (runStart + length) - match.Span.Start))
                    : new LithoEmphasis(use, children, SpanAt(match.Span.Start, (runStart + length) - match.Span.Start));
                parent.Add(node);
                frame.Length -= use;
                length -= use;
                if (frame.Length > 0)
                {
                    // Leftover opener keeps matching: move the new node back
                    // inside so later matches wrap it (***x*** nests).
                    parent.RemoveAt(parent.Count - 1);
                    frame.Children = [node];
                    _stack.Insert(opener, frame);
                }
            }

            return length;
        }

        private List<LithoInline> ParentOf(int index)
        {
            var entry = _stack[index - 1];
            if (entry is FrameList list)
            {
                return list.Children;
            }

            if (entry is DelimiterFrame delimiter)
            {
                return delimiter.Children;
            }

            return ((BracketFrame)entry).Children;
        }

        private static (bool CanOpen, bool CanClose) Flanking(char marker, char before, char after)
        {
            var beforeWs = IsBlankChar(before);
            var afterWs = IsBlankChar(after);
            var beforePunct = IsAsciiPunctuation(before);
            var afterPunct = IsAsciiPunctuation(after);
            var leftFlanking = !afterWs && (!afterPunct || beforeWs || beforePunct);
            var rightFlanking = !beforeWs && (!beforePunct || afterWs || afterPunct);
            if (marker == '_')
            {
                return (leftFlanking && (!rightFlanking || beforePunct),
                    rightFlanking && (!leftFlanking || afterPunct));
            }

            return (leftFlanking, rightFlanking);
        }

        private static bool IsBlankChar(char ch) => ch is ' ' or '\t' or '\n' or '\r' or '\0';

        private static bool IsAsciiPunctuation(char ch) =>
            (ch >= '!' && ch <= '/') || (ch >= ':' && ch <= '@')
            || (ch >= '[' && ch <= '`') || (ch >= '{' && ch <= '~');

    }
}
