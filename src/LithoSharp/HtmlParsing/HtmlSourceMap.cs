using System.Text;
using LithoSharp.Quality;

namespace LithoSharp.HtmlParsing;

internal sealed record HtmlMappedSegment(int ValueStart, int ValueLength, HtmlSpan? Source, string? Reason = null);
internal sealed record HtmlRawSourcePiece(HtmlSpan Source, string Raw);

// Value partitions are contiguous; origin ranges need not be. Null is an explicit
// missing origin, never an invented offset or a containing-attribute substitute.
internal sealed class HtmlSourceMap
{
    private readonly HtmlRawSourcePiece[] orderedPieces;
    private readonly int[] pieceEndPrefix;
    internal HtmlSourceMap(string value, IEnumerable<HtmlMappedSegment> segments,
        IEnumerable<HtmlRawSourcePiece> pieces, HtmlFactsProjection projection, HtmlSpan? emptyOrigin = null,
        string? emptyReason = null)
    {
        Value = value;
        Segments = Array.AsReadOnly(segments.ToArray());
        RawPieces = Array.AsReadOnly(pieces.DistinctBy(piece => piece.Source).ToArray());
        orderedPieces = RawPieces.OrderBy(piece => piece.Source.Start).ToArray();
        pieceEndPrefix = new int[orderedPieces.Length];
        for (var i = 0; i < orderedPieces.Length; i++)
            pieceEndPrefix[i] = Math.Max(i == 0 ? 0 : pieceEndPrefix[i - 1], orderedPieces[i].Source.End);
        var covered = 0;
        foreach (var segment in Segments)
        {
            if (segment.ValueStart != covered || segment.ValueLength <= 0 || segment.ValueLength > value.Length - covered
                || segment.Source is { Start: < 0 } or { Length: <= 0 })
                throw new ArgumentException("Mapped value partitions are invalid.", nameof(segments));
            covered += segment.ValueLength;
        }
        if (covered != value.Length || RawPieces.Any(piece => piece.Source.Start < 0
            || piece.Source.Length != piece.Raw.Length)) throw new ArgumentException("Mapped source pieces are invalid.");
        EmptyOrigin = emptyOrigin;
        var reasons = Segments.Where(segment => segment.Source is null)
            .Select(segment => new HtmlFactsCoverageReason(segment.Reason ?? "source_mapping_unavailable", null))
            .Concat(emptyReason is null ? [] : [new HtmlFactsCoverageReason(emptyReason, null)]).Distinct().ToArray();
        Coverage = new(projection, reasons.Length == 0 ? HtmlFactsCoverageStatus.Complete : HtmlFactsCoverageStatus.Partial, reasons);
    }

    internal string Value { get; }
    internal IReadOnlyList<HtmlMappedSegment> Segments { get; }
    internal IReadOnlyList<HtmlRawSourcePiece> RawPieces { get; }
    internal HtmlFactsProjectionCoverage Coverage { get; }
    private HtmlSpan? EmptyOrigin { get; }
    internal HtmlSpan? Source => Segments.Any(segment => segment.Source is null) ? null : ContiguousSource();
    internal string? Raw => ContiguousSource() is null ? null : string.Concat(RawPieces.Select(piece => piece.Raw));
    internal long RetentionCharge => 96L + 2L * Value.Length + Segments.Count * 32L
        + RawPieces.Sum(piece => 32L + 2L * piece.Raw.Length);

    private HtmlSpan? ContiguousSource()
    {
        if (RawPieces.Count == 0) return EmptyOrigin;
        var start = RawPieces[0].Source.Start;
        var end = start;
        foreach (var piece in RawPieces)
        {
            if (piece.Source.Start != end) return null;
            end = piece.Source.End;
        }
        return new(start, end - start);
    }

    internal static HtmlSourceMap FromRaw(HtmlRawValueSnapshot value, HtmlFactsProjection projection) =>
        new(value.Decoded, value.Mapping.Select(s => new HtmlMappedSegment(s.ValueStart, s.ValueLength, s.Source)),
            [new(value.Source, value.Raw)], projection, value.Decoded.Length == 0 ? value.Source : null);

    internal static HtmlSourceMap Identity(string value, HtmlFactsProjection projection) => new(value,
        value.Length == 0 ? [] : [new(0, value.Length, new HtmlSpan(0, value.Length))],
        [new(new(0, value.Length), value)], projection, new(0, 0));

