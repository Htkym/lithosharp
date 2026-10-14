using ParsedHtmlText = global::LithoSharp.HtmlParsing.HtmlText;

namespace LithoSharp.HtmlParsing;

internal enum HtmlFactsCoverageStatus { Complete, Partial, Failed, NotRequested }
internal enum HtmlFactsProjection { RawAttributes, Metadata, Anchors, References, InlineCss, Search, Headings }
internal sealed record HtmlFactsCoverageReason(string Code, HtmlSpan? Source);

internal sealed class HtmlFactsProjectionCoverage
{
    internal HtmlFactsProjectionCoverage(HtmlFactsProjection projection, HtmlFactsCoverageStatus status,
        IEnumerable<HtmlFactsCoverageReason> reasons)
    {
        Projection = projection;
        Status = status;
        Reasons = Array.AsReadOnly(reasons.ToArray());
    }

    internal HtmlFactsProjection Projection { get; }
    internal HtmlFactsCoverageStatus Status { get; }
    internal IReadOnlyList<HtmlFactsCoverageReason> Reasons { get; }
}

internal sealed class HtmlRawValueSnapshot
{
    private HtmlRawValueSnapshot(string raw, ParsedHtmlText decoded, HtmlTextSegment[] mapping)
    {
        Raw = raw;
        Decoded = decoded.Value;
        Source = decoded.Source;
        Mapping = Array.AsReadOnly(mapping);
    }

    internal string Raw { get; }
    internal string Decoded { get; }
    internal HtmlSpan Source { get; }
    internal IReadOnlyList<HtmlTextSegment> Mapping { get; }

    internal static bool TryCapture(string source, ParsedHtmlText text, CancellationToken token,
        out HtmlRawValueSnapshot? snapshot, out HtmlFactsCoverageReason? reason)
    {
        snapshot = null;
        reason = null;
        token.ThrowIfCancellationRequested();
        var span = text.Source;
        if (span.Start < 0 || span.Length < 0 || span.Start > source.Length
            || span.Length > source.Length - span.Start)
        {
            reason = new("raw_attribute_source_unavailable", null);
            return false;
        }

        // Validate and retain the same defensive copy, not the caller's mutable mapping.
        var mapping = text.Segments.ToArray();
        string? invalid = null;
        if (text.Value.Length == 0)
        {
            // HtmlTextBuilder.Build uses (emptyOffset, 0) and no segments for empty/boolean values.
            if (span.Length != 0 || mapping.Length != 0) invalid = "raw_attribute_empty_mapping_invalid";
        }
        else if (mapping.Length == 0) invalid = "raw_attribute_mapping_missing";
        else
        {
            var covered = 0;
            var lastSourceEnd = span.Start;
            foreach (var segment in mapping)
            {
                token.ThrowIfCancellationRequested();
                if (segment.Source.Start < span.Start || segment.Source.Length <= 0
                    || segment.Source.Start > span.End || segment.Source.Length > span.End - segment.Source.Start)
                {
                    invalid = "raw_attribute_segment_outside_attribute_source";
                    break;
                }
                if (segment.ValueStart != covered || segment.ValueLength <= 0
                    || segment.ValueLength > text.Value.Length - covered)
                {
                    invalid = "raw_attribute_decoded_mapping_incomplete";
                    break;
                }
                // HtmlTextBuilder does not require adjacent or disjoint source segments.
                covered += segment.ValueLength;
                lastSourceEnd = segment.Source.End;
            }
            if (invalid is null && covered != text.Value.Length) invalid = "raw_attribute_decoded_mapping_incomplete";
            if (invalid is null && (mapping[0].Source.Start != span.Start || lastSourceEnd != span.End))
                invalid = "raw_attribute_source_mapping_bounds_invalid";
        }
        if (invalid is not null)
        {
            reason = new(invalid, span);
            return false;
        }
        token.ThrowIfCancellationRequested();
        snapshot = new(source.Substring(span.Start, span.Length), text, mapping);
        return true;
    }
}

internal sealed record HtmlRawAttributeSnapshot(string Name, string? Namespace, string? Prefix,
    HtmlRawValueSnapshot RawName, HtmlRawValueSnapshot Value);

