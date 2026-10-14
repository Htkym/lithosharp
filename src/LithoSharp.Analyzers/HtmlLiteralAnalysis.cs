using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using LithoSharp.HtmlParsing;
using LithoSharp.Internal;
using LithoSharp.Quality;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

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
    internal const int MaxLiteralCount = 64;
    internal const int MaxExpressionDepth = 64;

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
        var input = Extract(argument, context.CancellationToken);
        if (input is null) return;
        var facts = Parse(input.Value, context.CancellationToken);
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
                    var candidateSource = MapCandidate(url.Value, candidate.Source, input.Value.Length,
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
                var span = input.Map(source);
                if (span is null) return;
                var properties = ImmutableDictionary<string, string?>.Empty.Add("staticState", "Known")
                    .Add("parserStatus", "Complete").Add("runtimeRule", "LSQ001")
                    .Add("origin", "original-literal-url-utf16-span").Add("reason", issue.ToString());
                context.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(argument.Syntax.SyntaxTree, span.Value),
                    properties, url.AttributeName, issue.ToString()));
            }
        }
    }

    private sealed record LiteralPiece(int Start, int Length, CSharpLiteralMap Map);

    private sealed record LiteralInput(string Value, IReadOnlyList<LiteralPiece> Pieces)
    {
        internal TextSpan? Map(HtmlSpan source)
        {
            if (source.Start < 0 || source.Length < 0 || source.Start > Value.Length
                || source.Length > Value.Length - source.Start) return null;
            // A zero-length origin at a concatenation boundary has more than one possible token.
            if (source.Length == 0 && Pieces.Count > 1
                && Pieces.Any(piece => source.Start == piece.Start || source.Start == piece.Start + piece.Length)) return null;
            foreach (var piece in Pieces)
                if (source.Start >= piece.Start && source.End <= piece.Start + piece.Length)
                    return piece.Map.Map(source.Start - piece.Start, source.Length);
            return null;
        }
    }

    private static LiteralInput? Extract(IOperation argument, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var syntax = argument.Syntax;
        var wrappingDepth = 0;
        while (syntax.Parent is ParenthesizedExpressionSyntax parent)
        {
            cancellation.ThrowIfCancellationRequested();
            if (++wrappingDepth >= MaxExpressionDepth) return null;
            syntax = parent;
            if (syntax.Span.Length > MaxSourceLength) return null;
        }
        if (argument.Type?.SpecialType != SpecialType.System_String || syntax.Span.Length > MaxSourceLength
            || !argument.ConstantValue.HasValue || argument.ConstantValue.Value is not string constant) return null;
        var syntaxLeaves = 0;
        if (!WithinSyntaxBudget(syntax, 0)) return null;
        var pieces = new List<LiteralPiece>();
        var value = new StringBuilder();
        if (!Visit(argument, 0)) return null;
        var decoded = value.ToString();
        return decoded == constant ? new LiteralInput(decoded, pieces.AsReadOnly()) : null;

        // C# parentheses can be absent from IOperation. Bound the original syntax as well.
        bool WithinSyntaxBudget(SyntaxNode node, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth >= MaxExpressionDepth) return false;
            if (node is ParenthesizedExpressionSyntax parentheses) return WithinSyntaxBudget(parentheses.Expression, depth + 1);
            if (node is BinaryExpressionSyntax binary)
                return WithinSyntaxBudget(binary.Left, depth + 1) && WithinSyntaxBudget(binary.Right, depth + 1);
            return node is LiteralExpressionSyntax && ++syntaxLeaves <= MaxLiteralCount;
        }

        bool Visit(IOperation operation, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth >= MaxExpressionDepth || operation.Type?.SpecialType != SpecialType.System_String) return false;
            if (operation is IParenthesizedOperation parentheses) return Visit(parentheses.Operand, depth + 1);
            if (operation is IConversionOperation conversion)
                return conversion.IsImplicit && conversion.Conversion.IsIdentity && !conversion.Conversion.IsUserDefined
                    && Visit(conversion.Operand, depth + 1);
            if (operation is IBinaryOperation binary)
                return binary.OperatorKind == BinaryOperatorKind.Add && binary.OperatorMethod is null && !binary.IsLifted
                    && binary.LeftOperand.Type?.SpecialType == SpecialType.System_String
                    && binary.RightOperand.Type?.SpecialType == SpecialType.System_String
                    && Visit(binary.LeftOperand, depth + 1) && Visit(binary.RightOperand, depth + 1);
            if (operation is not ILiteralOperation) return false;
            var syntax = operation.Syntax;
            while (syntax is ParenthesizedExpressionSyntax parenthesized)
            {
                cancellation.ThrowIfCancellationRequested();
                if (++depth >= MaxExpressionDepth) return false;
                syntax = parenthesized.Expression;
            }
            if (syntax is not LiteralExpressionSyntax literal || literal.ContainsDiagnostics
                || !operation.ConstantValue.HasValue || operation.ConstantValue.Value is not string text
                || text != literal.Token.ValueText || pieces.Count >= MaxLiteralCount
                || text.Length > MaxInputLength - value.Length) return false;
            var map = CSharpLiteralMap.Create(literal.Token);
            if (map is null) return false;
            pieces.Add(new LiteralPiece(value.Length, text.Length, map));
            value.Append(text); // Decode each token first; never reinterpret escapes across token boundaries.
            return true;
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
