using System.Text;
using LithoSharp.HtmlParsing;

namespace LithoSharp.Quality;

// Extract literal CSS URLs and, for stylesheets, top-level @import strings.
// This observes references, not cascade ordering, conditions, layout or variables.
// CSS Syntax 3 token boundaries/escapes apply; comments inside an unquoted URL
// are URL characters, while comments and ordinary strings outside it are skipped.
internal static class CssUrlScanner
{
    // Optional compiler-host work budget; runtime callers retain the existing unlimited scan.
    internal sealed class Budget(int remaining)
    {
        internal int Remaining = remaining;
        internal void Step()
        {
            if (Remaining-- <= 0)
                throw new BudgetExceededException();
        }
    }
    private sealed class BudgetExceededException : Exception { }
    internal sealed record Candidate(string Value, HtmlSpan Source, IReadOnlyList<HtmlTextSegment> Mapping);
    internal static IEnumerable<string> Read(string css, CancellationToken cancellationToken, bool includeImports = false)
        => ReadCore(css, cancellationToken, includeImports, null, null).Select(candidate => candidate.Value);

    internal static (IReadOnlyList<string> Values, HtmlFactsProjectionCoverage Coverage) Collect(
        string css, CancellationToken cancellationToken, bool includeImports = false)
    {
        var collected = CollectCandidates(css, cancellationToken, includeImports);
        return (Array.AsReadOnly(collected.Candidates.Select(candidate => candidate.Value).ToArray()), collected.Coverage);
    }

