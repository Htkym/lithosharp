using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task LiteralHtmlUrlsUseActualPortableBridgeAndSharedRuntimeCorpus()
    {
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "literal-runtime-parity.json")));
        var proof = new List<object>();
        foreach (var row in runtime.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = row.GetProperty("name").GetString()!;
            var html = row.GetProperty("html").GetString()!;
            var source = "class C { void M() { LithoSharp.Html.UnsafeRaw(" + Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(html, true) + "); } }";
            var compilation = Compile(source, true, name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Literal corpus binds: " + name);
            var diagnostics = await Analyze(compilation);
            var expected = row.GetProperty("analyzerError").GetBoolean() ? 1 : 0;
            Check(diagnostics.Length == expected && diagnostics.All(d => d.Id == "LSA1105" && d.Severity == DiagnosticSeverity.Error), name + ": " + string.Join(";", diagnostics));
            foreach (var diagnostic in diagnostics)
                Check(diagnostic.Properties["parserStatus"] == "Complete" && diagnostic.Properties["runtimeRule"] == "LSQ001"
                    && diagnostic.Properties["origin"] == "original-literal-url-utf16-span", "Registry/origin/complete proof");
            proof.Add(new
            {
                name,
                source,
                expected,
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
        File.WriteAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "literal-analyzer-parity.json"), JsonSerializer.Serialize(new { status = "PASS", cases = proof }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task LiteralHtmlUrlSpansMapNormalVerbatimRawUnicodeAndEntitySourceExactly()
    {
        var cases = new (string Literal, string Original)[] {
            ("\"😀<a href='javascript:alert(1)'>X</a>\"", "javascript:alert(1)"),
            ("\"\\u003ca href='\\u006aavascript:alert(1)'>X</a>\"", "\\u006aavascript:alert(1)"),
            ("\"\\x003ca href='\\x006aavascript:alert(1)'>X</a>\"", "\\x006aavascript:alert(1)"),
            ("\"<a href='\\U0000006aavascript:alert(1)'>X</a>\"", "\\U0000006aavascript:alert(1)"),
            ("\"\\U0001f600<a href='javascript:alert(1)'>X</a>\"", "javascript:alert(1)"),
            ("\"<a href='javascript:alert(\\\"x\\\")'>X</a>\"", "javascript:alert(\\\"x\\\")"),
            ("@\"😀<a href=\"\"javascript:alert(1)\"\">X</a>\"", "javascript:alert(1)"),
            ("@\"<a href='javascript:alert(\"\"x\"\")'>X</a>\"", "javascript:alert(\"\"x\"\")"),
            ("\"\"\"😀<a href=\"javascript:alert(1)\">X</a>\"\"\"", "javascript:alert(1)"),
            ("\"\"\"\n    <p>😀</p>\n    <a href=\"javascript:alert(1)\">X</a>\n    \"\"\"", "javascript:alert(1)"),
            ("\"\"\"\r\n\t<p>😀</p>\r\n\t<a href=\"javascript:alert(1)\">X</a>\r\n\t\"\"\"", "javascript:alert(1)"),
            ("\"\"\"\n    <p>😀</p>\n  \n    <a href=\"javascript:alert(1)\">X</a>\n    \"\"\"", "javascript:alert(1)"),
            ("\"\"\"\n    <p>😀</p>\n      \n    <a href=\"javascript:alert(1)\">X</a>\n    \"\"\"", "javascript:alert(1)"),
            ("\"<a href='java&#x73;cript:alert(1)'>X</a>\"", "java&#x73;cript:alert(1)"),
            ("\"<img src='\\t\\n'>\"", "\\t\\n"),
        };
        var proof = new List<object>();
        foreach (var (literal, original) in cases)
        {
            var source = "// 😀 source offset\nclass C { void M() { LithoSharp.Html.UnsafeRaw(" + literal + "); } }";
            var compilation = Compile(source, true, "literal-span.cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Source form binds: " + literal);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == 1 && diagnostics[0].Id == "LSA1105", "Source form diagnostic: " + literal + " " + string.Join(";", diagnostics));
            var expected = new TextSpan(source.IndexOf(original, StringComparison.Ordinal), original.Length);
            Check(diagnostics[0].Location.SourceSpan == expected && diagnostics[0].Location.SourceTree!.FilePath == "literal-span.cs", "Exact original URL span: " + literal);
            proof.Add(new { source, original, start = expected.Start, length = expected.Length, diagnostics[0].Properties });
        }
        foreach (var (literal, marker) in new[] { ("\"<img src=\\\"\\\">\"", "src=\\\""),
            ("@\"<img src=\"\"\"\">\"", "src=\"\""), ("\"\"\"<img src=\"\">\"\"\"", "src=\"") })
        {
            var source = "class C { void M() { LithoSharp.Html.UnsafeRaw(" + literal + "); } }";
            var compilation = Compile(source, true, "empty-literal-span.cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Empty URL source binds");
            var diagnostics = await Analyze(compilation);
            var expected = new TextSpan(source.IndexOf(marker, StringComparison.Ordinal) + marker.Length, 0);
            Check(diagnostics.Length == 1 && diagnostics[0].Id == "LSA1105" && diagnostics[0].Location.SourceSpan == expected, "Original empty URL boundary: " + literal);
            proof.Add(new { source, original = "", start = expected.Start, length = expected.Length, diagnostics[0].Properties });
        }
        File.WriteAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "literal-original-spans.json"), JsonSerializer.Serialize(new { status = "PASS", cases = proof }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task DynamicCompositeUnknownAndIncompleteHtmlNeverBecomeLiteralErrors()
    {
        var expressions = new[] { "unknown", "$\"<a href='{unknown}'>X</a>\"", "$\"{unknown}<a href='javascript:alert(1)'>X</a>\"",
            "\"<a href='\" + unknown + \"'>X</a>\"", "known", "\"<a href='\" + \"javascript:alert(1)'>X</a>\"",
            "$\"<a href='{\"javascript:alert(1)\"}'>X</a>\"", "Getter" };
        foreach (var expression in expressions)
        {
            var source = "class C { static string Getter => throw new System.Exception(\"GETTER_SENTINEL\"); void M(string unknown) { const string known=\"<a href='javascript:alert(1)'>X</a>\"; LithoSharp.Html.UnsafeRaw(" + expression + "); } }";
            var compilation = Compile(source, true);
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Deferred fixture binds");
            Check(!(await Analyze(compilation)).Any(d => d.Id == "LSA1105"), "No literal inference for " + expression);
        }
        var partial = "<a href='javascript:alert(1)'>X</a>" + string.Concat(Enumerable.Repeat("<div>", 130));
        var unsupported = "<a href='javascript:alert(1)'>X</a><template shadowrootmode=open><p>X</p></template>";
        var proof = new List<object>();
        foreach (var html in new[] { partial, unsupported })
        {
            var type = Packed.Value.Assembly.GetType("LithoSharp.Analyzers.HtmlLiteralAnalysis")!;
            var facts = type.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { html, CancellationToken.None })!;
            var status = facts.GetType().GetProperty("Status")!.GetValue(facts)!.ToString();
            Check(status == "Partial", "Partial fixture reaches actual portable bridge");
            var source = "class C { void M() { LithoSharp.Html.UnsafeRaw(" + Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(html, true) + "); } }";
            var compilation = Compile(source, true);
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Partial source binds");
            Check(!(await Analyze(compilation)).Any(d => d.Id == "LSA1105"), "Partial prefix facts are never Error");
            proof.Add(new { html, status, definiteErrors = 0 });
        }
        const string fake = "namespace LithoSharp { static class Html { public static string UnsafeRaw(string x)=>x; } } class C { void M() { LithoSharp.Html.UnsafeRaw(\"<a href='javascript:alert(1)'>X</a>\"); } }";
        Check((await Analyze(Compile(fake, false))).Length == 0, "Lookalike API excluded");
        const string ordinary = "class C { string html=\"<a href='javascript:alert(1)'>X</a>\"; }";
        Check((await Analyze(Compile(ordinary, true))).Length == 0, "Ordinary string not HTML sink");
        File.WriteAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "literal-deferred.json"), JsonSerializer.Serialize(new { status = "PASS", dynamicCompositeCases = expressions.Length, partial = proof }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
