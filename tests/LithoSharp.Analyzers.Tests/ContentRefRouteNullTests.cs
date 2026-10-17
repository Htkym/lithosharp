using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task ContentRefNullRoutesUseExactConstructorAndEvaluationSnapshots()
    {
        var proof = new List<object>();
        foreach (var (name, body) in new[]
        {
            ("plain", "new Ref(collection,entry,null);"),
            ("forgiving-parentheses", "new Ref(collection,entry,((null!)));"),
            ("default", "new Ref(collection,entry,default(Route)!);"),
            ("known-local", "Route route=null!;new Ref(collection,entry,route!);"),
            ("all-null-branch", "Route route=flag?null!:default(Route)!;new Ref(collection,entry,route);"),
            ("generic-int-original-definition", "new LithoSharp.Content.ContentRef<int>(collection,entry,null!);"),
            ("named-reversed", "new Ref(route:((null!)),id:entry,collectionId:collection);"),
            ("named-route-before-later-call", "Route route=null!;new Ref(route:route,collectionId:Touch(ref route),id:entry);"),
            ("named-route-before-later-getter", "Route route=null!;new Ref(route:route,id:EntryGetter,collectionId:collection);"),
            ("earlier-call-literal-null", "Route route=unknown;new Ref(Touch(ref route),entry,null!);"),
            ("earlier-getter-literal-null", "new Ref(CollectionGetter,entry,null!);"),
            ("earlier-null-guards-no-completion-claim", "new Ref(null!,null!,null!);"),
            ("primary-pragma", "#pragma warning disable LSA1002\r\nnew Ref(collection,entry,null!);\r\n#pragma warning restore LSA1002\r\n/*😀*/new Ref(collection,entry,((null!)));"),
        })
        {
            var source = ContentRefNullSource(body);
            var compilation = Compile(source, true, name + ".cs");
            ContentRefNullBinds(compilation, name);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == 1, name + ": " + string.Join(";", diagnostics));
            var d = diagnostics[0];
            var tree = compilation.SyntaxTrees.Single();
            var creation = tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Last(n => n.Type.ToString() == "Ref" || n.Type.ToString().Contains("ContentRef<", StringComparison.Ordinal));
            var op = (IObjectCreationOperation)compilation.GetSemanticModel(tree).GetOperation(creation)!;
            var boundRoute = op.Arguments.Single(a => a.Parameter?.Ordinal == 2);
            var argument = creation.ArgumentList!.Arguments.SingleOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "route")
                ?? creation.ArgumentList.Arguments[2];
            var expression = argument.Expression;
            var position = d.Location.GetLineSpan().StartLinePosition;var at = expression.SpanStart;
            Check(op.Constructor!.ContainingAssembly.Identity.Name == "LithoSharp"
                && op.Constructor.OriginalDefinition.ContainingType.TypeParameters.Length == 1
                && boundRoute.ArgumentKind == ArgumentKind.Explicit, "Actual generic metadata ctor binding: " + name);
            Check(d.Id == "LSA1002" && d.Severity == DiagnosticSeverity.Error
                && d.GetMessage().Contains("'route'", StringComparison.Ordinal)
                && d.GetMessage().Contains("ContentRef..ctor", StringComparison.Ordinal)
                && d.AdditionalLocations.Count == 0, "Only the route argument contract, not completion: " + name);
            Check(d.Location.SourceSpan == expression.Span && d.Location.SourceTree == tree
                && position.Line + 1 == source[..at].Count(c => c == '\n') + 1
                && position.Character + 1 == at - source.LastIndexOf('\n', at - 1), "Original whole UTF16/CRLF route expression: " + name);
            Check(d.Properties["staticState"] == "Known" && d.Properties["candidateCount"] == "1"
                && d.Properties["origin"] == "original-argument-utf16-span", "Existing ordinal2 evaluation snapshot: " + name);
            proof.Add(new { name, source, originalDefinition = op.Constructor.OriginalDefinition.ToDisplayString(),
                diagnostics = diagnostics.Select(ContentRefNullDiagnostic).ToArray() });
        }
        var standardSource = ContentRefNullSource("new Ref(collection,entry,null!);");
        var standard = Compile(standardSource, true, "standard.cs");
        foreach (var severity in new[] { ReportDiagnostic.Warn, ReportDiagnostic.Suppress })
        {
            var diagnostics = await Analyze(standard.WithOptions(standard.Options.WithSpecificDiagnosticOptions(
                new Dictionary<string, ReportDiagnostic> { ["LSA1002"] = severity })));
            Check(severity == ReportDiagnostic.Warn
                ? diagnostics.Length == 1 && diagnostics[0].Id == "LSA1002" && diagnostics[0].Severity == DiagnosticSeverity.Warning
                : diagnostics.Length == 0, "Existing severity/suppression: " + severity);
            proof.Add(new { name = severity.ToString(), source = standardSource, diagnostics = diagnostics.Select(ContentRefNullDiagnostic).ToArray() });
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No Core or user runtime execution");
        ManifestEvidence("contentref-route-known", proof);
    }

    [Test]
    public async Task ContentRefNullRoutesDeferUnknownAndKeepDiagnosticOwnership()
    {
        var proof = new List<object>();
        foreach (var (name, body) in new[]
        {
            ("nonnull-valid-route", "new Ref(collection,entry,Route.ForFile(\"ok.html\"));"),
            ("nonnull-valid-local", "var route=Route.ForFile(\"ok.html\");new Ref(collection,entry,route);"),
            ("unknown", "new Ref(collection,entry,unknown!);"),
            ("mixed-null-nonnull", "var route=flag?null:Route.ForFile(\"ok.html\");new Ref(collection,entry,route!);"),
            ("mixed-null-unknown", "var route=flag?null:unknown;new Ref(collection,entry,route!);"),
            ("route-getter", "new Ref(collection,entry,RouteGetter!);"),
            ("user-conversion", "new Ref(collection,entry,new Convertible());"),
            ("earlier-collection-call-invalidates", "Route route=null!;new Ref(Touch(ref route),entry,route!);"),
            ("earlier-entry-call-invalidates", "Route route=null!;new Ref(collection,TouchEntry(ref route),route!);"),
            ("earlier-collection-getter-invalidates", "Route route=null!;new Ref(CollectionGetter,entry,route!);"),
            ("earlier-entry-getter-invalidates", "Route route=null!;new Ref(collection,EntryGetter,route!);"),
            ("named-call-before-route", "Route route=null!;new Ref(collectionId:Touch(ref route),route:route!,id:entry);"),
            ("named-getter-before-route", "Route route=null!;new Ref(id:EntryGetter,route:route!,collectionId:collection);"),
            ("reassignment-unknown", "Route route=null!;route=unknown;new Ref(collection,entry,route!);"),
            ("other-null-parameters", "new Ref(null!,null!,Route.ForFile(\"ok.html\"));"),
        })
        {
            var source = ContentRefNullSource(body);
            var compilation = Compile(source, true, name + ".cs");
            ContentRefNullBinds(compilation, name);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == 0, "Unknown/mixed/other parameters do not prove null route: " + name + " " + string.Join(";", diagnostics));
            proof.Add(new { name, source, diagnostics = diagnostics.Select(ContentRefNullDiagnostic).ToArray() });
        }
        const string fake = """
            namespace LithoSharp.Content {
                public sealed class ContentRef<T> {
                    public ContentRef(ContentCollectionId collectionId, ContentEntryId id, LithoSharp.Routing.SiteRoute route)
                    {throw new System.Exception("FAKE_CTOR_SENTINEL");}
                }
            }
            class C {void M(LithoSharp.Content.ContentCollectionId c,LithoSharp.Content.ContentEntryId id) {
                new LithoSharp.Content.ContentRef<string>(c,id,null!);
                LithoSharp.SiteUrl.FromAbsolute("relative");
            }}
            """;
        var shadow = Compile(fake, true, "source-lookalike.cs");
        ContentRefNullBinds(shadow, "source-lookalike");
        var control = await Analyze(shadow);
        Check(control.Length == 1 && control[0].Id == "LSA1001", "Fake ctor excluded; old static/self-return registration retained");
        proof.Add(new { name = "source-lookalike", source = fake, diagnostics = control.Select(ContentRefNullDiagnostic).ToArray() });
        foreach (var (name, route, expected) in new[]
        {
            ("existing-mismatch-owner", "LithoSharp.Routing.SiteRoute.ForDirectoryIndex(\"Intro/\",baseUrl:null)!", "LSA1402"),
            ("matching-catalog-reference", "LithoSharp.Routing.SiteRoute.ForDirectoryIndex(\"intro/\",baseUrl:null)!", ""),
            ("null-route-not-a-contradiction", "((null!))", "LSA1002"),
        })
        {
            var source = "// 😀 UTF16\r\n" + ManifestModel + "\r\nclass Calls {void M(){new ContentRef<string>(Guides.Catalog.CollectionId,new ContentEntryId(\"intro\")," + route + ");}}";
            var generated = ManifestGenerate(source);
            ContentRefNullBinds(generated.Compilation, name);Check(generated.Diagnostics.Length == 0, "Actual Generator remains valid: " + name);
            var diagnostics = await ManifestAnalyze(generated.Compilation);
            Check(expected.Length == 0 ? diagnostics.Length == 0 : diagnostics.Length == 1 && diagnostics[0].Id == expected,
                "Existing diagnostic ownership: " + name + " " + string.Join(";", diagnostics));
            if (expected == "LSA1402")
                Check(diagnostics[0].Properties["coverage"] == "Closed"
                    && diagnostics[0].Properties["entryId"] == "intro"
                    && diagnostics[0].Properties["expectedPublicPath"] == "/intro/"
                    && diagnostics[0].Properties["actualPublicPath"] == "/Intro/"
                    && diagnostics[0].Properties["currentFingerprint"] == diagnostics[0].Properties["expectedFingerprint"],
                    "Existing mismatch values and fresh Closed proof retained");
            proof.Add(new { name, source, diagnostics = diagnostics.Select(ContentRefNullDiagnostic).ToArray() });
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No Core/getter/call/conversion execution");
        ManifestEvidence("contentref-route-deferred", proof);
    }

    private static string ContentRefNullSource(string body) => """
        using Ref=LithoSharp.Content.ContentRef<string>;
        using Route=LithoSharp.Routing.SiteRoute;
        using Collection=LithoSharp.Content.ContentCollectionId;
        using Entry=LithoSharp.Content.ContentEntryId;
        // 😀 original UTF16 source
        class Calls {
            static Collection CollectionGetter=>throw new System.Exception("COLLECTION_GETTER_SENTINEL");
            static Entry EntryGetter=>throw new System.Exception("ENTRY_GETTER_SENTINEL");
            static Route RouteGetter=>null!;
            static Collection Touch(ref Route route){route=Route.ForFile("ok.html");throw new System.Exception("CALL_SENTINEL");}
            static Entry TouchEntry(ref Route route){route=Route.ForFile("ok.html");throw new System.Exception("ENTRY_CALL_SENTINEL");}
            sealed class Convertible {public static implicit operator Route(Convertible value)=>null!;}
            void M(bool flag,Route unknown,Collection collection,Entry entry) {
        """.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n" + body + "\r\n} }";
    private static void ContentRefNullBinds(CSharpCompilation compilation, string name) =>
        Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error),
            "Actual Core fixture binds: " + name + " " + string.Join(";", compilation.GetDiagnostics()));
    private static object ContentRefNullDiagnostic(Diagnostic d) => new
    {
        d.Id, severity = d.Severity.ToString(), message = d.GetMessage(), start = d.Location.SourceSpan.Start,
        length = d.Location.SourceSpan.Length, expression = d.Location.SourceTree?.GetText().ToString(d.Location.SourceSpan),
        related = d.AdditionalLocations.Select(l => new { l.SourceSpan.Start, l.SourceSpan.Length }).ToArray(), d.Properties
    };
}

