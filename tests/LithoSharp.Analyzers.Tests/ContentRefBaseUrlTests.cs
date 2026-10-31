using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task ContentRefBaseUrlsUseRuntimeGuardAndEvaluationSnapshots()
    {
        var proof = new List<object>();
        foreach (var (name, body, candidates) in new[]
        {
            ("string-ref", "/*😀*/ reference.GetUrl(\"relative\");", 1),
            ("int-ref", "numbers.GetUrl(\"file:///tmp\");", 1),
            ("named-empty", "reference.GetUrl(baseUrl:\"\");", 1),
            ("query-is-not-a-base", "reference.GetUrl(\"https://example.test/?q=1\");", 1),
            ("fragment-is-not-a-base", "reference.GetUrl(\"https://example.test/#part\");", 1),
            ("all-known-invalid", "var url=b?\"relative\":\"\"; reference.GetUrl(url);", 2),
            ("receiver-assignment-before-argument", "var url=\"https://example.test/\"; ((url=\"relative\")==\"\"?reference:reference).GetUrl(url);", 1),
            ("getter-receiver-literal-argument", "ReceiverGetter.GetUrl(\"relative\");", 1),
            ("call-receiver-literal-argument", "var url=\"https://example.test/\"; Select(ref url,reference).GetUrl(\"relative\");", 1),
            ("pragma", "#pragma warning disable LSA1002\r\nreference.GetUrl(\"hidden-relative\");\r\n#pragma warning restore LSA1002\r\n/*😀*/ reference.GetUrl(\"visible-relative\");", 1),
        })
        {
            var source = ContentRefBaseSource(body);
            var compilation = Compile(source, true, name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Actual generic Core method binds: " + name);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == 1, name + ": " + string.Join("; ", diagnostics.Select(d => d.ToString())));
            var invocation = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Last(node => node.Expression is MemberAccessExpressionSyntax member && member.Name.Identifier.ValueText == "GetUrl");
            var expression = invocation.ArgumentList.Arguments.Single().Expression;
            var diagnostic = diagnostics[0];
            var at = expression.SpanStart;
            var position = diagnostic.Location.GetLineSpan().StartLinePosition;
            Check(diagnostic.Id == "LSA1002" && diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.GetMessage().Contains("'baseUrl'", StringComparison.Ordinal)
                && diagnostic.GetMessage().Contains("ContentRef.GetUrl", StringComparison.Ordinal), "Only the base argument contract: " + name);
            Check(diagnostic.Location.SourceSpan == expression.Span && diagnostic.Location.SourceTree!.FilePath == name + ".cs"
                && position.Line + 1 == source[..at].Count(c => c == '\n') + 1
                && position.Character + 1 == at - source.LastIndexOf('\n', at - 1), "Original UTF16 argument location: " + name);
            Check(diagnostic.Properties["staticState"] == "Known" && diagnostic.Properties["candidateCount"] == candidates.ToString()
                && diagnostic.Properties["origin"] == "original-argument-utf16-span", "Argument-time value snapshot: " + name);
            proof.Add(new { name, source, diagnostics = diagnostics.Select(ContentRefBaseDiagnostic).ToArray() });
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No Core facade or receiver/getter execution");
        ContentRefBaseEvidence("content-ref-base-known", proof);
    }

    [Test]
    public async Task ContentRefBaseUrlsDeferUnknownReceiverEffectsAndLookalikes()
    {
        var proof = new List<object>();
        foreach (var (name, body) in new[]
        {
            ("valid", "reference.GetUrl(\"https://example.test/docs/\"); numbers.GetUrl(\"https://example.test/\");"),
            ("omitted-and-null", "reference.GetUrl(); reference.GetUrl(null); numbers.GetUrl(baseUrl:null);"),
            ("unknown", "reference.GetUrl(unknown);"),
            ("unknown-mixed", "var url=b?\"relative\":unknown; reference.GetUrl(url);"),
            ("valid-invalid-mixed", "var url=b?\"relative\":\"https://example.test/\"; reference.GetUrl(url);"),
            ("receiver-assignment-replaces-old-invalid", "var url=\"relative\"; ((url=\"https://example.test/\")==\"\"?reference:reference).GetUrl(url);"),
            ("receiver-call-invalidates-local", "var url=\"relative\"; Select(ref url,reference).GetUrl(url);"),
            ("receiver-getter-invalidates-local", "var url=\"relative\"; ReceiverGetter.GetUrl(url);"),
            ("argument-getter-not-executed", "reference.GetUrl(BaseGetter);"),
            ("getter-body-not-a-known-literal", "reference.GetUrl(LiteralGetter);"),
        })
        {
            var source = ContentRefBaseSource(body);
            var compilation = Compile(source, true, name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Negative generic fixture binds: " + name);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == 0, name + ": " + string.Join("; ", diagnostics.Select(d => d.ToString())));
            proof.Add(new { name, source, diagnostics = diagnostics.Select(ContentRefBaseDiagnostic).ToArray() });
        }
        const string fake = "namespace LithoSharp.Content { public sealed class ContentRef<T> { public LithoSharp.SiteUrl GetUrl(string baseUrl=null)=>throw new System.InvalidOperationException(\"FAKE_GETTER_SENTINEL\"); } } class C { void M(LithoSharp.Content.ContentRef<string> value){value.GetUrl(\"relative\");LithoSharp.SiteUrl.FromAbsolute(\"relative\");} }";
        var shadow = Compile(fake, true, "lookalike-with-core.cs");
        Check(!shadow.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Source same-name generic API binds");
        var control = await Analyze(shadow);
        Check(control.Length == 1 && control[0].Id == "LSA1001", "Source lookalike excluded, existing static metadata control retained");
        proof.Add(new { name = "lookalike-with-core", source = fake, diagnostics = control.Select(ContentRefBaseDiagnostic).ToArray() });
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No user getter or Core runtime assembly loaded");
        ContentRefBaseEvidence("content-ref-base-deferred", proof);
    }

    private static string ContentRefBaseSource(string body) => "using Ref=LithoSharp.Content.ContentRef<string>;\r\n// 😀 original UTF16 source\r\nclass C {\r\n"
        + "static Ref ReceiverGetter=>throw new System.InvalidOperationException(\"RECEIVER_GETTER_SENTINEL\");\r\n"
        + "static string BaseGetter=>throw new System.InvalidOperationException(\"BASE_GETTER_SENTINEL\");\r\nstatic string LiteralGetter=>\"relative\";\r\n"
        + "static Ref Select(ref string value,Ref receiver){value=\"https://example.test/\";return receiver;}\r\n"
        + "void M(bool b,string unknown,Ref reference,LithoSharp.Content.ContentRef<int> numbers){\r\n" + body + "\r\n} }";
    private static object ContentRefBaseDiagnostic(Diagnostic d) => new
    {
        d.Id,
        severity = d.Severity.ToString(),
        message = d.GetMessage(),
        start = d.Location.SourceSpan.Start,
        length = d.Location.SourceSpan.Length,
        file = d.Location.SourceTree?.FilePath,
        argument = d.Location.SourceTree?.GetText().ToString(d.Location.SourceSpan),
        d.Properties
    };
    private static void ContentRefBaseEvidence(string name, List<object> proof) => File.WriteAllText(
        Path.Combine(Required("RA03_EVIDENCE_DIR"), name + ".json"),
        JsonSerializer.Serialize(new { status = "PASS", cases = proof }, new JsonSerializerOptions { WriteIndented = true }));
}
