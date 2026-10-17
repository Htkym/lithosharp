using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using LithoSharp.HtmlParsing;
using LithoSharp.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers;

internal sealed record HtmlUrlViolation(Location Location, string Attribute, bool Resource, string Value, SiteQualityUrlIssue Issue);
internal sealed record HtmlViolationKey(SyntaxTree Tree, TextSpan Span, string Attribute, bool Resource, string Value, SiteQualityUrlIssue Issue);

internal sealed class HtmlFlowUrlAnalysis(Compilation compilation)
{
    internal const int MaxRelatedLocations = 16;
    private readonly ConcurrentDictionary<HtmlViolationKey, ConcurrentDictionary<(SyntaxTree Tree, TextSpan Span), Location>> proven = new();
    private readonly Dictionary<SyntaxTree, int> treeOrder = compilation.SyntaxTrees.Select((tree, index) => (tree, index)).ToDictionary(p => p.tree, p => p.index);

    internal void Analyze(OperationBlockAnalysisContext context, HtmlStringOrigins? origins, Location use)
    {
        if (origins is null || origins.Alternatives.Count > HtmlStringOrigins.MaxAlternatives) return;
        var distinct = origins.Alternatives.Select(a => a.Value).Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length > StaticValueFlow.MaxCandidates || distinct.Sum(value => (long)value.Length) > HtmlLiteralAnalysis.MaxInputLength) return;
        var facts = new Dictionary<string, HtmlLiteralFacts>(StringComparer.Ordinal);
        foreach (var value in distinct)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var parsed = HtmlLiteralAnalysis.Parse(value, context.CancellationToken);
            if (parsed.Status != HtmlTokenizationStatus.Complete || parsed.Diagnostics.Any(d => d.IncompleteCoverage)) return;
            facts.Add(value, parsed);
        }
        var remaining = HtmlLiteralAnalysis.MaxOperations;
        var cssBudget = new LithoSharp.Quality.CssUrlScanner.Budget(HtmlLiteralAnalysis.MaxOperations);
        Dictionary<HtmlViolationKey, HtmlUrlViolation>? common = null;
        foreach (var alternative in origins.Alternatives)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var violations = HtmlLiteralAnalysis.Violations(facts[alternative.Value], alternative.Value.Length, alternative.Map, context.CancellationToken, ref remaining, cssBudget)
                .GroupBy(v => new HtmlViolationKey(v.Location.SourceTree!, v.Location.SourceSpan, v.Attribute, v.Resource, v.Value, v.Issue))
                .ToDictionary(group => group.Key, group => group.First());
            if (common is null) common = violations;
            else foreach (var key in common.Keys.ToArray()) if (!violations.ContainsKey(key)) common.Remove(key);
            if (common.Count == 0) return;
        }
        if (common is null) return;
        // Intersection belongs to this sink only. Aggregation is a union of independently proven sinks.
        foreach (var key in common.Keys)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var locations = proven.GetOrAdd(key, _ => new());
            locations.TryAdd((use.SourceTree!, use.SourceSpan), use);
            // Retain only the deterministic smallest bounded set, even under concurrent block analysis.
            lock (locations)
            {
                foreach (var excess in Order(locations.Values).Skip(MaxRelatedLocations).ToArray())
                    locations.TryRemove((excess.SourceTree!, excess.SourceSpan), out _);
            }
        }
    }

    private IOrderedEnumerable<Location> Order(IEnumerable<Location> locations) => locations.OrderBy(l => l.SourceTree!.FilePath, StringComparer.Ordinal)
        .ThenBy(l => treeOrder[l.SourceTree!]).ThenBy(l => l.SourceSpan.Start).ThenBy(l => l.SourceSpan.Length);

    internal void Report(CompilationAnalysisContext context)
    {
        foreach (var item in proven.OrderBy(p => p.Key.Tree.FilePath, StringComparer.Ordinal).ThenBy(p => treeOrder[p.Key.Tree])
            .ThenBy(p => p.Key.Span.Start).ThenBy(p => p.Key.Span.Length).ThenBy(p => p.Key.Attribute, StringComparer.Ordinal)
            .ThenBy(p => p.Key.Value, StringComparer.Ordinal).ThenBy(p => p.Key.Issue))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var properties = ImmutableDictionary<string, string?>.Empty.Add("staticState", "Known").Add("parserStatus", "Complete")
                .Add("runtimeRule", "LSQ001").Add("origin", "original-literal-url-utf16-span").Add("reason", item.Key.Issue.ToString())
                .Add("flowProof", "per-sink-all-values-and-origin-alternatives");
            var location = Location.Create(item.Key.Tree, item.Key.Span);
            context.ReportDiagnostic(Diagnostic.Create(HtmlLiteralAnalysis.Rule, location, Order(item.Value.Values).ToArray(), properties,
                item.Key.Attribute, item.Key.Issue.ToString()));
        }
    }
}
