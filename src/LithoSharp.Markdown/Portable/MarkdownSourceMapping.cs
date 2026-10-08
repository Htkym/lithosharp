using System;
using System.Collections.Generic;
using System.Linq;

namespace LithoSharp.Content.Compilation;

internal static class MdSourceMapping
{
    internal static MdMappingResult Map(MdTextProjection text, MdRawRange query)
    {
        if (text.Text is null) return new MdMappingResult(MdFreeze.Empty<MdRawRange>(), MdMappingPrecision.Unknown);
        if (query.End > text.Text.Length) throw new ArgumentOutOfRangeException(nameof(query));
        if (query.Length == 0)
        {
            var boundaries = new HashSet<int>();
            var unknown = false;
            foreach (var s in text.SourceSegments)
            {
                if (query.Start < s.DecodedSpan.Start || query.Start > s.DecodedSpan.End) continue;
                if (s.Kind == MdSegmentKind.Unknown) { unknown = true; continue; }
                var raw = s.RawSpan!.Value;
                if (s.Kind == MdSegmentKind.Linear) boundaries.Add(raw.Start + query.Start - s.DecodedSpan.Start);
                else if (query.Start == s.DecodedSpan.Start) boundaries.Add(raw.Start);
                else if (query.Start == s.DecodedSpan.End) boundaries.Add(raw.End);
                else unknown = true;
            }
            return !unknown && boundaries.Count == 1
                ? new MdMappingResult(new[] { new MdRawRange(boundaries.Single(), 0) }, MdMappingPrecision.Exact)
                : new MdMappingResult(MdFreeze.Empty<MdRawRange>(), MdMappingPrecision.Unknown);
        }
        var fragments = new List<MdRawRange>();
        var hasUnknown = false; var atomic = false;
        foreach (var s in text.SourceSegments)
        {
            var from = Math.Max(s.DecodedSpan.Start, query.Start);
            var end = Math.Min(s.DecodedSpan.End, query.End);
            if (from >= end) continue;
            if (s.Kind == MdSegmentKind.Unknown) { hasUnknown = true; continue; }
            var raw = s.RawSpan!.Value;
            if (s.Kind == MdSegmentKind.Linear) raw = new MdRawRange(raw.Start + from - s.DecodedSpan.Start, end - from);
            else atomic = true;
            if (fragments.Count != 0 && fragments[fragments.Count - 1].End == raw.Start)
            {
                var previous = fragments[fragments.Count - 1];
                fragments[fragments.Count - 1] = new MdRawRange(previous.Start, raw.End - previous.Start);
            }
            else fragments.Add(raw);
        }
        var precision = fragments.Count == 0 ? MdMappingPrecision.Unknown
            : hasUnknown ? MdMappingPrecision.Partial : atomic ? MdMappingPrecision.CoveringTokens : MdMappingPrecision.Exact;
        return new MdMappingResult(fragments, precision);
    }

    internal static MdTextProjection Unknown(string? text) => new(text,
        text is null || text.Length == 0 ? MdFreeze.Empty<MdSourceSegment>()
        : new[] { new MdSourceSegment(new MdRawRange(0, text.Length), null, MdSegmentKind.Unknown) });

    internal static MdTextProjection Local(string decoded, string original, LithoLineMap map,
        int from, int length, bool atomic = false)
    {
        if (decoded.Length == 0) return new(decoded, MdFreeze.Empty<MdSourceSegment>());
        var result = new List<MdSourceSegment>();
        if (atomic)
        {
            if (length <= 0) return Unknown(decoded);
            // A token crossing a removed prefix has no single raw token range.
            var start = map.Global(from); var end = map.Global(from + length - 1) + 1;
            return end - start == length
                ? new(decoded, new[] { new MdSourceSegment(new MdRawRange(0, decoded.Length), new MdRawRange(start, length), MdSegmentKind.Atomic) })
                : Unknown(decoded);
        }
        if (decoded.Length != length) return Unknown(decoded);
        var decodedStart = 0;
        while (decodedStart < length)
        {
            var rawStart = map.Global(from + decodedStart);
            var end = decodedStart + 1;
            while (end < length && map.Global(from + end) == rawStart + end - decodedStart) end++;
            var run = end - decodedStart;
            result.Add(new MdSourceSegment(new MdRawRange(decodedStart, run), new MdRawRange(rawStart, run), MdSegmentKind.Linear));
            decodedStart = end;
        }
        return new(decoded, result);
    }

    internal static MdTextProjection Shift(MdTextProjection text, int rawOffset) => new(text.Text,
        text.SourceSegments.Select(s => new MdSourceSegment(s.DecodedSpan,
            s.RawSpan.HasValue ? new MdRawRange(checked(s.RawSpan.Value.Start + rawOffset), s.RawSpan.Value.Length) : null, s.Kind)).ToArray());

    internal static MdTextProjection Join(IEnumerable<MdTextProjection> parts)
    {
        var text = new System.Text.StringBuilder(); var segments = new List<MdSourceSegment>();
        foreach (var part in parts)
        {
            if (part.Text is null) throw new InvalidOperationException("Cannot concatenate an unprojected region.");
            foreach (var s in part.SourceSegments)
                segments.Add(new MdSourceSegment(new MdRawRange(text.Length + s.DecodedSpan.Start, s.DecodedSpan.Length), s.RawSpan, s.Kind));
            text.Append(part.Text);
        }
        return new(text.ToString(), segments);
    }
}

internal sealed record MdFenceSyntax(SourceSpan Opening, SourceSpan? Info, SourceSpan Content,
    SourceSpan? Closing, string? InfoText, MdTextProjection Text);
internal sealed record MdLinkSyntax(MdLinkKind Kind, string? ReferenceLabel, SourceSpan? Target);
