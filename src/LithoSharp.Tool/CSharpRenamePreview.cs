using System.Text;
using LithoSharp.Inspection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Syntamark;

namespace LithoSharp.Tool;

internal sealed record CSharpRenameIssue(string Reason, string? File = null, int? Start = null, string? Detail = null);
internal sealed record CSharpRenameCompilerDiagnostic(string Id, string Severity, string Message, string? File, int? Start);

// A metadata-bound subset, not a project-wide rename. Applying returns buffers and never writes files.
internal sealed class CSharpRenamePreview
{
    private readonly ExplicitCSharpSnapshot snapshot;
    private readonly DocumentReferenceEditPlan edits;

    private CSharpRenamePreview(ExplicitCSharpSnapshot snapshot, DocumentReferenceEditPlan edits,
        IReadOnlyList<CSharpRenameIssue> issues, IReadOnlyList<CSharpRenameCompilerDiagnostic> diagnostics,
        IReadOnlyList<string> generatedFiles, string? target)
        => (this.snapshot, this.edits, Issues, Diagnostics, GeneratedFiles, Target) =
            (snapshot, edits, issues, diagnostics, generatedFiles, target);

    internal DocumentReferenceEditPlan Edits => edits;
    internal IReadOnlyList<CSharpRenameIssue> Issues { get; }
    internal IReadOnlyList<CSharpRenameCompilerDiagnostic> Diagnostics { get; }
    internal IReadOnlyList<string> GeneratedFiles { get; }
    internal string? Target { get; }
    internal bool CanApply => edits.CanApply;

    internal static async Task<CSharpRenamePreview> CreateAsync(ExplicitCSharpSnapshot snapshot, string metadataTypeName,
        string memberName, string newName, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var replacementToken = SyntaxFactory.ParseToken(newName);
        if (!replacementToken.IsKind(SyntaxKind.IdentifierToken) || replacementToken.ContainsDiagnostics
            || replacementToken.FullSpan.Length != newName.Length || replacementToken.Text != newName
            || replacementToken.LeadingTrivia.Count != 0 || replacementToken.TrailingTrivia.Count != 0)
            throw new ArgumentException("The new name must be one C# identifier; escape a keyword with @.", nameof(newName));

        var documents = snapshot.Sources.Select(source => new DocumentReferenceDocument(source.File, source.Text.ToString(), 0)).ToArray();
        var generated = new List<string>();
        var active = new List<ExplicitCSharpSource>();
        foreach (var source in snapshot.Sources)
        {
            token.ThrowIfCancellationRequested();
            if (IsGenerated(source, await source.Tree.GetRootAsync(token).ConfigureAwait(false)))
                generated.Add(source.File);
            else
                active.Add(source);
        }
        var compilation = CSharpCompilation.Create("CSharpRenameInput", active.Select(source => source.Tree), snapshot.Metadata,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var issues = new List<CSharpRenameIssue>();
        var diagnostics = compilation.GetDiagnostics(token).Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic =>
            {
                var source = active.FirstOrDefault(input => input.Tree == diagnostic.Location.SourceTree);
                return new CSharpRenameCompilerDiagnostic(diagnostic.Id, diagnostic.Severity.ToString(),
                    diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), source?.File,
                    source is null ? null : diagnostic.Location.SourceSpan.Start + source.Offset);
            }).ToArray();
        if (diagnostics.Length != 0)
            issues.Add(new("input_compilation_errors", Detail: "Only individually proven bindings are eligible; resolve the reported errors and review the remaining scope."));
        foreach (var file in generated)
            issues.Add(new("generated_source_excluded", file));

        var owner = compilation.GetTypeByMetadataName(metadataTypeName);
        var candidates = owner?.GetMembers(memberName).Where(member => member.IsStatic
            && member is IFieldSymbol or IPropertySymbol or IMethodSymbol).ToArray() ?? [];
        var target = candidates.Length == 1 ? candidates[0] : null;
        var blocked = owner is null || owner.Locations.Any(location => location.IsInSource) || target is null;
        if (blocked)
            issues.Add(new("unique_metadata_member_required", Detail: "Supply the original metadata DLL and an exact type name with one static field, property, or method."));
        else if (memberName == replacementToken.ValueText || HierarchyMembers(owner!, replacementToken.ValueText).Any())
        {
            blocked = true;
            issues.Add(new("member_name_unchanged_or_conflicting"));
        }

