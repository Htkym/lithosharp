using System;

namespace LithoSharp.Content.Compilation;

/// <summary>Finds opaque token envelopes only at positions reached by the Markdown inline scanner.</summary>
internal static class MdOpaqueSyntax
{
    internal static bool TryTag(string text, int start, MdParseContext context, out int end)
    {
        end = start;
        if (At(text, start, context) != '<') return false;
        if (Match(text, start, "<!--", context)) return Through(text, start + 4, "-->", context, out end);
        if (Match(text, start, "<![CDATA[", context)) return Through(text, start + 9, "]]>", context, out end);
        if (Match(text, start, "<?", context)) return Through(text, start + 2, "?>", context, out end);
        var cursor = start + 1;
        var closing = At(text, cursor, context) == '/';
        if (closing) cursor++;
        if (At(text, cursor, context) == '>') { end = cursor + 1; return true; } // JSX fragment
        if (At(text, cursor, context) == '!')
        {
            cursor++;
            if (!Letter(At(text, cursor, context))) return false;
            while (Letter(At(text, cursor, context))) cursor++;
            return Through(text, cursor, ">", context, out end);
        }
        if (!Letter(At(text, cursor, context))) return false;
        cursor++;
        while (NamePart(At(text, cursor, context))) cursor++;
        while (cursor < text.Length)
        {
            var separator = false;
            while (White(At(text, cursor, context))) { cursor++; separator = true; }
            var current = At(text, cursor, context);
            if (current == '>') { end = cursor + 1; return true; }
            if (!closing && current == '/' && At(text, cursor + 1, context) == '>') { end = cursor + 2; return true; }
            if (closing || !separator) return false;
            if (current == '{')
            {
                if (!Expression(text, ref cursor, context)) return false; // JSX spread/expression attribute
                continue;
            }
            if (!AttributeStart(current)) return false;
            cursor++;
            while (NamePart(At(text, cursor, context))) cursor++;
            var afterName = cursor;
            while (White(At(text, cursor, context))) cursor++;
            if (At(text, cursor, context) != '=') { cursor = afterName; continue; } // boolean attribute
            cursor++;
            while (White(At(text, cursor, context))) cursor++;
            current = At(text, cursor, context);
            if (current is '"' or '\'')
            {
                if (!Quoted(text, ref cursor, current, context, escaped: false)) return false;
            }
            else if (current == '{')
            {
                if (!Expression(text, ref cursor, context)) return false;
            }
            else
            {
                var valueStart = cursor;
                while (cursor < text.Length)
                {
                    current = At(text, cursor, context);
                    if (White(current) || current == '>') break;
                    if (current is '<' or '"' or '\'' or '=' || current == (char)96) return false;
                    cursor++;
                }
                if (cursor == valueStart) return false;
            }
        }
        return false;
    }

    internal static bool TryEsm(string text, MdParseContext context, out int end)
    {
        end = 0; var cursor = 0;
        while (White(At(text, cursor, context))) cursor++;
        var import = Match(text, cursor, "import ", context);
        var export = Match(text, cursor, "export ", context);
        if (!import && !export) return false;
        cursor += 7;
        while (White(At(text, cursor, context))) cursor++;
        if (!EsmStart(text, cursor, import, context)) return false;
        while (cursor < text.Length)
        {
            var current = At(text, cursor, context);
            if (current is '\r' or '\n') { end = cursor; return true; }
            if (current == ';') { end = cursor + 1; return true; }
            if (current is '"' or '\'')
            {
                if (!Quoted(text, ref cursor, current, context, escaped: true)) return false;
            }
            else if (current == '{')
            {
                if (!Expression(text, ref cursor, context)) return false;
            }
            else cursor++;
        }
        end = cursor; return true;
    }

    private static bool EsmStart(string text, int cursor, bool import, MdParseContext context)
    {
        var first = At(text, cursor, context);
        if (first is '{' or '*') return true;
        if (import && (first is '"' or '\'')) return true; // side-effect import
        if (!(Letter(first) || first is '_' or '$')) return false;
        var start = cursor++;
        while (Letter(At(text, cursor, context)) || At(text, cursor, context) is >= '0' and <= '9' or '_' or '$') cursor++;
        if (!import)
            return Word("const") || Word("let") || Word("var") || Word("function") || Word("class") || Word("default") || Word("async");
        var end = cursor;
        while (White(At(text, cursor, context))) cursor++;
        if (At(text, cursor, context) == ',') return true;
        if (cursor == end || !Match(text, cursor, "from", context) || !White(At(text, cursor + 4, context))) return false;
        cursor += 4;
        while (White(At(text, cursor, context))) cursor++;
        return At(text, cursor, context) is '"' or '\'';

        bool Word(string value)
        {
            context.Scan(value.Length);
            return cursor - start == value.Length && string.CompareOrdinal(text, start, value, 0, value.Length) == 0;
        }
    }

    private static bool Expression(string text, ref int cursor, MdParseContext context)
    {
        var depth = 0;
        while (cursor < text.Length)
        {
            var current = At(text, cursor, context);
            if (current == '{') { context.Depth(++depth); cursor++; }
            else if (current == '}') { cursor++; if (--depth == 0) return true; }
            else if (current is '"' or '\'' || current == (char)96)
            {
                if (!Quoted(text, ref cursor, current, context, escaped: true)) return false;
            }
            else cursor++;
        }
        return false;
    }

    private static bool Quoted(string text, ref int cursor, char quote, MdParseContext context, bool escaped)
    {
        cursor++;
        while (cursor < text.Length)
        {
            var current = At(text, cursor++, context);
            if (current == quote) return true;
            if (escaped && current == '\\' && cursor < text.Length) { context.Scan(); cursor++; }
        }
        return false;
    }
    private static bool Through(string text, int from, string delimiter, MdParseContext context, out int end)
    {
        while (from < text.Length)
        {
            if (Match(text, from, delimiter, context)) { end = from + delimiter.Length; return true; }
            from++;
        }
        end = from; return false;
    }
    private static bool Match(string text, int from, string value, MdParseContext context)
    {
        context.Scan(value.Length);
        return from + value.Length <= text.Length && string.CompareOrdinal(text, from, value, 0, value.Length) == 0;
    }
    private static char At(string text, int index, MdParseContext context)
    {
        context.Scan(); return index < text.Length ? text[index] : '\0';
    }
    private static bool Letter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    private static bool NamePart(char value) => Letter(value) || value is >= '0' and <= '9' or '_' or ':' or '-' or '.';
    private static bool AttributeStart(char value) => Letter(value) || value is '_' or ':';
    private static bool White(char value) => value is ' ' or '\t' or '\r' or '\n';
}