    internal static (IReadOnlyList<Candidate> Candidates, HtmlFactsProjectionCoverage Coverage) CollectCandidates(
        string css, CancellationToken cancellationToken, bool includeImports = false, Budget? budget = null)
    {
        var reasons = new List<HtmlFactsCoverageReason>();
        Candidate[] values;
        try
        {
            values = ReadCore(css, cancellationToken, includeImports, reasons, budget).ToArray();
        }
        catch (BudgetExceededException)
        {
            // Never expose a successful prefix after the bounded scan was interrupted.
            values = Array.Empty<Candidate>();
            reasons.Add(new("css_work_budget", null));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (Array.AsReadOnly(values), new(HtmlFactsProjection.InlineCss,
            reasons.Count == 0 ? HtmlFactsCoverageStatus.Complete : HtmlFactsCoverageStatus.Partial, reasons));
    }

    private static IEnumerable<Candidate> ReadCore(string css, CancellationToken cancellationToken, bool includeImports,
        List<HtmlFactsCoverageReason>? reasons, Budget? budget)
    {
        var index = 0;
        var blockDepth = 0;
        var functionDepth = 0;
        var bracketDepth = 0;
        var rule = RuleKind.None;
        var pendingImport = false;
        while (index < css.Length)
        {
            Step();
            if (SkipComment())
                continue;
            if (IsWhitespace(css[index]))
            {
                index++;
                continue;
            }
            if (pendingImport && css[index] is '@' or '#')
            {
                reasons?.Add(new("css_import_unresolved", null));
                pendingImport = false;
            }
            if (css[index] is '\'' or '"')
            {
                StartQualifiedRule();
                ReadQuoted();
                continue;
            }
            if (css[index] == '@')
            {
                var startsRule = TopLevel() && rule == RuleKind.None;
                index++;
                var name = ReadName();
                if (startsRule)
                    rule = RuleKind.AtRule;
                if (includeImports && startsRule
                    && name.Equals("import", StringComparison.OrdinalIgnoreCase))
                {
                    do
                    {
                        SkipWhitespace();
                    } while (SkipComment());
                    if (index < css.Length && css[index] is '\'' or '"')
                    {
                        var value = ReadQuoted();
                        if (value is not null)
                            yield return value;
                        else
                            reasons?.Add(new("css_import_unresolved", null));
                    }
                    else
                        pendingImport = true;
                }
                continue;
            }
            if (css[index] == '#')
            {
                StartQualifiedRule();
                index++;
                ReadName();
                continue;
            }
            if (IsName(css[index]) || ValidEscape())
            {
                StartQualifiedRule();
                var name = ReadName();
                if (pendingImport)
                {
                    if (!name.Equals("url", StringComparison.OrdinalIgnoreCase) || index == css.Length || css[index] != '(')
                        reasons?.Add(new("css_import_unresolved", null));
                    pendingImport = false;
                }
                if (name.Equals("url", StringComparison.OrdinalIgnoreCase) && index < css.Length && css[index] == '(')
                {
                    index++;
                    var value = ReadUrl();
                    if (value is not null)
                        yield return value;
                    else
                        reasons?.Add(new("css_url_unresolved", null));
                }
                continue;
            }
            StartQualifiedRule();
            if (pendingImport)
            {
                reasons?.Add(new("css_import_unresolved", null));
                pendingImport = false;
            }
            switch (css[index++])
            {
                case '{':
                    blockDepth++;
                    break;
                case '}':
                    if (blockDepth > 0 && --blockDepth == 0 && TopLevel())
                        rule = RuleKind.None;
                    break;
                // A semicolon ends an at-rule, but remains part of a qualified-rule prelude.
                case ';':
                    if (TopLevel() && rule == RuleKind.AtRule)
                        rule = RuleKind.None;
                    break;
                case '(':
                    functionDepth++;
                    break;
                case ')':
                    if (functionDepth > 0)
                        functionDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
            }
        }

        if (pendingImport)
            reasons?.Add(new("css_import_unresolved", null));

        void Step()
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget?.Step();
        }

        bool TopLevel() => blockDepth == 0 && functionDepth == 0 && bracketDepth == 0;
        void StartQualifiedRule()
        {
            if (TopLevel() && rule == RuleKind.None)
                rule = RuleKind.QualifiedRule;
        }

        string ReadName()
        {
            var value = new StringBuilder();
            while (index < css.Length)
            {
                Step();
                if (IsName(css[index]))
                    AppendCurrent(value);
                else if (ValidEscape())
                {
                    index++;
                    AppendEscape(value);
                }
                else
                    break;
            }
            return value.ToString();
        }

        Candidate? ReadUrl()
        {
            SkipWhitespace();
            if (index < css.Length && css[index] is '\'' or '"')
            {
                var quoted = ReadQuoted();
                while (true)
                {
                    SkipWhitespace();
                    if (!SkipComment())
                        break;
                }
                if (quoted is not null && (index == css.Length || css[index] == ')'))
                {
                    if (index < css.Length)
                        index++;
                    return quoted;
                }
                // This function is not a proven literal URL. Do not rediscover
                // url(...) inside its invalid or unsupported arguments.
                SkipFunction();
                return null;
            }
            var start = index;
            var value = new StringBuilder();
            var mapping = new List<HtmlTextSegment>();
            while (index < css.Length)
            {
                Step();
                var c = css[index];
                if (c == ')')
                {
                    var end = index++;
                    return MakeCandidate(value, start, end, mapping);
                }
                if (IsWhitespace(c))
                {
                    var end = index;
                    SkipWhitespace();
                    if (index == css.Length)
                        return MakeCandidate(value, start, end, mapping);
                    if (css[index] == ')')
                    {
                        index++;
                        return MakeCandidate(value, start, end, mapping);
                    }
                    break;
                }
                if (c is '\'' or '"' or '(' || IsNonPrintable(c))
                    break;
                if (c == '\\')
                {
                    if (!ValidEscape())
                        break;
                    var escapeStart = index++;
                    AppendEscape(value, mapping, escapeStart);
                }
                else
                    AppendCurrent(value, mapping);
            }
            // CSS recovers a URL token at EOF, retaining its decoded value.
            if (index == css.Length)
                return MakeCandidate(value, start, index, mapping);
            while (index < css.Length)
            {
                Step();
                if (css[index] == ')')
                {
                    index++;
                    break;
                }
                if (ValidEscape())
                {
                    index++;
                    AppendEscape(null);
                }
                else
                    index++;
            }
            return null;
        }

        Candidate? ReadQuoted()
        {
            var start = index + 1;
            var quote = css[index++];
            var value = new StringBuilder();
            var mapping = new List<HtmlTextSegment>();
            while (index < css.Length)
            {
                Step();
                if (css[index] == quote)
                {
                    var end = index++;
                    return MakeCandidate(value, start, end, mapping);
                }
                if (IsNewline(css[index]))
                    return null; // Bad string; leave newline for recovery.
                if (css[index] == '\\')
                {
                    var escapeStart = index++;
                    if (index == css.Length)
                        break; // String escape at EOF adds nothing.
                    if (IsNewline(css[index]))
                        SkipNewline();
                    else
                        AppendEscape(value, mapping, escapeStart);
                }
                else
                    AppendCurrent(value, mapping);
            }
            return MakeCandidate(value, start, index, mapping); // CSS string/function EOF recovery.
        }

        void AppendEscape(StringBuilder? value, List<HtmlTextSegment>? mapping = null, int sourceStart = -1)
        {
            var before = value?.Length ?? 0;
            if (index == css.Length)
                value?.Append('\uFFFD');
            else if (IsHex(css[index]))
            {
                var codePoint = 0;
                for (var count = 0; count < 6 && index < css.Length && IsHex(css[index]); count++, index++)
                {
                    var c = css[index];
                    codePoint = codePoint * 16 + (c <= '9' ? c - '0' : (c | 32) - 'a' + 10);
                }
                if (index < css.Length && IsWhitespace(css[index]))
                {
                    if (IsNewline(css[index]))
                        SkipNewline();
                    else
                        index++;
                }
                if (codePoint == 0 || codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
                    value?.Append('\uFFFD');
                else
                    value?.Append(char.ConvertFromUtf32(codePoint));
            }
            else
                AppendCurrent(value);
            AddMapping(mapping, before, (value?.Length ?? 0) - before, new(sourceStart, index - sourceStart));
        }

        void AppendCurrent(StringBuilder? value, List<HtmlTextSegment>? mapping = null)
        {
            var start = index;
            var before = value?.Length ?? 0;
            var c = css[index++];
            if (c == '\0')
                value?.Append('\uFFFD');
            else if (char.IsHighSurrogate(c) && index < css.Length && char.IsLowSurrogate(css[index]))
            {
                value?.Append(c).Append(css[index]);
                index++;
            }
            else
                value?.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
            AddMapping(mapping, before, (value?.Length ?? 0) - before, new(start, index - start));
        }

        Candidate MakeCandidate(StringBuilder value, int start, int end, List<HtmlTextSegment> mapping) =>
            new(value.ToString(), new(start, end - start), Array.AsReadOnly(mapping.ToArray()));

        void AddMapping(List<HtmlTextSegment>? mapping, int start, int length, HtmlSpan source)
        {
            if (mapping is null || length == 0)
                return;
            if (mapping.Count > 0 && mapping[mapping.Count - 1] is { } previous && previous.ValueLength == previous.Source.Length
                && length == source.Length && previous.Source.End == source.Start)
                mapping[mapping.Count - 1] = previous with
                {
                    ValueLength = previous.ValueLength + length,
                    Source = new(previous.Source.Start, previous.Source.Length + source.Length)
                };
            else
                mapping.Add(new(start, length, source));
        }

        bool SkipComment()
        {
            if (index + 1 >= css.Length || css[index] != '/' || css[index + 1] != '*')
                return false;
            index += 2;
            while (index + 1 < css.Length)
            {
                Step();
                if (css[index] == '*' && css[index + 1] == '/')
                {
                    index += 2;
                    return true;
                }
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
                Step();
                if (SkipComment())
                    continue;
                if (css[index] is '\'' or '"')
                {
                    ReadQuoted();
                    continue;
                }
                if (ValidEscape())
                {
                    index++;
                    AppendEscape(null);
                    continue;
                }
                if (css[index] == '(')
                    depth++;
                else if (css[index] == ')')
                    depth--;
                index++;
            }
        }

        void SkipWhitespace()
        {
            while (index < css.Length && IsWhitespace(css[index]))
            {
                Step();
                index++;
            }
        }

        void SkipNewline()
        {
            if (css[index++] == '\r' && index < css.Length && css[index] == '\n')
                index++;
        }

        bool ValidEscape() => index < css.Length && css[index] == '\\'
            && (index + 1 == css.Length || !IsNewline(css[index + 1]));
    }

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    private static bool IsName(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '\0' || c >= 0x80;
    private enum RuleKind { None, AtRule, QualifiedRule }
    private static bool IsNewline(char c) => c is '\r' or '\n' or '\f';
    private static bool IsWhitespace(char c) => c is ' ' or '\t' || IsNewline(c);
    private static bool IsNonPrintable(char c) => c is >= '\u0001' and <= '\u0008' or '\u000B'
        or >= '\u000E' and <= '\u001F' or '\u007F';
}
