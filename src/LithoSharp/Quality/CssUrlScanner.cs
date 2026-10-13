using System.Text;

namespace LithoSharp.Quality;

// Extract literal CSS URLs and, for stylesheets, top-level @import strings.
// This observes references, not cascade ordering, conditions, layout or variables.
// CSS Syntax 3 token boundaries/escapes apply; comments inside an unquoted URL
// are URL characters, while comments and ordinary strings outside it are skipped.
internal static class CssUrlScanner
{
    internal static IEnumerable<string> Read(string css, CancellationToken cancellationToken, bool includeImports = false)
    {
        var index = 0;
        var blockDepth = 0;
        var functionDepth = 0;
        var bracketDepth = 0;
        var rule = RuleKind.None;
        while (index < css.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SkipComment()) continue;
            if (IsWhitespace(css[index])) { index++; continue; }
            if (css[index] is '\'' or '"') { StartQualifiedRule(); ReadQuoted(); continue; }
            if (css[index] == '@')
            {
                var startsRule = TopLevel() && rule == RuleKind.None;
                index++;
                var name = ReadName();
                if (startsRule) rule = RuleKind.AtRule;
                if (includeImports && startsRule
                    && name.Equals("import", StringComparison.OrdinalIgnoreCase))
                {
                    do { SkipWhitespace(); } while (SkipComment());
                    if (index < css.Length && css[index] is '\'' or '"')
                    {
                        var value = ReadQuoted();
                        if (value is not null) yield return value;
                    }
                }
                continue;
            }
            if (css[index] == '#') { StartQualifiedRule(); index++; ReadName(); continue; }
            if (IsName(css[index]) || ValidEscape())
            {
                StartQualifiedRule();
                var name = ReadName();
                if (name.Equals("url", StringComparison.OrdinalIgnoreCase) && index < css.Length && css[index] == '(')
                {
                    index++;
                    var value = ReadUrl();
                    if (value is not null) yield return value;
                }
                continue;
            }
            StartQualifiedRule();
            switch (css[index++])
            {
                case '{': blockDepth++; break;
                case '}':
                    if (blockDepth > 0 && --blockDepth == 0 && TopLevel()) rule = RuleKind.None;
                    break;
                // A semicolon ends an at-rule, but remains part of a qualified-rule prelude.
                case ';': if (TopLevel() && rule == RuleKind.AtRule) rule = RuleKind.None; break;
                case '(': functionDepth++; break;
                case ')': if (functionDepth > 0) functionDepth--; break;
                case '[': bracketDepth++; break;
                case ']': if (bracketDepth > 0) bracketDepth--; break;
            }
        }

        bool TopLevel() => blockDepth == 0 && functionDepth == 0 && bracketDepth == 0;
        void StartQualifiedRule()
        {
            if (TopLevel() && rule == RuleKind.None) rule = RuleKind.QualifiedRule;
        }