        var changes = new List<DocumentReferenceTextEdit>();
        if (!blocked)
        {
            foreach (var source in active)
            {
                token.ThrowIfCancellationRequested();
                var model = compilation.GetSemanticModel(source.Tree);
                var root = await source.Tree.GetRootAsync(token).ConfigureAwait(false);
                foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    token.ThrowIfCancellationRequested();
                    if (name.Identifier.ValueText != memberName)
                        continue;
                    var binding = model.GetSymbolInfo(name, token);
                    if (!SymbolEqualityComparer.Default.Equals(binding.Symbol?.OriginalDefinition, target!.OriginalDefinition))
                    {
                        if (binding.Symbol is null)
                            issues.Add(new("unresolved_or_ambiguous_name", source.File, name.Identifier.SpanStart + source.Offset));
                        continue;
                    }
                    var reason = UnsafeUse(name, model, target, replacementToken.ValueText, token);
                    if (reason is not null)
                    {
                        issues.Add(new(reason, source.File, name.Identifier.SpanStart + source.Offset));
                        continue;
                    }
                    var replacement = name.Identifier.Text.StartsWith('@') && !newName.StartsWith('@') ? "@" + newName : newName;
                    var document = documents.Single(document => document.SourcePath == source.File);
                    changes.Add(new(document, new RawSpan(name.Identifier.SpanStart + source.Offset, name.Identifier.Span.Length), replacement));
                }
            }
        }

        if (!await InputsMatchAsync(snapshot, token).ConfigureAwait(false))
        {
            blocked = true;
            issues.Add(new("stale_source_or_metadata"));
        }
        var manual = issues.Select(issue => issue.Reason + (issue.File is null ? string.Empty : ": " + issue.File)).ToList();
        manual.Add("Files and conditional-compilation configurations outside the explicit inputs were not inspected.");
        manual.Add("Regenerate the metadata declaration from its source input; generated declarations are never edited here.");
        manual.Add("Review reflection, strings, comments, documentation references, inferred names, and downstream dependencies separately.");
        var plan = new DocumentReferenceEditPlan(documents, changes, [], manual, blocked);
        return new(snapshot, plan, Array.AsReadOnly(issues.ToArray()), Array.AsReadOnly(diagnostics),
            Array.AsReadOnly(generated.ToArray()), target?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
    }

    internal async Task<IReadOnlyDictionary<string, string>?> TryApplyAsync(CancellationToken token)
    {
        if (!CanApply || !await InputsMatchAsync(snapshot, token).ConfigureAwait(false))
            return null;
        var current = new List<DocumentReferenceDocument>();
        try
        {
            foreach (var input in snapshot.Sources)
            {
                var bytes = await File.ReadAllBytesAsync(input.File, token).ConfigureAwait(false);
                if (ExplicitCSharpSnapshot.Hash(bytes) != input.Hash)
                    return null;
                current.Add(new(input.File, new UTF8Encoding(false, true).GetString(bytes), 0));
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        token.ThrowIfCancellationRequested();
        return edits.TryApply(current, out var changed) ? changed : null;
    }

    private static async Task<bool> InputsMatchAsync(ExplicitCSharpSnapshot snapshot, CancellationToken token)
    {
        foreach (var (file, expected) in snapshot.Sources.Select(source => (source.File, source.Hash))
            .Concat(snapshot.References.Select(reference => (reference.File, reference.Hash))))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (ExplicitCSharpSnapshot.Hash(await File.ReadAllBytesAsync(file, token).ConfigureAwait(false)) != expected)
                    return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
        return true;
    }

    private static IEnumerable<ISymbol> HierarchyMembers(INamedTypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
            foreach (var member in current.GetMembers(name))
                yield return member;
    }

    private static bool IsGenerated(ExplicitCSharpSource source, SyntaxNode root)
        => source.File.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || source.File.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || source.File.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            || root.GetLeadingTrivia().ToFullString().Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);

    private static string? UnsafeUse(SimpleNameSyntax name, SemanticModel model, ISymbol target, string newName, CancellationToken token)
    {
        ExpressionSyntax expression = name;
        if (name.Parent is MemberAccessExpressionSyntax access && access.Name == name)
        {
            if (!SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression, token).Symbol, target.ContainingType))
                return "receiver_not_exact_metadata_owner";
            expression = access;
        }
        else if (name.Parent is MemberBindingExpressionSyntax)
            return "conditional_member_access_requires_review";
        else if (model.LookupSymbols(name.SpanStart, name: newName).Any(symbol => !SymbolEqualityComparer.Default.Equals(symbol, target)))
            return "unqualified_new_name_would_capture_symbol";

        while (expression.Parent is ParenthesizedExpressionSyntax parentheses)
            expression = parentheses;
        if (expression.Parent is AnonymousObjectMemberDeclaratorSyntax { NameEquals: null }
            || expression.Parent is ArgumentSyntax { NameColon: null } argument && argument.Parent is TupleExpressionSyntax)
            return "inferred_member_name_requires_review";
        return null;
    }
}
