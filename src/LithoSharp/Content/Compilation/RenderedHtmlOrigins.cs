using LithoSharp.Build;
using LithoSharp.Diagnostics;
using Syntamark.Compilation;

namespace LithoSharp.Content.Compilation;

internal sealed record RenderedHtmlOrigin(int Start, int Length, CachedCollectionSourceLocation? Source,
    IReadOnlyList<CachedCollectionSourceLocation> Related, string Precision, bool GeneratedCause = false);
internal sealed record RenderedHtmlFragment(string Html, RenderedHtmlOrigins Origins);
internal readonly record struct RenderedHtmlInsertion(int Offset, int Length);

// Only compact, artifact-bound locations survive rendering. Parser buffers and native source maps stay operation-local.
internal sealed record RenderedHtmlOrigins(string HtmlHash, int HtmlLength, IReadOnlyList<RenderedHtmlOrigin> Origins, int Version = 0)
{
    private const int CurrentVersion = 2;

    internal bool IsValid() => Version == CurrentVersion && HtmlLength >= 0 && IsHash(HtmlHash) && Origins is not null && Origins.All(origin => origin is not null
        && origin.Start >= 0 && origin.Length > 0 && origin.Start <= HtmlLength && origin.Length <= HtmlLength - origin.Start
        && origin.Precision is "Exact" or "CoveringTokens" or "Partial" or "Unknown"
        && ((origin.Precision is "Exact" or "CoveringTokens") == (origin.Source is not null))
        && (!origin.GeneratedCause || origin.Precision == "Unknown")
        && (origin.Source is null || origin.Source.IsValid()) && origin.Related is not null
        && origin.Related.All(static location => location is not null && location.IsValid()));

    internal static RenderedHtmlOrigins Bind(string html, string body, IReadOnlyList<MarkdownRenderOrigin> emitted, DocumentSource? source)
    {
        var hash = SiteGenerator.ComputeTextContentSha256(html);
        if (emitted.Count == 0)
            return new(hash, html.Length, [], CurrentVersion);
        var located = new List<RenderedHtmlOrigin>();
        var known = source.CanMapBody(body, source?.FilePath)
            && (source?.SourceMap is not null || !string.IsNullOrWhiteSpace(source?.FilePath))
            && MdHash.InvalidUnicode(html, CancellationToken.None) < 0;
        var bodySnapshot = known ? source!.SourceMap?.Output
            ?? new MdSourceSnapshot("lithosharp:body:" + source.FilePath, MdHash.Compute(body), body) : null;
        var output = bodySnapshot is null ? null : new MdSourceSnapshot("lithosharp:html:" + hash, hash, html);
        var sourceTexts = new Dictionary<MdSourceSnapshot, SourceText>(ReferenceEqualityComparer.Instance);
        foreach (var origin in emitted)
        {
            if (!Valid(origin, html.Length, body.Length))
                continue;
            MdSourceMap? map = null;
            if (output is not null && !origin.GeneratedCause)
            {
                var same = !origin.TokenOnly && origin.HtmlLength == origin.BodyLength
                    && html.AsSpan(origin.HtmlStart, origin.HtmlLength).SequenceEqual(body.AsSpan(origin.BodyStart, origin.BodyLength));
                map = new(output, [new MdSourceMapSegment(new(origin.HtmlStart, origin.HtmlLength),
                    new(bodySnapshot!, new(origin.BodyStart, origin.BodyLength)), same ? MdSegmentKind.Linear : MdSegmentKind.Atomic)]);
                if (source!.SourceMap is { } previous)
                    map = map.Compose([previous]);
            }
            var result = map is not null ? map.Map(new(origin.HtmlStart, origin.HtmlLength))
                : origin.GeneratedCause && bodySnapshot is not null
                    ? source!.SourceMap?.Map(new(origin.BodyStart, origin.BodyLength))
                        ?? new MdSourceMapResult([new(bodySnapshot, new(origin.BodyStart, origin.BodyLength))], MdMappingPrecision.Exact)
                    : null;
            var locations = result?.Origins.Select(range => Location(source!, range, sourceTexts)).ToArray() ?? [];
            var related = new List<SiteSourceLocation>();
            var precision = origin.GeneratedCause ? "Unknown" : result?.Precision.ToString() ?? "Unknown";
            var exact = precision is "Exact" or "CoveringTokens";
            related.AddRange(exact ? locations.Skip(1) : locations);
            if (source is not null)
            {
                related.AddRange(source.InclusionOrigins.Where(inclusion => origin.BodyStart < inclusion.OutputSpan.End
                    && origin.BodyStart + origin.BodyLength >= inclusion.OutputSpan.Start).Select(inclusion => new SiteSourceLocation(
                        OriginPath(source, inclusion.Directive.FilePath), inclusion.Directive.Line, inclusion.Directive.Column,
                        inclusion.Directive.EndLine, inclusion.Directive.EndColumn)));
                if (!known && !string.IsNullOrWhiteSpace(source.FilePath))
                    related.Add(new(source.FilePath));
            }
            if (locations.Length == 0 && related.Count == 0)
                continue;
            located.Add(new(origin.HtmlStart, origin.HtmlLength,
                exact && locations.Length != 0 ? CachedCollectionSourceLocation.Capture(locations[0]) : null,
                related.Select(CachedCollectionSourceLocation.Capture).Distinct().ToArray(), precision, origin.GeneratedCause));
        }
        return new(hash, html.Length, located, CurrentVersion);
    }

