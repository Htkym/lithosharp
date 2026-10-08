#if NETSTANDARD2_0
namespace System;

// Internal compiler support for the parser's string slices and from-end indexes.
// These types are absent from the netstandard2.0 reference assemblies.
internal readonly struct Index
{
    private readonly int _value;

    public Index(int value, bool fromEnd = false)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        _value = fromEnd ? ~value : value;
    }

    public int Value => _value < 0 ? ~_value : _value;
    public bool IsFromEnd => _value < 0;
    public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
    public static Index Start => new(0);
    public static Index End => new(0, fromEnd: true);
    public static implicit operator Index(int value) => new(value);
}

internal readonly struct Range
{
    public Range(Index start, Index end) { Start = start; End = end; }
    public Index Start { get; }
    public Index End { get; }
    public static Range All => new(Index.Start, Index.End);
    public static Range StartAt(Index start) => new(start, Index.End);
    public static Range EndAt(Index end) => new(Index.Start, end);

    public (int Offset, int Length) GetOffsetAndLength(int length)
    {
        var start = Start.GetOffset(length);
        var end = End.GetOffset(length);
        if (length < 0 || (uint)end > (uint)length || (uint)start > (uint)end)
            throw new ArgumentOutOfRangeException(nameof(length));
        return (start, end - start);
    }
}
#endif
