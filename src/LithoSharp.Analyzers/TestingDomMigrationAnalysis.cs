using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

// A metadata-only migration notice. No DOM, getter or user code is executed.
internal static class TestingDomMigrationAnalysis
{
    internal static readonly DiagnosticDescriptor Rule = new("LSA1501", "Legacy Testing DOM access",
        "Migrate SiteTestDocument.Document to Snapshot.Query/QueryAll or Assert methods; DOM mutation and casts require manual migration",
        "Migration", DiagnosticSeverity.Warning, isEnabledByDefault: true,
        helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/docs/Testing-v2-api.md#migration");

    internal static void Register(CompilationStartAnalysisContext start)
    {
        var owner = start.Compilation.GetTypeByMetadataName("LithoSharp.Testing.SiteTestDocument");
        if (owner?.ContainingAssembly.Identity.Name != "LithoSharp.Testing" || owner.Locations.Any(l => l.IsInSource)) return;
        var candidates = owner.GetMembers("Document").OfType<IPropertySymbol>().Where(property =>
            !property.IsStatic && !property.IsIndexer && property.Parameters.Length == 0
            && property.DeclaredAccessibility == Accessibility.Public && property.SetMethod is null
            && property.GetMethod?.DeclaredAccessibility == Accessibility.Public
            && property.Type is INamedTypeSymbol { TypeKind: TypeKind.Interface } result
            && result.MetadataName == "IDocument" && result.ContainingType is null
            && result.ContainingNamespace.ToDisplayString() == "AngleSharp.Dom"
            && result.ContainingAssembly.Identity.Name == "AngleSharp" && !result.Locations.Any(l => l.IsInSource)).ToArray();
        if (candidates.Length != 1) return;
        var legacy = candidates[0].OriginalDefinition;
        start.RegisterOperationAction(context =>
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (context.Operation is IPropertyReferenceOperation { IsImplicit: false } reference
                && SymbolEqualityComparer.Default.Equals(reference.Property.OriginalDefinition, legacy))
                context.ReportDiagnostic(Diagnostic.Create(Rule, reference.Syntax.GetLocation()));
        }, OperationKind.PropertyReference);
    }
}