        string ReadName()
        {
            var value = new StringBuilder();
            while (index < css.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsName(css[index])) AppendCurrent(value);
                else if (ValidEscape()) { index++; AppendEscape(value); }
                else break;
            }
            return value.ToString();
        }

        string? ReadUrl()
        {
            SkipWhitespace();
            if (index < css.Length && css[index] is '\'' or '"')
            {
                var quoted = ReadQuoted();
                while (true)
                {
                    SkipWhitespace();
                    if (!SkipComment()) break;
                }
                if (quoted is not null && (index == css.Length || css[index] == ')'))
                {
                    if (index < css.Length) index++;
                    return quoted;
                }
                // This function is not a proven literal URL. Do not rediscover
                // url(...) inside its invalid or unsupported arguments.
                SkipFunction();
                return null;
            }
            var value = new StringBuilder();
            while (index < css.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var c = css[index];
                if (c == ')') { index++; return value.ToString(); }
                if (IsWhitespace(c))
                {
                    SkipWhitespace();
                    if (index == css.Length) return value.ToString();
                    if (css[index] == ')') { index++; return value.ToString(); }
                    break;
                }
                if (c is '\'' or '"' or '(' || IsNonPrintable(c)) break;
                if (c == '\\')
                {
                    if (!ValidEscape()) break;
                    index++;
                    AppendEscape(value);
                }
                else AppendCurrent(value);
            }
            // CSS recovers a URL token at EOF, retaining its decoded value.
            if (index == css.Length) return value.ToString();
            while (index < css.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (css[index] == ')') { index++; break; }
                if (ValidEscape()) { index++; AppendEscape(null); }
                else index++;
            }
            return null;
        }

        string? ReadQuoted()
        {
            var quote = css[index++];
            var value = new StringBuilder();
            while (index < css.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (css[index] == quote) { index++; return value.ToString(); }
                if (IsNewline(css[index])) return null; // Bad string; leave newline for recovery.
                if (css[index] == '\\')
                {
                    index++;
                    if (index == css.Length) break; // String escape at EOF adds nothing.
                    if (IsNewline(css[index])) SkipNewline();
                    else AppendEscape(value);
                }
                else AppendCurrent(value);
            }
            return value.ToString(); // CSS string/function EOF recovery.
        }

        void AppendEscape(StringBuilder? value)
        {
            if (index == css.Length) { value?.Append('\uFFFD'); return; }
            if (char.IsAsciiHexDigit(css[index]))
            {
                var codePoint = 0;
                for (var count = 0; count < 6 && index < css.Length && char.IsAsciiHexDigit(css[index]); count++, index++)
                {
                    var c = css[index];
                    codePoint = codePoint * 16 + (c <= '9' ? c - '0' : (c | 32) - 'a' + 10);
                }
                if (index < css.Length && IsWhitespace(css[index]))
                {
                    if (IsNewline(css[index])) SkipNewline();
                    else index++;
                }
                if (codePoint == 0 || codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
                    value?.Append('\uFFFD');
                else value?.Append(char.ConvertFromUtf32(codePoint));
            }
            else AppendCurrent(value);
        }

        void AppendCurrent(StringBuilder? value)
        {
            var c = css[index++];
            if (c == '\0') value?.Append('\uFFFD');
            else if (char.IsHighSurrogate(c) && index < css.Length && char.IsLowSurrogate(css[index]))
            {
                value?.Append(c).Append(css[index]);
                index++;
            }
            else value?.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
        }

        bool SkipComment()
        {
            if (index + 1 >= css.Length || css[index] != '/' || css[index + 1] != '*') return false;
            index += 2;
            while (index + 1 < css.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (css[index] == '*' && css[index + 1] == '/') { index += 2; return true; }
                index++;
            }
            index = css.Length;
            return true;
        }

        void SkipFunction()
        {
            var depth = 1;
            while (index < css.Length && depth != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SkipComment()) continue;
                if (css[index] is '\'' or '"') { ReadQuoted(); continue; }
                if (ValidEscape()) { index++; AppendEscape(null); continue; }
                if (css[index] == '(') depth++;
                else if (css[index] == ')') depth--;
                index++;
            }
        }

        void SkipWhitespace()
        {
            while (index < css.Length && IsWhitespace(css[index]))
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
            }
        }

        void SkipNewline()
        {
            if (css[index++] == '\r' && index < css.Length && css[index] == '\n') index++;
        }

        bool ValidEscape() => index < css.Length && css[index] == '\\'
            && (index + 1 == css.Length || !IsNewline(css[index + 1]));
    }

    private static bool IsName(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '\0' || c >= 0x80;
    private enum RuleKind { None, AtRule, QualifiedRule }
    private static bool IsNewline(char c) => c is '\r' or '\n' or '\f';
    private static bool IsWhitespace(char c) => c is ' ' or '\t' || IsNewline(c);
    private static bool IsNonPrintable(char c) => c is >= '\u0001' and <= '\u0008' or '\u000B'
        or >= '\u000E' and <= '\u001F' or '\u007F';
}