    internal static HtmlSourceMap Unknown(string value, HtmlFactsProjection projection, string reason) =>
        new(value, value.Length == 0 ? [] : [new(0, value.Length, null, reason)], [], projection,
            emptyReason: value.Length == 0 ? reason : null);

    internal void RequireComplete()
    {
        if (Coverage.Status != HtmlFactsCoverageStatus.Complete) throw new HtmlQualitySnapshotIncompleteException(Coverage);
    }

    internal HtmlSourceMap Slice(HtmlSpan range, HtmlFactsProjection? projection = null)
    {
        if (range.Start < 0 || range.Length < 0 || range.Start > Value.Length || range.Length > Value.Length - range.Start)
            throw new ArgumentOutOfRangeException(nameof(range));
        var mapped = new List<HtmlMappedSegment>();
        var pieces = new List<HtmlRawSourcePiece>();
        var first = 0;
        var last = Segments.Count;
        while (first < last)
        {
            var middle = first + (last - first) / 2;
            if (Segments[middle].ValueStart + Segments[middle].ValueLength <= range.Start) first = middle + 1;
            else last = middle;
        }
        for (var segmentIndex = first; segmentIndex < Segments.Count; segmentIndex++)
        {
            var segment = Segments[segmentIndex];
            if (segment.ValueStart >= range.End) break;
            var start = Math.Max(range.Start, segment.ValueStart);
            var end = Math.Min(range.End, segment.ValueStart + segment.ValueLength);
            if (end <= start) continue;
            HtmlSpan? source = segment.Source;
            var reason = segment.Reason;
            if (source is { } known && (start != segment.ValueStart || end != segment.ValueStart + segment.ValueLength))
            {
                if (known.Length == segment.ValueLength) source = new(known.Start + start - segment.ValueStart, end - start);
                else { source = null; reason = "partial_transformed_segment"; }
            }
            mapped.Add(new(start - range.Start, end - start, source, reason));
            if (source is { } span)
            {
                var aIndex = 0;
                var bIndex = pieceEndPrefix.Length;
                while (aIndex < bIndex)
                {
                    var middle = aIndex + (bIndex - aIndex) / 2;
                    if (pieceEndPrefix[middle] <= span.Start) aIndex = middle + 1;
                    else bIndex = middle;
                }
                for (var pieceIndex = aIndex; pieceIndex < orderedPieces.Length && orderedPieces[pieceIndex].Source.Start < span.End; pieceIndex++)
                {
                    var piece = orderedPieces[pieceIndex];
                    var a = Math.Max(span.Start, piece.Source.Start);
                    var b = Math.Min(span.End, piece.Source.End);
                    if (b > a) pieces.Add(new(new(a, b - a), piece.Raw.Substring(a - piece.Source.Start, b - a)));
                }
            }
        }
        HtmlSpan? point = null;
        if (range.Length == 0)
        {
            if (Value.Length == 0) point = EmptyOrigin;
            else foreach (var segment in Segments)
                if (range.Start >= segment.ValueStart && range.Start <= segment.ValueStart + segment.ValueLength && segment.Source is { } origin)
                {
                    if (origin.Length == segment.ValueLength) point = new(origin.Start + range.Start - segment.ValueStart, 0);
                    else if (range.Start == segment.ValueStart) point = new(origin.Start, 0);
                    else if (range.Start == segment.ValueStart + segment.ValueLength) point = new(origin.End, 0);
                    if (point is not null) break;
                }
        }
        return new(Value.Substring(range.Start, range.Length), mapped, pieces, projection ?? Coverage.Projection, point,
            range.Length == 0 && point is null && Coverage.Status == HtmlFactsCoverageStatus.Partial ? "source_point_unavailable" : null);
    }

    // Compose scanner offsets/mapping with this input's original source mapping.
    internal HtmlSourceMap Compose(string value, HtmlSpan lexeme, IReadOnlyList<HtmlTextSegment> decoder,
        HtmlFactsProjection projection)
    {
        var raw = Slice(lexeme, projection);
        var mapped = new List<HtmlMappedSegment>();
        foreach (var segment in decoder)
        {
            var part = Slice(segment.Source, projection);
            if (segment.ValueLength == segment.Source.Length)
                mapped.AddRange(part.Segments.Select(s => s with { ValueStart = segment.ValueStart + s.ValueStart }));
            else mapped.Add(new(segment.ValueStart, segment.ValueLength, part.Source,
                part.Source is null ? "source_composition_ambiguous" : null));
        }
        return new(value, mapped, raw.RawPieces, projection, value.Length == 0 ? raw.Source : null,
            value.Length == 0 && raw.Coverage.Status == HtmlFactsCoverageStatus.Partial ? "source_point_unavailable" : null);
    }

