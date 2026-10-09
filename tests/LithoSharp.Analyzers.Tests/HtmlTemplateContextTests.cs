using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task HtmlContextRegistryPositiveNegativeAndDeferredCasesUsePackedAnalyzer()
    {
        var cases = new (string Name, string Expression, string[] Expected)[]
        {
            ("literal-raw", "\"<b>trusted</b>\"", []),
            ("unknown-raw", "unknown", ["LSA1101"]),
            ("known-local-raw", "known", []),
            ("encoded-text", "$\"<p>{Html.Encode(unknown)}</p>\"", ["LSA1101"]),
            ("encoded-attribute", "$\"<p title=\\\"{Html.Encode(unknown)}\\\">ok</p>\"", ["LSA1101"]),
            ("attribute-wrapper", "$\"<p title=\\\"{new HtmlAttributeValue(unknown)}\\\">ok</p>\"", ["LSA1101"]),
            ("text-wrapper", "$\"<p title=\\\"{new HtmlText(unknown)}\\\">ok</p>\"", ["LSA1101"]),
            ("fragment-attribute", "$\"<p title=\\\"{Html.UnsafeRaw(\"<b/>\")}\\\">ok</p>\"", ["LSA1101", "LSA1102"]),
            ("fragment-string-attribute", "$\"<p title=\\\"{Html.UnsafeRaw(\"<b/>\").ToHtmlString()}\\\">ok</p>\"", ["LSA1101", "LSA1102"]),
            ("interface-unknown", "$\"<p title=\\\"{content}\\\">ok</p>\"", ["LSA1101"]),
            ("tag-name", "$\"<{unknown}>\"", ["LSA1101", "LSA1103"]),
            ("attribute-name", "$\"<p {unknown}=\\\"v\\\">\"", ["LSA1101", "LSA1103"]),
            ("unquoted", "$\"<p title={Html.Encode(unknown)}>\"", ["LSA1101", "LSA1103"]),
            ("unquoted-continuation", "$\"<p title=x{unknown}>\"", ["LSA1101", "LSA1103"]),
            ("constant-name", "$\"<p {\"title\"}=\\\"v\\\">\"", ["LSA1103"]),
            ("script", "$\"<script>{Html.Encode(unknown)}</script>\"", ["LSA1101", "LSA1104"]),
            ("style", "$\"<style>{Html.Encode(unknown)}</style>\"", ["LSA1101", "LSA1104"]),
            ("event", "$\"<p onclick=\\\"{Html.Encode(unknown)}\\\">\"", ["LSA1101", "LSA1104"]),
            ("style-attribute", "$\"<p style=\\\"{new HtmlText(unknown)}\\\">\"", ["LSA1101", "LSA1104"]),
            ("script-fake-tag", "$\"<script>let x='<p title=\\\"'; {Html.Encode(unknown)}</script>\"", ["LSA1101", "LSA1104"]),
            ("script-close", "$\"<script>x</script><p {unknown}>\"", ["LSA1101", "LSA1103"]),
            ("script-close-prefix", "$\"<script>x</scriptx><p title=\\\"{Html.Encode(unknown)}\\\">\"", ["LSA1101", "LSA1104"]),
            ("unknown-before-attribute", "$\"{unknown}<p title=\\\"{Html.UnsafeRaw(\"<b/>\")}\\\">\"", ["LSA1101"]),
            ("unknown-before-script", "$\"{unknown}<script>{Html.Encode(unknown)}</script>\"", ["LSA1101"]),
            ("unknown-attribute-breaks-following", "$\"<p title=\\\"{unknown}\\\" {unknown}>\"", ["LSA1101"]),
            ("safe-attribute-keeps-following", "$\"<p title=\\\"{Html.Encode(unknown)}\\\" {unknown}>\"", ["LSA1101", "LSA1103"]),
            ("comment", "$\"<!--{Html.Encode(unknown)}--><p {unknown}>\"", ["LSA1101"]),
            ("literal-comment-close", "$\"<!--literal--><p {unknown}>\"", ["LSA1101", "LSA1103"]),
            ("foreign-deferred", "$\"<svg><p title=\\\"{Html.UnsafeRaw(\"<b/>\")}\\\">\"", ["LSA1101"]),
            ("declaration-deferred", "$\"<!DOCTYPE html><p {unknown}>\"", ["LSA1101"]),
            ("declaration-hole-deferred", "$\"<!{unknown}><p {unknown}>\"", ["LSA1101"]),
            ("rcdata-deferred", "$\"<textarea><p {unknown}>\"", ["LSA1101"]),
            ("escaped-script-deferred", "$\"<script><!--<script></script><p {unknown}>\"", ["LSA1101"]),
            ("malformed-deferred", "$\"<p == {unknown}>\"", ["LSA1101"]),
            ("format-deferred", "$\"<p title=\\\"{content,10}\\\">\"", ["LSA1101"]),
            ("getter-not-executed", "$\"{Getter}<p {unknown}>\"", ["LSA1101"]),
            ("fake-encode", "$\"<script>{Other.Encode(unknown)}</script>\"", ["LSA1101"]),
            ("encoded-wrapper-method", "$\"<script>{new HtmlText(unknown).ToHtmlString()}</script>\"", ["LSA1101", "LSA1104"]),
            ("encoded-wrapper-tostring", "$\"<p title=\\\"{new HtmlAttributeValue(unknown).ToString()}\\\">ok</p>\"", ["LSA1101"]),
            ("formatted-encoded-deferred", "$\"<script>{new HtmlText(unknown):anything}</script>\"", ["LSA1101"]),
            ("ordinary-string-outside-sink", "\"<p>ok</p>\"", []),
        };
        var proof = new List<object>();
        foreach (var row in cases)
        {
            var source = "using LithoSharp; static class Other { public static string Encode(string x) => x; } class C { static string Getter => throw new System.Exception(\"GETTER_SENTINEL\"); void M(string unknown, IHtmlContent content) { string known = \"<b>known</b>\"; var result = Html.UnsafeRaw(" + row.Expression + "); } }";
            var compilation = Compile(source, true, row.Name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "HTML fixture binds: " + row.Name + " " + string.Join(";", compilation.GetDiagnostics()));
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Select(d => d.Id).Order().SequenceEqual(row.Expected.Order()), row.Name + ": " + string.Join("; ", diagnostics));
            foreach (var diagnostic in diagnostics)
            {
                var expectedSeverity = diagnostic.Id switch { "LSA1101" => DiagnosticSeverity.Info, "LSA1104" => DiagnosticSeverity.Warning, _ => DiagnosticSeverity.Error };
                Check(diagnostic.Severity == expectedSeverity, "Registry severity " + diagnostic.Id);
                Check(diagnostic.Properties["origin"] == "original-expression-utf16-span", "Original span property");
            }
            proof.Add(new
            {
                row.Name,
                source,
                expected = row.Expected,
                diagnostics = diagnostics.Select(d => new
                {
                    d.Id,
                    severity = d.Severity.ToString(),
                    d.Properties,
                    start = d.Location.SourceSpan.Start,
                    length = d.Location.SourceSpan.Length,
                    original = source.Substring(d.Location.SourceSpan.Start, d.Location.SourceSpan.Length)
                }).ToArray()
            });
        }
        File.WriteAllText(Path.Combine(Required("RA04_EVIDENCE_DIR"), "html-context-matrix.json"), JsonSerializer.Serialize(new { status = "PASS", cases = proof }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task HtmlInterpolationSpansComeFromOriginalNormalVerbatimRawAndEscapedExpressions()
    {
        var proof = new List<object>();
        foreach (var (id, prefix, value, suffix) in new[] {
            ("LSA1102", "<p title=\"", "Html.UnsafeRaw(\"<b/>\")", "\">"),
            ("LSA1103", "<p ", "unknown", ">"),
            ("LSA1104", "<script>", "Html.Encode(unknown)", "</script>") })
        {
            string Normal(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string Verbatim(string text) => text.Replace("\"", "\"\"");
            var expressions = new[] {
                "$\"😀" + Normal(prefix) + "{" + value + "}" + Normal(suffix) + "\"",
                "$@\"😀" + Verbatim(prefix) + "{" + value + "}" + Verbatim(suffix) + "\"",
                "$$\"\"\"😀" + prefix + "{{" + value + "}}" + suffix + "\"\"\"",
                "$$\"\"\"\n    😀" + prefix + "{{" + value + "}}" + suffix + "\n    \"\"\"",
                "$\"" + Normal(prefix).Replace("<", "\\u003c") + "{" + value + "}" + Normal(suffix) + "\"",
            };
            foreach (var expression in expressions)
            {
                var source = "using LithoSharp; class C { void M(string unknown) { var x = Html.UnsafeRaw(" + expression + "); } }";
                var compilation = Compile(source, true, "span-fixture.cs");
                Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "String form must bind");
                var hole = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<InterpolationSyntax>().Single();
                var diagnostics = await Analyze(compilation);
                var error = diagnostics.Single(d => d.Id == id);
                var info = diagnostics.Single(d => d.Id == "LSA1101");
                Check(error.Location.SourceSpan == hole.Expression.Span && error.Location.SourceTree!.FilePath == "span-fixture.cs", "Exact original hole expression span");
                Check(info.Location.SourceSpan == hole.Parent!.Span, "Info uses original sink argument span");
                proof.Add(new { id, source, context = new { start = error.Location.SourceSpan.Start, length = error.Location.SourceSpan.Length }, info = new { start = info.Location.SourceSpan.Start, length = info.Location.SourceSpan.Length } });
            }
        }
        File.WriteAllText(Path.Combine(Required("RA04_EVIDENCE_DIR"), "html-original-spans.json"), JsonSerializer.Serialize(new { status = "PASS", cases = proof }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task RawTextClosingTagPrefixesCannotSkipEncodedHoles()
    {
        var proof = new List<object>();
        var passed = true;
        foreach (var (name, prefix, hole, suffix, expected) in new[] {
            ("script-prefix-broken", "<script></scr", "Html.Encode(x)", "ipt><p {name}>", new[] { "LSA1101", "LSA1104" }),
            ("script-prefix-completed", "<script></scr", "Html.Encode(x)", "><p title=\\\"{Html.Encode(name)}\\\">", new[] { "LSA1101", "LSA1104" }),
            ("style-prefix-broken", "<style></sty", "new HtmlText(x)", "le><p {name}>", new[] { "LSA1101", "LSA1104" }),
            ("style-prefix-completed", "<style></sty", "new HtmlAttributeValue(x)", "><p title=\\\"{Html.Encode(name)}\\\">", new[] { "LSA1101", "LSA1104" }),
            ("opening-angle-completed", "<script><", "Html.Encode(x)", "><p title=\\\"{Html.Encode(name)}\\\">", new[] { "LSA1101", "LSA1104" }),
            ("full-name-broken", "<script></script", "Html.Encode(x)", "><p {name}>", new[] { "LSA1101", "LSA1104" }),
            ("no-prefix-keeps-context", "<script>", "Html.Encode(x)", "</script><p {name}>", new[] { "LSA1101", "LSA1103", "LSA1104" }),
            ("constant-hole-completes-exactly", "<script></scr", "\"ipt\"", "><p {name}>", new[] { "LSA1101", "LSA1103" }),
            ("constant-hole-breaks-exactly", "<script></scr", "\"X\"", "ipt><p {Html.Encode(name)}>", new[] { "LSA1101", "LSA1104" }),
        })
        {
            var source = "using LithoSharp; class C { void M(string x, string name) { Html.UnsafeRaw($\"" + prefix + "{" + hole + "}" + suffix + "\"); } }";
            var compilation = Compile(source, true, name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "P1 fixture must bind: " + name);
            var diagnostics = await Analyze(compilation);
            var matches = diagnostics.Select(d => d.Id).Order().SequenceEqual(expected.Order());
            passed &= matches;
            var holes = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<InterpolationSyntax>().ToArray();
            foreach (var diagnostic in diagnostics.Where(d => d.Id is "LSA1103" or "LSA1104"))
                Check(holes.Any(h => h.Expression.Span == diagnostic.Location.SourceSpan), "P1 diagnostic uses original hole expression span");
            proof.Add(new
            {
                name,
                source,
                expected,
                matches,
                diagnostics = diagnostics.Select(d => new
                {
                    d.Id,
                    severity = d.Severity.ToString(),
                    start = d.Location.SourceSpan.Start,
                    length = d.Location.SourceSpan.Length,
                    expression = source.Substring(d.Location.SourceSpan.Start, d.Location.SourceSpan.Length)
                }).ToArray()
            });
        }
        File.WriteAllText(Path.Combine(Required("RA04_EVIDENCE_DIR"), "raw-closing-prefix-regression.json"), JsonSerializer.Serialize(new { status = passed ? "PASS" : "EXPECTED_RED", cases = proof }, new JsonSerializerOptions { WriteIndented = true }));
        Check(passed, "Encoded holes cannot be ignored inside raw-text closing tag prefixes; see individual regression diagnostics");
    }

    [Test]
    public async Task HtmlConcurrentRepeatedCompilationsHaveStableOriginalSpansAndSeverity()
    {
        const string source = "using LithoSharp; class C { void M(string x) { Html.UnsafeRaw($\"<script>{Html.Encode(x)}</script>\"); } }";
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => await Analyze(Compile(source, true))));
        string Signature(System.Collections.Immutable.ImmutableArray<Diagnostic> diagnostics) =>
            string.Join(";", diagnostics.OrderBy(d => d.Id).Select(d => $"{d.Id}:{d.Severity}:{d.Location.SourceSpan.Start}:{d.Location.SourceSpan.Length}"));
        Check(results.All(r => Signature(r) == Signature(results[0])) && results[0].Length == 2, "Parallel HTML rules deterministic without global cache");
        File.WriteAllText(Path.Combine(Required("RA04_EVIDENCE_DIR"), "html-parallel.json"), JsonSerializer.Serialize(new { status = "PASS", compilations = 8, signature = Signature(results[0]) }));
    }

    [Test]
    public async Task HtmlLookalikesOrdinaryStringsBindingErrorsAndBudgetsStayOutsideDefiniteContextErrors()
    {
        const string lookalike = "namespace LithoSharp { static class Html { public static string UnsafeRaw(string x) => x; } } class C { void M(string x) { LithoSharp.Html.UnsafeRaw($\"<p {x}>\"); } }";
        Check((await Analyze(Compile(lookalike, false))).Length == 0, "Same name user API is excluded");
        Check((await Analyze(Compile("class C { void M(string x) { var html = $\"<p {x}>\"; } }", true))).Length == 0, "No arbitrary string inference");
        Check((await Analyze(Compile("class C { void M() { LithoSharp.Html.UnsafeRaw($\"<p {missing}>\"); } }", true))).Length == 0, "Binding error excluded");
        var overLength = "class C { void M(string x) { LithoSharp.Html.UnsafeRaw($\"" + new string('a', 65536) + "<p {x}>\"); } }";
        Check(!(await Analyze(Compile(overLength, true))).Any(d => d.Severity == DiagnosticSeverity.Error), "Template length budget defers context errors");
        var overHoles = "class C { void M() { LithoSharp.Html.UnsafeRaw($\"" + string.Concat(Enumerable.Repeat("{\"\"}", 257)) + "<p {\"title\"}>\"); } }";
        Check(!(await Analyze(Compile(overHoles, true))).Any(d => d.Severity == DiagnosticSeverity.Error), "Hole budget defers context errors");
        var longName = "class C { void M(string x) { LithoSharp.Html.UnsafeRaw($\"<" + new string('a', 129) + " {x}>\"); } }";
        Check(!(await Analyze(Compile(longName, true))).Any(d => d.Severity == DiagnosticSeverity.Error), "Name budget defers context errors");
        var overDecoded = "class C { const string Prefix = \"" + new string('a', 65536) + "\"; void M(string x) { LithoSharp.Html.UnsafeRaw($\"{Prefix}<p {x}>\"); } }";
        Check(!(await Analyze(Compile(overDecoded, true))).Any(d => d.Severity == DiagnosticSeverity.Error), "Total decoded text budget includes constant holes");
        var generated = "// <auto-generated/>\nclass C { void M(string x) { LithoSharp.Html.UnsafeRaw($\"<p {x}>\"); } }";
        Check((await Analyze(Compile(generated, true, "generated.g.cs"))).Length == 0, "Generated policy applies to HTML rules");
    }
}
