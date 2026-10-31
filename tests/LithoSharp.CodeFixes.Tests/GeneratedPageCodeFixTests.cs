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
        using (var target = File.Create(path))
            source.CopyTo(target);
        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        return (CodeFixProvider)Activator.CreateInstance(assembly.GetType("LithoSharp.CodeFixes.GeneratedPageReferenceCodeFix")!)!;
    }
    private static async Task<(AdhocWorkspace Workspace, Document Document, Diagnostic Diagnostic)> FixWorkspace(
        string source, int expectedDiagnostics = 1, params string[] otherSources)
    {
        var workspace = new AdhocWorkspace();
        var document = await AddFixProject(workspace, "Site", source, otherSources);
        var ds = await FixProjectDiagnostics(document.Project);
        var recommendations = ds.Where(d => d.Id == "LSA1401").ToArray();
        Check(recommendations.Length == expectedDiagnostics, "Expected actual literal recommendations");
        return (workspace, document, recommendations[0]);
    }
    private static async Task<Document> AddFixProject(AdhocWorkspace workspace, string name, string source, params string[] otherSources)
    {
        var initial = Compile(source, true).AddReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) == "System.Collections.dll").Select(p => MetadataReference.CreateFromFile(p)));
        var id = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, name, LanguageNames.CSharp,
            compilationOptions: initial.Options, parseOptions: initial.SyntaxTrees.First().Options, metadataReferences: initial.References));
        var documentId = DocumentId.CreateNewId(id);
        solution = solution.AddDocument(documentId, "canary.cs", SourceText.From(source), filePath: $"D:/site/{name}/canary.cs");
        for (var i = 0; i < otherSources.Length; i++)
            solution = solution.AddDocument(DocumentId.CreateNewId(id), $"consumer{i + 1}.cs", SourceText.From(otherSources[i]), filePath: $"D:/site/{name}/consumer{i + 1}.cs");
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
        await GenerateFixProject(workspace, id);
        return workspace.CurrentSolution.GetDocument(documentId)!;
    }
    private static async Task GenerateFixProject(AdhocWorkspace workspace, ProjectId id)
    {
        var solution = workspace.CurrentSolution;
        // These documents came from this actual generator, including after a fix.
        // Rerun from changed original inputs; never patch a generated proof stamp.
        foreach (var generated in solution.GetProject(id)!.Documents.Where(d => d.Name.EndsWith(".g.cs", StringComparison.Ordinal)).ToArray())
            solution = solution.RemoveDocument(generated.Id);
        Check(workspace.TryApplyChanges(solution), "Original inputs apply before actual generation");
        var project = workspace.CurrentSolution.GetProject(id)!;
        var compilation = (CSharpCompilation)(await project.GetCompilationAsync())!;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(ManifestGenerators.Value, project.AnalyzerOptions.AdditionalFiles,
            (CSharpParseOptions)project.ParseOptions!, project.AnalyzerOptions.AnalyzerConfigOptionsProvider);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Check(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), "Actual generator binds in workspace: " + string.Join("\n", diagnostics));
        solution = workspace.CurrentSolution;
        foreach (var tree in output.SyntaxTrees.Where(t => !compilation.SyntaxTrees.Contains(t)))
            solution = solution.AddDocument(DocumentId.CreateNewId(id), Path.GetFileName(tree.FilePath), tree.GetText(), filePath: tree.FilePath);
        Check(workspace.TryApplyChanges(solution), "Generated workspace source applies");
    }
    private static async Task<ImmutableArray<Diagnostic>> FixProjectDiagnostics(Project project, CancellationToken cancellation = default)
    {
        var compilation = (await project.GetCompilationAsync(cancellation))!;
        var diagnostics = await compilation.WithAnalyzers(Packed.Value.Analyzers, project.AnalyzerOptions).GetAnalyzerDiagnosticsAsync(cancellation);
        Check(!diagnostics.Any(d => d.Id is "AD0001" or "CS8032" or "CS9057"), "FixAll diagnostic retrieval cannot hide analyzer failures");
        return diagnostics;
    }
    private static async Task<List<CodeAction>> Actions(Document document, Diagnostic diagnostic, CancellationToken cancellation = default)
    {
        var actions = new List<CodeAction>();
        await Fixer.Value.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), cancellation));
        return actions;
    }

    // Keep actual AdditionalDocument content intact. Arm only after generation and
    // packed diagnostics, so the callback occurs in the provider's new proof read.
    private sealed class ProofReadText(SourceText original) : SourceText
    {
        private Action? afterRead;
        public int Callbacks { get; private set; }
        public override System.Text.Encoding? Encoding => original.Encoding;
        public override int Length => original.Length;
        public override char this[int position] => original[position];
        public override void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count)
            => original.CopyTo(sourceIndex, destination, destinationIndex, count);
        public void Arm(Action callback) => Check(Interlocked.CompareExchange(ref afterRead, callback, null) is null, "Only one proof callback is armed");
        public override string ToString()
        {
            var content = original.ToString();
            var callback = Interlocked.Exchange(ref afterRead, null);
            if (callback is not null)
            {
                Callbacks++;
                callback();
            }
            return content;
        }
    }

    private const string FixAllKey = "LSA1401.GeneratedPagePath";
    private const string ClosedFixProbe = "\nclass ClosedProbe { void M() { Guides.Manifest.GetUrl(\"/missing/\"); } }";
    private const string ScopedFixCalls = """
        #nullable enable
        public static class FirstCalls {
          public const string Ordinary = "/intro/";
          public static LithoSharp.SiteUrl M() => Guides.Manifest.GetUrl(/* target */ "/intro/" /* tail */);
        }
        """;
    private const string FixAllCalls = """

        #nullable enable
        public static class Calls {
          public const string Ordinary = "/intro/";
          public static string Events = "";
          public static string Mark(string value) { Events += value; return ""; }
          public static string M() => Mark("before")
            + Guides.Manifest.GetUrl(/* first */ "/intro/" /* first tail */).Value
            + Mark("between")
            + Guides.Manifest.GetUrl(/* second */ "/intro/" /* second tail */).Value
            + Mark("after");
          public static string OutputPath() => Guides.Catalog.GetEntry(new LithoSharp.Content.ContentEntryId("intro")).Route.RelativeOutputPath;
        }
        """;

    // The adapter runs the actual packed analyzer; extra diagnostics exercise the
    // IDE boundary without replacing the generator, binding or proof machinery.
    private sealed class FixAllDiagnostics(params Diagnostic[] extra) : FixAllContext.DiagnosticProvider
    {
        public int DocumentRequests { get; private set; }
        public int AllRequests { get; private set; }
        public ProjectId? ExtraProjectId { get; init; }
        public Action<Project>? AfterAllDiagnostics { get; init; }
        public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
        {
            DocumentRequests++;
            var tree = await document.GetSyntaxTreeAsync(cancellationToken);
            var diagnostics = await FixProjectDiagnostics(document.Project, cancellationToken);
            return diagnostics.Where(d => d.Id == "LSA1401" && d.Location.SourceTree == tree).Concat(extra).ToArray();
        }
        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Document FixAll must not query project diagnostics");
        public override async Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken)
        {
            AllRequests++;
            var diagnostics = await FixProjectDiagnostics(project, cancellationToken);
            AfterAllDiagnostics?.Invoke(project);
            return diagnostics.Where(d => d.Id == "LSA1401")
                .Concat(ExtraProjectId is null || ExtraProjectId == project.Id ? extra : []).ToArray();
        }
    }

    private static Task<CodeAction?> FixAllAction(Document document, FixAllDiagnostics diagnostics,
        FixAllScope scope = FixAllScope.Document, string key = FixAllKey, CancellationToken cancellation = default, TextSpan? diagnosticSpan = null)
        => Fixer.Value.GetFixAllProvider()!.GetFixAsync(diagnosticSpan is { } span
            ? new FixAllContext(document, span, Fixer.Value, scope, key, ["LSA1401"], diagnostics, cancellation)
            : new FixAllContext(document, Fixer.Value, scope, key, ["LSA1401"], diagnostics, cancellation));

    private static Task<CodeAction?> ScopedFixAllAction(Project project, FixAllDiagnostics diagnostics,
        FixAllScope scope, string key = FixAllKey, CancellationToken cancellation = default)
    {
        var context = new FixAllContext(project, Fixer.Value, scope, key, ["LSA1401"], diagnostics, cancellation);
        Check(context.Document is null, "Project constructor exercises FixAll without an entry document");
        return Fixer.Value.GetFixAllProvider()!.GetFixAsync(context);
    }
    private static async Task AssertFreshClosedFixProject(Project project, int expectedRecommendations)
    {
        var ds = await FixProjectDiagnostics(project);
        Check(ds.Count(d => d.Id == "LSA1401") == expectedRecommendations && !ds.Any(d => d.Id == "LSA1205"),
            "Regenerated original inputs retain freshness, not merely disappear into StaleDeferred");
        var missing = ds.Single(d => d.Id == "LSA1201");
        Check(missing.Properties["coverage"] == "Closed"
            && missing.Properties["currentFingerprint"] == missing.Properties["expectedFingerprint"],
            "Actual packed analyzer still proves Closed membership after generation");
    }

    [Test]
    public async Task CodeFixPackedHostAppliesOneEquivalentArgumentWithTriviaAndKeepsTheLookup()
    {
        var source = ManifestModel.Replace("Guides", "@event") + """

            public class Calls {
              public LithoSharp.SiteUrl M() => @event.Manifest.GetUrl(/* keep */ "/intro/" /* tail */);
            }
            """;
        var (workspace, document, diagnostic) = await FixWorkspace(source, otherSources: [ClosedFixProbe.Replace("Guides", "@event", StringComparison.Ordinal)]);
        using (workspace)
        {
            Check(diagnostic.Severity == DiagnosticSeverity.Info, "Default recommendation is Info");
            var original = (await document.Project.GetCompilationAsync())!;
            var originalNode = (await document.GetSyntaxRootAsync())!.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
            var originalSymbol = original.GetSemanticModel(originalNode.SyntaxTree).GetSymbolInfo(originalNode).Symbol;
            var actions = await Actions(document, diagnostic);
            Check(actions.Count == 1 && actions[0].EquivalenceKey == FixAllKey
                && Fixer.Value.GetFixAllProvider() is not null, "One proved fix and the matching document FixAll provider");
            var operation = (await actions[0].GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
            var changed = operation.ChangedSolution.GetDocument(document.Id)!;
            var text = (await changed.GetTextAsync()).ToString();
            Check(text.Contains("@event.Manifest.GetUrl(/* keep */ global::@event.Pages.Intro.Route.PublicPath /* tail */)"), "Fully qualified replacement and both comments: " + text);
            var compiled = (await changed.Project.GetCompilationAsync())!;
            Check(!compiled.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Changed document compiles: " + string.Join("\n", compiled.GetDiagnostics()));
            var changedNode = (await changed.GetSyntaxRootAsync())!.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
            Check(compiled.GetSemanticModel(changedNode.SyntaxTree).GetSymbolInfo(changedNode).Symbol!.ToDisplayString() == originalSymbol!.ToDisplayString(), "Same lookup overload and return type");
            Check(workspace.TryApplyChanges(operation.ChangedSolution), "Single replacement applies to the actual workspace");
            await GenerateFixProject(workspace, document.Project.Id);
            await AssertFreshClosedFixProject(workspace.CurrentSolution.GetProject(document.Project.Id)!, expectedRecommendations: 0);
            Check((await Actions(workspace.CurrentSolution.GetDocument(document.Id)!, diagnostic)).Count == 0, "Regenerated fresh single fix is idempotent");
            Check(workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(document.Id, SourceText.From(source))), "Workspace restoration retains original text");
            File.WriteAllText(Path.Combine(Required("RA05_EVIDENCE_DIR"), "codefix-original.cs"), source);
            File.WriteAllText(Path.Combine(Required("RA05_EVIDENCE_DIR"), "codefix-fixed.cs"), text);
            foreach (var doc in document.Project.Documents.Where(d => d.Id != document.Id))
                File.WriteAllText(Path.Combine(Required("RA05_EVIDENCE_DIR"), Path.GetFileName(doc.FilePath!)), (await doc.GetTextAsync()).ToString());
        }
    }

    [Test]
    public async Task CodeFixDocumentFixAllUsesOneOriginalSnapshotAndPreservesBothLookupsAndSequence()
    {
        var source = ManifestModel + FixAllCalls;
        var (workspace, document, diagnostic) = await FixWorkspace(source, expectedDiagnostics: 2, otherSources: [ClosedFixProbe]);
        using (workspace)
        {
            var provider = Fixer.Value.GetFixAllProvider();
            Check(provider is not null && provider.GetSupportedFixAllScopes().SequenceEqual([FixAllScope.Document, FixAllScope.Project, FixAllScope.Solution]), "FixAll advertises the three implemented atomic scopes");
            var original = (await document.Project.GetCompilationAsync())!;
            Check(!original.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Original two-lookup consumer compiles");
            var originalRoot = (await document.GetSyntaxRootAsync())!;
            var originalLookups = originalRoot.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(node => node.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetUrl" }).ToArray();
            var originalMethods = originalLookups.Select(node => (IMethodSymbol)original.GetSemanticModel(node.SyntaxTree).GetSymbolInfo(node).Symbol!).ToArray();
            var diagnostics = new FixAllDiagnostics(diagnostic); // Duplicate an actual span; it must be edited once.
            var action = await FixAllAction(document, diagnostics);
            Check(action is not null && action.EquivalenceKey == FixAllKey, "All original literal occurrences have one atomic action");
            var operation = (await action!.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
            Check(diagnostics.DocumentRequests == 1 && diagnostics.AllRequests == 0, "Document FixAll obtains only its original document diagnostics once");
            var changed = operation.ChangedSolution.GetDocument(document.Id)!;
            var text = (await changed.GetTextAsync()).ToString();
            var expected = source.Replace("/* first */ \"/intro/\"", "/* first */ global::Guides.Pages.Intro.Route.PublicPath", StringComparison.Ordinal)
                .Replace("/* second */ \"/intro/\"", "/* second */ global::Guides.Pages.Intro.Route.PublicPath", StringComparison.Ordinal);
            Check(text == expected, "Only both original arguments change; comments, lookup receivers and before/between/after sentinels remain byte-for-byte");
            var compiled = (await changed.Project.GetCompilationAsync())!;
            Check(!compiled.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error || d.Id.StartsWith("CS86", StringComparison.Ordinal)), "Atomic replacements preserve compilation and nullable contracts");
            var changedLookups = (await changed.GetSyntaxRootAsync())!.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(node => node.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "GetUrl" }).ToArray();
            Check(changedLookups.Length == originalMethods.Length && changedLookups.Length == 2, "Both runtime lookups remain");
            for (var i = 0; i < changedLookups.Length; i++)
            {
                var method = (IMethodSymbol)compiled.GetSemanticModel(changedLookups[i].SyntaxTree).GetSymbolInfo(changedLookups[i]).Symbol!;
                Check(method.ToDisplayString() == originalMethods[i].ToDisplayString()
                    && method.ReturnNullableAnnotation == originalMethods[i].ReturnNullableAnnotation
                    && method.Parameters[0].NullableAnnotation == originalMethods[i].Parameters[0].NullableAnnotation,
                    "Original lookup overload, return type and nullability are retained");
            }
            Check(workspace.TryApplyChanges(operation.ChangedSolution), "Atomic document change applies");
            await GenerateFixProject(workspace, document.Project.Id);
            await AssertFreshClosedFixProject(workspace.CurrentSolution.GetProject(document.Project.Id)!, expectedRecommendations: 0);
            Check(await FixAllAction(workspace.CurrentSolution.GetDocument(document.Id)!, new FixAllDiagnostics()) is null, "Second document request has no action");
            File.WriteAllText(Path.Combine(Required("RA05_EVIDENCE_DIR"), "codefix-fixall-original.cs"), source);
            File.WriteAllText(Path.Combine(Required("RA05_EVIDENCE_DIR"), "codefix-fixall-fixed.cs"), text);
            foreach (var generated in workspace.CurrentSolution.GetProject(document.Project.Id)!.Documents.Where(d => d.Name.EndsWith(".g.cs", StringComparison.Ordinal)))
                File.WriteAllText(Path.Combine(Required("RA05_EVIDENCE_DIR"), Path.GetFileName(generated.FilePath!)), (await generated.GetTextAsync()).ToString());
        }
    }

    [Test]
    public async Task CodeFixDocumentFixAllRejectsUnsupportedRequestsAndOneUnprovenTargetAtomically()
    {
        var source = ManifestModel + FixAllCalls;
        var (workspace, document, diagnostic) = await FixWorkspace(source, expectedDiagnostics: 2);
        using (workspace)
        {
            foreach (var scope in new[] { FixAllScope.ContainingMember, FixAllScope.ContainingType, FixAllScope.Custom })
                Check(await FixAllAction(document, new FixAllDiagnostics(), scope,
                    diagnosticSpan: scope is FixAllScope.ContainingMember or FixAllScope.ContainingType ? diagnostic.Location.SourceSpan : null) is null,
                    "No unsupported scope action: " + scope);
            Check(await FixAllAction(document, new FixAllDiagnostics(), key: "Other.Action") is null, "Unrelated equivalence key is not handled");
            var tree = (await document.GetSyntaxTreeAsync())!;
            var ordinary = (await document.GetSyntaxRootAsync())!.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Single(node => node.Identifier.ValueText == "Ordinary").Initializer!.Value;
            foreach (var badLocation in new[]
            {
                ordinary.GetLocation(),
                Location.Create(tree, new TextSpan(diagnostic.Location.SourceSpan.Start + 1, diagnostic.Location.SourceSpan.Length - 1)),
                Location.Create(CSharpSyntaxTree.ParseText(source, path: tree.FilePath), diagnostic.Location.SourceSpan)
            })
            {
                var unexpected = Diagnostic.Create(diagnostic.Descriptor, badLocation);
                Check(await FixAllAction(document, new FixAllDiagnostics(unexpected)) is null, "One unproven literal, partial span or foreign tree refuses the entire action");
                Check((await workspace.CurrentSolution.GetDocument(document.Id)!.GetTextAsync()).ToString() == source, "No valid subset is applied after a rejected target");
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var threw = false;
            try
            {
                await FixAllAction(document, new FixAllDiagnostics(), cancellation: cancelled.Token);
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }
            Check(threw, "Cancellation cannot produce a document action");
        }
    }

    [Test]
    public async Task CodeFixProjectAndSolutionFixAllUseFreshOriginalDocumentsAndPreserveUnrelatedInputs()
    {
        foreach (var scope in new[] { FixAllScope.Project, FixAllScope.Solution })
        {
            var (workspace, document, _) = await FixWorkspace(ManifestModel + ClosedFixProbe, expectedDiagnostics: 2,
                otherSources: [ScopedFixCalls, ScopedFixCalls.Replace("FirstCalls", "OtherCalls", StringComparison.Ordinal)]);
            using (workspace)
            {
                var second = await AddFixProject(workspace, "SecondSite", (ManifestModel + ClosedFixProbe).Replace("Guides", "SecondGuides", StringComparison.Ordinal),
                    ScopedFixCalls.Replace("FirstCalls", "ThirdCalls", StringComparison.Ordinal).Replace("Guides", "SecondGuides", StringComparison.Ordinal));
                var unrelatedId = ProjectId.CreateNewId();
                var unrelatedDocId = DocumentId.CreateNewId(unrelatedId);
                Check(workspace.TryApplyChanges(workspace.CurrentSolution
                    .AddProject(ProjectInfo.Create(unrelatedId, VersionStamp.Create(), "Unrelated", "Unrelated", LanguageNames.CSharp,
                        compilationOptions: document.Project.CompilationOptions, parseOptions: document.Project.ParseOptions,
                        metadataReferences: document.Project.MetadataReferences))
                    .AddDocument(unrelatedDocId, "Unrelated.cs", SourceText.From("public class Unrelated { public const int Value = 1; }"), filePath: "D:/site/Unrelated/Unrelated.cs")),
                    "An independent project without a diagnostic target does not need generated manifest proof");
                var original = workspace.CurrentSolution;
                document = original.GetDocument(document.Id)!;
                second = original.GetDocument(second.Id)!;
                await AssertFreshClosedFixProject(document.Project, 2);
                await AssertFreshClosedFixProject(second.Project, 1);
                var sameSpanTargets = (await FixProjectDiagnostics(document.Project)).Where(d => d.Id == "LSA1401").ToArray();
                Check(sameSpanTargets.Length == 2 && sameSpanTargets[0].Location.SourceSpan == sameSpanTargets[1].Location.SourceSpan
                    && sameSpanTargets[0].Location.SourceTree != sameSpanTargets[1].Location.SourceTree,
                    "Identical literal spans in distinct original documents must both be fixed");
                var beforeTexts = new Dictionary<DocumentId, string>();
                foreach (var doc in original.Projects.SelectMany(project => project.Documents))
                    beforeTexts.Add(doc.Id, (await doc.GetTextAsync()).ToString());
                var targets = document.Project.Documents.Where(doc => doc.Name.StartsWith("consumer", StringComparison.Ordinal)).ToList();
                if (scope == FixAllScope.Solution)
                    targets.AddRange(second.Project.Documents.Where(doc => doc.Name.StartsWith("consumer", StringComparison.Ordinal)));
                var originalMethods = new Dictionary<DocumentId, IMethodSymbol>();
                foreach (var target in targets)
                {
                    var lookup = (await target.GetSyntaxRootAsync())!.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
                    var compiled = (await target.Project.GetCompilationAsync())!;
                    originalMethods.Add(target.Id, (IMethodSymbol)compiled.GetSemanticModel(lookup.SyntaxTree).GetSymbolInfo(lookup).Symbol!);
                }
                var diagnostics = new FixAllDiagnostics();
                var action = await ScopedFixAllAction(document.Project, diagnostics, scope);
                Check(action is not null && action.EquivalenceKey == FixAllKey, "Document-null context offers an atomic action: " + scope);
                var operation = (await action!.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
                Check(diagnostics.DocumentRequests == 0 && diagnostics.AllRequests == (scope == FixAllScope.Project ? 1 : 3),
                    "GetAll reads each scoped project once, without silently narrowing to the entry document");
                var changedIds = operation.ChangedSolution.GetChanges(original).GetProjectChanges().SelectMany(change => change.GetChangedDocuments()).ToHashSet();
                Check(changedIds.SetEquals(targets.Select(doc => doc.Id)), "Only all diagnostic-bearing target documents change together: " + scope);
                foreach (var target in targets)
                {
                    var guide = target.Project.Id == document.Project.Id ? "Guides" : "SecondGuides";
                    var changed = operation.ChangedSolution.GetDocument(target.Id)!;
                    var text = (await changed.GetTextAsync()).ToString();
                    Check(text == beforeTexts[target.Id].Replace("/* target */ \"/intro/\"", $"/* target */ global::{guide}.Pages.Intro.Route.PublicPath", StringComparison.Ordinal),
                        "Only the original argument changes; ordinary literal and both comments remain: " + target.Name);
                    var compiled = (await changed.Project.GetCompilationAsync())!;
                    Check(!compiled.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error || d.Id.StartsWith("CS86", StringComparison.Ordinal)),
                        "Each changed original project still compiles with its nullable contract");
                    var lookup = (await changed.GetSyntaxRootAsync())!.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
                    var method = (IMethodSymbol)compiled.GetSemanticModel(lookup.SyntaxTree).GetSymbolInfo(lookup).Symbol!;
                    var before = originalMethods[target.Id];
                    Check(method.ToDisplayString() == before.ToDisplayString()
                        && method.ReturnNullableAnnotation == before.ReturnNullableAnnotation
                        && method.Parameters[0].NullableAnnotation == before.Parameters[0].NullableAnnotation,
                        "Lookup symbol, return type and nullability are retained across the atomic scope");
                }
                foreach (var before in beforeTexts.Where(entry => !changedIds.Contains(entry.Key)))
                    Check((await operation.ChangedSolution.GetDocument(before.Key)!.GetTextAsync()).ToString() == before.Value,
                        "Non-target documents, generated proof and other projects remain byte-for-byte");
                Check(workspace.TryApplyChanges(operation.ChangedSolution), "One scoped solution applies");
                await GenerateFixProject(workspace, document.Project.Id);
                if (scope == FixAllScope.Solution)
                    await GenerateFixProject(workspace, second.Project.Id);
                await AssertFreshClosedFixProject(workspace.CurrentSolution.GetProject(document.Project.Id)!, 0);
                await AssertFreshClosedFixProject(workspace.CurrentSolution.GetProject(second.Project.Id)!, scope == FixAllScope.Project ? 1 : 0);
                Check(await ScopedFixAllAction(workspace.CurrentSolution.GetProject(document.Project.Id)!, new FixAllDiagnostics(), scope) is null,
                    "Regenerated Closed input is idempotent in the requested scope");
            }
        }
    }

    [Test]
    public async Task CodeFixProjectAndSolutionFixAllRejectMixedTargetsAndStaleOrCancelledWholeSolutions()
    {
        foreach (var scope in new[] { FixAllScope.Project, FixAllScope.Solution })
        {
            var (workspace, document, diagnostic) = await FixWorkspace(ManifestModel + ClosedFixProbe, expectedDiagnostics: 2,
                otherSources: [ScopedFixCalls, ScopedFixCalls.Replace("FirstCalls", "OtherCalls", StringComparison.Ordinal)]);
            using (workspace)
            {
                var otherId = ProjectId.CreateNewId();
                var otherDocId = DocumentId.CreateNewId(otherId);
                Check(workspace.TryApplyChanges(workspace.CurrentSolution
                    .AddProject(ProjectInfo.Create(otherId, VersionStamp.Create(), "Other", "Other", LanguageNames.CSharp,
                        compilationOptions: document.Project.CompilationOptions, parseOptions: document.Project.ParseOptions,
                        metadataReferences: document.Project.MetadataReferences))
                    .AddDocument(otherDocId, "Other.cs", SourceText.From("public class Other { }"), filePath: "D:/site/Other/Other.cs")), "Non-target independent project applies");
                document = workspace.CurrentSolution.GetDocument(document.Id)!;
                var project = document.Project;
                var target = project.GetDocument(diagnostic.Location.SourceTree!)!;
                var tree = (await target.GetSyntaxTreeAsync())!;
                var source = (await target.GetTextAsync()).ToString();
                var ordinary = (await target.GetSyntaxRootAsync())!.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single().Initializer!.Value;
                var generated = project.Documents.Single(doc => doc.Name.EndsWith(".g.cs", StringComparison.Ordinal));
                var generatedLiteral = (await generated.GetSyntaxRootAsync())!.DescendantNodes().OfType<LiteralExpressionSyntax>()
                    .First(node => node.IsKind(SyntaxKind.StringLiteralExpression));
                foreach (var badLocation in new[] {
                    ordinary.GetLocation(),
                    Location.Create(tree, new TextSpan(diagnostic.Location.SourceSpan.Start + 1, diagnostic.Location.SourceSpan.Length - 1)),
                    Location.Create(CSharpSyntaxTree.ParseText(source, path: tree.FilePath), diagnostic.Location.SourceSpan),
                    Location.None,
                    generatedLiteral.GetLocation()
                })
                {
                    var before = workspace.CurrentSolution;
                    var extras = new FixAllDiagnostics(Diagnostic.Create(diagnostic.Descriptor, badLocation)) { ExtraProjectId = project.Id };
                    Check(await ScopedFixAllAction(project, extras, scope) is null, "One unproven/non-source/foreign/generated target rejects all scoped documents: " + scope);
                    Check(workspace.CurrentSolution == before, "No valid subset is applied after mixed-target refusal");
                }
                Check(await ScopedFixAllAction(project, new FixAllDiagnostics(), scope, key: "Other.Action") is null, "Wrong equivalence key has no scoped action");
                var originals = new Dictionary<DocumentId, SourceText>();
                foreach (var doc in workspace.CurrentSolution.Projects.SelectMany(p => p.Documents))
                    originals.Add(doc.Id, await doc.GetTextAsync());
                var additional = project.AdditionalDocuments.Single();
                var config = project.AnalyzerConfigDocuments.Single();
                var additionalText = await additional.GetTextAsync();
                var configText = await config.GetTextAsync();
                var parseOptions = project.ParseOptions!;
                Solution RestoreInputs()
                {
                    var restored = workspace.CurrentSolution;
                    foreach (var added in restored.Projects.SelectMany(p => p.DocumentIds).Where(id => !originals.ContainsKey(id)).ToArray())
                        restored = restored.RemoveDocument(added);
                    foreach (var original in originals)
                        restored = restored.WithDocumentText(original.Key, original.Value);
                    return restored.WithAdditionalDocumentText(additional.Id, additionalText)
                        .WithAnalyzerConfigDocumentText(config.Id, configText).WithProjectParseOptions(project.Id, parseOptions);
                }
                foreach (var input in new[] { "source", "additional", "config", "project", "non-target-source", "other-project-source" })
                {
                    Check(workspace.TryApplyChanges(RestoreInputs()), "Restore actual original inputs before independent stale case");
                    var currentProject = workspace.CurrentSolution.GetProject(project.Id)!;
                    var registered = await ScopedFixAllAction(currentProject, new FixAllDiagnostics(), scope);
                    Check(registered is not null, "Fresh full scope registers before concurrent edit: " + input);
                    var edited = input switch
                    {
                        "source" => workspace.CurrentSolution.WithDocumentText(target.Id, SourceText.From(source + "\n// concurrent target edit")),
                        "additional" => workspace.CurrentSolution.WithAdditionalDocumentText(additional.Id, SourceText.From(additionalText + "\nChanged body")),
                        "config" => workspace.CurrentSolution.WithAnalyzerConfigDocumentText(config.Id, SourceText.From(configText.ToString().Replace("Configuration = Release", "Configuration = Debug", StringComparison.Ordinal))),
                        "project" => workspace.CurrentSolution.WithProjectParseOptions(project.Id, ((CSharpParseOptions)parseOptions).WithLanguageVersion(LanguageVersion.CSharp12)),
                        "non-target-source" => workspace.CurrentSolution.WithDocumentText(document.Id, SourceText.From(originals[document.Id] + "\n// non-target source edit")),
                        _ => workspace.CurrentSolution.WithDocumentText(otherDocId, SourceText.From(originals[otherDocId] + "\n// independent other-project edit"))
                    };
                    Check(workspace.TryApplyChanges(edited), "Concurrent input edit applies: " + input);
                    var current = workspace.CurrentSolution;
                    var operation = (await registered!.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
                    Check(operation.ChangedSolution == current, "Entire stale action returns the exact current solution, retaining all edits: " + input);
                    Check(await ScopedFixAllAction(currentProject, new FixAllDiagnostics(), scope) is null, "Obsolete project snapshot cannot register a fresh action");
                }
                Check(workspace.TryApplyChanges(RestoreInputs()), "Restore original inputs before editing across the GetAll await");
                var duringRetrieval = SourceText.From(originals[otherDocId] + "\n// edit during actual diagnostic retrieval");
                var retrievalDiagnostics = new FixAllDiagnostics
                {
                    AfterAllDiagnostics = queried =>
                    {
                        if (queried.Id == project.Id)
                            Check(workspace.TryApplyChanges(workspace.CurrentSolution.WithDocumentText(otherDocId, duringRetrieval)),
                                "Non-target edit applies after actual packed analyzer retrieval and before its reply");
                    }
                };
                Check(await ScopedFixAllAction(workspace.CurrentSolution.GetProject(project.Id)!, retrievalDiagnostics, scope) is null,
                    "Workspace change across the GetAll await cannot register an old-snapshot action");
                Check((await workspace.CurrentSolution.GetDocument(otherDocId)!.GetTextAsync()).ToString() == duringRetrieval.ToString()
                    && (await workspace.CurrentSolution.GetDocument(target.Id)!.GetTextAsync()).ToString() == source,
                    "Await-boundary refusal keeps the concurrent edit and leaves every target unchanged");
                Check(workspace.TryApplyChanges(RestoreInputs()), "Restore original inputs before linked peer rejection");
                var linkedId = DocumentId.CreateNewId(otherId);
                Check(workspace.TryApplyChanges(workspace.CurrentSolution.AddDocument(linkedId, "Linked.cs", SourceText.From(source), filePath: target.FilePath)), "Real linked peer outside the entry project applies");
                var linkedProject = workspace.CurrentSolution.GetProject(project.Id)!;
                Check(linkedProject.GetDocument(target.Id)!.GetLinkedDocumentIds().Length > 0, "Roslyn identifies a linked diagnostic target");
                Check(await ScopedFixAllAction(linkedProject, new FixAllDiagnostics(), scope) is null, "Linked peer rejects the whole action even outside Project scope");
                Check(workspace.TryApplyChanges(RestoreInputs()), "Restore original inputs before cancellation");
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                var threw = false;
                try
                {
                    await ScopedFixAllAction(workspace.CurrentSolution.GetProject(project.Id)!, new FixAllDiagnostics(), scope, cancellation: cancelled.Token);
                }
                catch (OperationCanceledException)
                {
                    threw = true;
                }
                Check(threw, "Cancelled retrieval cannot offer a scoped action");
                var action = await ScopedFixAllAction(workspace.CurrentSolution.GetProject(project.Id)!, new FixAllDiagnostics(), scope);
                Check(action is not null, "Fresh scope is restored before cancelled application");
                var beforeCancellation = workspace.CurrentSolution;
                threw = false;
                try
                {
                    await action!.GetOperationsAsync(cancelled.Token);
                }
                catch (OperationCanceledException)
                {
                    threw = true;
                }
                Check(threw && workspace.CurrentSolution == beforeCancellation, "Cancelled application cannot leave one target replaced");
            }
        }
    }

    [Test]
    public async Task CodeFixDocumentFixAllPreservesConcurrentSourceAdditionalConfigAndProjectEdits()
    {
        var source = ManifestModel + FixAllCalls;
        var (workspace, document, _) = await FixWorkspace(source, expectedDiagnostics: 2);
        using (workspace)
        {
            var additional = document.Project.AdditionalDocuments.Single();
            var config = document.Project.AnalyzerConfigDocuments.Single();
            var additionalText = (await additional.GetTextAsync()).ToString();
            var configText = (await config.GetTextAsync()).ToString();
            var parseOptions = document.Project.ParseOptions!;
            Solution RestoredInputs() => workspace.CurrentSolution
                .WithDocumentText(document.Id, SourceText.From(source))
                .WithAdditionalDocumentText(additional.Id, SourceText.From(additionalText))
                .WithAnalyzerConfigDocumentText(config.Id, SourceText.From(configText))
                .WithProjectParseOptions(document.Project.Id, parseOptions);
            foreach (var input in new[] { "source", "additional", "config", "project" })
            {
                Check(workspace.TryApplyChanges(RestoredInputs()), "Restore inputs from the current solution before independent stale action: " + input);
                var currentDocument = workspace.CurrentSolution.GetDocument(document.Id)!;
                var registered = await FixAllAction(currentDocument, new FixAllDiagnostics());
                Check(registered is not null, "Fresh two-target action registers before edit: " + input);
                var edited = input switch
                {
                    "source" => workspace.CurrentSolution.WithDocumentText(document.Id, SourceText.From(source + "\n// concurrent source edit")),
                    "additional" => workspace.CurrentSolution.WithAdditionalDocumentText(additional.Id, SourceText.From(additionalText + "\nChanged body")),
                    "config" => workspace.CurrentSolution.WithAnalyzerConfigDocumentText(config.Id, SourceText.From(configText.Replace("Configuration = Release", "Configuration = Debug", StringComparison.Ordinal))),
                    _ => workspace.CurrentSolution.WithProjectParseOptions(document.Project.Id, ((CSharpParseOptions)document.Project.ParseOptions!).WithLanguageVersion(LanguageVersion.CSharp12))
                };
                Check(workspace.TryApplyChanges(edited), "Concurrent input edit applies: " + input);
                var current = workspace.CurrentSolution;
                var operation = (await registered!.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
                Check(operation.ChangedSolution == current, "Stale action preserves the exact current solution and every edit: " + input);
                Check(await FixAllAction(currentDocument, new FixAllDiagnostics()) is null, "Obsolete document snapshot offers no action: " + input);
            }
            Check(workspace.TryApplyChanges(RestoredInputs()), "Restore current inputs before application cancellation");
            var action = await FixAllAction(workspace.CurrentSolution.GetDocument(document.Id)!, new FixAllDiagnostics());
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var threw = false;
            try
            {
                await action!.GetOperationsAsync(cancelled.Token);
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }
            Check(threw && (await workspace.CurrentSolution.GetDocument(document.Id)!.GetTextAsync()).ToString() == source, "Cancelled application cannot leave one target replaced");
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
            var additional = document.Project.AdditionalDocuments.Single();
            var config = document.Project.AnalyzerConfigDocuments.Single();
            var additionalText = await additional.GetTextAsync();
            var configText = await config.GetTextAsync();
            var parseOptions = document.Project.ParseOptions!;
            Solution RestoredInputs() => workspace.CurrentSolution.WithDocumentText(document.Id, SourceText.From(source))
                .WithAdditionalDocumentText(additional.Id, additionalText).WithAnalyzerConfigDocumentText(config.Id, configText)
                .WithProjectParseOptions(document.Project.Id, parseOptions);
            foreach (var input in new[] { "source", "additional", "config", "project" })
            {
                Check(workspace.TryApplyChanges(RestoredInputs()), "Restore actual inputs before independent single stale case: " + input);
                var currentDocument = workspace.CurrentSolution.GetDocument(document.Id)!;
                var registered = (await Actions(currentDocument, diagnostic)).Single();
                var edited = input switch
                {
                    "source" => workspace.CurrentSolution.WithDocumentText(document.Id, SourceText.From(source + "\n// concurrent source edit")),
                    "additional" => workspace.CurrentSolution.WithAdditionalDocumentText(additional.Id, SourceText.From(additionalText + "\nChanged body")),
                    "config" => workspace.CurrentSolution.WithAnalyzerConfigDocumentText(config.Id, SourceText.From(configText.ToString().Replace("Configuration = Release", "Configuration = Debug", StringComparison.Ordinal))),
                    _ => workspace.CurrentSolution.WithProjectParseOptions(document.Project.Id, ((CSharpParseOptions)parseOptions).WithLanguageVersion(LanguageVersion.CSharp12))
                };
                Check(workspace.TryApplyChanges(edited), "Concurrent single-fix input edit applies: " + input);
                var current = workspace.CurrentSolution;
                var staleOperation = (await registered.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
                Check(staleOperation.ChangedSolution == current, "Stale single action returns exact current solution and every edit: " + input);
                Check((await Actions(currentDocument, diagnostic)).Count == 0, "Obsolete solution cannot register another single action: " + input);
                Check((await Actions(current.GetDocument(document.Id)!, diagnostic)).Count == 0, "Changed actual fingerprint offers no fix: " + input);
            }
            Check(workspace.TryApplyChanges(RestoredInputs()), "Restore original for independent ambiguity cases");
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
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var threw = false;
            try
            {
                await Actions(document, diagnostic, cancelled.Token);
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }
            Check(threw, "Cancellation never offers an action");
        }
    }

    [Test]
    public Task CodeFixSingleRegistrationProofReadRejectsConcurrentWorkspaceEditsAndCancellation()
        => SingleProofReadRejectsConcurrentWorkspaceEditsAndCancellation("registration");

    [Test]
    public Task CodeFixSingleActionProofReadRejectsConcurrentWorkspaceEditsAndCancellation()
        => SingleProofReadRejectsConcurrentWorkspaceEditsAndCancellation("action");

    private async Task SingleProofReadRejectsConcurrentWorkspaceEditsAndCancellation(string stage)
    {
        var source = ManifestModel + "\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/intro/\"); } }";
        var (workspace, document, _) = await FixWorkspace(source, otherSources: [ClosedFixProbe]);
        using (workspace)
        {
            var additional = document.Project.AdditionalDocuments.Single();
            var config = document.Project.AnalyzerConfigDocuments.Single();
            var additionalText = await additional.GetTextAsync();
            var configText = await config.GetTextAsync();
            var parseOptions = document.Project.ParseOptions!;
            async Task<(Document Document, Diagnostic Diagnostic, ProofReadText Text)> PrepareProofRead()
            {
                var text = new ProofReadText(additionalText);
                var restored = workspace.CurrentSolution.WithDocumentText(document.Id, SourceText.From(source))
                    .WithAdditionalDocumentText(additional.Id, text, PreservationMode.PreserveIdentity)
                    .WithAnalyzerConfigDocumentText(config.Id, configText).WithProjectParseOptions(document.Project.Id, parseOptions);
                Check(workspace.TryApplyChanges(restored), "Same actual inputs with an unarmed proof reader apply");
                var current = workspace.CurrentSolution.GetDocument(document.Id)!;
                Check(ReferenceEquals(await current.Project.GetAdditionalDocument(additional.Id)!.GetTextAsync(), text), "Workspace preserves the real AdditionalDocument reader identity");
                var ds = await FixProjectDiagnostics(current.Project);
                var diagnostic = ds.Single(d => d.Id == "LSA1401");
                Check(!ds.Any(d => d.Id == "LSA1205") && diagnostic.Properties["coverage"] == "Closed"
                    && diagnostic.Properties["currentFingerprint"] == diagnostic.Properties["expectedFingerprint"],
                    "Unchanged forwarded content retains actual packed Closed proof before arming");
                return (current, diagnostic, text);
            }
            Solution Edited(string input) => input switch
            {
                "source" => workspace.CurrentSolution.WithDocumentText(document.Id, SourceText.From(source + "\n// edit during single proof")),
                "additional" => workspace.CurrentSolution.WithAdditionalDocumentText(additional.Id, SourceText.From(additionalText + "\nEdited during proof")),
                "config" => workspace.CurrentSolution.WithAnalyzerConfigDocumentText(config.Id, SourceText.From(configText.ToString().Replace("Configuration = Release", "Configuration = Debug", StringComparison.Ordinal))),
                _ => workspace.CurrentSolution.WithProjectParseOptions(document.Project.Id, ((CSharpParseOptions)parseOptions).WithLanguageVersion(LanguageVersion.CSharp12))
            };
            foreach (var input in new[] { "source", "additional", "config", "project" })
            {
                var prepared = await PrepareProofRead();
                var action = stage == "action" ? (await Actions(prepared.Document, prepared.Diagnostic)).Single() : null;
                Solution? interrupted = null;
                prepared.Text.Arm(() =>
                {
                    Check(workspace.TryApplyChanges(Edited(input)), "Actual input edit applies during " + stage + " proof: " + input);
                    interrupted = workspace.CurrentSolution;
                });
                List<CodeAction>? registrations = null;
                ApplyChangesOperation? operation = null;
                if (action is null)
                    registrations = await Actions(prepared.Document, prepared.Diagnostic);
                else
                    operation = (await action.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
                Check(prepared.Text.Callbacks == 1 && interrupted is not null && workspace.CurrentSolution == interrupted,
                    "One actual proof read reaches the callback and preserves all concurrent edits: " + stage + "/" + input);
                if (registrations is not null)
                    Check(registrations.Count == 0, "Proof begun on a stale registration snapshot offers no action: " + input);
                else
                    Check(operation!.ChangedSolution == interrupted,
                        "Edit during action proof returns exact current solution rather than an old replacement: " + input);
            }
            var cancelling = await PrepareProofRead();
            var registered = stage == "action" ? (await Actions(cancelling.Document, cancelling.Diagnostic)).Single() : null;
            var beforeCancellation = workspace.CurrentSolution;
            using var cancellation = new CancellationTokenSource();
            cancelling.Text.Arm(cancellation.Cancel);
            var threw = false;
            try
            {
                if (registered is null)
                    await Actions(cancelling.Document, cancelling.Diagnostic, cancellation.Token);
                else
                    await registered.GetOperationsAsync(cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }
            Check(threw && cancelling.Text.Callbacks == 1 && workspace.CurrentSolution == beforeCancellation,
                "Cancellation during actual " + stage + " proof throws without edits or a successful action");
        }
    }
}
