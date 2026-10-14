using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using LithoSharp.HtmlParsing;
using LithoSharp.Internal;
using LithoSharp.Quality;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

internal static class HtmlLiteralAnalysis
{
    internal static readonly DiagnosticDescriptor Rule = new("LSA1105", "Invalid URL in literal HTML",
        "Literal HTML '{0}' URL violates the LSQ001 contract ({1})", "LithoSharp", DiagnosticSeverity.Error,
        isEnabledByDefault: true, helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/src/LithoSharp.Analyzers/README.md#lsa1105");
    internal const int MaxInputLength = 65536;
    internal const int MaxSourceLength = 655360;
    internal const int MaxNodes = 8192;
    internal const int MaxDepth = 128;
    internal const int MaxOperations = 262144;

    internal static HtmlLiteralFacts Parse(string html, CancellationToken cancellation) => HtmlLiteralFacts.Parse(html,
        limits: new HtmlTreeLimits
        {
            MaxNodes = MaxNodes,
            MaxDepth = MaxDepth,
            MaxOperations = MaxOperations,
            Tokenizer = new HtmlTokenizerLimits { MaxInputChars = MaxInputLength, MaxInputUtf8Bytes = 4 * MaxInputLength }
        },
        cancellationToken: cancellation);

    // Called only after the exact metadata UnsafeRaw(string) sink has bound successfully.
    internal static void Analyze(OperationBlockAnalysisContext context, IOperation argument)
    {
        var syntax = argument.Syntax;
        while (syntax is ParenthesizedExpressionSyntax parentheses) syntax = parentheses.Expression;
        if (syntax is not LiteralExpressionSyntax literal || argument.Type?.SpecialType != SpecialType.System_String
            || literal.Token.ValueText.Length > MaxInputLength || literal.Token.Span.Length > MaxSourceLength) return;
        var map = CSharpLiteralMap.Create(literal.Token);
        if (map is null) return;
        var facts = Parse(literal.Token.ValueText, context.CancellationToken);
        if (facts.Status != HtmlTokenizationStatus.Complete || facts.Diagnostics.Any(d => d.IncompleteCoverage)) return;
        var remainingMappingOperations = MaxOperations;
        foreach (var url in facts.Urls)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (url.AttributeNamespace is not null || url.ElementName == "base"
                || (url.Rel ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Contains("canonical", StringComparer.OrdinalIgnoreCase)) continue;
            if (url.AttributeName == "srcset")
            {
                var segmentIndex = 0;
                foreach (var candidate in SrcsetUrlScanner.ReadCandidates(url.Value.Value, context.CancellationToken))
                {
                    var candidateSource = MapCandidate(url.Value, candidate.Source, literal.Token.ValueText.Length,
                        context.CancellationToken, ref segmentIndex, ref remainingMappingOperations);
                    if (candidateSource is not { } source) continue;
                    Report(candidate.Value, true, source);
                }
                continue;
            }
            bool resource;
            switch (url.AttributeName)
            {
                case "href": resource = url.ElementName is not ("a" or "area"); break;
                case "src": case "poster": resource = true; break;
                case "data" when url.ElementName == "object": resource = true; break;
                case "action" when url.ElementName == "form": resource = false; break;
                default: continue; // CSS splitting and formaction are not this runtime LSQ001 projection.
            }
            Report(url.Value.Value, resource, url.Value.Source);

            void Report(string value, bool isResource, HtmlSpan source)
            {
                var issue = SiteQualityUrlRules.IndependentIssue(value, isResource);
                if (issue == SiteQualityUrlIssue.None) return;
                var span = map.Map(source.Start, source.Length);
                if (span is null) return;
                var properties = ImmutableDictionary<string, string?>.Empty.Add("staticState", "Known")
                    .Add("parserStatus", "Complete").Add("runtimeRule", "LSQ001")
                    .Add("origin", "original-literal-url-utf16-span").Add("reason", issue.ToString());
                context.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(literal.SyntaxTree, span.Value),
                    properties, url.AttributeName, issue.ToString()));
            }
        }
    }

    // Candidate offsets are decoded attribute offsets, never offsets in the source literal.
    // Advance through this attribute's segments once; an unprovable candidate stays Deferred.
    internal static HtmlSpan? MapCandidate(HtmlText value, HtmlSpan candidate, int sourceLength,
        CancellationToken cancellation, ref int segmentIndex, ref int remainingOperations)
    {
        if (candidate.Start < 0 || candidate.Length <= 0 || candidate.Start > value.Value.Length
            || candidate.Length > value.Value.Length - candidate.Start || segmentIndex < 0
            || value.Source.Start < 0 || value.Source.Length < 0 || value.Source.Start > sourceLength
            || value.Source.Length > sourceLength - value.Source.Start) return null;
        var position = candidate.Start;
        var sourceStart = -1;
        var sourceEnd = -1;
        while (segmentIndex < value.Segments.Count)
        {
            cancellation.ThrowIfCancellationRequested();
            if (remainingOperations-- <= 0) return null;
            var segment = value.Segments[segmentIndex];
            if (segment.ValueStart < 0 || segment.ValueLength <= 0 || segment.ValueStart > value.Value.Length
                || segment.ValueLength > value.Value.Length - segment.ValueStart) return null;
            var end = segment.ValueStart + segment.ValueLength;
            if (end <= position) { segmentIndex++; continue; }
            if (segment.ValueStart > position || segment.Source.Start < 0 || segment.Source.Length <= 0
                || segment.Source.Start > sourceLength || segment.Source.Length > sourceLength - segment.Source.Start
                || segment.Source.Start < value.Source.Start || segment.Source.End > value.Source.End) return null;
            var takeEnd = Math.Min(end, candidate.End);
            int mappedStart, mappedEnd;
            if (segment.ValueLength == segment.Source.Length)
            {
                mappedStart = segment.Source.Start + position - segment.ValueStart;
                mappedEnd = segment.Source.Start + takeEnd - segment.ValueStart;
            }
            else
            {
                if (position != segment.ValueStart || takeEnd != end) return null;
                mappedStart = segment.Source.Start;
                mappedEnd = segment.Source.End;
            }
            if (sourceEnd >= 0 && mappedStart != sourceEnd) return null;
            if (sourceStart < 0) sourceStart = mappedStart;
            sourceEnd = mappedEnd;
            position = takeEnd;
            if (takeEnd == end) segmentIndex++;
            if (position == candidate.End) return new HtmlSpan(sourceStart, sourceEnd - sourceStart);
        }
        return null;
    }
}
