using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task StaticManifestGetUrlNullUsesKnownSnapshotsAndOriginalArgumentLocations()
    {
        var evidence = new List<object>();
        foreach (var (name, body) in new[]
        {
            ("plain-null", "manifest.GetUrl(null);"),
            ("null-forgiving", "/*😀*/ manifest.GetUrl(null!);"),
            ("typed-default", "manifest.GetUrl(default(string)!);"),
            ("target-default", "manifest.GetUrl(default);"),
            ("named-parenthesized", "manifest.GetUrl(publicPath:(null!));"),
            ("parenthesized-literal", "manifest.GetUrl((null)!);"),
            ("constant-local", "const string path=null; manifest.GetUrl(path!);"),
            ("local-snapshot", "string path=null; manifest.GetUrl(path!);"),
            ("assignment-snapshot", "string path=unknown; manifest.GetUrl((path=null)!);"),
            ("finite-all-null", "manifest.GetUrl((flag?null:default(string))!);"),
            ("opaque-receiver", "OpaqueManifest.GetUrl(null!);"),
            ("factory-receiver", "ManifestFactory().GetUrl(null!);"),
            ("receiver-existence-not-certified", "((StaticSiteManifest)null!).GetUrl(null!);"),
            ("pragma", "#pragma warning disable LSA1002\r\nmanifest.GetUrl(null!);\r\n#pragma warning restore LSA1002\r\n/*😀*/ manifest.GetUrl(publicPath:default(string)!);"),
        })
        {
            var source = ManifestNullSource(body);
            var compilation = Compile(source, true);
            ManifestNullBinds(compilation, name);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == 1 && diagnostics[0].Id == "LSA1002" && diagnostics[0].Severity == DiagnosticSeverity.Error,
                "Only the proven null argument guard: " + name + " " + string.Join(";", diagnostics));
            var diagnostic = diagnostics[0];
            var argument = ManifestNullRequests(compilation).Last().ArgumentList.Arguments.Single().Expression;
            var position = diagnostic.Location.GetLineSpan().StartLinePosition;
            Check(diagnostic.Location.SourceSpan == argument.Span && diagnostic.Location.SourceTree!.FilePath == "canary.cs"
                && position.Line == source[..argument.SpanStart].Count(c => c == '\n')
                && position.Character == argument.SpanStart - source.LastIndexOf('\n', argument.SpanStart - 1) - 1, "Original UTF16 argument including !/parentheses: " + name);
            Check(diagnostic.Properties["staticState"] == "Known" && diagnostic.Properties["candidateCount"] == "1"
                && diagnostic.Properties["origin"] == "original-argument-utf16-span"
                && diagnostic.GetMessage().Contains("'publicPath'", StringComparison.Ordinal)
                && diagnostic.GetMessage().Contains("StaticSiteManifest.GetUrl", StringComparison.Ordinal), "Nonempty Known all-null argument snapshot: " + name);
            evidence.Add(new { name, source, diagnostics = diagnostics.Select(ManifestDiagnostic).ToArray() });
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No Core, receiver getter or factory execution");
        ManifestEvidence("manifest-geturl-null-known", evidence);
    }

    [Test]
    public async Task StaticManifestGetUrlNullPreservesUnknownAndCoverageOwnership()
    {
        var evidence = new List<object>();
        foreach (var (name, body) in new[]
        {
            ("nonnull", "manifest.GetUrl(\"/intro/\");"),
            ("empty-is-not-null", "manifest.GetUrl(\"\");"),
            ("missing-path-is-not-null", "manifest.GetUrl(\"/missing/\"!);"),
            ("unknown", "manifest.GetUrl(unknown);"),
            ("unknown-forgiving", "manifest.GetUrl(unknown!);"),
            ("getter", "manifest.GetUrl(Getter!);"),
            ("null-getter-not-interpreted", "manifest.GetUrl(NullGetter!);"),
            ("null-factory-not-interpreted", "manifest.GetUrl(NullFactory()!);"),
            ("null-unknown-join", "manifest.GetUrl((flag?null:unknown)!);"),
            ("null-nonnull-join", "manifest.GetUrl((flag?null:\"/intro/\")!);"),
            ("call-invalidates", "string path=null; Touch(); manifest.GetUrl(path!);"),
            ("ref-invalidates", "string path=null; Replace(ref path,unknown); manifest.GetUrl(path!);"),
            ("getter-invalidates", "string path=null; var ignored=Getter; manifest.GetUrl(path!);"),
            ("receiver-getter-invalidates", "string path=null; OpaqueManifest.GetUrl(path!);"),
            ("user-conversion", "manifest.GetUrl(new Convertible()!);"),
            ("source-method-lookalike", "new Fake().GetUrl(null!);"),
        })
        {
            var source = ManifestNullSource(body);
            var compilation = Compile(source, true);
            ManifestNullBinds(compilation, name);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == 0, "Unknown or non-null never becomes a null-guard Error: " + name + " " + string.Join(";", diagnostics));
            evidence.Add(new { name, source, diagnostics = diagnostics.Select(ManifestDiagnostic).ToArray() });
        }
        const string fakeSource = "namespace LithoSharp.Content { public sealed class StaticSiteManifest { public LithoSharp.SiteUrl GetUrl(string publicPath)=>throw new System.Exception(\"FAKE_SENTINEL\"); } } class C { void M(){new LithoSharp.Content.StaticSiteManifest().GetUrl(null!); LithoSharp.SiteUrl.FromRoute(null!);} }";
        var fake = Compile(fakeSource, true);
        ManifestNullBinds(fake, "source-same-name-type");
        var fakeDiagnostics = await Analyze(fake);
        Check(fakeDiagnostics.Length == 1 && fakeDiagnostics[0].Id == "LSA1002"
            && fakeDiagnostics[0].GetMessage().Contains("SiteUrl.FromRoute", StringComparison.Ordinal), "Same-name source manifest excluded; real metadata control retained");
        evidence.Add(new { name = "source-same-name-type", source = fakeSource, diagnostics = fakeDiagnostics.Select(ManifestDiagnostic).ToArray() });

        const string calls = "Guides.Manifest.GetUrl(null); Guides.Manifest.GetUrl(null!);";
        foreach (var state in new[] { "fresh", "open", "stale" })
        {
            var source = ManifestNullSource(calls);
            ManifestInput[] inputs = state == "open" ? [new(Content: "---\ntitle: Hello\ncount: wrong\n---\nBody")] : [new()];
            var generated = ManifestGenerate(source, inputs);
            ManifestNullBinds(generated.Compilation, state);
            Check(state == "open" ? generated.Diagnostics.Length == 1 && generated.Diagnostics[0].Id == "LSG006" : generated.Diagnostics.Length == 0,
                "Generator owns the existing Open error: " + state + " " + string.Join(";", generated.Diagnostics));
            var analyzeInputs = state == "stale" ? new ManifestInput[] { new(Content: "---\ntitle: Changed\n---\nBody") } : inputs;
            var disabled = await ManifestAnalyze(generated.Compilation, analyzeInputs);
            var enabled = await ManifestAnalyze(ManifestNullPolicy(generated.Compilation), analyzeInputs);
            var requests = ManifestNullRequests(generated.Compilation);
            var scalar = disabled.Where(d => d.Id == "LSA1002").ToArray();
            Check(scalar.Length == 2 && disabled.Length == (state == "stale" ? 4 : 2)
                && scalar.All(d => d.Severity == DiagnosticSeverity.Error && d.Properties["staticState"] == "Known" && d.Properties["candidateCount"] == "1")
                && scalar.Select(d => d.Location.SourceSpan).OrderBy(span => span.Start).SequenceEqual(requests.Select(r => r.ArgumentList.Arguments.Single().Expression.Span)),
                "Real generated receiver: default retains only null guards plus independent Stale Info: " + state + " " + string.Join(";", disabled));
            var policies = enabled.Where(d => d.Id == "LSA9001").OrderBy(d => d.Location.SourceSpan.Start).ToArray();
            Check(policies.Length == 2 && enabled.Length == disabled.Length + 2 && policies.All(d => d.Properties["scope"] == "manifest-route-lookup"
                && d.Properties["scopeState"] == (state == "fresh" ? "Unavailable" : state == "open" ? "Open" : "Stale")
                && d.Properties["origin"] == "original-coverage-request-utf16-span")
                && policies.Select(d => d.Location.SourceSpan).SequenceEqual(requests.Select(r => r.Span)), "Null never proves Complete coverage; policy still owns the entire call: " + state);
            Check(!enabled.Any(d => d.Id is "LSA1201" or "LSA1401") && disabled.Count(d => d.Id == "LSA1205") == (state == "stale" ? 2 : 0)
                && disabled.Select(ManifestNullSignature).Order().SequenceEqual(enabled.Where(d => d.Id != "LSA9001").Select(ManifestNullSignature).Order()), "Membership/Stale/literal-only owners are unchanged: " + state);
            if (state == "fresh")
            {
                var suppressScalar = generated.Compilation.WithOptions(generated.Compilation.Options.WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>
                    { ["LSA1002"] = ReportDiagnostic.Suppress, ["LSA9001"] = ReportDiagnostic.Error }));
                var suppressed = await ManifestAnalyze(suppressScalar);
                Check(suppressed.Length == 2 && suppressed.All(d => d.Id == "LSA9001"), "Suppressing argument Error does not settle or suppress coverage");
                evidence.Add(new { name = "independent-owner-suppression", diagnostics = suppressed.Select(ManifestDiagnostic).ToArray() });
            }
            evidence.Add(new { name = "generated-" + state, source, generatorDiagnostics = generated.Diagnostics.Select(ManifestDiagnostic).ToArray(), defaultDiagnostics = disabled.Select(ManifestDiagnostic).ToArray(), enabledDiagnostics = enabled.Select(ManifestDiagnostic).ToArray() });
        }
        var invalidatedSource = ManifestNullSource("string path=null; Guides.Manifest.GetUrl(path!);");
        var invalidated = ManifestGenerate(invalidatedSource);
        ManifestNullBinds(invalidated.Compilation, "generated-local-invalidated");
        Check(invalidated.Diagnostics.Length == 0, "Fresh generator input");
        var ordinary = await ManifestAnalyze(invalidated.Compilation);
        var ordinaryPolicy = await ManifestAnalyze(ManifestNullPolicy(invalidated.Compilation));
        Check(ordinary.Length == 0 && ordinaryPolicy.Length == 1 && ordinaryPolicy[0].Id == "LSA9001"
            && ordinaryPolicy[0].Properties["scopeState"] == "Unknown" && ordinaryPolicy[0].Location.SourceSpan == ManifestNullRequests(invalidated.Compilation).Single().Span,
            "Generated receiver getter still invalidates the ordinary local; syntax never revives Known null");
        evidence.Add(new { name = "generated-local-invalidated", source = invalidatedSource, defaultDiagnostics = ordinary.Select(ManifestDiagnostic).ToArray(), enabledDiagnostics = ordinaryPolicy.Select(ManifestDiagnostic).ToArray() });
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "Getters, factories, conversions and Core never execute");
        ManifestEvidence("manifest-geturl-null-deferred", evidence);
    }

    private static void ManifestNullBinds(CSharpCompilation compilation, string name) => Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Real metadata fixture binds: " + name + " " + string.Join(";", compilation.GetDiagnostics()));
    private static CSharpCompilation ManifestNullPolicy(CSharpCompilation compilation) => compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["LSA9001"] = ReportDiagnostic.Error }));
    private static string ManifestNullSignature(Diagnostic d) => d.Id + "|" + d.Severity + "|" + d.Location.SourceSpan + "|" + d.GetMessage();
    private static InvocationExpressionSyntax[] ManifestNullRequests(CSharpCompilation compilation) => compilation.SyntaxTrees.Single(t => t.FilePath == "canary.cs").GetRoot().DescendantNodes()
        .OfType<InvocationExpressionSyntax>().Where(n => n.Expression.ToString().EndsWith(".GetUrl", StringComparison.Ordinal)).OrderBy(n => n.SpanStart).ToArray();
    private static string ManifestNullSource(string body) => "// 😀 original UTF16 source\r\n" + ManifestModel + "\r\n" + """
        class Fake { public LithoSharp.SiteUrl GetUrl(string publicPath)=>throw new System.Exception("FAKE_SENTINEL"); }
        class Calls {
            static string Getter=>throw new System.Exception("GETTER_SENTINEL");
            static string NullGetter=>null;
            static string NullFactory()=>null;
            static StaticSiteManifest OpaqueManifest=>throw new System.Exception("RECEIVER_SENTINEL");
            static StaticSiteManifest ManifestFactory()=>throw new System.Exception("FACTORY_SENTINEL");
            static void Touch()=>throw new System.Exception("CALL_SENTINEL");
            static void Replace(ref string value,string other){value=other;}
            sealed class Convertible { public static implicit operator string(Convertible value)=>null; }
            void M(StaticSiteManifest manifest,string unknown,bool flag) {
        """ + "\r\n" + body + "\r\n} }";
}
