using System.Text.RegularExpressions;
using LithoSharp.Content;
using Syntamark;

namespace LithoSharp.Proofreading;

/// <summary>One excluded source range and the editorial policy responsible for excluding it.</summary>
public sealed class ProofreadingExclusion
{
    internal ProofreadingExclusion(string reason, RawSpan? range)
    {
        Reason = reason;
        Range = range;
    }
    public string Reason { get; }
    public RawSpan? Range { get; }
}

/// <summary>Decoded prose from one native projection. Gaps around excluded content remain separate segments.</summary>
public sealed class ProofreadingSegment
{
    private readonly MarkdownTextProjection projection;
    private readonly RawSpan decoded;
    private readonly SourceMap bodyMap;

    internal ProofreadingSegment(string kind, MarkdownTextProjection projection, RawSpan decoded, SourceMap bodyMap)
    {
        Kind = kind;
        this.projection = projection;
        this.decoded = decoded;
        this.bodyMap = bodyMap;
        Text = projection.Text!.Substring(decoded.Start, decoded.Length);
    }

    public string Kind { get; }
    public string Text { get; }

    internal ProseMapping Map(RawSpan local, CancellationToken cancellationToken = default)
    {
        if (local.End > decoded.Length)
            throw new ArgumentOutOfRangeException(nameof(local));
        var mapped = projection.Map(new RawSpan(decoded.Start + local.Start, local.Length));
        var origins = new List<SourceRange>();
        var precision = mapped.Precision;
        foreach (var fragment in mapped.RawFragments)
        {
            var original = bodyMap.Map(fragment, cancellationToken);
            origins.AddRange(original.Origins);
            if (original.Precision is MarkdownMappingPrecision.Unknown or MarkdownMappingPrecision.Partial)
                precision = original.Precision;
            else if (original.Precision == MarkdownMappingPrecision.CoveringTokens && precision == MarkdownMappingPrecision.Exact)
                precision = original.Precision;
        }
        return new(origins.AsReadOnly(), origins.Count == 0 ? MarkdownMappingPrecision.Unknown : precision);
    }
}

internal sealed record ProseMapping(IReadOnlyList<SourceRange> Origins, MarkdownMappingPrecision Precision);

/// <summary>An immutable selection tied to the source version and exact source text.</summary>
public sealed class ProofreadingSelection
{
    internal ProofreadingSelection(SourceSnapshot source, string profileId, string parserVersion, MarkdownParseStatus status,
        bool hasSyntax, IReadOnlyList<ProofreadingSegment> segments, IReadOnlyList<ProofreadingExclusion> exclusions)
    {
        Source = source;
        ProfileId = profileId;
        ParserVersion = parserVersion;
        ParseStatus = status;
        HasSyntax = hasSyntax;
        Segments = Array.AsReadOnly(segments.ToArray());
        Exclusions = Array.AsReadOnly(exclusions.ToArray());
    }

    public SourceSnapshot Source { get; }
    public string ProfileId { get; }
    public string ParserVersion { get; }
    public MarkdownParseStatus ParseStatus { get; }
    public bool HasSyntax { get; }
    public IReadOnlyList<ProofreadingSegment> Segments { get; }
    public IReadOnlyList<ProofreadingExclusion> Exclusions { get; }

    /// <summary>Previews an explicitly selected code-point range. Decoded entities and ambiguous mappings cannot be edited directly.</summary>
    public ProofreadingEditPreview PreviewReplacement(int segmentIndex, int codePointStart, int codePointLength, string replacement)
    {
        var segment = Segments[segmentIndex];
        var range = new YomiyasuCoordinates(segment.Text).ToRaw(codePointStart, codePointLength);
        return new ProofreadingEditPreview(segment.Map(range), replacement);
    }
}

