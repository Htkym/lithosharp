using Syntamark.Compilation;
using SiteDiagnostics = LithoSharp.Diagnostics;

namespace LithoSharp.Content.Compilation;

internal static partial class LithoLimits
{
    internal static IReadOnlyList<SiteDiagnostics.SiteDiagnostic> MapDiagnostics(
        MdAnalysisResult analysis, string? filePath, int bodyStartLine = 1,
        bool includeCompatibilityAdvisories = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<SiteDiagnostics.SiteDiagnostic>();
        var advisories = new List<(int Offset, SiteDiagnostics.SiteDiagnostic Diagnostic)>();
        var seen = new HashSet<MdUnsupportedSyntaxKind>();
        var source = new SourceText(analysis.RawText);
        if (analysis.GetAvailability(MdAnalysisParts.UnsupportedSyntax) is MdAnalysisAvailability.Complete or MdAnalysisAvailability.Partial
            && analysis.UnsupportedSyntax is { } candidates)
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var diagnostic = UnsupportedDiagnostic(candidate, source, filePath, bodyStartLine);
                if (diagnostic is null || !seen.Add(candidate.Kind))
                    continue;
                if (candidate.Kind == MdUnsupportedSyntaxKind.Footnote)
                    result.Add(diagnostic);
                else if (includeCompatibilityAdvisories)
                    advisories.Add((candidate.RawSpan.Start, diagnostic));
            }
        }

        if (BrowserAssetOffset(analysis, cancellationToken) is { } offset)
            result.Add(BrowserAsset(analysis.RawText, filePath, offset, bodyStartLine));
        advisories.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
        result.AddRange(advisories.Select(static item => item.Diagnostic));
        return result;
    }

    private static SiteDiagnostics.SiteDiagnostic? UnsupportedDiagnostic(
        MdUnsupportedSyntaxCandidate candidate, SourceText source, string? filePath, int bodyStartLine)
    {
        var descriptor = candidate.Kind switch
        {
            MdUnsupportedSyntaxKind.Footnote when candidate.Reason is "footnoteReference" or "footnoteDefinition" =>
                (UnsupportedFootnoteDiagnosticId, SiteDiagnostics.SiteDiagnosticSeverity.Warning,
                "Footnote syntax stays literal text in the Litho compiler (U01-footnotes); previously it rendered footnote links. Use an explicit link or section instead."),
            MdUnsupportedSyntaxKind.DefinitionList when candidate.Reason == "definitionListMarker" =>
                (UnsupportedDefinitionListDiagnosticId, SiteDiagnostics.SiteDiagnosticSeverity.Info,
                "Definition list syntax stays literal paragraphs in the Litho compiler (U02-definition-lists). Use a bullet list or a GFM pipe table instead."),
            MdUnsupportedSyntaxKind.GenericAttribute when candidate.Reason == "genericAttribute" =>
                (UnsupportedGenericAttributeDiagnosticId, SiteDiagnostics.SiteDiagnosticSeverity.Info,
                "Generic attribute syntax stays literal text in the Litho compiler (U13-generic-attributes). Remove the attribute or use a supported heading and link form instead."),
            MdUnsupportedSyntaxKind.GridTable when candidate.Reason == "gridTableBorder" =>
                (UnsupportedGridTableDiagnosticId, SiteDiagnostics.SiteDiagnosticSeverity.Info,
                "Grid table syntax stays literal paragraphs in the Litho compiler (U12-grid-tables). Use a GFM pipe table instead."),
            _ => ((string?)null, SiteDiagnostics.SiteDiagnosticSeverity.Info, string.Empty),
        };
        if (descriptor.Item1 is null)
            return null;
        SiteDiagnostics.SiteSourceLocation? location = null;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var (line, column) = source.GetLineAndColumn(candidate.RawSpan.Start);
            location = new SiteDiagnostics.SiteSourceLocation(filePath, bodyStartLine + line - 1, column);
        }
        return new SiteDiagnostics.SiteDiagnostic(descriptor.Item1, descriptor.Item2, descriptor.Item3, location);
    }

    private static int? BrowserAssetOffset(MdAnalysisResult analysis, CancellationToken cancellationToken)
    {
        int? offset = null;
        void Consider(int start) => offset = offset is { } previous ? Math.Min(previous, start) : start;
        if (analysis.Blocks is { } blocks && analysis.BodySpan is { } body)
        {
            foreach (var block in WalkDiagnosticBlocks(blocks, cancellationToken))
            {
                if (block is LithoMath or LithoCode { Fenced: true, Info: "mermaid" or "nomnoml" })
                    Consider(checked(body.Start + block.Span.Start));
                IEnumerable<IReadOnlyList<LithoInline>> inlines = block switch
                {
                    LithoParagraph paragraph => [paragraph.Inlines],
                    LithoHeading heading => [heading.Inlines],
                    LithoTable table => table.Rows.SelectMany(static row => row),
                    _ => [],
                };
                foreach (var inlineGroup in inlines)
                    foreach (var (inline, exit) in LithoInline.Walk(inlineGroup, cancellationToken: cancellationToken))
                        if (!exit && inline is LithoMathInline)
                            Consider(checked(body.Start + inline.Span.Start));
            }
            return offset;
        }

        if (analysis.GetAvailability(MdAnalysisParts.Fences) is MdAnalysisAvailability.Complete or MdAnalysisAvailability.Partial
            && analysis.Fences is { } fences)
            foreach (var fence in fences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fence.Info?.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is "mermaid" or "nomnoml")
                    Consider(fence.RawSpan.Start);
            }
        if (analysis.GetAvailability(MdAnalysisParts.Syntax) is MdAnalysisAvailability.Complete or MdAnalysisAvailability.Partial
            && analysis.Root is { } root)
            foreach (var node in root.DescendantsAndSelf(cancellationToken))
                if ((node.Kind is MdSyntaxKind.MathBlock or MdSyntaxKind.MathInline
                    || node is { Kind: MdSyntaxKind.FencedCodeBlock, CodeInfo: "mermaid" or "nomnoml" })
                    && node.RawSpan is { } span)
                    Consider(span.Start);
        return offset;
    }

    private static IEnumerable<LithoBlock> WalkDiagnosticBlocks(
        IReadOnlyList<LithoBlock> blocks, CancellationToken cancellationToken)
    {
        var pending = new Stack<IReadOnlyList<LithoBlock>>();
        pending.Push(blocks);
        while (pending.TryPop(out var group))
            foreach (var block in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return block;
                switch (block)
                {
                    case LithoQuote quote:
                        pending.Push(quote.Children);
                        break;
                    case LithoAdmonition admonition:
                        pending.Push(admonition.Children);
                        break;
                    case LithoDirective directive:
                        pending.Push(directive.Children);
                        break;
                    case LithoList list:
                        foreach (var item in list.Items)
                            pending.Push(item.Children);
                        break;
                }
            }
    }
}
