using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task ArgumentValuesFollowEvaluationOrderBeforeLaterAssignments()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "StaticApiArgumentEvaluationCases.cs"));
        var compilation = Compile(source, true, "argument-evaluation-cases.cs");
        Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Shared runtime fixture must bind in the packed host");
        var diagnostics = await Analyze(compilation);
        var expected = new Dictionary<string, string[]> {
            ["PositionalValid"] = [], ["NamedValid"] = [], ["NamedReversedValid"] = [],
            ["PositionalInvalidFirst"] = ["relativePath"], ["NamedReversedInvalidFirst"] = ["baseUrl"],
            ["PositionalSwappedInvalidBoth"] = ["relativePath", "baseUrl"],
            ["NamedSwappedInvalidBoth"] = ["relativePath", "baseUrl"],
            ["WithinArgumentValid"] = [], ["OutputNamedReversedInvalidFirst"] = ["relativeOutputPath"],
            ["OptionLaterAssignmentValid"] = [],
        };
        var proof = new List<object>();
        foreach (var (name, parameters) in expected)
        {
            var method = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == name);
            var results = diagnostics.Where(d => method.Span.Contains(d.Location.SourceSpan)).ToArray();
            Check(results.Length == parameters.Length, name + ": expected " + parameters.Length + " diagnostics, got " + string.Join("; ", results.Select(d => d.ToString())));
            foreach (var parameter in parameters)
                Check(results.Count(d => d.GetMessage().Contains("'" + parameter + "'", StringComparison.Ordinal)) == 1, name + ": original parameter " + parameter);
            proof.Add(new { name, diagnostics = results.Select(d => new { d.Id, message = d.GetMessage(), start = d.Location.SourceSpan.Start,
                length = d.Location.SourceSpan.Length, argument = source.Substring(d.Location.SourceSpan.Start, d.Location.SourceSpan.Length) }).ToArray() });
        }
        Check(diagnostics.Length == expected.Values.Sum(p => p.Length), "No diagnostics outside the expected fixture arguments");
        File.WriteAllText(Path.Combine(Required("RA03_EVIDENCE_DIR"), "argument-evaluation-analyzer.json"), JsonSerializer.Serialize(new { status = "PASS", cases = proof }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task AllRegisteredSinksUseActualSymbolsAndNamedParameters()
    {
        await Expect("LithoSharp.SiteUrl.FromAbsolute(\"relative\");", "LSA1001");
        await Expect("LithoSharp.SiteUrl.ForFile(\"../secret\");", "LSA1002");
        await Expect("LithoSharp.SiteUrl.ForDirectory(\"%2e%2e\");", "LSA1002");
        await Expect("LithoSharp.Routing.SiteRoute.ForFile(baseUrl: \"file:///tmp\", relativePath: \"valid.html\");", "LSA1002");
        await Expect("LithoSharp.Routing.SiteRoute.ForDirectoryIndex(\"CON\");", "LSA1002");
        await Expect("new LithoSharp.SiteAssetOutput(relativeOutputPath: \"../escape\", id: \"asset\");", "LSA1003");
        await Expect("new LithoSharp.Quality.SiteQualityOptions(failureThreshold: (LithoSharp.Diagnostics.SiteDiagnosticSeverity)9);", "LSA1004");
        await Expect("LithoSharp.SiteUrl.ForDirectory(\"\"); LithoSharp.Routing.SiteRoute.ForFile(\"café/😀.html\"); new LithoSharp.SiteAssetOutput(\"a\", \"ok.html\"); new LithoSharp.Quality.SiteQualityOptions();");
    }

    [Test]
    public async Task ConstantFormsAndSingleAssignmentKeepOriginalArgumentSpans()
    {
        await Expect("const string scheme = \"file\"; var url = scheme + \":///tmp\"; LithoSharp.SiteUrl.FromAbsolute(url);", "LSA1001");
        await Expect("const string x = \"file\"; LithoSharp.SiteUrl.FromAbsolute($\"{x}:///tmp\");", "LSA1001");
        await Expect("string x = \"file\"; LithoSharp.SiteUrl.FromAbsolute($\"{x}:///tmp\");", "LSA1001");
        await Expect("LithoSharp.SiteUrl.FromAbsolute(@\"file:///tmp\");", "LSA1001");
        await Expect("LithoSharp.SiteUrl.FromAbsolute(\"\"\"file:///tmp\"\"\");", "LSA1001");
        await Expect("const string x = \"file:///tmp\"; LithoSharp.SiteUrl.FromAbsolute((string)x);", "LSA1001");
        const string source = "// 😀 UTF16\nclass C { void M() { string x = \"file:///tmp\"; LithoSharp.SiteUrl.FromAbsolute(x); } }";
        var result = await Analyze(Compile(source, true));
        var span = source.LastIndexOf("(x)", StringComparison.Ordinal) + 1;
        Check(result.Length == 1 && result[0].Location.SourceSpan.Start == span && result[0].Location.SourceSpan.Length == 1,
            "Flow origin falls back to the original argument, never a fabricated substring");
        File.WriteAllText(Path.Combine(Required("RA03_EVIDENCE_DIR"), "origin-map.json"), JsonSerializer.Serialize(new
        { source, span, length = 1, diagnostic = result[0].Id, result[0].Properties }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task InitializersAndNestedFunctionsDoNotCrashOrLoseConstantDiagnostics()
    {
        foreach (var source in new[] {
            "class C { object x = LithoSharp.SiteUrl.FromAbsolute(\"file:///a\"); }",
            "class C { object X => LithoSharp.SiteUrl.FromAbsolute(\"file:///a\"); }",
            "class C { void M() { void Local() { LithoSharp.SiteUrl.FromAbsolute(\"file:///a\"); } Local(); } }",
            "class C { void M() { System.Action action = () => { LithoSharp.SiteUrl.FromAbsolute(\"file:///a\"); }; action(); } }" })
            Check((await Analyze(Compile(source, true))).Count(d => d.Id == "LSA1001") == 1, "Initializer/nested function: " + source);
    }

    [Test]
    public async Task FiniteBranchJoinsRequireAllValuesToBeInvalid()
    {
        await Expect("string x; if (b) x = \"file:///a\"; else x = \"ftp://a\"; LithoSharp.SiteUrl.FromAbsolute(x);", "LSA1001");
        await Expect("var x = b ? \"file:///a\" : \"ftp://a\"; LithoSharp.SiteUrl.FromAbsolute(x);", "LSA1001");
        await Expect("var x = b ? \"file:///a\" : \"https://a\"; LithoSharp.SiteUrl.FromAbsolute(x);");
        await Expect("var x = b ? \"file:///a\" : unknown; LithoSharp.SiteUrl.FromAbsolute(x);");
        await Expect("if (false) LithoSharp.SiteUrl.FromAbsolute(\"file:///dead\");");
        await Expect("string x; if (true) x = \"https://a\"; else x = \"file:///dead\"; LithoSharp.SiteUrl.FromAbsolute(x);");
    }

    [Test]
    public async Task EightCandidatesRemainKnownAndNineAreDeferred()
    {
        string Branches(int count) => string.Join(" ", Enumerable.Range(0, count - 1).Select(i => $"n == {i} ? \"file:///{i}\" :")) + $" \"file:///{count - 1}\"";
        var eight = await Expect("var x = " + Branches(8) + "; LithoSharp.SiteUrl.FromAbsolute(x);", "LSA1001");
        Check(eight[0].Properties["candidateCount"] == "8", "Finite set cap is eight actual candidates");
        await Expect("var x = " + Branches(9) + "; LithoSharp.SiteUrl.FromAbsolute(x);");
        File.WriteAllText(Path.Combine(Required("RA03_EVIDENCE_DIR"), "budget-results.json"), JsonSerializer.Serialize(new
        { cap = 8, eight = "Known/Error/all-invalid", nine = "Deferred/candidate-budget", loop = "Deferred/unassigned-or-loop" }));
    }

    [Test]
    public async Task UserCodeAndAliasEscapeNeverBecomeKnown()
    {
        const string source = "class C { static readonly string Field = \"file:///tmp\"; static string Getter => throw new System.InvalidOperationException(\"GETTER_SENTINEL\"); void M(string unknown) { LithoSharp.SiteUrl.FromAbsolute(Field); LithoSharp.SiteUrl.FromAbsolute(Getter); LithoSharp.SiteUrl.FromAbsolute(System.Environment.GetEnvironmentVariable(\"HOME\")); } }";
        Check((await Analyze(Compile(source, true))).Length == 0, "No getter/readonly/static initializer/environment execution");
        await Expect("string x = \"file:///tmp\"; ref string y = ref x; y = \"https://a\"; LithoSharp.SiteUrl.FromAbsolute(x);");
        await Expect("string x = \"file:///tmp\"; void Set() { x = \"https://a\"; } Set(); LithoSharp.SiteUrl.FromAbsolute(x);");
        await Expect("string x = \"file:///tmp\"; void Set(ref string y) { y = unknown; } Set(ref x); LithoSharp.SiteUrl.FromAbsolute(x);");
        await Expect("string x = \"file:///tmp\"; void Set(out string y) { y = unknown; } Set(out x); LithoSharp.SiteUrl.FromAbsolute(x);");
        await Expect("object x = n; LithoSharp.SiteUrl.FromAbsolute($\"{x}\");");
        await Expect("string x = \"file:///tmp\"; while (b) { x = unknown; } LithoSharp.SiteUrl.FromAbsolute(x);");
        await Expect("string x = \"file:///tmp\"; try { } finally { x = \"https://a\"; } LithoSharp.SiteUrl.FromAbsolute(x);");
        await Expect("string x = \"file:///tmp\"; try { if (b) throw new System.Exception(); } catch { x = \"https://a\"; } LithoSharp.SiteUrl.FromAbsolute(x);");
    }

    [Test]
    public async Task AnalyzerAndRuntimeRejectTheSameBadCorpus()
    {
        using var proof = JsonDocument.Parse(File.ReadAllText(Required("RA03_RUNTIME_PROOF")));
        var matched = new List<object>();
        foreach (var row in proof.RootElement.GetProperty("results").EnumerateArray())
        {
            var sink = row.GetProperty("sink").GetString()!;
            var input = row.GetProperty("input");
            var literal = input.GetRawText();
            var (body, id) = sink switch
            {
                "SiteUrl.FromAbsolute" => ($"LithoSharp.SiteUrl.FromAbsolute({literal});", "LSA1001"),
                "SiteRoute.ForFile" => ($"LithoSharp.Routing.SiteRoute.ForFile({literal});", "LSA1002"),
                "SiteRoute.ForDirectoryIndex" => ($"LithoSharp.Routing.SiteRoute.ForDirectoryIndex({literal});", "LSA1002"),
                "SiteAssetOutput" => ($"new LithoSharp.SiteAssetOutput(\"asset\", {literal});", "LSA1003"),
                "SiteQualityOptions" => ($"new LithoSharp.Quality.SiteQualityOptions((LithoSharp.Diagnostics.SiteDiagnosticSeverity)({input.GetString()}));", "LSA1004"),
                _ => throw new InvalidOperationException("Unmapped runtime corpus sink: " + sink),
            };
            await Expect(body, id);
            matched.Add(new { sink, input = input.ToString(), id, runtimeException = row.GetProperty("exception").GetString() });
        }
        await Expect("new LithoSharp.SiteAssetOutput(\"a\", \"%2e%2e.html\"); LithoSharp.SiteUrl.FromAbsolute(\"https://example.test/café?q=1\");");
        File.WriteAllText(Path.Combine(Required("RA03_EVIDENCE_DIR"), "analyzer-runtime-parity.json"), JsonSerializer.Serialize(new { status = "PASS", matched }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task LookalikeRoutesPathsAndOptionsStayOutsideScope()
    {
        const string source = "namespace LithoSharp.Routing { public class SiteRoute { public static SiteRoute ForFile(string relativePath, string baseUrl = null) => new SiteRoute(); } } namespace LithoSharp { public class SiteAssetOutput { public SiteAssetOutput(string id, string relativeOutputPath) {} } } namespace LithoSharp.Quality { public class SiteQualityOptions { public SiteQualityOptions(int failureThreshold = 0) {} } } class C { void M() { LithoSharp.Routing.SiteRoute.ForFile(\"../a\"); new LithoSharp.SiteAssetOutput(\"a\", \"../a\"); new LithoSharp.Quality.SiteQualityOptions(999); } }";
        Check((await Analyze(Compile(source, false))).Length == 0, "Fake names in user assembly are not registered sinks");
        var compilation = Compile("class C { void M() { LithoSharp.SiteUrl.FromAbsolute(123); } }", true);
        Check(compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Fixture has a real binding error");
        Check((await Analyze(compilation)).Length == 0, "Compiler owns failed conversion");
    }

    [Test]
    public async Task BudgetExhaustionAndParallelRepeatedRunsAreSafe()
    {
        await Expect(string.Join(" ", Enumerable.Range(0, 1400).Select(i => $"int x{i} = {i};")) + "LithoSharp.SiteUrl.FromAbsolute(\"file:///budget\");");
        await Expect("string x = \"file:///\"; LithoSharp.SiteUrl.FromAbsolute(x" + string.Concat(Enumerable.Repeat(" + \"a\"", 70)) + ");");
        var source = "class C { void M(bool b) { var x = b ? \"file:///a\" : \"ftp://a\"; LithoSharp.SiteUrl.FromAbsolute(x); } }";
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Analyze(Compile(source, true))));
        Check(results.All(r => r.Length == 1 && r[0].Id == "LSA1001" && r[0].Location.SourceSpan == results[0][0].Location.SourceSpan), "Deterministic parallel compilations");
    }

    private static async Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> Expect(string body, params string[] ids)
    {
        var compilation = Compile("class C { void M(bool b, string unknown, int n) { " + body + " } }", true);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Check(errors.Length == 0, "Fixture must bind: " + string.Join("; ", errors.Select(d => d.ToString())));
        var diagnostics = await Analyze(compilation);
        Check(diagnostics.Select(d => d.Id).Order().SequenceEqual(ids.Order()),
            "Expected " + string.Join(",", ids) + " but got " + string.Join("; ", diagnostics.Select(d => d.ToString())) + " in " + body);
        return diagnostics;
    }
}