    internal HtmlSourceMap Normalize(bool asciiWhitespace, int? maximumLength = null)
    {
        var builder = new HtmlSourceMapBuilder(Coverage.Projection);
        var index = 0;
        while (index < Value.Length)
        {
            var start = index;
            var whitespace = IsWhitespace(Value[index]);
            while (index < Value.Length && IsWhitespace(Value[index]) == whitespace) index++;
            if (!whitespace) builder.Append(Slice(new(start, index - start)));
            else if (builder.Length > 0 && index < Value.Length)
            {
                var run = Slice(new(start, index - start));
                builder.AppendReplacement(" ", run, "normalized_separator_unmapped");
            }
        }
        var result = builder.Build();
        return maximumLength is { } limit && result.Value.Length > limit ? result.Slice(new(0, limit)) : result;
        bool IsWhitespace(char c) => asciiWhitespace ? c is ' ' or '\t' or '\r' or '\n' or '\f' : char.IsWhiteSpace(c);
    }
}

internal sealed class HtmlSourceMapBuilder(HtmlFactsProjection projection)
{
    private readonly StringBuilder value = new();
    private readonly List<HtmlMappedSegment> segments = [];
    private readonly List<HtmlRawSourcePiece> pieces = [];
    internal int Length => value.Length;
    internal void Append(HtmlSourceMap map)
    {
        var start = value.Length;
        value.Append(map.Value);
        segments.AddRange(map.Segments.Select(segment => segment with { ValueStart = start + segment.ValueStart }));
        pieces.AddRange(map.RawPieces);
    }
    internal void Append(string text, HtmlSpan? source, string? reason)
    {
        if (text.Length == 0) return;
        segments.Add(new(value.Length, text.Length, source, reason));
        value.Append(text);
    }
    internal void AppendReplacement(string text, HtmlSourceMap origin, string missingReason)
    {
        Append(text, origin.Source, origin.Source is null ? missingReason : null);
        pieces.AddRange(origin.RawPieces);
    }
    internal HtmlSourceMap Build() => new(value.ToString(), segments, pieces, projection);
}

internal sealed record HtmlHeadingMaterial(int ElementOrdinal, int Level, string? Anchor, HtmlSpan? OpeningTag,
    HtmlSourceMap Text, HtmlSourceMap SearchTitle);
internal sealed record HtmlSearchSectionMaterial(HtmlHeadingMaterial Heading, HtmlSourceMap Body);
internal sealed record HtmlSearchSourceSnapshot(HtmlSourceMap Body, HtmlSourceMap IndexText,
    HtmlSourceMap NormalizedIndex, IReadOnlyList<HtmlSearchSectionMaterial> Sections)
{
    internal HtmlFactsProjectionCoverage Coverage
    {
        get
        {
            var reasons = Body.Coverage.Reasons.Concat(IndexText.Coverage.Reasons)
                .Concat(NormalizedIndex.Coverage.Reasons)
                .Concat(Sections.SelectMany(section => section.Body.Coverage.Reasons
                    .Concat(section.Heading.Text.Coverage.Reasons)
                    .Concat(section.Heading.SearchTitle.Coverage.Reasons))).ToArray();
            return new(HtmlFactsProjection.Search,
                reasons.Length == 0 ? HtmlFactsCoverageStatus.Complete : HtmlFactsCoverageStatus.Partial, reasons);
        }
    }
}
internal sealed record HtmlTextMaterials(HtmlSourceMap Title, IReadOnlyList<HtmlHeadingMaterial> Headings,
    IReadOnlyDictionary<int, HtmlSourceMap> Styles, HtmlSearchSourceSnapshot? Search)
{
    internal long RetentionCharge => Title.RetentionCharge + Headings.Sum(h => h.Text.RetentionCharge + h.SearchTitle.RetentionCharge)
        + Styles.Values.Sum(s => s.RetentionCharge) + (Search is null ? 0 : Search.Body.RetentionCharge
            + Search.IndexText.RetentionCharge + Search.NormalizedIndex.RetentionCharge + Search.Sections.Sum(s => s.Body.RetentionCharge));
}
