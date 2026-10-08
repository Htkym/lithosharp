using System;
using System.Threading;

namespace LithoSharp.Content.Compilation;

/// <summary>Delimiter facts shared by strict optional parsing and the site compatibility adapter.</summary>
internal readonly record struct MdFrontMatterEnvelope(
    MdRawRange? Opening, MdRawRange? Yaml, MdRawRange? Closing, MdRawRange? Body, int FailureLine);

internal static class MdFrontMatterScan
{
    internal static MdFrontMatterEnvelope Scan(string raw, CancellationToken cancellationToken, MdParseContext? context = null)
    {
        if (raw is null) throw new ArgumentNullException(nameof(raw));
        cancellationToken.ThrowIfCancellationRequested();
        var first = raw.Length != 0 && raw[0] == '\uFEFF' ? 1 : 0;
        var openingEnd = LineEnd(first);
        if (!Marker(first, openingEnd))
            return new MdFrontMatterEnvelope(null, null, null, new MdRawRange(first, raw.Length - first), 1);
        var opening = new MdRawRange(first, 3);
        var yamlStart = AfterBreak(openingEnd);
        var lineStart = yamlStart; var line = 2;
        while (lineStart < raw.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = LineEnd(lineStart);
            if (Marker(lineStart, end))
            {
                var bodyStart = AfterBreak(end);
                return new MdFrontMatterEnvelope(opening, new MdRawRange(yamlStart, lineStart - yamlStart),
                    new MdRawRange(lineStart, 3), new MdRawRange(bodyStart, raw.Length - bodyStart), 1);
            }
            lineStart = AfterBreak(end); line++;
        }
        return new MdFrontMatterEnvelope(opening, new MdRawRange(yamlStart, raw.Length - yamlStart), null, null, line);

        int LineEnd(int cursor)
        {
            while (cursor < raw.Length)
            {
                if (context is not null) context.Scan();
                else if ((cursor & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (raw[cursor] is '\r' or '\n') break;
                cursor++;
            }
            return cursor;
        }
        bool Marker(int start, int end)
        {
            if (end - start != 3) return false;
            context?.Scan(3);
            return string.CompareOrdinal(raw, start, "---", 0, 3) == 0;
        }
        int AfterBreak(int end)
        {
            if (end >= raw.Length) return end;
            context?.Scan();
            if (raw[end] != '\r' || end + 1 >= raw.Length) return end + 1;
            context?.Scan();
            return end + (raw[end + 1] == '\n' ? 2 : 1);
        }
    }
}
