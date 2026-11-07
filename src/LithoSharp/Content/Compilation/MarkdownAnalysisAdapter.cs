using Syntamark.Compilation;
using LithoSharp.Diagnostics;

namespace LithoSharp.Content.Compilation;

internal static class MarkdownAnalysisAdapter
{
    internal const string IncompleteAnalysisDiagnosticId = "LSM011";
    internal const MdAnalysisParts InspectionParts = MdAnalysisParts.FrontMatter | MdAnalysisParts.Headings
        | MdAnalysisParts.Sections | MdAnalysisParts.Links | MdAnalysisParts.Fences | MdAnalysisParts.UnsupportedSyntax;

    private const int BodyInputLimit = 1024 * 1024;
    private const string BodyScope = "lithosharp";
    private const MdAnalysisParts BodyParts = MdAnalysisParts.UnsupportedSyntax;
    private static readonly MdOptions BodyOptions = new(MdInputKind.Body);
    internal static string BodyOptionsHash => BodyOptions.Hash;

    // Eager public-reader warnings inspect the original, pre-inclusion body. Choose
    // its budget up front so larger accepted originals do not inherit the final
    // compiler's stricter input limit or require a second recognition attempt.
    private static readonly MdOptions OriginalBodyOptions = new(MdInputKind.Body,
        maxInputUtf16: 8 * 1024 * 1024, maxOutputItems: 1024 * 1024, maxScanUnits: 512 * 1024 * 1024);

    // Editor snapshots already support multi-megabyte documents; compiler hosts keep their stricter defaults.
    private static readonly MdOptions EditorOptions = new(MdFrontMatterRecovery.TreatAsBody,
        maxInputUtf16: 8 * 1024 * 1024, maxOutputItems: 1024 * 1024, maxScanUnits: 512 * 1024 * 1024);

    internal static MdAnalysisResult InspectDocument(string raw, string sourcePath, CancellationToken cancellationToken) =>
        MdParser.AnalyzeWithBlocks(raw, InspectionParts, "lithosharp", sourcePath, null, EditorOptions, cancellationToken);

    internal static MdAnalysisResult AnalyzeBody(string body, string? sourcePath, CancellationToken cancellationToken) =>
        MdParser.AnalyzeWithBlocks(body, BodyParts, BodyScope,
            BodySourceId(sourcePath), null, BodyOptions, cancellationToken);

    internal static MarkdownBodyAnalysisSnapshot AnalyzeOriginalBody(
        string body, string? sourcePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var options = body.Length <= BodyInputLimit ? BodyOptions : OriginalBodyOptions;
        return new MarkdownBodyAnalysisSnapshot(MdParser.AnalyzeWithBlocks(body, BodyParts, BodyScope,
            BodySourceId(sourcePath), null, options, cancellationToken));
    }

    /// <summary>Retains authoritative evidence only for the identical strict body request.</summary>
    internal static MarkdownBodyAnalysisSnapshot? RetainForBody(
        MarkdownBodyAnalysisSnapshot snapshot, string body, string? sourcePath)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(body);
        var analysis = snapshot.Analysis;
        if (!string.Equals(analysis.RawText, body, StringComparison.Ordinal)
            || !string.Equals(analysis.ParserVersion, MdParserVersion.Value, StringComparison.Ordinal)
            || !string.Equals(analysis.ProfileId, BodyOptions.ProfileId, StringComparison.Ordinal)
            || !string.Equals(analysis.OptionsHash, BodyOptions.Hash, StringComparison.Ordinal)
            || !string.Equals(analysis.ScopeId, BodyScope, StringComparison.Ordinal)
            || !string.Equals(analysis.SourceId, BodySourceId(sourcePath), StringComparison.Ordinal)
            || analysis.SourceVersion is not null
            || analysis.RequestedParts != BodyParts)
            return null;

        if (analysis.Blocks is not null)
            return analysis.BodySpan is { Start: 0 } span && span.Length == body.Length ? snapshot : null;

        // A failed or resource-stopped request is reusable error evidence, not a
        // successful empty tree. Keeping it avoids retrying the identical request.
        return analysis.Diagnostics.Any(item => item.Origin is "input" or "resource") ? snapshot : null;
    }

    internal static IReadOnlyList<SiteDiagnostic> BodyDiagnostics(
        MarkdownBodyAnalysisSnapshot snapshot, string? sourcePath, int bodyStartLine = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var diagnostics = LithoLimits.MapDiagnostics(snapshot.Analysis, sourcePath, bodyStartLine,
            cancellationToken: cancellationToken).ToList();
        if (IncompleteDiagnostic(snapshot.Analysis, sourcePath, bodyStartLine) is { } incomplete)
            diagnostics.Add(incomplete);
        return Array.AsReadOnly(diagnostics.ToArray());
    }

    private static string BodySourceId(string? sourcePath) =>
        string.IsNullOrWhiteSpace(sourcePath) ? "body" : sourcePath;

    internal static SiteDiagnostic? IncompleteDiagnostic(MdAnalysisResult analysis, string? sourcePath, int bodyStartLine = 1)
    {
        var failure = analysis.Diagnostics.FirstOrDefault(item => item.Origin is "input" or "resource");
        if (analysis.Status != MdParseStatus.Failed && failure is null)
            return null;
        SiteSourceLocation? location = null;
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            if (failure?.RawSpan is { } range)
            {
                var source = new SourceText(analysis.RawText);
                var (line, column) = source.GetLineAndColumn(range.Start);
                var (endLine, endColumn) = source.GetLineAndColumn(range.End);
                location = new SiteSourceLocation(sourcePath, bodyStartLine + line - 1, column,
                    bodyStartLine + endLine - 1, endColumn);
            }
            else
            {
                location = new SiteSourceLocation(sourcePath);
            }
        }
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
