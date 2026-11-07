using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task SiteAssetRelativePathsUseRuntimeGuardsAndOriginalArguments()
    {
        var proof = new List<object>();
        foreach (var (name, body, ordinals, candidates) in new[]
        {
            ("input", "/*😀*/ new LithoSharp.SiteAsset(\"a\",root,\"../in.txt\",\"out.txt\");", new[]{2}, 1),
            ("output", "new LithoSharp.SiteAsset(\"a\",root,\"in.txt\",\"/out.txt\");", new[]{3}, 1),
            ("named-reversed-both", "new LithoSharp.SiteAsset(relativeOutputPath:\"../out.txt\",relativeInputPath:null,inputRoot:root,id:\"a\");", new[]{2,3}, 1),
            ("locals-both", "var input=\"\"; var output=\"../out.txt\"; var alias=input; new LithoSharp.SiteAsset(\"a\",root,alias,output);", new[]{2,3}, 1),
            ("all-known-invalid", "var input=b?\"../in-a\":\"../in-b\"; var output=b?\"../out-a\":\"../out-b\"; new LithoSharp.SiteAsset(\"a\",root,input,output);", new[]{2,3}, 2),
            ("input-snapshot", "var path=\"../before\"; new LithoSharp.SiteAsset(relativeInputPath:path,relativeOutputPath:(path=\"after.txt\"),inputRoot:root,id:\"a\");", new[]{2}, 1),
            ("output-snapshot", "var path=\"../before\"; new LithoSharp.SiteAsset(relativeOutputPath:path,relativeInputPath:(path=\"after.txt\"),inputRoot:root,id:\"a\");", new[]{3}, 1),
            ("pragma", "#pragma warning disable LSA1003\r\nnew LithoSharp.SiteAsset(\"hidden\",root,\"../hidden-in\",\"../hidden-out\");\r\n#pragma warning restore LSA1003\r\n/*😀*/ new LithoSharp.SiteAsset(\"visible\",root,\"../visible-in\",\"out.txt\");", new[]{2}, 1),
        })
        {
            var source = SiteAssetPathSource(body);
            var compilation = Compile(source, true, name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Real SiteAsset metadata binds: " + name);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == ordinals.Length, name + ": " + string.Join("; ", diagnostics.Select(d => d.ToString())));
            var creation = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Last(node => node.Type.ToString() == "LithoSharp.SiteAsset");
            foreach (var ordinal in ordinals)
            {
                var parameter = ordinal == 2 ? "relativeInputPath" : "relativeOutputPath";
                var arguments = creation.ArgumentList!.Arguments;
                var argument = arguments.Any(a => a.NameColon is not null)
                    ? arguments.Single(a => a.NameColon!.Name.Identifier.ValueText == parameter) : arguments[ordinal];
                var diagnostic = diagnostics.Single(d => d.Location.SourceSpan == argument.Expression.Span);
                var at = argument.Expression.SpanStart;
                var position = diagnostic.Location.GetLineSpan().StartLinePosition;
                Check(diagnostic.Id == "LSA1003" && diagnostic.Severity == DiagnosticSeverity.Error
                    && diagnostic.GetMessage().Contains("'" + parameter + "'", StringComparison.Ordinal)
                    && diagnostic.GetMessage().Contains("SiteAsset..ctor", StringComparison.Ordinal), "Registered argument identity: " + name);
                Check(position.Line + 1 == source[..at].Count(c => c == '\n') + 1
                    && position.Character + 1 == at - source.LastIndexOf('\n', at - 1)
                    && diagnostic.Location.SourceTree!.FilePath == name + ".cs", "Original UTF16 line/column: " + name);
                Check(diagnostic.Properties["staticState"] == "Known" && diagnostic.Properties["candidateCount"] == candidates.ToString()
                    && diagnostic.Properties["origin"] == "original-argument-utf16-span", "Existing flow proof: " + name);
            }
            proof.Add(new { name, source, diagnostics = diagnostics.Select(SiteAssetPathDiagnostic).ToArray() });
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "Core/IO/runtime facade never loaded");
        SiteAssetPathEvidence("site-asset-paths-known", proof);
    }

    [Test]
    public async Task SiteAssetRelativePathsDeferUnknownMixedAndLookalikeInputs()
    {
        var proof = new List<object>();
        foreach (var (name, body) in new[]
        {
            ("valid", "new LithoSharp.SiteAsset(\"a\",root,\"images/in.png\",\"assets/out.png\");"),
            ("literal-percent-unicode", "new LithoSharp.SiteAsset(\"a\",root,\"%2e%2e/café.png\",\"assets/😀%20.png\");"),
            ("unknown-input", "new LithoSharp.SiteAsset(\"a\",root,unknown,\"out.txt\");"),
            ("unknown-output", "new LithoSharp.SiteAsset(\"a\",root,\"in.txt\",unknown);"),
            ("unknown-mixed-both", "var input=b?\"../in\":unknown; var output=b?\"../out\":unknown; new LithoSharp.SiteAsset(\"a\",root,input,output);"),
            ("valid-invalid-mixed-both", "var input=b?\"../in\":\"in.txt\"; var output=b?\"../out\":\"out.txt\"; new LithoSharp.SiteAsset(\"a\",root,input,output);"),
            ("earlier-valid-snapshots", "var path=\"ok.txt\"; new LithoSharp.SiteAsset(relativeInputPath:path,relativeOutputPath:path,inputRoot:(path=\"../later\"),id:\"a\");"),
            ("id-root-outside-scope", "new LithoSharp.SiteAsset(\"\",\"\",\"in.txt\",\"out.txt\");"),
        })
        {
            var source = SiteAssetPathSource(body);
            var compilation = Compile(source, true, name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Negative fixture binds: " + name);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == 0, name + ": " + string.Join("; ", diagnostics.Select(d => d.ToString())));
            proof.Add(new { name, source, diagnostics = diagnostics.Select(SiteAssetPathDiagnostic).ToArray() });
        }
        const string fake = "namespace LithoSharp { public class SiteAsset { public SiteAsset(string id,string inputRoot,string relativeInputPath,string relativeOutputPath){} } } class C { void M(){new LithoSharp.SiteAsset(\"a\",\"\",\"../in\",\"../out\");new LithoSharp.SiteAssetOutput(\"control\",\"../control\");} }";
        var shadow = Compile(fake, true, "lookalike-with-core.cs");
        Check(!shadow.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Source lookalike shadows metadata without binding failure");
        var control = await Analyze(shadow);
        Check(control.Length == 1 && control[0].Id == "LSA1003" && control[0].GetMessage().Contains("SiteAssetOutput..ctor", StringComparison.Ordinal),
            "Fake SiteAsset excluded while the actual metadata control remains active");
        proof.Add(new { name = "lookalike-with-core", source = fake, diagnostics = control.Select(SiteAssetPathDiagnostic).ToArray() });
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No user constructors or Core runtime execution");
        SiteAssetPathEvidence("site-asset-paths-deferred", proof);
    }

    private static string SiteAssetPathSource(string body) => "// 😀 original UTF16 source\r\nclass C { void M(bool b,string unknown,string root) {\r\n" + body + "\r\n} }";
    private static object SiteAssetPathDiagnostic(Diagnostic d) => new
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
    private static void SiteAssetPathEvidence(string name, List<object> proof) => File.WriteAllText(
        Path.Combine(Required("RA03_EVIDENCE_DIR"), name + ".json"),
        JsonSerializer.Serialize(new { status = "PASS", cases = proof }, new JsonSerializerOptions { WriteIndented = true }));
}
