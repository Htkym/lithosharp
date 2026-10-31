using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task StaticCoveragePolicyUsesStandardOptInAndOriginalRequestLocations()
    {
        var rule = Packed.Value.Analyzers.Single().SupportedDiagnostics.Single(d => d.Id == "LSA9001");
        Check(!rule.IsEnabledByDefault && rule.DefaultSeverity == DiagnosticSeverity.Error, "Policy is disabled by default and uses standard severity");
        var evidence = new List<object>();
        foreach (var (name, body, state) in new (string, string, string?)[]
        {
            ("known-present","Guides.Manifest.GetUrl(\"/intro/\");",null),
            ("known-missing","Guides.Manifest.GetUrl(\"/missing/\");",null),
            ("known-mixed","Guides.Manifest.GetUrl(flag?\"/intro/\":\"/missing/\");",null),
            ("unknown-value","Guides.Manifest.GetUrl(unknown);","Unknown"),
            ("receiver-alias","var alias=Guides.Manifest; alias.GetUrl(\"/intro/\");","Unavailable"),
            ("opaque-getter","Opaque.GetUrl(\"/intro/\");","Unavailable"),
            ("opaque-factory","Factory().GetUrl(\"/intro/\");","Unavailable"),
            ("open","Guides.Manifest.GetUrl(\"/intro/\");","Open"),
            ("stale","Guides.Manifest.GetUrl(\"/intro/\");","Stale"),
            ("lookalike","new Fake().GetUrl(unknown);",null),
            ("general-unknown","LithoSharp.Html.UnsafeRaw(unknown);",null),
        })
        {
            var source = CoverageManifestSource(body);
            ManifestInput[] inputs = name == "open" ? [new(Content: "---\nunknown: true\n---\nBody")] : [new()];
            var generated = ManifestGenerate(source, inputs);
            Check(!generated.Compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Policy input binds: " + name);
            var analyzeInputs = name == "stale" ? new ManifestInput[] { new(Content: "---\ntitle: Changed\n---\nBody") } : inputs;
            var disabled = await ManifestAnalyze(generated.Compilation, analyzeInputs);
            Check(!disabled.Any(d => d.Id == "LSA9001"), "Default policy disabled: " + name);
            var enabled = await ManifestAnalyze(CoverageCompilation(generated.Compilation), analyzeInputs);
            var policy = enabled.Where(d => d.Id == "LSA9001").ToArray();
            Check(policy.Length == (state is null ? 0 : 1), "One original request policy: " + name + " " + string.Join(";", enabled));
            if (state is not null)
            {
                var request = generated.Compilation.SyntaxTrees.Single(t => t.FilePath == "canary.cs").GetRoot().DescendantNodes()
                    .OfType<InvocationExpressionSyntax>().Single(n => n.Expression.ToString().EndsWith(".GetUrl", StringComparison.Ordinal));
                Check(policy[0].Location.SourceSpan == request.Span && policy[0].Properties["scopeState"] == state
                    && policy[0].Properties["scope"] == "manifest-route-lookup" && policy[0].Properties["reason"] is { Length: > 0 }
                    && policy[0].Severity == DiagnosticSeverity.Error, "Original request span and unverified state: " + name);
            }
            Check(disabled.Select(ManifestDiagnosticSignature).Order().SequenceEqual(enabled.Where(d => d.Id != "LSA9001").Select(ManifestDiagnosticSignature).Order()), "Existing diagnostic meanings/locations unchanged: " + name);
            if (name == "known-missing")
                Check(enabled.Count(d => d.Id == "LSA1201") == 1, "Known absence remains LSA1201, not policy failure");
            if (name == "stale")
                Check(enabled.Count(d => d.Id == "LSA1205") == 1 && !enabled.Any(d => d.Id == "LSA1201"), "Stale Info remains separate");
            evidence.Add(new { name, source, diagnostics = enabled.Select(ManifestDiagnostic).ToArray() });
        }
        var original = CoverageManifestSource("Guides.Manifest.GetUrl(unknown);");
        var output = ManifestGenerate(original);
        var warning = await ManifestAnalyze(CoverageCompilation(output.Compilation, ReportDiagnostic.Warn));
        Check(warning.Single(d => d.Id == "LSA9001").Severity == DiagnosticSeverity.Warning, "Standard severity override");
        Check(!(await ManifestAnalyze(CoverageCompilation(output.Compilation, ReportDiagnostic.Suppress))).Any(d => d.Id == "LSA9001"), "Standard per-ID suppression / NoWarn");
        var pragma = ManifestGenerate("#pragma warning disable LSA9001\n" + original);
        Check(!(await ManifestAnalyze(CoverageCompilation(pragma.Compilation))).Any(d => d.Id == "LSA9001"), "Original source pragma suppresses compilation-end policy");
        var generatedCode = Compile("class G { void M(LithoSharp.Content.StaticSiteManifest manifest,string value){manifest.GetUrl(value);} }", true, "generated.g.cs");
        Check(!(await Analyze(CoverageCompilation(generatedCode))).Any(d => d.Id == "LSA9001"), "Generated-code exclusion");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No runtime facade or user execution");
        ManifestEvidence("coverage-policy-opt-in", new { cases = evidence, defaultDisabled = true, severityOverride = true, suppression = true, pragma = true, generatedExcluded = true });
    }

    [Test]
    public async Task StaticCoveragePolicyBudgetsUnavailableAndEmptySelectionsStayDistinct()
    {
        var evidence = new List<object>();
        foreach (var (name, prefix, reason) in new[]
        {
            ("operation-budget","int n=0;"+string.Concat(Enumerable.Repeat("n++;",1500)),"operation-budget"),
            ("block-budget","int n=0;"+string.Concat(Enumerable.Repeat("if(flag)n++;",140)),"block-budget"),
        })
        {
            var source = CoverageManifestSource(prefix + "Guides.Manifest.GetUrl(\"/intro/\");");
            var output = ManifestGenerate(source);
            Check(!output.Compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Budget fixture binds");
            var diagnostics = await ManifestAnalyze(CoverageCompilation(output.Compilation));
            var policy = diagnostics.Single(d => d.Id == "LSA9001");
            Check(policy.Properties["scopeState"] == "Unavailable" && policy.Properties["reason"] == reason, "Budget reaches original request discovery: " + name + " " + string.Join(";", diagnostics));
            var request = output.Compilation.SyntaxTrees.Single(t => t.FilePath == "canary.cs").GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single(n => n.Expression.ToString().EndsWith(".GetUrl", StringComparison.Ordinal));
            Check(policy.Location.SourceSpan == request.Span, "Budget primary is original request");
            evidence.Add(new { name, diagnostics = diagnostics.Select(ManifestDiagnostic).ToArray() });
        }
        var nested = ManifestGenerate(CoverageManifestSource(string.Concat(Enumerable.Range(0, 70).Select(i => "string L" + i + "()=>Guides.Manifest.GetUrl(unknown).ToString();"))));
        var nestedDiagnostics = (await ManifestAnalyze(CoverageCompilation(nested.Compilation))).Where(d => d.Id == "LSA9001").ToArray();
        Check(nestedDiagnostics.Length == 70 && nestedDiagnostics.Any(d => d.Properties["reason"] == "nested-graph-budget"), "Nested graph exhaustion never loses discovered requests");
        evidence.Add(new { name = "nested-graph-budget", diagnostics = nestedDiagnostics.Select(ManifestDiagnostic).ToArray() });

        var huge = new ManifestInput(Content: new string('x', 8 * 1024 * 1024 + 1));
        var docs = "var docs=new DocumentationSite(new MdxOptions(\"D:/site\",\"D:/worker\")); docs.AddCollection(new DocumentationCollection(\"guide\",[new DocumentVariant(\"current\",\"en\",\"D:/content\",\"guide\")]));";
        var operationBudget = "int n=0;" + string.Concat(Enumerable.Repeat("n++;", 1500));
        foreach (var (name, selection, registration, prefix, state, info, large) in new (string, string, string, string, string?, int, bool)[]
        {
            ("known-empty-producer","[empty]","[empty]","",null,0,false),
            ("known-deferred","[docs,docs]","[empty,docs]",docs,"Deferred",1,false),
            ("opaque-selection","GetList()","[empty]","","Unavailable",0,false),
            ("opaque-registration","[empty]","GetList()","","Unavailable",0,false),
            ("not-registered","[new EmptyProducer()]","[]","","Unavailable",0,false),
            ("unknown-identity","[Getter]","[empty]","","Unavailable",0,false),
            ("branch","[empty]","[empty]","if(flag)empty=new EmptyProducer();","Unavailable",0,false),
            ("conditional-identity","[chosen]","[empty]","var chosen=flag?empty:new EmptyProducer();","Unavailable",0,false),
            ("producer-budget","["+string.Join(",",Enumerable.Repeat("empty",65))+"]","[empty]","","Unavailable",0,false),
            ("producer-flow-budget","[empty]","[empty]",operationBudget,"Unavailable",0,false),
            ("fingerprint-unavailable","[docs]","[docs]",docs,"Unavailable",0,true),
            ("empty-before-registration-and-fingerprint","[]","GetList()","",null,0,true),
            ("empty-before-flow-budget","[]","GetList()",operationBudget,null,0,true),
            ("empty-array-before-registration","new ISiteBuildExtension[]{}","GetList()","",null,0,true),
            ("empty-sized-array-before-registration","new ISiteBuildExtension[0]","GetList()","",null,0,true),
            ("known-empty-before-fingerprint","[empty]","[empty]","",null,0,true),
        })
        {
            var source = CoverageProducerSource(selection, registration, prefix);
            var compilation = CoverageProducerCompilation(source);
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Coverage producer binds: " + name + " " + string.Join(";", compilation.GetDiagnostics()));
            ManifestInput[] inputs = large ? [huge] : [];
            var disabled = await ManifestAnalyze(compilation, inputs);
            var enabled = await ManifestAnalyze(CoverageCompilation(compilation), inputs);
            var policies = enabled.Where(d => d.Id == "LSA9001").ToArray();
            Check(!disabled.Any(d => d.Id == "LSA9001") && policies.Length == (state is null ? 0 : 1), "Policy scope versus empty selection: " + name + " " + string.Join(";", enabled));
            Check(enabled.Count(d => d.Id == "LSA1403") == info && disabled.Select(ManifestDiagnosticSignature).Order().SequenceEqual(enabled.Where(d => d.Id != "LSA9001").Select(ManifestDiagnosticSignature).Order()), "Producer Info unchanged: " + name);
            if (state is not null)
            {
                Check(policies[0].Properties["scopeState"] == state && policies[0].Location.SourceSpan == new TextSpan(source.IndexOf(selection, source.IndexOf("StaticRouteGuaranteeProducers =", StringComparison.Ordinal), StringComparison.Ordinal), selection.Length), "Original producer selection/state: " + name);
                if (name == "producer-flow-budget")
                    Check(policies[0].Properties["reason"] == "operation-budget", "Producer callback skipped but policy retained budget reason");
                if (name == "fingerprint-unavailable")
                    Check(policies[0].Properties["reason"] == "fingerprint-unavailable", "No dynamic Info from missing fingerprint");
            }
            evidence.Add(new { name, source, largeFingerprintInput = large, diagnostics = enabled.Select(ManifestDiagnostic).ToArray() });
        }
        var standalone = CoverageProducerCompilation("using LithoSharp; class C { object M()=>new SiteGenerationOptions { StaticRouteGuaranteeProducers=[] }; }");
        Check(!(await ManifestAnalyze(CoverageCompilation(standalone), [huge])).Any(d => d.Id == "LSA9001"), "Empty selection before unknown owner/registration/fingerprint");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            await CoverageCompilation(nested.Compilation).WithAnalyzers(Packed.Value.Analyzers).GetAnalyzerDiagnosticsAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Check(cancelled, "Cancellation cannot return partial policy success");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!) || a.GetName().Name == "LithoSharp.Mdx"), "Core/MDX/user methods never execute");
        ManifestEvidence("coverage-policy-boundaries", new { cases = evidence, cancelled, emptyStandalone = true });
    }

    private static CSharpCompilation CoverageCompilation(CSharpCompilation compilation, ReportDiagnostic severity = ReportDiagnostic.Error) => compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { { "LSA9001", severity } }));
    private static string ManifestDiagnosticSignature(Diagnostic d) => d.Id + "|" + d.Severity + "|" + d.GetMessage() + "|" + d.Location.GetLineSpan() + "|" + d.Location.SourceSpan;
    private static string CoverageManifestSource(string body) => "// 😀 original policy position\n" + ManifestModel + "\nclass Fake { public string GetUrl(string value)=>throw new System.Exception(); } class Calls { static StaticSiteManifest Opaque=>throw new System.Exception(); static StaticSiteManifest Factory()=>throw new System.Exception(); void M(string unknown,bool flag){" + body + "} }";
    private static CSharpCompilation CoverageProducerCompilation(string source) => Compile(source, true).AddReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Where(p => new[] { "System.Collections.dll", "System.Threading.dll", "System.Threading.Tasks.dll", "System.Text.Json.dll" }.Contains(Path.GetFileName(p))).Select(p => MetadataReference.CreateFromFile(p))).AddReferences(MetadataReference.CreateFromFile(Required("RA05_MDX_METADATA")));
    private static string CoverageProducerSource(string selection, string registration, string prefix) => """
        using System; using System.Threading; using System.Threading.Tasks;
        using LithoSharp; using LithoSharp.Build; using LithoSharp.Configuration;
        using LithoSharp.Documentation; using LithoSharp.Mdx;
        sealed class EmptyProducer : ISiteBuildExtension {
            public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context,CancellationToken cancellationToken=default)=>Task.FromResult(new SiteBuildContribution());
        }
        static class Scopes {
            static ISiteBuildExtension Getter=>throw new Exception();
            static System.Collections.Generic.IReadOnlyList<ISiteBuildExtension> GetList()=>throw new Exception();
            static SiteDefinition M(bool flag) { var empty=new EmptyProducer();
        """ + prefix + "\nreturn new SiteDefinition(new SiteSettings {Title=\"Docs\",BaseUrl=\"https://example.test/\"},[]) {Options=new SiteGenerationOptions {Extensions=" + registration + ",StaticRouteGuaranteeProducers = " + selection + "}}; } }";
}
