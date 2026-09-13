namespace LithoSharp.Content.Compilation;

/// <summary>
/// Source text with precomputed line starts for offset-to-line/column mapping.
/// Line breaks are LF, CRLF, or lone CR. Columns count UTF-16 code units and are 1-based.
/// The instance is owned by the analysis call or the document owner; it is never
/// stored in a global cache, so releasing the document releases the text.
/// </summary>
internal sealed class SourceText
{
    private readonly int[] _lineStarts;

    /// <summary>Creates source text and indexes its line starts.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public SourceText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                starts.Add(index + 1);
            }
            else if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        _lineStarts = [.. starts];
    }

    /// <summary>The full text.</summary>
    public string Text { get; }

    /// <summary>Number of lines (at least 1, even for empty text).</summary>
    public int LineCount => _lineStarts.Length;

    /// <summary>Resolves a 0-based offset to a 1-based line and column.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is outside the text.</exception>
    public (int Line, int Column) GetLineAndColumn(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > Text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var line = Array.BinarySearch(_lineStarts, offset);
        if (line < 0)
        {
            line = ~line - 1;
        }

        return (line + 1, offset - _lineStarts[line] + 1);
    }
}
