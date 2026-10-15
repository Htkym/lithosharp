using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task InlineCssUrlsKeepRuntimeTokensAndExactOriginalSourceSpans()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Required("INLINE_CSS_CORPUS")));
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "inline-css-runtime.json")));
        var observed = runtime.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(row => row.GetProperty("name").GetString()!);
        var proof = new List<object>();
        foreach (var row in corpus.RootElement.EnumerateArray())
        {
            var name = row.GetProperty("name").GetString()!;
            Check(observed[name].GetProperty("values").EnumerateArray().Select(v => v.GetString()).SequenceEqual(
                row.GetProperty("values").EnumerateArray().Select(v => v.GetString())), "Runtime CSS token decisions: " + name);
            var html = row.GetProperty("html").GetString()!;
            foreach (var form in new[] { "normal", "verbatim", "raw" })
            {
                string Escape(string text) => form == "normal" ? Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(text, false)
                    : form == "verbatim" ? text.Replace("\"", "\"\"") : text;
                var literal = form == "normal" ? Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(html, true)
                    : form == "verbatim" ? "@\"" + Escape(html) + "\"" : "\"\"\"" + html + "\"\"\"";
                var source = "// 😀 UTF16\nclass C { void M() { const string html=" + literal + "; LithoSharp.Html.UnsafeRaw(html); } }";
                var compilation = Compile(source, true, name + "-" + form + ".cs");
                Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Inline CSS fixture binds: " + name + "/" + form);
                var diagnostics = (await Analyze(compilation)).Where(d => d.Id == "LSA1105").OrderBy(d => d.Location.SourceSpan.Start).ToArray();
                var expected = row.GetProperty("errors").EnumerateArray().Select(v => Escape(v.GetString()!))
                    .Select(text => new TextSpan(source.IndexOf(text, StringComparison.Ordinal), text.Length)).ToList();
                if (row.TryGetProperty("zeroAfter", out var zero))
                {
                    var marker = Escape(zero.GetString()!);
                    expected.Add(new(source.IndexOf(marker, StringComparison.Ordinal) + marker.Length, 0));
                }
                Check(diagnostics.Select(d => d.Location.SourceSpan).SequenceEqual(expected.OrderBy(span => span.Start)),
                    "Exact CSS/independent HTML origins: " + name + "/" + form + " " + string.Join(";", diagnostics.Select(d => d.ToString())));
                Check(diagnostics.All(d => d.Severity == DiagnosticSeverity.Error && d.AdditionalLocations.Count == 1
                    && d.Properties["runtimeRule"] == "LSQ001"), "Existing collector contract: " + name);
                proof.Add(new { name, form, expected = expected.Select(span => new { span.Start, span.Length }).ToArray() });
            }
        }
        const string shared = "class C { void M(bool b) { const string bad=\"javascript:shared\"; var prefix=b?\"<div>\":\"<section>\"; var html=prefix+$\"<p style=\\\"background:url('{bad}')\\\">X</p>\"; LithoSharp.Html.UnsafeRaw(html); LithoSharp.Html.UnsafeRaw($\"<p style=\\\"background:url('{bad}')\\\">X</p>\"); } }";
        var common = (await Analyze(Compile(shared, true))).Where(d => d.Id == "LSA1105").ToArray();
        Check(common.Length == 1 && common[0].Location.SourceSpan == new TextSpan(shared.IndexOf("javascript:shared", StringComparison.Ordinal), 17)
            && common[0].AdditionalLocations.Count == 2, "Shared CSS origin intersection, dedup and use union");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "Compiler host never loads runtime");
        File.WriteAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "inline-css-inputs.json"), JsonSerializer.Serialize(
            new { status = "PASS", runtimeCases = observed.Count, cases = proof, sharedOrigin = true }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task InlineCssUnknownPartialAndUnmappableInputsStayDeferred()
    {
        var cases = new (string Name, string Body)[] {
            ("unknown", "LithoSharp.Html.UnsafeRaw(unknown);"),
            ("getter", "LithoSharp.Html.UnsafeRaw(Getter);"),
            ("unknown-hole", "LithoSharp.Html.UnsafeRaw($\"<p style=\\\"a:url('{unknown}')\\\">\");"),
            ("cross-piece", "LithoSharp.Html.UnsafeRaw(\"<p style=\\\"a:url('java\"+\"script:bad')\\\">\");"),
            ("different-origins", "var html=b?\"<p style=\\\"a:url('javascript:x')\\\">\":\"<p style=\\\"a:url('javascript:x')\\\">\"; LithoSharp.Html.UnsafeRaw(html);"),
            ("zero-piece-boundary", "LithoSharp.Html.UnsafeRaw(\"<p style=\\\"a:url('\"+\"')\\\">\");"),
            ("html-partial", "LithoSharp.Html.UnsafeRaw(\"<p style=\\\"a:url('javascript:x')\\\"><template shadowrootmode=open>X</template>\");")
        };
        foreach (var (name, body) in cases)
        {
            var source = "class C { static string Getter=>throw new System.Exception(\"GETTER_SENTINEL\"); void M(bool b,string unknown) { " + body + " } }";
            var compilation = Compile(source, true, name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "CSS Deferred fixture binds: " + name);
            Check(!(await Analyze(compilation)).Any(d => d.Id == "LSA1105"), "CSS Deferred: " + name);
        }
        var longHtml = "<a href='javascript:href'>X</a><img src='javascript:src'><p style=\"/*"
            + new string('x', 60000) + "*/a:url('javascript:css')\"></p>";
        var declaration = "class C { const string Markup=" + Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(longHtml, true) + "; void M(bool b){";
        var control = declaration + "LithoSharp.Html.UnsafeRaw(Markup);}}";
        Check((await Analyze(Compile(control, true))).Count(d => d.Id == "LSA1105") == 3, "One CSS input is provable within budget");
        var expanded = declaration + string.Concat(Enumerable.Range(0, 6).Select(i => "var p" + i + "=b?\"\":\"\";"))
            + "LithoSharp.Html.UnsafeRaw(p0+p1+p2+p3+p4+p5+Markup);}}";
        var preserved = (await Analyze(Compile(expanded, true))).Where(d => d.Id == "LSA1105").ToArray();
        Check(preserved.Length == 2 && preserved.Select(d => expanded.Substring(d.Location.SourceSpan.Start, d.Location.SourceSpan.Length))
            .Order().SequenceEqual(new[] { "javascript:href", "javascript:src" }), "Actual 64-origin CSS work exhaustion preserves independent href/src");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var canceledObserved = false;
        try { await Analyze(Compile("class C { void M(){LithoSharp.Html.UnsafeRaw(\"<p style='a:url()'>\");}}", true), canceled.Token); }
        catch (OperationCanceledException) { canceledObserved = true; }
        Check(canceledObserved, "CSS cancellation propagates");
        File.WriteAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "inline-css-boundaries.json"), JsonSerializer.Serialize(
            new { status = "PASS", cases = cases.Select(row => row.Name), budgetPreservesIndependentHtml = true, cancellation = true }));
    }

}
