namespace LithoSharp.HtmlParsing;

[Flags]
internal enum HtmlReferenceErrors
{
    None = 0, MissingSemicolon = 1, UnknownName = 2, MissingDigits = 4, Null = 8,
    OutOfRange = 16, Surrogate = 32, Noncharacter = 64, Control = 128
}

internal readonly record struct HtmlCharacterReference(string Value, int RawLength, HtmlReferenceErrors Errors);

internal static partial class HtmlCharacterReferences
{
    private static readonly Dictionary<string, string> Named = ReadTable();

    private static Dictionary<string, string> ReadTable()
    {
        var result = new Dictionary<string, string>(2231, StringComparer.Ordinal);
        foreach (var row in EncodedEntries.Split('\n'))
        {
            var fields = row.TrimEnd('\r').Split('=');
            var value = string.Concat(fields[1].Split(',').Select(hex => char.ConvertFromUtf32(Convert.ToInt32(hex, 16))));
            result.Add(fields[0], value);
        }
        return result;
    }

    // index points to '&'. Only the longest table match is consumed.
    public static HtmlCharacterReference Read(string source, int index, bool attribute, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cursor = index + 1;
        if (cursor == source.Length) return new("&", 1, HtmlReferenceErrors.None);
        if (source[cursor] == '#') return ReadNumber(source, index, cancellationToken);
        if (!IsAsciiAlphanumeric(source[cursor])) return new("&", 1, HtmlReferenceErrors.None);

        string? matched = null;
        var matchedEnd = cursor;
        for (var end = cursor; end < source.Length && end - cursor < LongestName; end++)
        {
            var c = source[end];
            if (!IsAsciiAlphanumeric(c) && c != ';') break;
            if (Named.TryGetValue(source.Substring(cursor, end - cursor + 1), out var text))
            {
                matched = text;
                matchedEnd = end + 1;
            }
            if (c == ';') break;
        }
        if (matched is not null)
        {
            var terminated = source[matchedEnd - 1] == ';';
            if (!terminated && attribute && matchedEnd < source.Length &&
                (source[matchedEnd] == '=' || IsAsciiAlphanumeric(source[matchedEnd])))
                return new("&", 1, HtmlReferenceErrors.None);
            return new(matched, matchedEnd - index,
                terminated ? HtmlReferenceErrors.None : HtmlReferenceErrors.MissingSemicolon);
        }

        // An unknown name is literal text. Its final ';' still has a parse error.
        while (cursor < source.Length && IsAsciiAlphanumeric(source[cursor]))
        {
            if ((cursor & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            cursor++;
        }
        return new("&", 1, cursor < source.Length && source[cursor] == ';'
            ? HtmlReferenceErrors.UnknownName : HtmlReferenceErrors.None);
    }

    private static HtmlCharacterReference ReadNumber(string source, int start, CancellationToken cancellationToken)
    {
        var cursor = start + 2;
        var hex = cursor < source.Length && source[cursor] is 'x' or 'X';
        if (hex) cursor++;
        var digitStart = cursor;
        var number = 0;
        while (cursor < source.Length)
        {
            if ((cursor & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var digit = Digit(source[cursor], hex);
            if (digit < 0) break;
            // Saturate before overflow; continue consuming all digits.
            number = Math.Min(0x110000, number * (hex ? 16 : 10) + digit);
            cursor++;
        }
        if (cursor == digitStart)
            return new(source.Substring(start, cursor - start), cursor - start, HtmlReferenceErrors.MissingDigits);
        var errors = HtmlReferenceErrors.None;
        if (cursor < source.Length && source[cursor] == ';') cursor++;
        else errors |= HtmlReferenceErrors.MissingSemicolon;

        if (number == 0) { errors |= HtmlReferenceErrors.Null; number = 0xFFFD; }
        else if (number > 0x10FFFF) { errors |= HtmlReferenceErrors.OutOfRange; number = 0xFFFD; }
        else if (number is >= 0xD800 and <= 0xDFFF) { errors |= HtmlReferenceErrors.Surrogate; number = 0xFFFD; }
        else
        {
            if (IsNoncharacter(number)) errors |= HtmlReferenceErrors.Noncharacter;
            if (IsControl(number) || number == 13)
            {
                errors |= HtmlReferenceErrors.Control;
                number = number switch
                {
                    0x80 => 0x20AC, 0x82 => 0x201A, 0x83 => 0x0192, 0x84 => 0x201E,
                    0x85 => 0x2026, 0x86 => 0x2020, 0x87 => 0x2021, 0x88 => 0x02C6,
                    0x89 => 0x2030, 0x8A => 0x0160, 0x8B => 0x2039, 0x8C => 0x0152,
                    0x8E => 0x017D, 0x91 => 0x2018, 0x92 => 0x2019, 0x93 => 0x201C,
                    0x94 => 0x201D, 0x95 => 0x2022, 0x96 => 0x2013, 0x97 => 0x2014,
                    0x98 => 0x02DC, 0x99 => 0x2122, 0x9A => 0x0161, 0x9B => 0x203A,
                    0x9C => 0x0153, 0x9E => 0x017E, 0x9F => 0x0178, _ => number
                };
            }
        }
        return new(char.ConvertFromUtf32(number), cursor - start, errors);
    }

    private static int Digit(char c, bool hex) => c is >= '0' and <= '9' ? c - '0' :
        hex && c is >= 'a' and <= 'f' ? c - 'a' + 10 :
        hex && c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;

    internal static bool IsAsciiAlphanumeric(int c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    internal static bool IsNoncharacter(int c) => c is >= 0xFDD0 and <= 0xFDEF ||
        (c & 0xFFFF) is 0xFFFE or 0xFFFF;

    internal static bool IsControl(int c) => c is >= 1 and <= 8 or 11 or >= 14 and <= 31 or >= 0x7F and <= 0x9F;
}
