using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task ManifestExplicitArgumentsKeepClosedProofAndOriginalAbsenceSpans()
    {
        var evidence = new List<object>();
        foreach (var (name, body, missing, candidates, typed) in new[]
        {
            ("plain-missing", "Guides.Manifest.GetUrl(\"/missing/\");", 1, "1", 0),
            ("forgiving-missing", "/*😀*/ Guides.Manifest.GetUrl(\"/missing/\"!);", 1, "1", 0),
            ("constant-local-missing", "const string path=\"/missing/\"; Guides.Manifest.GetUrl(path!);", 1, "1", 0),
            ("assignment-snapshot-missing", "string path=unknown; Guides.Manifest.GetUrl((path=\"/missing/\")!);", 1, "1", 0),
            ("named-parenthesized", "Guides.Manifest.GetUrl(publicPath:(\"/missing/\"!));", 1, "1", 0),
            ("finite-all-missing", "Guides.Manifest.GetUrl((flag?\"/missing/\":\"/other/\")!);", 1, "2", 0),
            ("pragma-missing", "#pragma warning disable LSA1201\r\nGuides.Manifest.GetUrl(\"/hidden/\"!);\r\n#pragma warning restore LSA1201\r\n/*😀*/ Guides.Manifest.GetUrl((\"/missing/\")!);", 1, "1", 0),
            ("direct-literal-present-control", "Guides.Manifest.GetUrl(\"/intro/\");", 0, "0", 1),
            ("forgiving-present-no-typed-fix", "Guides.Manifest.GetUrl(\"/intro/\"!);", 0, "0", 0),
            ("parenthesized-present-no-typed-fix", "Guides.Manifest.GetUrl((\"/intro/\"));", 0, "0", 0),
            ("constant-local-present-no-typed-fix", "const string path=\"/intro/\"; Guides.Manifest.GetUrl(path!);", 0, "0", 0),
            ("mixed-present-missing", "Guides.Manifest.GetUrl((flag?\"/intro/\":\"/missing/\")!);", 0, "0", 0),
        })
        {
            var source = ManifestExplicitSource(body);
            var generated = ManifestGenerate(source);
            Check(generated.Diagnostics.Length == 0 && !generated.Compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Fresh actual package input binds: " + name + " " + string.Join(";", generated.Compilation.GetDiagnostics()));
            var disabled = await ManifestAnalyze(generated.Compilation);
            var enabled = await ManifestAnalyze(ManifestExplicitCoverage(generated.Compilation));
            Check(disabled.Select(ManifestExplicitSignature).Order().SequenceEqual(enabled.Select(ManifestExplicitSignature).Order()), "Known snapshots retain Complete coverage with policy opt-in: " + name);
            Check(enabled.Length == missing + typed && enabled.Count(d => d.Id == "LSA1201") == missing
                && enabled.Count(d => d.Id == "LSA1401") == typed && !enabled.Any(d => d.Id == "LSA9001"), "Only proven absence or direct literal recommendation: " + name + " " + string.Join(";", enabled));
            if (missing == 1)
            {
                var expression = ManifestExplicitRequest(generated.Compilation).ArgumentList.Arguments.Single().Expression;
                var diagnostic = enabled.Single(d => d.Id == "LSA1201");
                var position = diagnostic.Location.GetLineSpan().StartLinePosition;
                Check(diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Location.SourceSpan == expression.Span
                    && diagnostic.Location.SourceTree!.FilePath == "canary.cs"
                    && position.Line == source[..expression.SpanStart].Count(c => c == '\n')
                    && position.Character == expression.SpanStart - source.LastIndexOf('\n', expression.SpanStart - 1) - 1, "Whole original argument including !/parentheses has UTF16 span: " + name);
                Check(diagnostic.Properties["coverage"] == "Closed" && diagnostic.Properties["currentFingerprint"] == diagnostic.Properties["expectedFingerprint"]
                    && diagnostic.Properties["candidateCount"] == candidates && diagnostic.Properties["site"] == "docs"
                    && diagnostic.Properties["variant"] == "ja", "Same fresh Closed lookup proof and bounded candidates: " + name);
            }
            evidence.Add(new { name, source, defaultDiagnostics = disabled.Select(ManifestDiagnostic).ToArray(), enabledDiagnostics = enabled.Select(ManifestDiagnostic).ToArray() });
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "Core and user code never execute");
        ManifestEvidence("manifest-explicit-known", evidence);
    }

    [Test]
    public async Task ManifestExplicitArgumentsKeepUnknownAndCoveragePolicyBoundaries()
    {
        var rule = Packed.Value.Analyzers.Single().SupportedDiagnostics.Single(d => d.Id == "LSA9001");
        Check(!rule.IsEnabledByDefault && rule.DefaultSeverity == DiagnosticSeverity.Error, "Standard opt-in default remains disabled Error");
        var evidence = new List<object>();
        foreach (var (name, body, state) in new[]
        {
            ("unknown", "Guides.Manifest.GetUrl(unknown!);", "Unknown"),
            ("receiver-invalidates-plain-local", "string path=\"/missing/\"; Guides.Manifest.GetUrl(path);", "Unknown"),
            ("receiver-invalidates-forgiving-local", "string path=\"/missing/\"; Guides.Manifest.GetUrl(path!);", "Unknown"),
            ("getter", "Guides.Manifest.GetUrl(Getter!);", "Unknown"),
            ("known-looking-getter", "Guides.Manifest.GetUrl(MissingGetter!);", "Unknown"),
            ("unknown-join", "string path=flag?\"/missing/\":unknown; Guides.Manifest.GetUrl(path!);", "Unknown"),
            ("call-invalidates-local", "string path=\"/missing/\"; Touch(); Guides.Manifest.GetUrl(path!);", "Unknown"),
            ("getter-invalidates-local", "string path=\"/missing/\"; var ignored=Getter; Guides.Manifest.GetUrl(path!);", "Unknown"),
            ("user-conversion", "Guides.Manifest.GetUrl(new Convertible()!);", "Unavailable"),
            ("receiver-getter", "Opaque.GetUrl(\"/missing/\"!);", "Unavailable"),
            ("receiver-alias", "var manifest=Guides.Manifest; manifest.GetUrl(\"/missing/\"!);", "Unavailable"),
            ("open", "Guides.Manifest.GetUrl(\"/missing/\"!);", "Open"),
            ("stale", "Guides.Manifest.GetUrl(\"/missing/\"!);", "Stale"),
            ("source-lookalike", "new Fake().GetUrl(\"/missing/\"!);", "None"),
        })
        {
            var source = ManifestExplicitSource(body);
            ManifestInput[] inputs = name == "open" ? [new(Content: "---\ntitle: Hello\ncount: wrong\n---\nBody")] : [new()];
            var generated = ManifestGenerate(source, inputs);
            Check(!generated.Compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Negative input binds: " + name);
            Check(name == "open" ? generated.Diagnostics.Length == 1 && generated.Diagnostics[0].Id == "LSG006" : generated.Diagnostics.Length == 0, "Generator ownership is unchanged: " + name + " " + string.Join(";", generated.Diagnostics));
            var analyzeInputs = name == "stale" ? new ManifestInput[] { new(Content: "---\ntitle: Changed\n---\nBody") } : inputs;
            var disabled = await ManifestAnalyze(generated.Compilation, analyzeInputs);
            var enabled = await ManifestAnalyze(ManifestExplicitCoverage(generated.Compilation), analyzeInputs);
            Check(!disabled.Any(d => d.Id == "LSA9001") && disabled.Select(ManifestExplicitSignature).Order().SequenceEqual(enabled.Where(d => d.Id != "LSA9001").Select(ManifestExplicitSignature).Order()), "Opt-in preserves other owners: " + name);
            var policies = enabled.Where(d => d.Id == "LSA9001").ToArray();
            Check(enabled.Length == (state == "None" ? 0 : 1) + (name == "stale" ? 1 : 0)
                && policies.Length == (state == "None" ? 0 : 1) && !enabled.Any(d => d.Id is "LSA1201" or "LSA1401"), "Unknown/Open/Stale never prove absence or a typed fix: " + name + " " + string.Join(";", enabled));
            if (policies.Length == 1)
            {
                var policy = policies[0]; var request = ManifestExplicitRequest(generated.Compilation);
                Check(policy.Severity == DiagnosticSeverity.Error && policy.Location.SourceSpan == request.Span
                    && policy.Properties["scope"] == "manifest-route-lookup" && policy.Properties["scopeState"] == state
                    && policy.Properties["reason"] is { Length: > 0 } && policy.Properties["origin"] == "original-coverage-request-utf16-span", "Original whole-call coverage location/key and state: " + name);
            }
            if (name == "stale")
            {
                var stale = enabled.Single(d => d.Id == "LSA1205");
                Check(stale.Severity == DiagnosticSeverity.Info && stale.Properties["coverage"] == "Stale"
                    && stale.Location.SourceSpan == new TextSpan(source.IndexOf("Guides.Manifest.GetUrl", StringComparison.Ordinal), "Guides.Manifest".Length), "Stale retains LSA1205 at original receiver");
            }
            evidence.Add(new { name, source, generatorDiagnostics = generated.Diagnostics.Select(ManifestDiagnostic).ToArray(), defaultDiagnostics = disabled.Select(ManifestDiagnostic).ToArray(), enabledDiagnostics = enabled.Select(ManifestDiagnostic).ToArray() });
        }
        var policyInput = ManifestGenerate(ManifestExplicitSource("Guides.Manifest.GetUrl(unknown!);"));
        var warning = await ManifestAnalyze(ManifestExplicitCoverage(policyInput.Compilation, ReportDiagnostic.Warn));
        Check(warning.Length == 1 && warning[0].Id == "LSA9001" && warning[0].Severity == DiagnosticSeverity.Warning, "Standard severity override");
        var suppressed = await ManifestAnalyze(ManifestExplicitCoverage(policyInput.Compilation, ReportDiagnostic.Suppress));
        Check(suppressed.Length == 0, "Standard per-ID suppression");
        var pragmaSource = ManifestExplicitSource("#pragma warning disable LSA9001\r\nGuides.Manifest.GetUrl(unknown!);\r\n#pragma warning restore LSA9001\r\n/*😀*/ Guides.Manifest.GetUrl((unknown!));");
        var pragmaInput = ManifestGenerate(pragmaSource);
        Check(pragmaInput.Diagnostics.Length == 0 && !pragmaInput.Compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Pragma input binds before suppression is checked: " + string.Join(";", pragmaInput.Compilation.GetDiagnostics()));
        var pragma = await ManifestAnalyze(ManifestExplicitCoverage(pragmaInput.Compilation));
        Check(pragma.Length == 1 && pragma[0].Id == "LSA9001" && pragma[0].Location.SourceSpan == ManifestExplicitRequest(pragmaInput.Compilation).Span, "Pragma uses the original whole-call primary location, including !");
        evidence.Add(new { name = "standard-policy-suppression", source = pragmaSource, warning = warning.Select(ManifestDiagnostic).ToArray(), suppressed = suppressed.Select(ManifestDiagnostic).ToArray(), pragma = pragma.Select(ManifestDiagnostic).ToArray() });
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "Getters, conversions and Core are not executed");
        ManifestEvidence("manifest-explicit-deferred", evidence);
    }

    private static CSharpCompilation ManifestExplicitCoverage(CSharpCompilation compilation, ReportDiagnostic severity = ReportDiagnostic.Error) => compilation.WithOptions(
        compilation.Options.WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["LSA9001"] = severity }));
    private static string ManifestExplicitSignature(Diagnostic diagnostic) => diagnostic.Id + "|" + diagnostic.Severity + "|" + diagnostic.Location.SourceSpan + "|" + diagnostic.GetMessage();
    private static InvocationExpressionSyntax ManifestExplicitRequest(CSharpCompilation compilation) => compilation.SyntaxTrees.Single(t => t.FilePath == "canary.cs").GetRoot()
        .DescendantNodes().OfType<InvocationExpressionSyntax>().Last(n => n.Expression.ToString().EndsWith(".GetUrl", StringComparison.Ordinal));
    private static string ManifestExplicitSource(string body) => "// 😀 original UTF16 source\r\n" + ManifestModel + "\r\n" + """
        class Fake { public string GetUrl(string value)=>throw new System.Exception("FAKE_SENTINEL"); }
        class Calls {
            static string Getter=>throw new System.Exception("GETTER_SENTINEL");
            static string MissingGetter=>"/missing/";
            static StaticSiteManifest Opaque=>throw new System.Exception("RECEIVER_SENTINEL");
            static void Touch()=>throw new System.Exception("CALL_SENTINEL");
            sealed class Convertible { public static implicit operator string(Convertible value)=>"/missing/"; }
            void M(string unknown,bool flag) {
        """ + "\r\n" + body + "\r\n} }";
}