/// <summary>Optional editorial selection using Syntamark syntax and projections; it does not execute a proofreader.</summary>
public static partial class MarkdownProofreading
{
    /// <summary>Selects headings, paragraphs, table cells and link labels. Code, HTML, math, image labels, destinations, URLs and explicit protected identifiers are excluded.</summary>
    /// <remarks>LithoSharp front matter is separated independently of the Markdown profile. Protected identifiers use exact ordinal decoded text, without guessing C# names.</remarks>
    public static ProofreadingSelection Select(SourceSnapshot source, string profileId = MarkdownOptions.LegacyProfile,
        IEnumerable<string>? protectedIdentifiers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        new MarkdownOptions { ProfileId = profileId }.Validate();
        var identifiers = protectedIdentifiers?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (identifiers.Any(identifier => string.IsNullOrWhiteSpace(identifier)
            || Syntamark.Compilation.MdHash.InvalidUnicode(identifier, cancellationToken) >= 0))
            throw new ArgumentException("Protected identifiers require nonempty, well-formed Unicode.", nameof(protectedIdentifiers));
        cancellationToken.ThrowIfCancellationRequested();
        var header = MarkdownParser.ScanFrontMatter(source.Text, cancellationToken: cancellationToken);
        var excluded = new List<ProofreadingExclusion>();
        var offset = 0;
        if (header.State == MarkdownFrontMatterScanState.Closed && header.Status == MarkdownParseStatus.Complete
            && header.HeaderSpan is { } headerSpan && header.BodySpan is { } bodySpan)
        {
            excluded.Add(new("frontmatter", headerSpan));
            offset = bodySpan.Start;
        }
        else if (header.State != MarkdownFrontMatterScanState.Absent || header.Status != MarkdownParseStatus.Complete)
            return new(source, profileId, header.ParserVersion, header.Status, false, [],
                [new("frontmatter-unavailable", header.FailureSpan)]);
        var body = source.Text[offset..];
        var document = MarkdownParser.ParseWithSyntax(body, "lithosharp-proofreading", source.SourceId, source.Version,
            new MarkdownParseOptions(MarkdownInputKind.Body, profileId: profileId), cancellationToken);
        var bodySource = new SourceSnapshot("lithosharp-proofreading-body:" + source.SourceId, source.Version, body);
        var bodyMap = new SourceMap(bodySource, body.Length == 0 ? [] :
            [new SourceMapSegment(new RawSpan(0, body.Length), new SourceRange(source, new RawSpan(offset, body.Length)), MarkdownSegmentKind.Linear)]);
        if (document.Root is null)
            return new(source, profileId, document.Facts.ParserVersion, document.Facts.Status, false, [],
                [.. excluded, new("syntax-unavailable", null)]);

        // Adapted from the Syntamark.Prose sample's caller policy, outside the Markdown parser.
        var masks = new List<RawSpan>();
        var pending = new Stack<MarkdownSyntaxNode>();
        pending.Push(document.Root);
        while (pending.TryPop(out var node))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reason = node.Kind switch
            {
                MarkdownSyntaxKind.FencedCodeBlock or MarkdownSyntaxKind.IndentedCodeBlock => "code-block",
                MarkdownSyntaxKind.CodeSpan => "inline-code",
                MarkdownSyntaxKind.Image => "image-alt",
                MarkdownSyntaxKind.Autolink => "autolink",
                MarkdownSyntaxKind.HtmlBlock => "html-block",
                MarkdownSyntaxKind.HtmlInline => "html-token",
                MarkdownSyntaxKind.MathBlock or MarkdownSyntaxKind.MathInline => "math-policy",
                MarkdownSyntaxKind.FootnoteReference => "footnote-marker",
                _ => null,
            };
            if (reason is not null)
                Exclude(reason, node.RawSpan);
            else
            {
                if (node.TargetRawSpan is { } destination)
                    Exclude("link-destination", destination);
                if (node.Directive is { } directive)
                {
                    Exclude("directive-header", directive.HeaderRawSpan);
                    Exclude("directive-marker", directive.ClosingMarkerRawSpan);
                }
                for (var index = node.Children.Count - 1; index >= 0; index--)
                    pending.Push(node.Children[index]);
            }
        }
        foreach (var link in document.Facts.Links)
            if (link.Kind == MarkdownLinkKind.ReferenceDefinition)
                Exclude("reference-definition", link.RawSpan);
        if (excluded.Any(item => item.Range is null))
            return new(source, profileId, document.Facts.ParserVersion, document.Facts.Status, true, [], excluded);

