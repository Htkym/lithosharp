using Syntamark.Compilation;
using LithoSharp.Diagnostics;

namespace LithoSharp.Content.Compilation;

internal static class MarkdownAnalysisAdapter
{
    internal const string IncompleteAnalysisDiagnosticId = "LSM011";
    internal const MdAnalysisParts InspectionParts = MdAnalysisParts.FrontMatter | MdAnalysisParts.Headings
        | MdAnalysisParts.Sections | MdAnalysisParts.Links | MdAnalysisParts.Fences | MdAnalysisParts.UnsupportedSyntax;

    // Editor snapshots already support multi-megabyte documents; compiler hosts keep their stricter defaults.
    private static readonly MdOptions EditorOptions = new(MdFrontMatterRecovery.TreatAsBody,
        maxInputUtf16: 8 * 1024 * 1024, maxOutputItems: 1024 * 1024, maxScanUnits: 512 * 1024 * 1024);

    internal static MdAnalysisResult InspectDocument(string raw, string sourcePath, CancellationToken cancellationToken) =>
        MdParser.AnalyzeWithBlocks(raw, InspectionParts, "lithosharp", sourcePath, null, EditorOptions, cancellationToken);

    internal static SiteDiagnostic? IncompleteDiagnostic(MdAnalysisResult analysis, string sourcePath)
    {
        var failure = analysis.Diagnostics.FirstOrDefault(item => item.Origin is "input" or "resource");
        if (analysis.Status != MdParseStatus.Failed && failure is null)
            return null;
        var source = new SourceText(analysis.RawText);
        var location = failure?.RawSpan is { } range
            ? new SourceSpan(range.Start, range.Length).ToSourceLocation(sourcePath, source)
            : new SiteSourceLocation(sourcePath);
        return new SiteDiagnostic(IncompleteAnalysisDiagnosticId, SiteDiagnosticSeverity.Error,
            "Markdown analysis is incomplete; unavailable facts were not validated."
                + (failure is null ? string.Empty : " " + failure.Message), location);
    }

    internal static IReadOnlyList<DocumentHeading> InspectionHeadings(MdAnalysisResult analysis, CancellationToken cancellationToken)
    {
        if (analysis.Blocks is not { } blocks || analysis.Headings is not { } facts || analysis.BodySpan is not { } body)
            return [];
        var headings = LithoMarkdownCompiler.WalkHeadings(blocks).ToArray();
        var texts = headings.Select(heading => LithoLegacyTextPolicy.Heading(heading.Inlines, cancellationToken)).ToList();
        var ids = LithoSlug.Assign(texts);
        var byStart = headings.Select((heading, index) => (Start: body.Start + heading.Span.Start, Index: index))
            .ToDictionary(heading => heading.Start, heading => heading.Index);
        var result = new List<DocumentHeading>();
        foreach (var heading in facts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byStart.TryGetValue(heading.RawSpan.Start, out var index))
                continue;
            result.Add(new DocumentHeading(texts[index], ids[index], heading.RawLevel,
                heading.RawLevel == 1 ? 2 : heading.RawLevel, new SourceSpan(heading.RawSpan.Start, heading.RawSpan.Length))
            {
                LocalKey = heading.LocalKey,
            });
        }
        return result;
    }

    internal static IReadOnlyList<DocumentLink> InspectionLinks(MdAnalysisResult analysis, CancellationToken cancellationToken)
    {
        var result = new List<DocumentLink>();
        foreach (var link in analysis.Links ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (link.Kind == MdLinkKind.ReferenceDefinition || link.Target is null)
                continue;
            var raw = link.Kind == MdLinkKind.Autolink ? link.Label.Text ?? string.Empty
                : analysis.RawText.Substring(link.RawSpan.Start, link.RawSpan.Length);
            result.Add(new DocumentLink(raw, link.Target, link.Title, link.Image,
                new SourceSpan(link.RawSpan.Start, link.RawSpan.Length)));
        }
        return result;
    }
}
