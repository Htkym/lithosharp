using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LithoSharp.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;

namespace LithoSharp.CodeFixes;

/// <summary>Replaces a proven literal argument while retaining the original runtime lookup.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GeneratedPageReferenceCodeFix)), Shared]
public sealed class GeneratedPageReferenceCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("LSA1401");
    // Independence under type initialization and overlapping edits is not proved.
    public override FixAllProvider? GetFixAllProvider() => null;
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var version = await context.Document.GetTextVersionAsync(context.CancellationToken).ConfigureAwait(false);
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        var literal = root?.FindNode(context.Span, getInnermostNodeForTie: true) as LiteralExpressionSyntax;
        if (literal is null || await Replacement(context.Document, literal, context.CancellationToken).ConfigureAwait(false) is null) return;
        context.RegisterCodeFix(CodeAction.Create("Use the generated page reference path", async cancellation =>
        {
            // Standard workspace actions use the original solution. Reject stale actions
            // rather than silently applying a span to another document version.
            var current = context.Document.Project.Solution.Workspace.CurrentSolution.GetDocument(context.Document.Id);
            if (current is null || current.Project.Solution != context.Document.Project.Solution
                || await current.GetTextVersionAsync(cancellation).ConfigureAwait(false) != version) return context.Document.Project.Solution.Workspace.CurrentSolution;
            var replacement = await Replacement(current, literal, cancellation).ConfigureAwait(false);
            if (replacement is null) return current.Project.Solution;
            var editor = await DocumentEditor.CreateAsync(current, cancellation).ConfigureAwait(false);
            editor.ReplaceNode(literal, SyntaxFactory.ParseExpression(replacement).WithTriviaFrom(literal));
            return editor.GetChangedDocument().Project.Solution;
        }, "LSA1401.GeneratedPagePath"), context.Diagnostics);
    }
    private static async Task<string?> Replacement(Document document, LiteralExpressionSyntax literal, CancellationToken cancellation)
    {
        var compilation = await document.Project.GetCompilationAsync(cancellation).ConfigureAwait(false);
        return compilation is null ? null : StaticManifestAnalysis.Create(compilation, document.Project.AnalyzerOptions, cancellation)
            ?.TryGetTypedReference(literal, cancellation);
    }
}