internal sealed class HtmlRawElementSnapshot
{
    internal HtmlRawElementSnapshot(int ordinal, string name, string elementNamespace, HtmlSpan? source,
        IEnumerable<HtmlRawAttributeSnapshot> attributes)
    {
        Ordinal = ordinal;
        Name = name;
        Namespace = elementNamespace;
        Source = source;
        Attributes = Array.AsReadOnly(attributes.ToArray());
    }

    internal int Ordinal { get; }
    internal string Name { get; }
    internal string Namespace { get; }
    internal HtmlSpan? Source { get; }
    internal IReadOnlyList<HtmlRawAttributeSnapshot> Attributes { get; }
}

// Immutable material snapshot. Source/tree capture belongs to the private source-only factory.
internal sealed class HtmlRawAttributeFacts
{
    internal HtmlRawAttributeFacts(IEnumerable<HtmlRawElementSnapshot> elements,
        IEnumerable<HtmlFactsProjectionCoverage> coverage, HtmlTextMaterials? materials = null)
    {
        Elements = Array.AsReadOnly(elements.ToArray());
        Coverage = Array.AsReadOnly(coverage.ToArray());
        Materials = materials;
    }

    internal IReadOnlyList<HtmlRawElementSnapshot> Elements { get; }
    internal IReadOnlyList<HtmlFactsProjectionCoverage> Coverage { get; }
    internal HtmlTextMaterials? Materials { get; }
    internal HtmlRawAttributeFacts WithMaterials(HtmlTextMaterials materials) => new(Elements, Coverage.Select(coverage =>
    {
        IEnumerable<HtmlFactsCoverageReason>? reasons = coverage.Projection switch
        {
            HtmlFactsProjection.Metadata => materials.Title.Coverage.Reasons,
            HtmlFactsProjection.Headings => materials.Headings.SelectMany(h => h.Text.Coverage.Reasons.Concat(h.SearchTitle.Coverage.Reasons)),
            HtmlFactsProjection.InlineCss => materials.Styles.Values.SelectMany(style => style.Coverage.Reasons),
            HtmlFactsProjection.Search when materials.Search is { } search => search.Coverage.Reasons,
            _ => null,
        };
        if (reasons is null) return coverage;
        var errors = reasons.ToArray();
        return new HtmlFactsProjectionCoverage(coverage.Projection,
            errors.Length == 0 ? HtmlFactsCoverageStatus.Complete : HtmlFactsCoverageStatus.Partial, errors);
    }), materials);
    internal HtmlRawAttributeFacts WithoutSearchMaterials() => Materials is null ? this
        : new(Elements, Coverage.Select(coverage => coverage.Projection == HtmlFactsProjection.Search
            ? new HtmlFactsProjectionCoverage(HtmlFactsProjection.Search, HtmlFactsCoverageStatus.NotRequested, []) : coverage),
            Materials with { Search = null });

    internal void RequireComplete()
    {
        var coverage = Coverage.Single(c => c.Projection == HtmlFactsProjection.RawAttributes);
        if (coverage.Status != HtmlFactsCoverageStatus.Complete)
            throw new HtmlRawFactsIncompleteException(coverage);
    }

    // Diagnostic retention accounting, not a quota or a measured heap size.
    internal long RetentionCharge => 128L + (Materials?.RetentionCharge ?? 0) + Elements.Sum(element => 96L + 2L * (element.Name.Length + element.Namespace.Length)
        + element.Attributes.Sum(attribute => 96L + 2L * (attribute.Name.Length + (long)(attribute.Namespace?.Length ?? 0)
            + (attribute.Prefix?.Length ?? 0) + attribute.RawName.Raw.Length + attribute.RawName.Decoded.Length
            + attribute.Value.Raw.Length + attribute.Value.Decoded.Length)
            + 24L * (attribute.RawName.Mapping.Count + attribute.Value.Mapping.Count)));
}

internal sealed class HtmlRawFactsIncompleteException(HtmlFactsProjectionCoverage coverage)
    : InvalidOperationException("Required raw HTML attributes are incomplete: " + string.Join(", ", coverage.Reasons.Select(r => r.Code)))
{
    internal HtmlFactsCoverageStatus Status { get; } = coverage.Status;
}
