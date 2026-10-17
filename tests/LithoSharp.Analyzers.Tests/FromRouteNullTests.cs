using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task FromRouteNullUsesKnownNonemptySnapshotsAndOriginalLocations()
    {
        var proof = new List<object>();
        foreach (var (name, body) in new[]
        {
            ("literal-null", "/*😀*/ LithoSharp.SiteUrl.FromRoute(null);"),
            ("null-forgiving", "/*😀*/ LithoSharp.SiteUrl.FromRoute(null!);"),
            ("default-forgiving", "LithoSharp.SiteUrl.FromRoute(default(Route)!);"),
            ("local-forgiving", "Route value=null; LithoSharp.SiteUrl.FromRoute(value!);"),
            ("named-parenthesized-forgiving", "LithoSharp.SiteUrl.FromRoute(route:(null!));"),
            ("typed-default", "LithoSharp.SiteUrl.FromRoute(default(Route));"),
            ("target-default", "LithoSharp.SiteUrl.FromRoute(default);"),
            ("named", "LithoSharp.SiteUrl.FromRoute(route: null);"),
            ("known-null-local", "Route value=null; LithoSharp.SiteUrl.FromRoute(value);"),
            ("all-null-branches", "Route value=b?null:default(Route); LithoSharp.SiteUrl.FromRoute(value);"),
            ("assignment-snapshot", "Route value=route; LithoSharp.SiteUrl.FromRoute(value=null);"),
            ("pragma", "#pragma warning disable LSA1002\r\nLithoSharp.SiteUrl.FromRoute(null);\r\n#pragma warning restore LSA1002\r\n/*😀*/ LithoSharp.SiteUrl.FromRoute(route:default(Route));"),
            ("pragma-forgiving", "#pragma warning disable LSA1002\r\nLithoSharp.SiteUrl.FromRoute(null!);\r\n#pragma warning restore LSA1002\r\n/*😀*/ LithoSharp.SiteUrl.FromRoute(route:default(Route)!);"),
        })
        {
            var source = FromRouteNullSource(body);
            var compilation = Compile(source, true, name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Actual FromRoute overload binds: " + name);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == 1, name + ": " + string.Join("; ", diagnostics.Select(d => d.ToString())));
            var invocation = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Last(node => node.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "FromRoute");
            var expression = invocation.ArgumentList.Arguments.Single().Expression;
            var diagnostic = diagnostics[0]; var at = expression.SpanStart;
            var position = diagnostic.Location.GetLineSpan().StartLinePosition;
            Check(diagnostic.Id == "LSA1002" && diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.GetMessage().Contains("'route'", StringComparison.Ordinal)
                && diagnostic.GetMessage().Contains("SiteUrl.FromRoute", StringComparison.Ordinal), "Only the null argument contract: " + name);
            Check(diagnostic.Location.SourceSpan == expression.Span && diagnostic.Location.SourceTree!.FilePath == name + ".cs"
                && position.Line + 1 == source[..at].Count(c => c == '\n') + 1
                && position.Character + 1 == at - source.LastIndexOf('\n', at - 1), "Original UTF16 argument location: " + name);
            Check(diagnostic.Properties["staticState"] == "Known" && diagnostic.Properties["candidateCount"] == "1"
                && diagnostic.Properties["origin"] == "original-argument-utf16-span", "Nonempty Known null snapshot: " + name);
            proof.Add(new { name, source, diagnostics = diagnostics.Select(FromRouteNullDiagnostic).ToArray() });
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No Core or user execution");
        FromRouteNullEvidence("from-route-null-known", proof);
    }

    [Test]
    public async Task FromRouteNullDefersUnknownInvalidationConversionsAndLookalikes()
    {
        var proof = new List<object>();
        foreach (var (name, body) in new[]
        {
            ("opaque-nonnull", "LithoSharp.SiteUrl.FromRoute(route);"),
            ("opaque-forgiving", "LithoSharp.SiteUrl.FromRoute(route!);"),
            ("runtime-route-factory", "LithoSharp.SiteUrl.FromRoute(Route.ForFile(\"valid.html\"));"),
            ("null-plus-unknown", "Route value=b?null:route; LithoSharp.SiteUrl.FromRoute(value);"),
            ("unknown-plus-null", "Route value=b?route:null; LithoSharp.SiteUrl.FromRoute(value);"),
            ("unknown-mixed-forgiving", "Route value=b?null!:route; LithoSharp.SiteUrl.FromRoute(value!);"),
            ("throwing-getter", "LithoSharp.SiteUrl.FromRoute(Getter);"),
            ("getter-forgiving", "LithoSharp.SiteUrl.FromRoute(Getter!);"),
            ("null-getter-not-interpreted", "LithoSharp.SiteUrl.FromRoute(NullGetter);"),
            ("throwing-call", "LithoSharp.SiteUrl.FromRoute(Factory());"),
            ("null-call-not-interpreted", "LithoSharp.SiteUrl.FromRoute(NullFactory());"),
            ("call-invalidates-null-local", "Route value=null; Touch(); LithoSharp.SiteUrl.FromRoute(value!);"),
            ("ref-call-invalidates-null-local", "Route value=null; Replace(ref value,route); LithoSharp.SiteUrl.FromRoute(value!);"),
            ("getter-invalidates-null-local", "Route value=null; var ignored=Getter; LithoSharp.SiteUrl.FromRoute(value!);"),
            ("user-conversion-not-null", "LithoSharp.SiteUrl.FromRoute(new Convertible()!);"),
            ("omitted-default-and-null-base-still-valid", "reference.GetUrl(); reference.GetUrl(baseUrl:null!);"),
        })
        {
            var source = FromRouteNullSource(body);
            var compilation = Compile(source, true, name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Negative FromRoute fixture binds: " + name);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == 0, name + ": " + string.Join("; ", diagnostics.Select(d => d.ToString())));
            proof.Add(new { name, source, diagnostics = diagnostics.Select(FromRouteNullDiagnostic).ToArray() });
        }
        const string fake = "namespace LithoSharp { public sealed class SiteUrl { public static SiteUrl FromRoute(LithoSharp.Routing.SiteRoute route)=>throw new System.InvalidOperationException(\"FAKE_SENTINEL\"); } } class C { void M(){LithoSharp.SiteUrl.FromRoute(null!);LithoSharp.Routing.SiteRoute.ForFile(\"../invalid\"!);} }";
        var shadow = Compile(fake, true, "lookalike-with-core.cs");
        Check(!shadow.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Source same-name API binds");
        var control = await Analyze(shadow);
        Check(control.Length == 1 && control[0].Id == "LSA1002"
            && control[0].GetMessage().Contains("SiteRoute.ForFile", StringComparison.Ordinal), "Source lookalike excluded, existing static metadata control retained");
        Check(control[0].Location.SourceTree!.GetText().ToString(control[0].Location.SourceSpan) == "\"../invalid\"!", "Shared explicit-argument handoff preserves the static control's original suppression expression");
        proof.Add(new { name = "lookalike-with-core", source = fake, diagnostics = control.Select(FromRouteNullDiagnostic).ToArray() });
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "Getters, calls, conversion bodies and Core runtime are not executed");
        FromRouteNullEvidence("from-route-null-deferred", proof);
    }

    private static string FromRouteNullSource(string body) => "using Route=LithoSharp.Routing.SiteRoute;\r\n// 😀 original UTF16 source\r\nclass C {\r\n"
        + "static Route Getter=>throw new System.InvalidOperationException(\"GETTER_SENTINEL\");\r\nstatic Route NullGetter=>null;\r\n"
        + "static Route Factory()=>throw new System.InvalidOperationException(\"FACTORY_SENTINEL\");\r\nstatic Route NullFactory()=>null;\r\n"
        + "static void Touch()=>throw new System.InvalidOperationException(\"TOUCH_SENTINEL\");\r\n"
        + "static void Replace(ref Route value,Route other){value=other;}\r\n"
        + "sealed class Convertible {public static implicit operator Route(Convertible value)=>null;}\r\n"
        + "void M(bool b,Route route,LithoSharp.Content.ContentRef<string> reference){\r\n" + body + "\r\n} }";
    private static object FromRouteNullDiagnostic(Diagnostic d) => new
    {
        d.Id, severity = d.Severity.ToString(), message = d.GetMessage(), start = d.Location.SourceSpan.Start,
        length = d.Location.SourceSpan.Length, file = d.Location.SourceTree?.FilePath,
        argument = d.Location.SourceTree?.GetText().ToString(d.Location.SourceSpan), d.Properties
    };
    private static void FromRouteNullEvidence(string name, List<object> proof) => File.WriteAllText(
        Path.Combine(Required("RA03_EVIDENCE_DIR"), name + ".json"),
        JsonSerializer.Serialize(new { status = "PASS", cases = proof }, new JsonSerializerOptions { WriteIndented = true }));
}
