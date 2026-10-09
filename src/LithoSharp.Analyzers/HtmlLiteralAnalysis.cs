using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using LithoSharp.HtmlParsing;
using LithoSharp.Internal;
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
        foreach (var url in facts.Urls)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (url.AttributeNamespace is not null || url.ElementName == "base"
                || (url.Rel ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Contains("canonical", StringComparer.OrdinalIgnoreCase)) continue;
            bool resource;
            switch (url.AttributeName)
            {
                case "href": resource = url.ElementName is not ("a" or "area"); break;
                case "src": case "poster": resource = true; break;
                case "data" when url.ElementName == "object": resource = true; break;
                case "action" when url.ElementName == "form": resource = false; break;
                default: continue; // srcset/CSS splitting and formaction are not this runtime LSQ001 projection.
            }
            var issue = SiteQualityUrlRules.IndependentIssue(url.Value.Value, resource);
            if (issue == SiteQualityUrlIssue.None) continue;
            var span = map.Map(url.Value.Source.Start, url.Value.Source.Length);
            if (span is null) continue;
            var properties = ImmutableDictionary<string, string?>.Empty.Add("staticState", "Known")
                .Add("parserStatus", "Complete").Add("runtimeRule", "LSQ001")
                .Add("origin", "original-literal-url-utf16-span").Add("reason", issue.ToString());
            context.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(literal.SyntaxTree, span.Value),
                properties, url.AttributeName, issue.ToString()));
        }
    }
}