    internal static RenderedHtmlOrigins Embed(string finalHtml, IEnumerable<RenderedHtmlFragment> fragments)
    {
        var hash = SiteGenerator.ComputeTextContentSha256(finalHtml);
        var located = new List<RenderedHtmlOrigin>();
        var output = MdHash.InvalidUnicode(finalHtml, CancellationToken.None) < 0
            ? new MdSourceSnapshot("lithosharp:artifact:" + hash, hash, finalHtml) : null;
        foreach (var group in fragments.Where(fragment => fragment.Html.Length != 0 && fragment.Origins.IsValid()
                     && fragment.Origins.HtmlLength == fragment.Html.Length
                     && fragment.Origins.HtmlHash == SiteGenerator.ComputeTextContentSha256(fragment.Html))
                     .GroupBy(static fragment => fragment.Origins.HtmlHash, StringComparer.Ordinal))
        {
            var entries = group.ToArray();
            var fragment = entries[0];
            var start = finalHtml.IndexOf(fragment.Html, StringComparison.Ordinal);
            if (start < 0)
                continue;
            var ambiguous = entries.Length != 1 || finalHtml.IndexOf(fragment.Html, start + 1, StringComparison.Ordinal) >= 0;
            if (ambiguous)
            {
                var related = entries.SelectMany(static entry => entry.Origins.Origins)
                    .SelectMany(static origin => origin.Source is { } source ? origin.Related.Append(source) : origin.Related).Distinct().ToArray();
                for (var at = start; at >= 0; at = finalHtml.IndexOf(fragment.Html, at + 1, StringComparison.Ordinal))
                    located.Add(new(at, fragment.Html.Length, null, related, "Unknown", true));
                continue;
            }
            // The producer declares the complete fragment. The native linear-map constructor verifies exact insertion identity.
            if (output is not null)
            {
                var input = new MdSourceSnapshot("lithosharp:fragment:" + fragment.Origins.HtmlHash, fragment.Origins.HtmlHash, fragment.Html);
                _ = new MdSourceMap(output,
                    [new MdSourceMapSegment(new(start, fragment.Html.Length), new(input, new(0, fragment.Html.Length)), MdSegmentKind.Linear)]);
            }
            located.AddRange(fragment.Origins.Origins.Select(origin => origin with { Start = start + origin.Start }));
        }
        return BindArtifact(finalHtml, hash, located);
    }

    internal RenderedHtmlOrigins Rebase(string original, string transformed, IReadOnlyList<RenderedHtmlInsertion> insertions)
    {
        if (!IsValid() || HtmlLength != original.Length || HtmlHash != SiteGenerator.ComputeTextContentSha256(original))
            return new(SiteGenerator.ComputeTextContentSha256(transformed), transformed.Length, [], CurrentVersion);
        var mapped = new List<RenderedHtmlOrigin>();
        foreach (var origin in Origins)
        {
            var start = origin.Start + insertions.Where(insertion => insertion.Offset <= origin.Start).Sum(static insertion => insertion.Length);
            var added = insertions.Where(insertion => insertion.Offset > origin.Start && insertion.Offset < origin.Start + origin.Length)
                .Sum(static insertion => insertion.Length);
            if (origin.GeneratedCause)
                mapped.Add(origin with { Start = start, Length = origin.Length + added });
            else if (added == 0 && start <= transformed.Length && origin.Length <= transformed.Length - start
                && original.AsSpan(origin.Start, origin.Length).SequenceEqual(transformed.AsSpan(start, origin.Length)))
                mapped.Add(origin with { Start = start });
        }
        return new(SiteGenerator.ComputeTextContentSha256(transformed), transformed.Length, mapped, CurrentVersion);
    }

