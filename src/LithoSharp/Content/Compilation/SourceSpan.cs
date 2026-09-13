using LithoSharp.Diagnostics;

namespace LithoSharp.Content.Compilation;

/// <summary>
/// A half-open range over source text in UTF-16 code units.
/// <see cref="Start"/> is 0-based and <see cref="Length"/> counts
/// <see langword="char"/> values, so surrogate pairs occupy two units.
/// Human-facing line/column rendering keeps the existing 1-based contract
/// via <see cref="SourceText"/>.
/// </summary>
internal readonly record struct SourceSpan
{
    /// <summary>Creates a span.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> or <paramref name="length"/> is negative.</exception>
    public SourceSpan(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Start = start;
        Length = length;
    }

    /// <summary>An empty span.</summary>
    public static SourceSpan Empty { get; } = new(0, 0);

    /// <summary>0-based start offset in UTF-16 code units.</summary>
    public int Start { get; init; }

    /// <summary>Covered length in UTF-16 code units.</summary>
    public int Length { get; init; }

    /// <summary>Creates a span from a Markdig-style inclusive start/end pair.</summary>
    public static SourceSpan FromInclusiveStartEnd(int start, int end) =>
        end < start ? Empty : new SourceSpan(start, end - start + 1);

    /// <summary>One past the last covered unit.</summary>
    public int End => Start + Length;

    /// <summary>Whether the span covers no units.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Whether the offset falls inside the half-open range.</summary>
    public bool Contains(int offset) => offset >= Start && offset < End;

    /// <summary>Shifts the span by the given offset (for example, a body start offset).</summary>
    /// <remarks>Empty spans keep their zero length but move with the offset.</remarks>
    public SourceSpan Shift(int offset) => new(Start + offset, Length);

    /// <summary>Resolves the span to a 1-based file location.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> or <paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The span falls outside the source text.</exception>
    public SiteSourceLocation ToSourceLocation(string filePath, SourceText source)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(source);
        if (End > source.Text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "The span falls outside the source text.");
        }

        var (line, column) = source.GetLineAndColumn(Start);
        var (endLine, endColumn) = source.GetLineAndColumn(End);
        return new SiteSourceLocation(filePath, line, column, endLine, endColumn);
    }
}