        var blocked = masks.OrderBy(span => span.Start).ToArray();
        var sources = document.Facts.Headings.Select(heading => ("heading", (RawSpan?)heading.RawSpan, heading.Text))
            .Concat(document.Facts.TextRegions.Where(region => region.Kind is "paragraph" or "tableCell")
                .Select(region => (region.Kind, region.RawSpan, region.Content)))
            .OrderBy(item => item.Item2?.Start ?? int.MaxValue);
        var segments = new List<ProofreadingSegment>();
        foreach (var (kind, _, projection) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (projection.Text is null)
                continue;
            var allowed = new List<RawSpan>();
            int? previousRawEnd = null;
            foreach (var part in projection.SourceSegments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (part.RawSpan is not { } raw || part.Kind == MarkdownSegmentKind.Unknown)
                {
                    Exclude("unknown-projection", part.RawSpan);
                    continue;
                }
                var cursor = part.DecodedSpan.Start;
                var end = part.DecodedSpan.End;
                foreach (var mask in blocked)
                {
                    if (mask.End <= raw.Start)
                        continue;
                    if (mask.Start >= raw.End)
                        break;
                    if (part.Kind == MarkdownSegmentKind.Atomic)
                    {
                        cursor = end;
                        break;
                    }
                    var left = part.DecodedSpan.Start + Math.Max(raw.Start, mask.Start) - raw.Start;
                    var right = part.DecodedSpan.Start + Math.Min(raw.End, mask.End) - raw.Start;
                    if (left > cursor)
                        Add(cursor, left, raw.Start + cursor - part.DecodedSpan.Start, raw.Start + left - part.DecodedSpan.Start);
                    cursor = Math.Max(cursor, right);
                }
                if (cursor < end)
                    Add(cursor, end, part.Kind == MarkdownSegmentKind.Atomic ? raw.Start : raw.Start + cursor - part.DecodedSpan.Start, raw.End);
            }
            foreach (var range in allowed)
                AddProtectedPieces(new ProofreadingSegment(kind, projection, range, bodyMap), range);

            void Add(int start, int end, int rawStart, int rawEnd)
            {
                var gap = previousRawEnd is { } prior && blocked.Any(mask => mask.Start < rawStart && mask.End > prior);
                if (allowed.Count != 0 && allowed[^1].End == start && !gap)
                    allowed[^1] = new RawSpan(allowed[^1].Start, end - allowed[^1].Start);
                else
                    allowed.Add(new RawSpan(start, end - start));
                previousRawEnd = rawEnd;
            }

            void AddProtectedPieces(ProofreadingSegment original, RawSpan selected)
            {
                var protectedRanges = new List<RawSpan>();
                foreach (var identifier in identifiers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cursor = 0;
                    while ((cursor = original.Text.IndexOf(identifier, cursor, StringComparison.Ordinal)) >= 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Protect(new RawSpan(cursor, identifier.Length), "protected-identifier");
                        cursor += identifier.Length;
                    }
                }
                foreach (Match match in BareUrl().Matches(original.Text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var value = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                    if (Uri.TryCreate(value, UriKind.Absolute, out _))
                        Protect(new RawSpan(match.Index, value.Length), "bare-url-policy");
                }
                var cursorAfter = 0;
                foreach (var mask in protectedRanges.OrderBy(span => span.Start))
                {
                    if (mask.Start > cursorAfter)
                        Piece(cursorAfter, mask.Start);
                    cursorAfter = Math.Max(cursorAfter, mask.End);
                }
                Piece(cursorAfter, original.Text.Length);

                void Protect(RawSpan span, string reason)
                {
                    protectedRanges.Add(span);
                    foreach (var origin in original.Map(span, cancellationToken).Origins)
                        excluded.Add(new(reason, origin.Span));
                }
                void Piece(int start, int end)
                {
                    if (end > start && !string.IsNullOrWhiteSpace(original.Text.Substring(start, end - start)))
                        segments.Add(new(kind, projection, new RawSpan(selected.Start + start, end - start), bodyMap));
                }
            }
        }
        return new(source, profileId, document.Facts.ParserVersion, document.Facts.Status, true, segments, excluded);

        void Exclude(string reason, RawSpan? range)
        {
            if (range is { } span)
            {
                masks.Add(span);
                excluded.Add(new(reason, new RawSpan(offset + span.Start, span.Length)));
            }
            else
                excluded.Add(new(reason, null));
        }
    }

    [GeneratedRegex("https?://[A-Za-z0-9:/?#@!$&*+,;=%._~+\\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BareUrl();
}