    private static RenderedHtmlOrigins BindArtifact(string original, string hash, IReadOnlyList<RenderedHtmlOrigin> origins)
    {
        // The writer normalizes every .NET line ending; Quality decoding removes an optional UTF-8 BOM.
        // The hash binds actual artifact bytes, while offsets describe exactly that decoded inspection buffer.
        var normalized = original.ReplaceLineEndings("\n");
        var bom = normalized.StartsWith('\uFEFF') ? 1 : 0;
        var inspected = bom == 0 ? normalized : normalized[1..];
        var removed = new List<int>();
        if (bom != 0)
            removed.Add(0);
        for (var index = 0; index + 1 < original.Length; index++)
            if (original[index] == '\r' && original[index + 1] == '\n')
                removed.Add(index++);
        if (original.Length - removed.Count != inspected.Length)
            return new(hash, inspected.Length, [], CurrentVersion);

        int Position(int offset)
        {
            var index = removed.BinarySearch(offset);
            return offset - (index < 0 ? ~index : index);
        }
        var mapped = new List<RenderedHtmlOrigin>(origins.Count);
        foreach (var origin in origins)
        {
            var start = Position(origin.Start);
            var end = Position(origin.Start + origin.Length);
            if (end <= start || start < 0 || end > inspected.Length)
                continue;
            var unchanged = original.AsSpan(origin.Start, origin.Length).SequenceEqual(inspected.AsSpan(start, end - start));
            mapped.Add(origin with
            {
                Start = start,
                Length = end - start,
                Precision = !unchanged && origin.Precision == "Exact" ? "CoveringTokens" : origin.Precision,
            });
        }
        return new(hash, inspected.Length, mapped, CurrentVersion);
    }

    internal SiteDiagnostic MapDiagnostic(SiteDiagnostic diagnostic) => CreateMapper()(diagnostic);

    internal Func<SiteDiagnostic, SiteDiagnostic> CreateMapper()
    {
        var attributes = Origins.Where(static origin => !origin.GeneratedCause).ToLookup(static origin => (origin.Start, origin.Length));
        var generated = Origins.Where(static origin => origin.GeneratedCause).ToArray();
        return Map;

        SiteDiagnostic Map(SiteDiagnostic diagnostic)
        {
            if (diagnostic.GeneratedReferenceSpan is not { } span)
                return diagnostic;
            var matches = attributes[(span.Start, span.Length)].Concat(generated.Where(origin => span.Start >= origin.Start
                && span.Start + span.Length <= origin.Start + origin.Length)).Take(2).ToArray();
            if (matches.Length != 1)
                return diagnostic;
            var origin = matches[0];
            var known = origin.Source is not null && origin.Precision is "Exact" or "CoveringTokens";
            return new(diagnostic.Id, diagnostic.Severity, diagnostic.Message, known ? origin.Source!.Restore() : diagnostic.Location,
                diagnostic.Category, diagnostic.RelatedLocations.Concat(origin.Related.Select(static location => location.Restore()))
                    .Concat(known && diagnostic.Location is not null ? [diagnostic.Location] : []))
            {
                SnapshotIdentity = diagnostic.SnapshotIdentity,
                MappingPrecision = origin.Precision,
                GeneratedReferenceSpan = diagnostic.GeneratedReferenceSpan,
            };
        }
    }

    private static bool Valid(MarkdownRenderOrigin origin, int htmlLength, int bodyLength) => origin.HtmlStart >= 0
        && origin.HtmlLength > 0 && origin.HtmlStart <= htmlLength && origin.HtmlLength <= htmlLength - origin.HtmlStart
        && origin.BodyStart >= 0 && origin.BodyLength > 0 && origin.BodyStart <= bodyLength && origin.BodyLength <= bodyLength - origin.BodyStart;
    private static bool IsHash(string value) => value is { Length: 64 } && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
    private static string OriginPath(DocumentSource source, string path) => path == source.OriginalSourceId && source.FilePath is not null ? source.FilePath : path;
    private static SiteSourceLocation Location(DocumentSource source, MdSourceRange range,
        IDictionary<MdSourceSnapshot, SourceText> sourceTexts)
    {
        if (!sourceTexts.TryGetValue(range.Source, out var text))
            sourceTexts.Add(range.Source, text = new SourceText(range.Source.Text));
        var from = text.GetLineAndColumn(range.Span.Start);
        var to = text.GetLineAndColumn(range.Span.End);
        var direct = source.SourceMap is null;
        return new(OriginPath(source, direct && source.FilePath is not null ? source.FilePath : range.Source.SourceId),
            from.Line + (direct ? source.BodyStartLine - 1 : 0), from.Column + (direct && from.Line == 1 ? source.BodyFirstColumnOffset : 0),
            to.Line + (direct ? source.BodyStartLine - 1 : 0), to.Column + (direct && to.Line == 1 ? source.BodyFirstColumnOffset : 0));
    }
}
