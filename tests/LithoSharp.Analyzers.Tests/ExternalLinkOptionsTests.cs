using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task ExternalLinkOptionsUseRuntimeGuardsAndOriginalArgumentSnapshots()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Required("EXTERNAL_OPTIONS_CORPUS")));
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(Required("RA03_EVIDENCE_DIR"), "external-options-runtime.json")));
        var actual = runtime.RootElement.GetProperty("results").EnumerateArray().ToDictionary(row => row.GetProperty("name").GetString()!);
        var proof = new List<object>();
        foreach (var row in corpus.RootElement.EnumerateArray())
        {
            var name = row.GetProperty("name").GetString()!;
            var slot = row.GetProperty("slot").GetInt32();
            var parameter = new[] { "cacheFilePath", "cacheDuration", "minimumRequestInterval", "requestTimeout" }[slot];
            var path = slot == 0 ? row.GetProperty("path").GetString() : "cache.json";
            var args = new List<string> { "cacheFilePath:" + (path is null ? "null" : SymbolDisplay.FormatLiteral(path, true)) };
            if (slot != 0) args.Add(parameter + ":" + row.GetProperty("expression").GetString());
            foreach (var reverse in new[] { false, true })
            {
                var source = "// 😀 UTF16\nclass C { void M() { new LithoSharp.Quality.ExternalLinkCheckOptions(" + string.Join(",", reverse ? args.AsEnumerable().Reverse() : args) + "); } }";
                var compilation = Compile(source, true, name + ".cs");
                Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Options corpus binds: " + name);
                var diagnostics = (await Analyze(compilation)).Where(d => d.Id == "LSA1004").ToArray();
                var rejected = actual[name].GetProperty("rejected").GetBoolean();
                Check(diagnostics.Length == (rejected ? 1 : 0), "Runtime/host options parity: " + name + " " + string.Join(";", diagnostics.Select(d => d.ToString())));
                if (rejected)
                {
                    var syntax = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                        .Single(node => node.Type.ToString() == "LithoSharp.Quality.ExternalLinkCheckOptions");
                    var argument = syntax.ArgumentList!.Arguments.Single(a => a.NameColon!.Name.Identifier.ValueText == parameter);
                    Check(diagnostics[0].Location.SourceSpan == argument.Expression.Span && diagnostics[0].Severity == DiagnosticSeverity.Error
                        && diagnostics[0].GetMessage().Contains("'" + parameter + "'", StringComparison.Ordinal), "Original parameter span: " + name);
                }
                proof.Add(new { name, reverse, rejected });
            }
        }
        await Options("var path=\"\"; var alias=path; new LithoSharp.Quality.ExternalLinkCheckOptions(alias);", "cacheFilePath");
        await Options("var t=System.TimeSpan.Zero; var alias=t; new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:alias);", "requestTimeout");
        await Options("var t=b?System.TimeSpan.Zero:System.TimeSpan.MinValue; new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:t);", "requestTimeout");
        await Options("var t=System.TimeSpan.Zero; new LithoSharp.Quality.ExternalLinkCheckOptions(requestTimeout:t,cacheFilePath:(t=System.TimeSpan.FromTicks(1L)).ToString());", "requestTimeout");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No compiler-host Core/user execution");
        File.WriteAllText(Path.Combine(Required("RA03_EVIDENCE_DIR"), "external-options-inputs.json"), JsonSerializer.Serialize(new { status="PASS", runtimeCases=actual.Count, cases=proof }, new JsonSerializerOptions { WriteIndented=true }));
    }

    [Test]
    public async Task ExternalLinkOptionsUnknownAndUnsupportedTimeSpansStayDeferred()
    {
        var bodies = new[] {
            "new LithoSharp.Quality.ExternalLinkCheckOptions(unknown);",
            "new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:duration);",
            "var t=b?System.TimeSpan.Zero:System.TimeSpan.FromTicks(1L); new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:t);",
            "var t=b?System.TimeSpan.Zero:duration; new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:t);",
            "new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:System.TimeSpan.FromSeconds(-0.5d));",
            "new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:System.TimeSpan.FromSeconds(double.NaN));",
            "new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:System.TimeSpan.FromDays(double.PositiveInfinity));",
            "new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:System.TimeSpan.FromDays(double.MaxValue));",
            "new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:System.TimeSpan.Parse(\"00:00:00\"));",
            "new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:Getter);",
            "new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:Field);",
            "new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:Fake.FromSeconds());",
            "var t=System.TimeSpan.Zero; Escape(); new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:t);",
            "var bad=\"\"; var good=\"cache\"; (bad,good)=(good,bad); new LithoSharp.Quality.ExternalLinkCheckOptions(bad);",
            "var good=System.TimeSpan.FromTicks(1L); var bad=System.TimeSpan.Zero; (bad,good)=(good,bad); new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:bad);",
            "var good=System.TimeSpan.Zero; var bad=System.TimeSpan.MinValue; (bad,good)=(good,bad); new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",cacheDuration:bad);"
        };
        foreach (var body in bodies) await Options(body);
        string Branches(int count) => string.Join(" ", Enumerable.Range(0,count-1).Select(i => "n=="+i+"?System.TimeSpan.FromTicks(-"+(i+1)+"L):"))+"System.TimeSpan.FromTicks(-"+count+"L)";
        await Options("var t="+Branches(8)+"; new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:t);", "requestTimeout");
        await Options("var t="+Branches(9)+"; new LithoSharp.Quality.ExternalLinkCheckOptions(\"c\",requestTimeout:t);");
        await Options("new LithoSharp.Quality.ExternalLinkCheckOptions(\"\",requestTimeout:duration);", "cacheFilePath");
        const string fake = "namespace LithoSharp.Quality { class ExternalLinkCheckOptions { public ExternalLinkCheckOptions(string cacheFilePath){} } } class C { void M(){new LithoSharp.Quality.ExternalLinkCheckOptions(\"\");}}";
        Check(!(await Analyze(Compile(fake, false))).Any(d=>d.Id=="LSA1004"), "Source lookalike options excluded");
        var suppressed = Compile("#pragma warning disable LSA1004\nclass C { void M(){new LithoSharp.Quality.ExternalLinkCheckOptions(\"\");}}", true);
        Check(!(await Analyze(suppressed)).Any(d=>d.Id=="LSA1004"), "Standard suppression");
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var propagated = false;
        try { await Analyze(Compile("class C{}", true),canceled.Token); } catch (OperationCanceledException) { propagated=true; }
        Check(propagated, "Cancellation propagates");
        File.WriteAllText(Path.Combine(Required("RA03_EVIDENCE_DIR"), "external-options-boundaries.json"), JsonSerializer.Serialize(new { status="PASS", cases=bodies, deconstructionStringAndTimeSpan=true, candidates8Known9Deferred=true, suppression=true, cancellation=true }));
    }

    private static async Task Options(string body, params string[] parameters)
    {
        var source = "static class Fake { public static System.TimeSpan FromSeconds()=>throw new System.Exception(\"FACTORY_SENTINEL\"); } class C { static readonly System.TimeSpan Field=System.TimeSpan.Zero; static System.TimeSpan Getter=>throw new System.Exception(\"GETTER_SENTINEL\"); static void Escape(){} void M(bool b,int n,string unknown,System.TimeSpan? duration){"+body+"} }";
        var compilation = Compile(source, true);
        Check(!compilation.GetDiagnostics().Any(d=>d.Severity==DiagnosticSeverity.Error), "Options flow fixture binds: "+body);
        var results = (await Analyze(compilation)).Where(d=>d.Id=="LSA1004").ToArray();
        Check(results.Length==parameters.Length && parameters.All(p=>results.Count(d=>d.GetMessage().Contains("'"+p+"'",StringComparison.Ordinal))==1), "Options proof: "+body+" "+string.Join(";",results.Select(d=>d.ToString())));
    }
}
