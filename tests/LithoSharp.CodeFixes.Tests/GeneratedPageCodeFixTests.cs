using System.Collections.Immutable;
using System.IO.Compression;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

[NotInParallel]
public sealed partial class AnalyzerPackageHostTests
{
    private static readonly Lazy<CodeFixProvider> Fixer = new(CreateFixer);
    private static CodeFixProvider CreateFixer()
    {
        _ = Packed.Value;
        using var zip = ZipFile.OpenRead(Required("RA06_CODEFIX_NUPKG"));
        Check(zip.Entries.Where(e => e.FullName.EndsWith(".dll")).Select(e => e.FullName)
            .SequenceEqual(["analyzers/dotnet/cs/LithoSharp.CodeFixes.dll"]), "CodeFix package has no Core, runtime or Workspaces payload");
        var path = Path.Combine(Required("RA01_PAYLOAD_DIR"), "LithoSharp.CodeFixes.dll");
        using (var source = zip.GetEntry("analyzers/dotnet/cs/LithoSharp.CodeFixes.dll")!.Open())
        using (var target = File.Create(path)) source.CopyTo(target);
        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        return (CodeFixProvider)Activator.CreateInstance(assembly.GetType("LithoSharp.CodeFixes.GeneratedPageReferenceCodeFix")!)!;
    }
    private static async Task<(AdhocWorkspace Workspace, Document Document, Diagnostic Diagnostic)> FixWorkspace(string source)
    {
        var initial = Compile(source, true).AddReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) == "System.Collections.dll").Select(p => MetadataReference.CreateFromFile(p)));
        var workspace = new AdhocWorkspace();
        var id = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), "Site", initial.AssemblyName!, LanguageNames.CSharp,
            compilationOptions: initial.Options, parseOptions: initial.SyntaxTrees.First().Options, metadataReferences: initial.References));
        var documentId = DocumentId.CreateNewId(id);
        solution = solution.AddDocument(documentId, "canary.cs", SourceText.From(source), filePath: "canary.cs");
        var input = new ManifestInput();
        solution = solution.AddAdditionalDocument(DocumentId.CreateNewId(id), "intro.md", SourceText.From(input.Content!), filePath: input.Path);
        var config = """
            is_global = true
            build_property.MSBuildProjectDirectory = D:/site
            build_property.TargetFramework = net10.0
            build_property.Configuration = Release
            build_property.LithoSharpAnalysisProfile = default

            [D:/site/intro.md]
            build_metadata.AdditionalFiles.LithoSharpCollection = guides
            build_metadata.AdditionalFiles.LithoSharpId = intro
            build_metadata.AdditionalFiles.LithoSharpRoute = intro/
            build_metadata.AdditionalFiles.LithoSharpSite = docs
            build_metadata.AdditionalFiles.LithoSharpVariant = ja
            """;
        solution = solution.AddAnalyzerConfigDocument(DocumentId.CreateNewId(id), ".globalconfig", SourceText.From(config), filePath: "D:/site/.globalconfig");
        Check(workspace.TryApplyChanges(solution), "Initial workspace applies");
        var project = workspace.CurrentSolution.GetProject(id)!;
        var compilation = (CSharpCompilation)(await project.GetCompilationAsync())!;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(ManifestGenerators.Value, project.AnalyzerOptions.AdditionalFiles,
            (CSharpParseOptions)project.ParseOptions!, project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Check(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), "Actual generator binds in workspace: " + string.Join("\n", diagnostics));
        foreach (var tree in output.SyntaxTrees.Where(t => t.FilePath != "canary.cs"))
            solution = workspace.CurrentSolution.AddDocument(DocumentId.CreateNewId(id), Path.GetFileName(tree.FilePath), tree.GetText(), filePath: tree.FilePath);
        Check(workspace.TryApplyChanges(solution), "Generated workspace source applies");
        var document = workspace.CurrentSolution.GetDocument(documentId)!;
        var ds = await ((await document.Project.GetCompilationAsync())!).WithAnalyzers(Packed.Value.Analyzers, document.Project.AnalyzerOptions).GetAnalyzerDiagnosticsAsync();
        Check(!ds.Any(d => d.Id == "AD0001"), "No analyzer crash");
        var diagnostic = ds.Single(d => d.Id == "LSA1401");
        return (workspace, document, diagnostic);
    }
    private static async Task<List<CodeAction>> Actions(Document document, Diagnostic diagnostic, CancellationToken cancellation = default)
    {
        var actions = new List<CodeAction>();
        await Fixer.Value.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), cancellation));
        return actions;
    }

    [Test]
    public async Task CodeFixPackedHostAppliesOneEquivalentArgumentWithTriviaAndKeepsTheLookup()
    {
        var source = ManifestModel.Replace("Guides", "@event") + """

            public class Calls {
              public LithoSharp.SiteUrl M() => @event.Manifest.GetUrl(/* keep */ "/intro/" /* tail */);
            }
            """;
        var (workspace, document, diagnostic) = await FixWorkspace(source);
        using (workspace)
        {
            Check(diagnostic.Severity == DiagnosticSeverity.Info, "Default recommendation is Info");
            var original = (await document.Project.GetCompilationAsync())!;
            var originalNode = (await document.GetSyntaxRootAsync())!.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
            var originalSymbol = original.GetSemanticModel(originalNode.SyntaxTree).GetSymbolInfo(originalNode).Symbol;
            var actions = await Actions(document, diagnostic);
            Check(actions.Count == 1 && Fixer.Value.GetFixAllProvider() is null, "One proved fix; no guessed FixAll");
            var operation = (await actions[0].GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
            var changed = operation.ChangedSolution.GetDocument(document.Id)!;
            var text = (await changed.GetTextAsync()).ToString();
            Check(text.Contains("@event.Manifest.GetUrl(/* keep */ global::@event.Pages.Intro.Route.PublicPath /* tail */)"), "Fully qualified replacement and both comments: " + text);
            var compiled = (await changed.Project.GetCompilationAsync())!;
            Check(!compiled.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Changed document compiles: " + string.Join("\n", compiled.GetDiagnostics()));
            var changedNode = (await changed.GetSyntaxRootAsync())!.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
            Check(compiled.GetSemanticModel(changedNode.SyntaxTree).GetSymbolInfo(changedNode).Symbol!.ToDisplayString() == originalSymbol!.ToDisplayString(), "Same lookup overload and return type");
            Check(!(await compiled.WithAnalyzers(Packed.Value.Analyzers, changed.Project.AnalyzerOptions).GetAnalyzerDiagnosticsAsync()).Any(d => d.Id == "LSA1401"), "Recommendation disappears, action is idempotent");
            Check(workspace.TryApplyChanges(operation.ChangedSolution)
                && workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(document.Id, SourceText.From(source))), "Workspace apply and restoration retain original text");
            File.WriteAllText(Path.Combine(Required("RA05_EVIDENCE_DIR"), "codefix-original.cs"), source);
            File.WriteAllText(Path.Combine(Required("RA05_EVIDENCE_DIR"), "codefix-fixed.cs"), text);
            foreach (var doc in document.Project.Documents.Where(d => d.Id != document.Id))
                File.WriteAllText(Path.Combine(Required("RA05_EVIDENCE_DIR"), Path.GetFileName(doc.FilePath!)), (await doc.GetTextAsync()).ToString());
        }
    }

    [Test]
    public async Task CodeFixReentrantInitializerExpressionsAndStandardSuppressionStayOutsideTheFixScope()
    {
        var source = ManifestModel + """

            public static partial class Guides {
              static string Reentrant = Start();
              static string Start() { try { Manifest.GetUrl("/intro/"); } catch { } return "started"; }
            }
            class Calls { void M() { Guides.Manifest.GetUrl("/intro/"); } }
            """;
        var reentrant = ManifestGenerate(source);
        Check(!(await ManifestAnalyze(reentrant.Compilation)).Any(d => d.Id == "LSA1401"), "User static initializer cannot fault the added Pages initializer");
        var expressions = ManifestGenerate(ManifestModel + """

            class Calls { static string Path() => "/intro/";
              void M() { const string path = "/intro/"; Guides.Manifest.GetUrl(path); Guides.Manifest.GetUrl(Path()); }
            }
            """);
        Check(!(await ManifestAnalyze(expressions.Compilation)).Any(d => d.Id == "LSA1401"), "No transformation of expressions or side effects");
        var literal = ManifestGenerate(ManifestModel + "\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/intro/\"); } }");
        Check((await ManifestAnalyze(literal.Compilation)).Single().Id == "LSA1401", "Fresh literal is recommended");
        var suppressed = literal.Compilation.WithOptions(literal.Compilation.Options.WithSpecificDiagnosticOptions(
            new Dictionary<string, ReportDiagnostic> { ["LSA1401"] = ReportDiagnostic.Suppress }));
        Check((await ManifestAnalyze(suppressed)).Length == 0, "Standard per-ID suppression");
        var pragma = ManifestGenerate(ManifestModel + "\n#pragma warning disable LSA1401\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/intro/\"); } }");
        Check((await ManifestAnalyze(pragma.Compilation)).Length == 0, "Standard original pragma suppression");
    }

    [Test]
    public async Task CodeFixRefusesChangedBuffersSnapshotAmbiguitySideEffectsAndOpenScope()
    {
        var source = ManifestModel + """

            public sealed class Evil {
              readonly LithoSharp.Routing.SiteRoute route;
              Evil(LithoSharp.Routing.SiteRoute value) { route = value; }
              public static implicit operator Evil(LithoSharp.Routing.SiteRoute value) => new Evil(value);
              public static explicit operator LithoSharp.Routing.SiteRoute(Evil value) => throw new System.Exception("conversion executed");
            }
            class Calls { void M() { Guides.Manifest.GetUrl("/intro/"); } }
            """;
        var (workspace, document, diagnostic) = await FixWorkspace(source);
        using (workspace)
        {
            var registered = (await Actions(document, diagnostic)).Single();
            var edited = document.Project.Solution.WithDocumentText(document.Id, SourceText.From(source + "\n// edit"));
            Check(workspace.TryApplyChanges(edited), "Concurrent source edit applies");
            var staleOperation = (await registered.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
            Check((await staleOperation.ChangedSolution.GetDocument(document.Id)!.GetTextAsync()).ToString() == source + "\n// edit", "Stale action preserves the concurrent edit");
            Check((await Actions(workspace.CurrentSolution.GetDocument(document.Id)!, diagnostic)).Count == 0, "Changed current compiler fingerprint offers no fix");
            Check(workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(document.Id, SourceText.From(source))), "Restore original for independent cases");
            var generated = document.Project.Documents.Single(d => d.Id != document.Id);
            var generatedText = (await generated.GetTextAsync()).ToString();
            foreach (var (name, replacement) in new[] {
                ("explicit-cctor", generatedText.Replace("public static class Pages {", "public static class Pages { static Pages() {}")),
                ("side-effect-getter", generatedText.Replace("public static class Pages {", "public static class Pages { public static string Evil => throw new System.Exception();")),
                ("user-conversion", generatedText.Replace("Catalog.GetEntry(new global::LithoSharp.Content.ContentEntryId(\"intro\")).Route);", "(global::LithoSharp.Routing.SiteRoute)(global::Evil)Catalog.GetEntry(new global::LithoSharp.Content.ContentEntryId(\"intro\")).Route);")),
                ("catalog-source-expression", generatedText.Replace("\"intro.md\"", "new string('x', 8)")),
                ("duplicate-page", generatedText.Replace("public static class Pages {", "public static class Pages { public static global::LithoSharp.Pages.PageRef<string> Duplicate { get; } = new(new global::LithoSharp.Pages.PageId(\"page:collection:6:guides:5:intro\"), Catalog.GetEntry(new global::LithoSharp.Content.ContentEntryId(\"intro\")).Route);")),
                ("open", System.Text.RegularExpressions.Regex.Replace(generatedText, "\\.Create\\(Catalog, \"[0-9a-f]+\"\\)", ".Open(Catalog)")) })
            {
                var changed = workspace.CurrentSolution.WithDocumentText(generated.Id, SourceText.From(replacement));
                Check(workspace.TryApplyChanges(changed), "Case applies " + name);
                Check((await Actions(workspace.CurrentSolution.GetDocument(document.Id)!, diagnostic)).Count == 0, "No fix for " + name);
            }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var threw = false;
            try { await Actions(document, diagnostic, cancelled.Token); } catch (OperationCanceledException) { threw = true; }
            Check(threw, "Cancellation never offers an action");
        }
    }
}
