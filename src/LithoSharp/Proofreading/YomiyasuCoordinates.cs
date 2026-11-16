using System.Buffers;
using System.Text;
using Syntamark;

namespace LithoSharp.Proofreading;

// Normalization matches Python input decoding while retaining original UTF-16 boundaries.
internal sealed class YomiyasuCoordinates
{
    private readonly int[] boundaries;
    private readonly int[] lineStarts;
    private readonly int[] lineEnds;

    internal YomiyasuCoordinates(string raw)
    {
        var position = raw.StartsWith('\uFEFF') ? 1 : 0;
        var points = new List<int> { position };
        var starts = new List<int> { 0 };
        var ends = new List<int>();
        var normalized = new StringBuilder();
        while (position < raw.Length)
        {
            bool newline;
            if (raw[position] == '\r')
            {
                position += position + 1 < raw.Length && raw[position + 1] == '\n' ? 2 : 1;
                normalized.Append('\n');
                newline = true;
            }
            else
            {
                if (Rune.DecodeFromUtf16(raw.AsSpan(position), out var rune, out var width) != OperationStatus.Done)
                    throw new ArgumentException("Proofreading requires well-formed Unicode.", nameof(raw));
                normalized.Append(rune.ToString());
                position += width;
                newline = rune.Value == '\n';
            }
            if (newline)
            {
                ends.Add(points.Count - 1);
                starts.Add(points.Count);
            }
            points.Add(position);
        }
        Text = normalized.ToString();
        boundaries = points.ToArray();
        ends.Add(points.Count - 1);
        lineStarts = starts.ToArray();
        lineEnds = ends.ToArray();
    }

    internal string Text { get; }
    internal int LineCount => lineStarts.Length;

    internal RawSpan ToRaw(int codePointStart, int codePointLength)
    {
        if (codePointStart < 0 || codePointLength < 0 || (long)codePointStart + codePointLength >= boundaries.Length)
            throw new ArgumentOutOfRangeException(nameof(codePointStart));
        var start = boundaries[codePointStart];
        return new RawSpan(start, boundaries[codePointStart + codePointLength] - start);
    }

    internal RawSpan ToRawLine(int line)
    {
        if (line < 1 || line > lineStarts.Length)
            throw new ArgumentOutOfRangeException(nameof(line));
        return ToRaw(lineStarts[line - 1], lineEnds[line - 1] - lineStarts[line - 1]);
    }
}
