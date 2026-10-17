using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    private const string TypedOriginRoute = "LithoSharp.Routing.SiteRoute.ForDirectoryIndex(\"Intro/\",baseUrl:null)";

    [Test]
    public async Task TypedReferenceOriginsKeepMismatchProofAndSeparateArgumentOwners()
    {
        var evidence = new List<object>();
        foreach (var (name, key, route, before, named, entryWrapped, outerWrapped) in new[]
        {
            ("plain", "\"intro\"", TypedOriginRoute, "", false, false, false),
            ("route-forgiving", "\"intro\"", TypedOriginRoute + "!", "", false, false, false),
            ("inner-id-forgiving", "(\"intro\"!)", TypedOriginRoute, "", false, false, false),
            ("nested-owners-named-reordered", "((\"intro\"!))", "(" + TypedOriginRoute + "!)", "", true, true, true),
            ("constant-id-reference", "((knownId!))", TypedOriginRoute + "!", "const string knownId=\"intro\";", true, true, false),
            ("route-parentheses", "\"intro\"", "((" + TypedOriginRoute + ")!)", "", true, false, false),
            ("related-pragma-does-not-suppress-primary", "\r\n#pragma warning disable LSA1402\r\n(\"intro\"!)\r\n#pragma warning restore LSA1402\r\n", "(" + TypedOriginRoute + "!)", "", true, true, false),
            ("primary-pragma", "((\"intro\"!))", "(" + TypedOriginRoute + "!)", "", true, true, false),
        })
        {
            var creation = TypedOriginCreation(key, route, named, entryWrapped, outerWrapped);
            var body = before + "var item=" + creation + ";";
            if (name == "primary-pragma") body = "#pragma warning disable LSA1402\r\nvar hidden=" + creation + ";\r\n#pragma warning restore LSA1402\r\n/*😀*/var item=" + creation + ";";
            var source = TypedOriginSource(body);
            var generated = ManifestGenerate(source);
            TypedOriginBinds(generated.Compilation, name);
            Check(generated.Diagnostics.Length == 0, "Fresh generated origin: " + name + " " + string.Join(";", generated.Diagnostics));
            var diagnostics = await ManifestAnalyze(generated.Compilation);
            Check(diagnostics.Length == 1 && diagnostics[0].Id == "LSA1402" && diagnostics[0].Severity == DiagnosticSeverity.Error,
                "Only the existing proven contradiction: " + name + " " + string.Join(";", diagnostics));
            var diagnostic = diagnostics[0];var tree = generated.Compilation.SyntaxTrees.Single(t => t.FilePath == "canary.cs");
            var outer = tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Last(n => n.Type is GenericNameSyntax type && type.Identifier.ValueText == "ContentRef");
            var expectedRoute = (outer.ArgumentList!.Arguments.SingleOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "route") ?? outer.ArgumentList.Arguments[2]).Expression;
            var outerEntry = (outer.ArgumentList.Arguments.SingleOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "id") ?? outer.ArgumentList.Arguments[1]).Expression;
            var inner = outerEntry.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>().Single(n => n.Type.ToString() == "ContentEntryId");
            var expectedKey = inner.ArgumentList!.Arguments.Single().Expression;
            Check(diagnostic.Location.SourceSpan == expectedRoute.Span && diagnostic.AdditionalLocations.Count == 1
                && diagnostic.AdditionalLocations[0].SourceSpan == expectedKey.Span && diagnostic.AdditionalLocations[0].SourceSpan != outerEntry.Span,
                "Primary outer ordinal2 and related inner ordinal0 stay separate: " + name);
            foreach (var location in new[] { diagnostic.Location, diagnostic.AdditionalLocations[0] })
            {
                var at = location.SourceSpan.Start;var position = location.GetLineSpan().StartLinePosition;
                Check(location.SourceTree == tree && position.Line == source[..at].Count(c => c == '\n')
                    && position.Character == at - source.LastIndexOf('\n', at - 1) - 1, "Original UTF16/CRLF position: " + name);
            }
            Check(diagnostic.Properties["entryId"] == "intro" && diagnostic.Properties["expectedPublicPath"] == "/intro/" && diagnostic.Properties["actualPublicPath"] == "/Intro/"
                && diagnostic.Properties["coverage"] == "Closed" && diagnostic.Properties["currentFingerprint"] == diagnostic.Properties["expectedFingerprint"]
                && diagnostic.Properties["site"] == "docs" && diagnostic.Properties["variant"] == "ja", "Same values and per-Compilation unique fresh Closed proof: " + name);
            if (name == "primary-pragma") Check(diagnostic.Location.SourceSpan.Start > source.IndexOf("#pragma warning restore LSA1402", StringComparison.Ordinal), "Only restored primary survives");
            evidence.Add(new { name, source, diagnostics = diagnostics.Select(TypedOriginDiagnostic).ToArray() });
        }
        var standardSource = TypedOriginSource("var item=" + TypedOriginCreation("(\"intro\"!)", TypedOriginRoute + "!", true, true, false) + ";");
        var standard = ManifestGenerate(standardSource);
        TypedOriginBinds(standard.Compilation, "standard-policy");Check(standard.Diagnostics.Length == 0, "Standard policy fresh generator");
        foreach (var severity in new[] { ReportDiagnostic.Warn, ReportDiagnostic.Suppress })
        {
            var compilation = standard.Compilation.WithOptions(standard.Compilation.Options.WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["LSA1402"] = severity }));
            var diagnostics = await ManifestAnalyze(compilation);
            Check(severity == ReportDiagnostic.Warn ? diagnostics.Length == 1 && diagnostics[0].Id == "LSA1402" && diagnostics[0].Severity == DiagnosticSeverity.Warning : diagnostics.Length == 0,
                "Standard severity/suppression keeps the same diagnostic owner: " + severity);
            evidence.Add(new { name = severity.ToString(), source = standardSource, diagnostics = diagnostics.Select(TypedOriginDiagnostic).ToArray() });
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No runtime Core/getter execution");
        ManifestEvidence("typed-origin-known", evidence);
    }

    [Test]
    public async Task TypedReferenceOriginsPreserveDeferredAndDiagnosticOwnership()
    {
        var evidence = new List<object>();
        foreach (var (name, key, route, before, scalar) in new[]
        {
            ("matching-pair", "(\"intro\"!)", "LithoSharp.Routing.SiteRoute.ForDirectoryIndex(\"intro/\",baseUrl:null)!", "", 0),
            ("missing-id-is-not-contradiction", "\"missing\"!", TypedOriginRoute + "!", "", 0),
            ("unknown-id", "unknown!", TypedOriginRoute + "!", "", 0),
            ("getter-id", "Getter!", TypedOriginRoute + "!", "", 0),
            ("user-conversion-id", "new Convertible()!", TypedOriginRoute + "!", "", 0),
            ("unknown-path", "\"intro\"!", "LithoSharp.Routing.SiteRoute.ForDirectoryIndex(unknown!,baseUrl:null)!", "", 0),
            ("unknown-base", "\"intro\"!", "LithoSharp.Routing.SiteRoute.ForDirectoryIndex(\"Intro/\",baseUrl:unknown!)!", "", 0),
            ("nonnull-base", "\"intro\"!", "LithoSharp.Routing.SiteRoute.ForDirectoryIndex(\"Intro/\",baseUrl:\"https://example.test/\")!", "", 0),
            ("route-alias", "\"intro\"!", "address!", "var address=" + TypedOriginRoute + ";", 0),
            ("route-getter", "\"intro\"!", "RouteGetter!", "", 0),
            ("invalid-route-owned-once", "\"intro\"!", "LithoSharp.Routing.SiteRoute.ForDirectoryIndex(\"../bad/\"!,baseUrl:null)!", "", 1),
        })
        {
            var source = TypedOriginSource(before + "var item=" + TypedOriginCreation(key, route, true, true, false) + ";");
            var generated = ManifestGenerate(source);TypedOriginBinds(generated.Compilation, name);
            Check(generated.Diagnostics.Length == 0, "Negative generator remains valid: " + name);
            var diagnostics = await ManifestAnalyze(generated.Compilation);
            Check(diagnostics.Length == scalar && diagnostics.All(d => d.Id == "LSA1002"), "No contradiction from unknown/unmatched values; scalar guard retains ownership: " + name + " " + string.Join(";", diagnostics));
            evidence.Add(new { name, source, diagnostics = diagnostics.Select(TypedOriginDiagnostic).ToArray() });
        }
        foreach (var state in new[] { "open", "stale", "ambiguous", "handwritten-origin", "explicit-static-constructor" })
        {
            var source = TypedOriginSource("var item=" + TypedOriginCreation("((\"intro\"!))", "(" + TypedOriginRoute + "!)", true, true, true) + ";");
            if (state == "explicit-static-constructor") source += "\r\npublic static partial class Guides {static Guides(){}}";
            ManifestInput[] inputs = state == "open" ? [new(Content: "---\ntitle: Hello\ncount: wrong\n---\nBody")] : [new()];
            var generated = ManifestGenerate(source, inputs);var compilation = generated.Compilation;
            TypedOriginBinds(compilation, state);
            Check(state == "open" ? generated.Diagnostics.Length == 1 && generated.Diagnostics[0].Id == "LSG006" : generated.Diagnostics.Length == 0,
                "Generator owns schema failures: " + state + " " + string.Join(";", generated.Diagnostics));
            if (state is "ambiguous" or "handwritten-origin")
            {
                var tree = compilation.SyntaxTrees.Single(t => t.FilePath != "canary.cs");var text = tree.ToString();
                if (state == "ambiguous")
                {
                    var lines = text.Split('\n');var line = Array.FindIndex(lines, l => l.Contains("StaticSiteManifest Manifest {", StringComparison.Ordinal));
                    Check(line > 0 && lines[line - 1].Contains("StaticSiteManifestSource", StringComparison.Ordinal), "Actual manifest provenance line");
                    text = text.Replace(lines[line], lines[line] + "\n" + lines[line - 1] + "\n" + lines[line].Replace("Manifest {", "SecondManifest {", StringComparison.Ordinal), StringComparison.Ordinal);
                }
                compilation = compilation.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(text, (CSharpParseOptions)tree.Options, state == "handwritten-origin" ? "manual.cs" : tree.FilePath));
                TypedOriginBinds(compilation, state + "-changed-generated-tree");
            }
            var analyzeInputs = state == "stale" ? new ManifestInput[] { new(Content: "---\ntitle: Changed\n---\nBody") } : inputs;
            var diagnostics = await ManifestAnalyze(compilation, analyzeInputs);
            Check(diagnostics.Length == 0, "No new absence/contradiction/lookup-policy diagnostic without the existing proof: " + state + " " + string.Join(";", diagnostics));
            evidence.Add(new { name = state, source, generatorDiagnostics = generated.Diagnostics.Select(ManifestDiagnostic).ToArray(), diagnostics = diagnostics.Select(TypedOriginDiagnostic).ToArray() });
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No Core/getter/initializer/conversion execution");
        ManifestEvidence("typed-origin-deferred", evidence);
    }

    private static string TypedOriginCreation(string key, string route, bool named, bool entryWrapped, bool outerWrapped)
    {
        var entry = "new ContentEntryId(" + key + ")";if (entryWrapped) entry = "((" + entry + ")!)";
        var arguments = named ? "id:" + entry + ",route:" + route + ",collectionId:Guides.Catalog.CollectionId" : "Guides.Catalog.CollectionId," + entry + "," + route;
        var expression = "new ContentRef<string>(" + arguments + ")";return outerWrapped ? "((" + expression + ")!)" : expression;
    }
    private static string TypedOriginSource(string body) => "// 😀 original UTF16\r\n" + ManifestModel + "\r\n" + """
        class Calls {
            static string Getter=>"intro";
            static LithoSharp.Routing.SiteRoute RouteGetter=>throw new System.Exception("ROUTE_SENTINEL");
            sealed class Convertible {public static implicit operator string(Convertible value)=>"intro";}
            void M(string unknown,bool flag) {
        """ + "\r\n" + body + "\r\n} }";
    private static void TypedOriginBinds(CSharpCompilation compilation, string name) => Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Typed fixture binds: " + name + " " + string.Join(";", compilation.GetDiagnostics()));
    private static object TypedOriginDiagnostic(Diagnostic d) => new { d.Id, severity = d.Severity.ToString(), start = d.Location.SourceSpan.Start, length = d.Location.SourceSpan.Length,
        expression = d.Location.SourceTree?.GetText().ToString(d.Location.SourceSpan), related = d.AdditionalLocations.Select(l => new { l.SourceSpan.Start, l.SourceSpan.Length, expression = l.SourceTree?.GetText().ToString(l.SourceSpan) }).ToArray(), d.Properties };
}
