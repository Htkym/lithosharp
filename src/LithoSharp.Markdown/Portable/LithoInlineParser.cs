using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

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
        CancellationToken cancellationToken = default, MdParseContext? context = null)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (map is null) throw new ArgumentNullException(nameof(map));
        if (references is null) throw new ArgumentNullException(nameof(references));
        return new Scanner(text, map, references, cancellationToken, context).Scan();
    }

    private sealed class Scanner(
        string text,
        LithoLineMap map,
        IReadOnlyDictionary<string, LithoReference> references,
        CancellationToken cancellationToken, MdParseContext? context)
    {
        private readonly List<object> _stack = [new FrameList()];
        private int _position;
        private int _textStart = -1;
        private SourceSpan? _targetSpan;
        private int _referenceTailEnd;
        private int _opaqueUntil;
        private LithoText TextNode(string value, int local, int length, SourceSpan legacySpan, bool atomic = false) =>
            new(value, legacySpan) { Projection = context is null ? null
                : MdSourceMapping.Local(value, text, map, local, length, atomic) };
        private MdTextProjection? Project(string value, int local, int length, bool atomic = false) =>
            context is null ? null : MdSourceMapping.Local(value, text, map, local, length, atomic);

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

        private int MaxInlineDepth(IReadOnlyList<LithoInline> nodes)
        {
            var max = 0; var depth = 0;
            foreach (var (node, exit) in LithoInline.Walk(nodes))
            {
                context?.Scan(8);
                if (exit) { depth--; continue; }
                if (node is LithoEmphasis or LithoStrike or LithoLink) { depth++; max = Math.Max(max, depth); }
            }
            return max;
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
                context?.Scan(8);
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
                            if (context is not null && _position >= _opaqueUntil
                                && MdOpaqueSyntax.TryTag(text, _position, context, out var opaqueEnd))
                            {
                                var span = Glob(new SourceSpan(_position, opaqueEnd - _position));
                                context.OpaqueTokens.Add(new MdRawRange(span.Start, span.Length));
                                _opaqueUntil = opaqueEnd;
                            }
                            AppendTextChar();
                        }

                        break;
                    case '!':
                        if (Peek(1) == '[')
                        {
                            FlushText();
                            context?.Depth(_stack.Count);
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
                        context?.Depth(_stack.Count);
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
            context?.Depth(MaxInlineDepth(((FrameList)_stack[0]).Children));
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
                context?.Scan(8);
                spaces++;
            }

            if (_textStart >= 0)
            {
                var contentEnd = _position - spaces;
                if (contentEnd > _textStart)
                {
                    Current.Add(TextNode(text[_textStart..contentEnd], _textStart, contentEnd - _textStart,
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

            Current.Add(new LithoLineBreak(spaces >= 2, SpanAt(_position, breakLength))
                { Projection = Project("\n", _position, breakLength, atomic: breakLength != 1) });
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

            Current.Add(TextNode(text[_textStart.._position], _textStart, _position - _textStart,
                new SourceSpan(_textStart, _position - _textStart)));
            _textStart = -1;
        }

        private char Peek(int ahead)
        {
            context?.Scan();
            var index = _position + ahead;
            return index < text.Length ? text[index] : '\0';
        }

        private bool MatchAt(string value) =>
            (context is null || ChargeMatch(value.Length)) &&
            _position + value.Length <= text.Length
            && string.CompareOrdinal(text, _position, value, 0, value.Length) == 0;

        private bool ChargeMatch(int length) { context!.Scan(length); return true; }

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
                Current.Add(new LithoLineBreak(true, SpanAt(_position, 2))
                    { Projection = Project("\n", _position, 2, atomic: true) });
                _position += 2;
            }
            else if (next == '\r')
            {
                FlushText();
                var length = Peek(2) == '\n' ? 3 : 2;
                Current.Add(new LithoLineBreak(true, SpanAt(_position, length))
                    { Projection = Project("\n", _position, length, atomic: true) });
                _position += length;
            }
            else if (next != '\0' && IsAsciiPunctuation(next))
            {
                FlushText();
                Current.Add(TextNode(next.ToString(), _position, 2, SpanAt(_position + 1, 1), atomic: true));
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
                context?.Scan(8);
                    AppendTextChar();
                }

                return;
            }

            FlushText();
            var content = text[(_position + run)..closer];
            context?.Scan((long)content.Length * 3);
            content = content.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', ' ');
            if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim().Length != 0)
            {
                content = content[1..^1];
            }

            Current.Add(new LithoCodeSpan(content, SpanAt(_position, (closer + run) - _position))
                { Projection = Project(content, _position + run, closer - _position - run, atomic: true) });
            _position = closer + run;
        }

        private int CountRun(char ch)
        {
            var run = 0;
            while (_position + run < text.Length && text[_position + run] == ch)
            {
                context?.Scan(8);
                run++;
            }

            return run;
        }

        private int FindCodeCloser(int from, int run)
        {
            var cursor = from;
            while (cursor < text.Length)
            {
                context?.Scan(8);
                if (text[cursor] == '`')
                {
                    var length = 0;
                    while (cursor + length < text.Length && text[cursor + length] == '`')
                    {
                context?.Scan(8);
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
                context?.Scan(8);
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
            while (nameEnd < text.Length && (LithoCharacters.IsAsciiLetterOrDigit(text[nameEnd])) && nameEnd - _position < 33)
            {
                context?.Scan(8);
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
            context?.Scan(candidate.Length);
            Current.Add(TextNode(string.Equals(decoded, candidate, StringComparison.Ordinal) ? candidate : decoded,
                _position, end - _position, SpanAt(_position, end - _position),
                atomic: !string.Equals(decoded, candidate, StringComparison.Ordinal)));
            _position = end;
        }

        private static bool IsHexDigit(char ch) =>
            (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');

        private bool ScanAutolink()
        {
            context?.Scan((long)(text.Length - _position - 1) * 6);
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
            Current.Add(new LithoAutolink(href, inner, SpanAt(_position, (close + 1) - _position))
                { Projection = Project(inner, _position + 1, inner.Length) });
            _position = close + 1;
            return true;
        }

        private bool IsSchemeLink(string inner)
        {
            var colon = inner.IndexOf(':');
            if (colon < 2 || colon > 32)
            {
                return false;
            }

            if (!LithoCharacters.IsAsciiLetter(inner[0]))
            {
                return false;
            }

            for (var i = 1; i < colon; i++)
            {
                context?.Scan(8);
                if (!(LithoCharacters.IsAsciiLetterOrDigit(inner[i]) || inner[i] is '+' or '.' or '-'))
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
                context?.Scan(8);
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
            Current.Add(new LithoAutolink(href, candidate, SpanAt(_position, candidate.Length))
                { Projection = Project(candidate, _position, candidate.Length) });
            _position += candidate.Length;
        }

        private string TrimAutolinkEnd(string candidate)
        {
            context?.Scan(candidate.Length);
            var end = candidate.Length;
            while (end > 0 && candidate[end - 1] is '?' or '!' or '.' or ',' or ':' or ';' or '*' or '_' or '~')
            {
                context?.Scan(8);
                end--;
            }

            var open = 0;
            for (var index = 0; index < end; index++)
            {
                context?.Scan(8);
                var ch = candidate[index];
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
                context?.Scan(8);
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
                context?.Scan(8);
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
                parent.Add(new LithoLink(frame.Children, url!, title, frame.Image, SpanAt(frame.Span.Start, end - frame.Span.Start))
                    { LinkSyntax = context is null ? null : new MdLinkSyntax(MdLinkKind.Inline, null, _targetSpan) });
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
                && references.TryGetValue(LithoBlockParser.NormalizeLabel(reference, context), out var definition))
            {
                _stack.RemoveAt(opener);
                parent.Add(new LithoLink(frame.Children, definition.Url, definition.Title, frame.Image, SpanAt(frame.Span.Start, after - frame.Span.Start))
                    { LinkSyntax = context is null ? null : new MdLinkSyntax(MdLinkKind.ReferenceUse, reference, null) });
                if (!frame.Image)
                {
                    DeactivateBrackets();
                }

                _position = after;
                return;
            }

            MdLink? unresolved = null;
            if (context is not null && reference is not null && frame.Span.Start >= _referenceTailEnd)
            {
                var labelProjection = MdInlineFacts.Text(frame.Children, cancellationToken);
                if (after > _position + 1) _referenceTailEnd = after;
                context.Emit(1 + labelProjection.SourceSegments.Count);
                unresolved = new MdLink(MdLinkKind.ReferenceUse, MdLinkResolution.UnresolvedReference, frame.Image,
                    new MdRawRange(Glob(new SourceSpan(frame.Span.Start, after - frame.Span.Start)).Start,
                        Glob(new SourceSpan(frame.Span.Start, after - frame.Span.Start)).Length),
                    labelProjection, null, null, null, reference);
            }
            // No link: the opener becomes literal text.
            frame.Active = false;
            _stack.RemoveAt(opener);
            parent.Add(TextNode(frame.Image ? "![" : "[", frame.Span.Start, frame.Image ? 2 : 1, Glob(frame.Span)) with
                { UnresolvedReference = unresolved });
            parent.AddRange(frame.Children);
            parent.Add(TextNode("]", _position, 1, SpanAt(_position, 1)));
            _position++;
        }

        private void DeactivateBrackets()
        {
            foreach (var entry in _stack)
            {
                context?.Scan(8);
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
                context?.Scan(8);
                var top = _stack[^1];
                _stack.RemoveAt(_stack.Count - 1);
                var parent = Current;
                if (top is DelimiterFrame delimiter)
                {
                    parent.Add(TextNode(new string(delimiter.Marker, delimiter.Length), delimiter.Span.Start, delimiter.Length, Glob(delimiter.Span)));
                    parent.AddRange(delimiter.Children);
                }
                else if (top is BracketFrame bracket)
                {
                    bracket.Active = false;
                    parent.Add(TextNode(bracket.Image ? "![" : "[", bracket.Span.Start, bracket.Image ? 2 : 1, Glob(bracket.Span)));
                    parent.AddRange(bracket.Children);
                }
            }
        }

        private void UnwindTo(int index)
        {
            while (_stack.Count - 1 > index)
            {
                context?.Scan(8);
                var top = _stack[^1];
                _stack.RemoveAt(_stack.Count - 1);
                var parent = Current;
                if (top is DelimiterFrame delimiter)
                {
                    parent.Add(TextNode(new string(delimiter.Marker, delimiter.Length), delimiter.Span.Start, delimiter.Length, Glob(delimiter.Span)));
                    parent.AddRange(delimiter.Children);
                }
                else if (top is BracketFrame bracket)
                {
                    parent.Add(TextNode(bracket.Image ? "![" : "[", bracket.Span.Start, bracket.Image ? 2 : 1, Glob(bracket.Span)));
                    parent.AddRange(bracket.Children);
                }
            }
        }

        private int FindLabelEnd(int open)
        {
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                context?.Scan(8);
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
            _targetSpan = null;
            end = _position + 1;
            if (end >= text.Length || text[end] != '(')
            {
                return false;
            }

            var cursor = end + 1;
            while (cursor < text.Length && text[cursor] is ' ' or '\t' or '\n' or '\r')
            {
                context?.Scan(8);
                cursor++;
            }

            if (cursor < text.Length && text[cursor] == ')')
            {
                url = string.Empty;
                _targetSpan = SpanAt(cursor, 0);
                end = cursor + 1;
                return true;
            }

            string parsed;
            if (cursor < text.Length && text[cursor] == '<')
            {
                var close = cursor + 1;
                while (close < text.Length && text[close] != '>')
                {
                context?.Scan(8);
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
                _targetSpan = SpanAt(cursor + 1, close - cursor - 1);
                cursor = close + 1;
            }
            else
            {
                var start = cursor;
                var depth = 0;
                while (cursor < text.Length && text[cursor] is not (' ' or '\t' or '\n' or '\r'))
                {
                context?.Scan(8);
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
                _targetSpan = SpanAt(start, cursor - start);
                if (parsed.Length == 0 || depth != 0)
                {
                    return false;
                }
            }

            url = DecodeEscapesAndEntities(parsed);
            var afterSpaces = cursor;
            while (afterSpaces < text.Length && text[afterSpaces] is ' ' or '\t' or '\n' or '\r')
            {
                context?.Scan(8);
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
                context?.Scan(8);
                cursor++;
            }

            if (cursor >= text.Length || text[cursor] != ')')
            {
                return false;
            }

            end = cursor + 1;
            return true;
        }

        private bool TryParseInlineTitle(string full, int pos, out string? title, out int end)
        {
            context?.Scan(full.Length);
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
                context?.Scan(8);
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
                context?.Scan(8);
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
                context?.Scan(8);
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
                context?.Scan(8);
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
                        SpanAt(_position, (cursor + length) - _position))
                        { Projection = Project(text[contentStart..cursor], contentStart, cursor - contentStart) });
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

        private bool ContainsDollarRun(string source, int from, int to)
        {
            for (var i = from; i + 1 < to; i++)
            {
                context?.Scan(8);
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
                context?.Scan(8);
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
                context?.Scan(8);
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
                    Current.Add(TextNode(text[_textStart..runStart], _textStart, runStart - _textStart,
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
                context?.Depth(_stack.Count);
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
                Current.Add(TextNode(new string(marker, length), runStart, length, SpanAt(runStart, length)));
            }
        }

        private int MatchCloser(char marker, int length, bool canOpen, int runStart)
        {
            while (length > 0)
            {
                context?.Scan(8);
                cancellationToken.ThrowIfCancellationRequested();
                var opener = -1;
                for (var i = _stack.Count - 1; i >= 1; i--)
                {
                context?.Scan(8);
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
