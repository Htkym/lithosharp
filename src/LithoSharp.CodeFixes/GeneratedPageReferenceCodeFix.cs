using System.Collections.Immutable;
using System.Collections.Generic;
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
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.CodeFixes;

/// <summary>Replaces a proven literal argument while retaining the original runtime lookup.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GeneratedPageReferenceCodeFix)), Shared]
public sealed class GeneratedPageReferenceCodeFix : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create("LSA1401");
    public override FixAllProvider GetFixAllProvider() => DocumentFixAll.Instance;
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

    private sealed class DocumentFixAll : FixAllProvider
    {
        internal static readonly DocumentFixAll Instance = new();
        public override IEnumerable<FixAllScope> GetSupportedFixAllScopes() => new[] { FixAllScope.Document };

        public override async Task<CodeAction?> GetFixAsync(FixAllContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var document = context.Document;
            if (context.Scope != FixAllScope.Document || document is null
                || context.CodeActionEquivalenceKey != "LSA1401.GeneratedPagePath"
                || document.Project.Solution.Workspace.CurrentSolution != document.Project.Solution) return null;
            var diagnostics = await context.GetDocumentDiagnosticsAsync(document).ConfigureAwait(false);
            if ((await Replacements(document, diagnostics, context.CancellationToken).ConfigureAwait(false)).IsDefaultOrEmpty) return null;
            var solution = document.Project.Solution;
            if (solution.Workspace.CurrentSolution != solution) return null;
            return CodeAction.Create("Use generated page reference paths in this document", async cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                var current = solution.Workspace.CurrentSolution;
                // Any source, AdditionalFile, analyzer config or project-context edit
                // invalidates the whole action, including edits outside this document.
                if (current != solution) return current;
                var replacements = await Replacements(document, diagnostics, cancellation).ConfigureAwait(false);
                if (replacements.IsDefaultOrEmpty) return solution.Workspace.CurrentSolution;
                var editor = await DocumentEditor.CreateAsync(document, cancellation).ConfigureAwait(false);
                foreach (var replacement in replacements)
                {
                    cancellation.ThrowIfCancellationRequested();
                    editor.ReplaceNode(replacement.Literal,
                        SyntaxFactory.ParseExpression(replacement.Path).WithTriviaFrom(replacement.Literal));
                }
                cancellation.ThrowIfCancellationRequested();
                var changed = editor.GetChangedDocument().Project.Solution;
                return solution.Workspace.CurrentSolution == solution ? changed : solution.Workspace.CurrentSolution;
            }, "LSA1401.GeneratedPagePath");
        }

        private static async Task<ImmutableArray<(LiteralExpressionSyntax Literal, string Path)>> Replacements(
            Document document, ImmutableArray<Diagnostic> diagnostics, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var root = await document.GetSyntaxRootAsync(cancellation).ConfigureAwait(false);
            var compilation = await document.Project.GetCompilationAsync(cancellation).ConfigureAwait(false);
            if (root is null || compilation is null) return default;
            var analysis = StaticManifestAnalysis.Create(compilation, document.Project.AnalyzerOptions, cancellation);
            if (analysis is null) return default;
            var spans = new HashSet<TextSpan>();
            var replacements = ImmutableArray.CreateBuilder<(LiteralExpressionSyntax Literal, string Path)>();
            // Prove every independent literal against the same original compilation.
            // Sequential single fixes would change the provenance fingerprint midway.
            foreach (var diagnostic in diagnostics)
            {
                cancellation.ThrowIfCancellationRequested();
                var span = diagnostic.Location.SourceSpan;
                if (diagnostic.Id != "LSA1401" || diagnostic.IsSuppressed
                    || diagnostic.Location.SourceTree != root.SyntaxTree || !root.FullSpan.Contains(span)
                    || root.FindNode(span, getInnermostNodeForTie: true) is not LiteralExpressionSyntax literal
                    || literal.Span != span || analysis.TryGetTypedReference(literal, cancellation) is not string path) return default;
                if (spans.Add(span)) replacements.Add((literal, path));
            }
            return replacements.ToImmutable();
        }
    }
}
