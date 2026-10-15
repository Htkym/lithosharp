using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers;

// Discovery uses Roslyn's original operation callbacks, independently of the bounded CFG proof.
// Missing proof is a policy result, never evidence of an absent reference or dynamic producer.
internal sealed class StaticCoveragePolicy(Compilation compilation)
{
    internal static readonly DiagnosticDescriptor Rule = new("LSA9001", "Requested static coverage is unverified",
        "The requested {0} static coverage remains {1} ({2})", "LithoSharp", DiagnosticSeverity.Error,
        isEnabledByDefault: false, helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/src/LithoSharp.Analyzers/README.md#lsa9001",
        customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });
    private sealed record Key(SyntaxTree Tree, TextSpan Span, string Scope);
    private sealed record Root(SyntaxTree Tree, TextSpan Span);
    private sealed record Request(Root Root, bool Empty);
    private sealed record Proof(string State, string Reason);
    private readonly ConcurrentDictionary<Key, Request> requests = new();
    private readonly ConcurrentDictionary<Key, Proof> proofs = new();
    private readonly ConcurrentDictionary<Root, string> interruptions = new();
    private readonly ImmutableDictionary<SyntaxTree, int> treeOrder = compilation.SyntaxTrees
        .Select((tree, index) => (tree, index)).ToImmutableDictionary(p => p.tree, p => p.index);

    private static Root OriginalRoot(IOperation operation)
    {
        while (operation.Parent is { } parent) operation = parent;
        return new(operation.Syntax.SyntaxTree, operation.Syntax.Span);
    }
    internal void Discover(IOperation operation, Location location, string scope, bool empty = false)
    {
        var key = new Key(location.SourceTree!, location.SourceSpan, scope);
        var request = new Request(OriginalRoot(operation), empty);
        requests.AddOrUpdate(key, request, (_, old) => old.Empty || empty ? old with { Empty = true } : old);
    }
    internal void Record(IOperation operation, Location location, string scope, string state, string reason)
    {
        Discover(operation, location, scope);
        var key = new Key(location.SourceTree!, location.SourceSpan, scope);
        var proof = new Proof(state, reason);
        // An actual incomplete proof absorbs a successful visit of the same original request.
        proofs.AddOrUpdate(key, proof, (_, old) => Rank(old.State) < Rank(state) ? old
            : Rank(old.State) > Rank(state) ? proof
            : string.CompareOrdinal(old.Reason, reason) <= 0 ? old : proof);
    }
    private static int Rank(string state) => state switch
    { "Unavailable" => 0, "Stale" => 1, "Open" => 2, "Unknown" => 3, "Deferred" => 4, _ => 5 };

    internal void Interrupt(IOperation root, string reason) => interruptions.AddOrUpdate(OriginalRoot(root), reason,
        (_, old) => string.CompareOrdinal(old, reason) <= 0 ? old : reason);

    internal void Report(CompilationAnalysisContext context)
    {
        foreach (var item in requests.OrderBy(p => p.Key.Tree.FilePath, StringComparer.Ordinal).ThenBy(p => treeOrder[p.Key.Tree])
            .ThenBy(p => p.Key.Span.Start).ThenBy(p => p.Key.Span.Length).ThenBy(p => p.Key.Scope, StringComparer.Ordinal))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            // A source-proven empty selection does not need registration, fingerprint or CFG proof.
            if (item.Value.Empty) continue;
            var proof = proofs.TryGetValue(item.Key, out var found) ? found : new Proof("Unavailable",
                interruptions.TryGetValue(item.Value.Root, out var reason) ? reason : "flow-proof-unavailable");
            if (proof.State == "Complete") continue;
            var properties = ImmutableDictionary<string, string?>.Empty.Add("scope", item.Key.Scope)
                .Add("scopeState", proof.State).Add("reason", proof.Reason).Add("stage", "compiler")
                .Add("ruleOwner", "Analyzer").Add("origin", "original-coverage-request-utf16-span")
                .Add("policy", "RequireStaticCoverage");
            context.ReportDiagnostic(Diagnostic.Create(Rule, Location.Create(item.Key.Tree, item.Key.Span), properties,
                item.Key.Scope, proof.State, proof.Reason));
        }
    }
}
