using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers;

// Mapping succeeds only when decoded characters exactly match Roslyn ValueText.
// No HTML offset is ever mapped by assuming that an escape/indent occupies one source unit.
internal sealed class CSharpLiteralMap
{
    private readonly List<TextSpan> characters = new();
    private readonly SyntaxToken token;
    private CSharpLiteralMap(SyntaxToken token) => this.token = token;

    internal static CSharpLiteralMap? Create(SyntaxToken token)
    {
        if (token.ContainsDiagnostics) return null;
        var map = new CSharpLiteralMap(token);
        var text = token.Text;
        bool success;
        if (token.IsKind(SyntaxKind.StringLiteralToken))
            success = text.StartsWith("@\"", StringComparison.Ordinal) ? map.Verbatim(text) : map.Normal(text);
        else if (token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken) || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken))
            success = map.Raw(text, token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken));
        else return null;
        return success && map.characters.Count == token.ValueText.Length ? map : null;
    }

    internal TextSpan? Map(int start, int length)
    {
        if (start < 0 || length < 0 || start > characters.Count || length > characters.Count - start) return null;
        if (length == 0)
            return new TextSpan(start < characters.Count ? characters[start].Start : characters.Count > 0
                ? characters[characters.Count - 1].End : token.SpanStart, 0);
        return TextSpan.FromBounds(characters[start].Start, characters[start + length - 1].End);
    }

    private bool Append(string value, int start, int length)
    {
        if (value.Length > token.ValueText.Length - characters.Count) return false;
        foreach (var c in value)
        {
            if (token.ValueText[characters.Count] != c) return false;
            characters.Add(new TextSpan(token.SpanStart + start, length));
        }
        return true;
    }

    private bool Normal(string text)
    {
        if (text.Length < 2 || text[0] != '"' || text[text.Length - 1] != '"') return false;
        for (var i = 1; i < text.Length - 1;)
        {
            var start = i;
            var c = text[i++];
            if (c != '\\') { if (!Append(c.ToString(), start, 1)) return false; continue; }
            if (i >= text.Length - 1) return false;
            var escape = text[i++];
            if (escape is 'x' or 'u' or 'U')
            {
                var max = escape == 'U' ? 8 : 4;
                var count = 0;
                uint scalar = 0;
                while (count < max && i < text.Length - 1 && Hex(text[i]) >= 0)
                { scalar = scalar * 16 + (uint)Hex(text[i++]); count++; }
                if (count == 0 || escape != 'x' && count != max || scalar > 0x10ffff) return false;
                var decoded = escape == 'U' && scalar > 0xffff ? char.ConvertFromUtf32((int)scalar) : ((char)scalar).ToString();
                if (!Append(decoded, start, i - start)) return false;
            }
            else
            {
                var decoded = escape switch
                {
                    '0' => '\0',
                    'a' => '\a',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'v' => '\v',
                    '\\' => '\\',
                    '\'' => '\'',
                    '"' => '"',
                    _ => (char)0xffff
                };
                if (decoded == 0xffff || !Append(decoded.ToString(), start, i - start)) return false;
            }
        }
        return true;
    }

    private bool Verbatim(string text)
    {
        for (var i = 2; i < text.Length - 1; i++)
        {
            if (text[i] == '"')
            { if (i + 1 >= text.Length - 1 || text[i + 1] != '"' || !Append("\"", i, 2)) return false; i++; }
            else if (!Append(text[i].ToString(), i, 1)) return false;
        }
        return true;
    }

    private bool Raw(string text, bool multiline)
    {
        var quotes = 0;
        while (quotes < text.Length && text[quotes] == '"') quotes++;
        if (quotes < 3 || text.Length < 2 * quotes) return false;
        var start = quotes;
        var end = text.Length - quotes;
        if (!multiline)
        {
            for (var i = start; i < end; i++) if (!Append(text[i].ToString(), i, 1)) return false;
            return true;
        }
        while (start < end && text[start] is not ('\r' or '\n')) start++;
        if (start == end) return false;
        start += text[start] == '\r' && start + 1 < end && text[start + 1] == '\n' ? 2 : 1;
        var closingLine = end;
        while (closingLine > start && text[closingLine - 1] is not ('\r' or '\n')) closingLine--;
        var indent = text.Substring(closingLine, end - closingLine);
        if (indent.IndexOfAny(new[] { '\r', '\n' }) >= 0) return false;
        end = closingLine;
        if (end > start && text[end - 1] == '\n') { end--; if (end > start && text[end - 1] == '\r') end--; }
        else if (end > start && text[end - 1] == '\r') end--;
        while (start < end)
        {
            var lineEnd = start;
            while (lineEnd < end && text[lineEnd] is not ('\r' or '\n')) lineEnd++;
            var stripped = 0;
            while (stripped < indent.Length && start + stripped < lineEnd && text[start + stripped] == indent[stripped]) stripped++;
            if (stripped != indent.Length && start + stripped != lineEnd) return false;
            for (var i = start + stripped; i < lineEnd; i++) if (!Append(text[i].ToString(), i, 1)) return false;
            start = lineEnd;
            if (start < end && text[start] == '\r') { if (!Append("\r", start++, 1)) return false; }
            if (start < end && text[start] == '\n') { if (!Append("\n", start++, 1)) return false; }
        }
        return true;
    }

    private static int Hex(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
}
