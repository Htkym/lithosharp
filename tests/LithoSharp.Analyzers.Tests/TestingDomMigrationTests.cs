using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task LegacyTestingMetadataReportsWarningAtOriginalDomGetterWithoutCoreSinks()
    {
        // This is a metadata-only consumer: even throwing user code is never executed.
        const string source = "// 😀 original UTF16 evidence\n"
            + "class C { static C() { throw new System.Exception(); } "
            + "static LithoSharp.Testing.SiteTestDocument Owner => throw new System.Exception(); "
            + "void M(LithoSharp.Testing.SiteTestDocument doc) { var unused = Owner; "
            + "var dom = doc.Document; dom.QuerySelector(\"p:has(a)\")?.SetAttribute(\"class\", \"changed\"); } }";
        var compilation = Compile(source, core: false, file: "legacy-testing.cs").AddReferences(
            MetadataReference.CreateFromFile(Required("LSA1501_LEGACY_TESTING_METADATA")),
            MetadataReference.CreateFromFile(Required("LSA1501_LEGACY_ANGLESHARP_METADATA")));
        Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Legacy consumer must bind the published Testing/AngleSharp metadata");
        Check(compilation.GetTypeByMetadataName("LithoSharp.SiteUrl") is null, "Testing-only compilation cannot register Core URL sinks");
        var tree = compilation.SyntaxTrees.Single();
        var access = tree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Single(node => node.ToString() == "doc.Document");
        var getter = compilation.GetSemanticModel(tree).GetSymbolInfo(access).Symbol as IPropertySymbol;
        var actualOwner = compilation.GetTypeByMetadataName("LithoSharp.Testing.SiteTestDocument");
        var actualDom = compilation.GetTypeByMetadataName("AngleSharp.Dom.IDocument");
        Check(getter is { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public, SetMethod: null }
            && getter.GetMethod?.DeclaredAccessibility == Accessibility.Public
            && getter.Locations.All(location => location.IsInMetadata)
            && SymbolEqualityComparer.Default.Equals(getter.ContainingType, actualOwner)
            && SymbolEqualityComparer.Default.Equals(getter.Type, actualDom), "Canary uses the actual public instance read-only DOM getter");
        var diagnostics = await Analyze(compilation);
        Check(diagnostics.Length == 1 && diagnostics[0].Id == "LSA1501"
            && diagnostics[0].Severity == DiagnosticSeverity.Warning, "DOM migration is one Warning, even without StaticCore sinks");
        var expected = new TextSpan(source.IndexOf("doc.Document", StringComparison.Ordinal), "doc.Document".Length);
        Check(diagnostics[0].Location.SourceSpan == expected && diagnostics[0].Location.SourceTree?.FilePath == "legacy-testing.cs",
            "Migration warning covers the original UTF16 member access, not its receiver or name alone");
        Check(diagnostics[0].GetMessage().Contains("Snapshot", StringComparison.Ordinal), "Warning identifies the read-only migration target");
        Check(Packed.Value.Assembly.GetReferencedAssemblies().All(reference => !Forbidden(reference.Name!)), "Packed analyzer does not bring IDE CodeFix or DOM runtime dependencies into this host");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name is "LithoSharp.Testing"
            || Forbidden(assembly.GetName().Name!)), "Testing and AngleSharp remain metadata only; user getters, static initializers and DOM mutation cannot run");
    }

    [Test]
    public async Task ReadOnlyV2UnresolvedDocumentAndUserOwnedApisDoNotReceiveMigrationWarnings()
    {
        const string snapshotSource = "class C { void M(LithoSharp.Testing.SiteTestDocument doc) { var text = doc.Snapshot.Query(\"p\")?.TextContent; } }";
        var v2 = Compile(snapshotSource, core: false).AddReferences(
            MetadataReference.CreateFromFile(Required("LSA1501_V2_TESTING_METADATA")));
        Check(!v2.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Actual v2 snapshot read must bind");
        Check((await Analyze(v2)).Length == 0, "Read-only v2 snapshot is already migrated");

        var removed = Compile("class C { void M(LithoSharp.Testing.SiteTestDocument doc) { var dom = doc.Document; } }", core: false)
            .AddReferences(MetadataReference.CreateFromFile(Required("LSA1501_V2_TESTING_METADATA")));
        Check(removed.GetDiagnostics().Any(d => d.Id == "CS1061" && d.Severity == DiagnosticSeverity.Error), "Removed v2 getter remains a compiler error");
        Check((await Analyze(removed)).Length == 0, "Unresolved Document is not a legacy migration warning");

        const string lookalike = "namespace LithoSharp.Testing { public sealed class SiteTestDocument { "
            + "static SiteTestDocument() { throw new System.Exception(); } "
            + "public AngleSharp.Dom.IDocument Document => throw new System.Exception(); } } "
            + "class C { void M(LithoSharp.Testing.SiteTestDocument doc) { var dom = doc.Document; } }";
        var userOwner = Compile(lookalike, core: false).WithAssemblyName("LithoSharp.Testing")
            .AddReferences(MetadataReference.CreateFromFile(Required("LSA1501_LEGACY_ANGLESHARP_METADATA")));
        Check(!userOwner.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Same namespace, assembly name and DOM return type still form a valid user API");
        Check((await Analyze(userOwner)).Length == 0, "Source-defined owner is excluded even when its names and return type resemble the legacy API");

        const string fakeApi = "class Other { public AngleSharp.Dom.IDocument Document => throw new System.Exception(); } "
            + "class C { void M(Other doc) { var dom = doc.Document; } }";
        var unrelated = Compile(fakeApi, core: false).AddReferences(
            MetadataReference.CreateFromFile(Required("LSA1501_LEGACY_TESTING_METADATA")),
            MetadataReference.CreateFromFile(Required("LSA1501_LEGACY_ANGLESHARP_METADATA")));
        Check(!unrelated.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Unrelated getter must bind in a compilation containing actual legacy metadata");
        Check((await Analyze(unrelated)).Length == 0, "Document property name and actual DOM return type do not authorize an unrelated owner");

        using var nestedDomImage = new MemoryStream();
        var nestedDomLibrary = Compile("namespace AngleSharp.Dom { public static class Container { public interface IDocument { } } }", core: false)
            .WithAssemblyName("AngleSharp");
        Check(nestedDomLibrary.Emit(nestedDomImage).Success, "Nested DOM lookalike emits as metadata without loading its assembly");
        var nestedDomReference = MetadataReference.CreateFromImage(nestedDomImage.ToArray());
        using var nestedOwnerImage = new MemoryStream();
        var nestedOwnerLibrary = Compile("namespace LithoSharp.Testing { public sealed class SiteTestDocument { "
            + "public AngleSharp.Dom.Container.IDocument Document => throw new System.Exception(); } }", core: false)
            .WithAssemblyName("LithoSharp.Testing").AddReferences(nestedDomReference);
        Check(nestedOwnerLibrary.Emit(nestedOwnerImage).Success, "Legacy-named owner with a nested return type emits as metadata");
        var nestedConsumer = Compile("class C { void M(LithoSharp.Testing.SiteTestDocument doc) { var dom = doc.Document; } }", core: false)
            .AddReferences(nestedDomReference, MetadataReference.CreateFromImage(nestedOwnerImage.ToArray()));
        Check(!nestedConsumer.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Nested metadata return type must bind; zero warnings cannot hide a compiler error");
        var nestedTree = nestedConsumer.SyntaxTrees.Single();
        var nestedAccess = nestedTree.GetRoot().DescendantNodes().OfType<MemberAccessExpressionSyntax>().Single();
        var nestedGetter = nestedConsumer.GetSemanticModel(nestedTree).GetSymbolInfo(nestedAccess).Symbol as IPropertySymbol;
        Check(nestedGetter?.Type is INamedTypeSymbol { MetadataName: "IDocument", ContainingType.MetadataName: "Container" } nestedResult
            && nestedResult.ContainingAssembly.Identity.Name == "AngleSharp"
            && nestedGetter.Locations.All(location => location.IsInMetadata)
            && nestedConsumer.GetTypeByMetadataName("AngleSharp.Dom.IDocument") is null,
            "Bound nested IDocument is metadata but is not the top-level DOM contract");
        Check((await Analyze(nestedConsumer)).Length == 0, "Nested IDocument in an AngleSharp-named assembly is outside the exact legacy return contract");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name is "LithoSharp.Testing"
            || Forbidden(assembly.GetName().Name!)), "Emitted lookalike assemblies remain metadata only");
    }

    [Test]
    public async Task LegacyTestingDomWarningHonorsPragmaAndGeneratedCodePolicy()
    {
        const string body = "class C { void M(LithoSharp.Testing.SiteTestDocument doc) { var dom = doc.Document; } }";
        foreach (var (source, file) in new[]
        {
            ("#pragma warning disable LSA1501\n" + body, "suppressed-testing.cs"),
            ("// <auto-generated/>\n" + body, "generated-testing.g.cs")
        })
        {
            var compilation = Compile(source, core: false, file: file).AddReferences(
                MetadataReference.CreateFromFile(Required("LSA1501_LEGACY_TESTING_METADATA")),
                MetadataReference.CreateFromFile(Required("LSA1501_LEGACY_ANGLESHARP_METADATA")));
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Suppressed/generated legacy consumer must bind: " + file);
            Check((await Analyze(compilation)).Length == 0, "Migration warning respects normal Roslyn policy: " + file);
        }
    }
}
