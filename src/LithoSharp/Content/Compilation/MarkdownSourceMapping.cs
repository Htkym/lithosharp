using LithoSharp.Diagnostics;
using Syntamark.Compilation;

namespace LithoSharp.Content.Compilation;

internal static class MarkdownSourceMapping
{
    internal static DocumentSource? CreateSource(MarkdownCodeInclusion.InclusionResult inclusion,
        string filePath, string finalBody, int bodyStartOffset, int bodyStartLine)
    {
        var start = 0;
        if (!string.Equals(finalBody, inclusion.Body, StringComparison.Ordinal))
        {
            if (!string.Equals(finalBody, inclusion.Body.Trim(), StringComparison.Ordinal))
                return null;
            start = inclusion.Body.Length - inclusion.Body.TrimStart().Length;
        }
        if (inclusion.SourceMap is not { } original)
        {
            var position = new SourceText(inclusion.Body).GetLineAndColumn(start);
            return new DocumentSource(filePath, bodyStartOffset + start)
            {
                ExactBody = finalBody,
                BodyStartLine = bodyStartLine + position.Line - 1,
                BodyFirstColumnOffset = position.Column - 1,
            };
        }
        var map = original;
        if (!string.Equals(finalBody, inclusion.Body, StringComparison.Ordinal))
        {
            var output = new MdSourceSnapshot("lithosharp:rendered:" + filePath, MdHash.Compute(finalBody), finalBody);
            map = new MdSourceMap(output, finalBody.Length == 0 ? [] :
                [new MdSourceMapSegment(new MdRawRange(0, finalBody.Length),
                    new MdSourceRange(original.Output, new MdRawRange(start, finalBody.Length)), MdSegmentKind.Linear)])
                .Compose([original]);
        }
        var origins = inclusion.Origins.Where(origin => origin.OutputSpan.End > start
                && origin.OutputSpan.Start < start + finalBody.Length)
            .Select(origin => new MarkdownInclusionOrigin(
                new MdRawRange(Math.Max(start, origin.OutputSpan.Start) - start,
                    Math.Min(start + finalBody.Length, origin.OutputSpan.End) - Math.Max(start, origin.OutputSpan.Start)),
                origin.Directive)).ToArray();
        return new DocumentSource(filePath, bodyStartOffset)
        {
            BodyStartLine = bodyStartLine,
            ExactBody = finalBody,
            SourceMap = map,
            OriginalSourceId = inclusion.OriginalSourceId,
            InclusionOrigins = Array.AsReadOnly(origins),
        };
    }

    internal static bool CanMapBody(this DocumentSource? source, string body, string? filePath) =>
        source is not null && string.Equals(source.FilePath, filePath, StringComparison.Ordinal)
        && string.Equals(source.ExactBody ?? source.SourceMap?.Output.Text, body, StringComparison.Ordinal);

    internal static SiteDiagnostic MapDiagnostic(this DocumentSource source, SiteDiagnostic diagnostic)
    {
        if (source.SourceMap is not { } map)
        {
            if (source.BodyFirstColumnOffset == 0 || diagnostic.Location is not { } direct
                || direct.FilePath != source.FilePath)
                return diagnostic;
            return new SiteDiagnostic(diagnostic.Id, diagnostic.Severity, diagnostic.Message,
                new SiteSourceLocation(direct.FilePath, direct.Line,
                    direct.Column + (direct.Line == source.BodyStartLine ? source.BodyFirstColumnOffset : 0),
                    direct.EndLine, direct.EndColumn + (direct.EndLine == source.BodyStartLine ? source.BodyFirstColumnOffset : 0)),
                diagnostic.Category, diagnostic.RelatedLocations)
            { SnapshotIdentity = diagnostic.SnapshotIdentity, MappingPrecision = "Exact" };
        }
        if (diagnostic.Location is not { Line: { } line, Column: { } column } location
            || !string.Equals(location.FilePath, source.FilePath, StringComparison.Ordinal)
            || !TryOffset(map.Output.Text, line - source.BodyStartLine + 1, column, out var start)
            || !TryOffset(map.Output.Text, (location.EndLine ?? line) - source.BodyStartLine + 1,
                location.EndColumn ?? column, out var end) || end < start)
            return diagnostic;
        var span = new MdRawRange(start, end - start);
        var result = map.Map(span);
        var origins = result.Origins.Select(origin =>
        {
            var text = new SourceText(origin.Source.Text);
            var from = text.GetLineAndColumn(origin.Span.Start);
            var to = text.GetLineAndColumn(origin.Span.End);
            return new SiteSourceLocation(OriginPath(source, origin.Source.SourceId), from.Line, from.Column, to.Line, to.Column);
        }).ToArray();
        var exact = result.Precision is MdMappingPrecision.Exact or MdMappingPrecision.CoveringTokens;
        var related = diagnostic.RelatedLocations.Concat(exact ? origins.Skip(1) : origins)
            .Concat(source.InclusionOrigins.Where(origin => span.Start < origin.OutputSpan.End
                    && span.End >= origin.OutputSpan.Start)
                .Select(origin => new SiteSourceLocation(OriginPath(source, origin.Directive.FilePath),
                    origin.Directive.Line, origin.Directive.Column, origin.Directive.EndLine, origin.Directive.EndColumn)));
        return new SiteDiagnostic(diagnostic.Id, diagnostic.Severity, diagnostic.Message,
            exact && origins.Length != 0 ? origins[0] : new SiteSourceLocation(source.FilePath ?? map.Output.SourceId),
            diagnostic.Category, related)
        {
            SnapshotIdentity = diagnostic.SnapshotIdentity,
            MappingPrecision = result.Precision.ToString(),
        };
    }

    private static string OriginPath(DocumentSource source, string path) =>
        path == source.OriginalSourceId && source.FilePath is not null ? source.FilePath : path;

    private static bool TryOffset(string text, int line, int column, out int offset)
    {
        offset = 0;
        if (line < 1 || column < 1)
            return false;
        for (var current = 1; current < line; current++)
        {
            while (offset < text.Length && text[offset] is not ('\r' or '\n'))
                offset++;
            if (offset == text.Length)
                return false;
            if (text[offset++] == '\r' && offset < text.Length && text[offset] == '\n')
                offset++;
        }
        var end = offset;
        while (end < text.Length && text[end] is not ('\r' or '\n'))
            end++;
        if (column - 1 > end - offset)
            return false;
        offset += column - 1;
        return true;
    }
}
