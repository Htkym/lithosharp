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

    [Test]
    public async Task LiteralSrcsetCandidatesPreserveRuntimeSelectionOffsetsAndOriginalSpans()
    {
        var assembly = Packed.Value.Assembly;
        Check(assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()!.FrameworkName
            == ".NETStandard,Version=v2.0", "Actual packed netstandard2.0 analyzer");
        var scanner = assembly.GetType("LithoSharp.Quality.SrcsetUrlScanner")!;
        var read = scanner.GetMethod("ReadCandidates", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var corpus = JsonDocument.Parse(File.ReadAllText(Required("SRCSET_CORPUS")));
        var scannerProof = new List<object>();
        foreach (var row in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = row.GetProperty("input").GetString()!;
            var expected = row.GetProperty("urls").EnumerateArray().Select(v => v.GetString()!).ToArray();
            var candidates = ((System.Collections.IEnumerable)read.Invoke(null, [input, CancellationToken.None])!).Cast<object>().ToArray();
            var values = candidates.Select(c => (string)c.GetType().GetProperty("Value")!.GetValue(c)!).ToArray();
            Check(values.SequenceEqual(expected), "Packed candidate selection: " + row.GetProperty("name"));
            var from = 0;
            var offsets = new List<object>();
            foreach (var candidate in candidates)
            {
                var value = (string)candidate.GetType().GetProperty("Value")!.GetValue(candidate)!;
                var span = candidate.GetType().GetProperty("Source")!.GetValue(candidate)!;
                var start = (int)span.GetType().GetProperty("Start")!.GetValue(span)!;
                var length = (int)span.GetType().GetProperty("Length")!.GetValue(span)!;
                Check(start == input.IndexOf(value, from, StringComparison.Ordinal) && length == value.Length,
                    "Packed decoded candidate offset");
                from = start + length;
                offsets.Add(new { value, start, length });
            }
            scannerProof.Add(new { name = row.GetProperty("name").GetString(), input, values, offsets });
        }
        var cases = new (string Literal, string[] Originals)[]
        {
            (Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                "<img srcset='safe.png 1x, javascript:bad 2x, https://u:p@example.test/x 3x'>", true),
                ["javascript:bad", "https://u:p@example.test/x"]),
            (Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                "<img srcset='data:image/svg+xml,a,b 1x, javascript:bad 2x'>", true), ["javascript:bad"]),
            (Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                "<img srcset='javascript:discard 1x 2x, javascript:discard2 0w, //example.test/x 2x'>", true), []),
            (Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral("<img srcset=', ,'>", true), []),
            (Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                "<img srcset='safe.png&#32;1x, java&#x73;cript:bad&#32;2x'>", true), ["java&#x73;cript:bad"]),
            ("\"<img srcset='safe.png 1x, \\u006aavascript:bad\\t2x'>\"", ["\\u006aavascript:bad"]),
            ("@\"😀<img srcset=\"\"safe.png 1x, javascript:bad 2x\"\">\"", ["javascript:bad"]),
            ("\"\"\"\n    😀<img srcset=\"safe.png 1x,\n    javascript:bad 2x\">\n    \"\"\"", ["javascript:bad"]),
            ("\"<a href='javascript:href'>X</a><img srcset='javascript:bad 2x'>\"", ["javascript:href", "javascript:bad"])
        };
        var spanProof = new List<object>();
        foreach (var (literal, originals) in cases)
        {
            var source = "// 😀 source prefix\nclass C { void M() { LithoSharp.Html.UnsafeRaw(" + literal + "); } }";
            var compilation = Compile(source, true, "srcset-literal.cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Srcset literal binds: " + literal);
            var diagnostics = (await Analyze(compilation)).OrderBy(d => d.Location.SourceSpan.Start).ToArray();
            Check(diagnostics.Length == originals.Length && diagnostics.All(d => d.Id == "LSA1105" && d.Severity == DiagnosticSeverity.Error),
                "Only accepted candidate independent LSQ001 issues: " + literal + " " + string.Join(";", diagnostics.Select(d => d.ToString())));
            for (var i = 0; i < originals.Length; i++)
            {
                var expected = new TextSpan(source.IndexOf(originals[i], StringComparison.Ordinal), originals[i].Length);
                Check(diagnostics[i].Location.SourceSpan == expected && diagnostics[i].Location.SourceTree!.FilePath == "srcset-literal.cs",
                    "Exact original candidate URL, excluding descriptor and other candidates");
                Check(diagnostics[i].Properties["origin"] == "original-literal-url-utf16-span"
                    && diagnostics[i].Properties["runtimeRule"] == "LSQ001" && diagnostics[i].Properties["parserStatus"] == "Complete",
                    "Existing registry and origin preserved");
            }
            spanProof.Add(new { source, originals, diagnostics = diagnostics.Select(d => new {
                d.Id, severity = d.Severity.ToString(), start = d.Location.SourceSpan.Start, length = d.Location.SourceSpan.Length,
                original = source.Substring(d.Location.SourceSpan.Start, d.Location.SourceSpan.Length), d.Properties }).ToArray() });
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No Core runtime or forbidden host loads");
        File.WriteAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "srcset-packed-selection-spans.json"),
            JsonSerializer.Serialize(new { status = "PASS", scanner = scannerProof, literals = spanProof },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task LiteralSrcsetUnknownPartialAndUnmappableCandidatesStayDeferred()
    {
        var expressions = new[] { "unknown", "$\"<img srcset='{unknown}'>\"", "\"<img srcset='\" + unknown + \"'>\"",
            "known", "\"<img srcset='\" + \"javascript:bad 1x'>\"", "Getter" };
        foreach (var expression in expressions)
        {
            var source = "class C { static string Getter => throw new System.Exception(\"GETTER_SENTINEL\"); "
                + "void M(string unknown) { const string known=\"<img srcset='javascript:bad 1x'>\"; LithoSharp.Html.UnsafeRaw(" + expression + "); } }";
            var compilation = Compile(source, true);
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Srcset Deferred binds");
            Check(!(await Analyze(compilation)).Any(d => d.Id == "LSA1105"), "Dynamic/composite/const/getter remains Deferred");
        }
        var partial = new[] {
            "<img srcset='javascript:bad 1x'>" + string.Concat(Enumerable.Repeat("<div>", 130)),
            "<img srcset='javascript:bad 1x'><template shadowrootmode=open>X</template>",
            "<img srcset='javascript:bad 1x'>" + new string('x', 65536)
        };
        foreach (var html in partial)
        {
            var source = "class C { void M() { LithoSharp.Html.UnsafeRaw(" + Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(html, true) + "); } }";
            Check(!(await Analyze(Compile(source, true))).Any(d => d.Id == "LSA1105"), "Partial/unsupported/budget prefix never Error");
        }
        const string fake = "namespace LithoSharp { static class Html { public static string UnsafeRaw(string x)=>x; } } "
            + "class C { void M() { LithoSharp.Html.UnsafeRaw(\"<img srcset='javascript:bad 1x'>\"); } }";
        Check((await Analyze(Compile(fake, false))).Length == 0, "Lookalike source sink excluded");

        var assembly = Packed.Value.Assembly;
        var spanType = assembly.GetType("LithoSharp.HtmlParsing.HtmlSpan")!;
        var segmentType = assembly.GetType("LithoSharp.HtmlParsing.HtmlTextSegment")!;
        var textType = assembly.GetType("LithoSharp.HtmlParsing.HtmlText")!;
        var mapper = assembly.GetType("LithoSharp.Analyzers.HtmlLiteralAnalysis")!.GetMethod("MapCandidate", BindingFlags.NonPublic | BindingFlags.Static)!;
        object Span(int start, int length) => Activator.CreateInstance(spanType, start, length)!;
        object Text(string value, params (int Start, int Length, int RawStart, int RawLength)[] entries)
        {
            var segments = Array.CreateInstance(segmentType, entries.Length);
            for (var i = 0; i < entries.Length; i++)
                segments.SetValue(Activator.CreateInstance(segmentType, entries[i].Start, entries[i].Length,
                    Span(entries[i].RawStart, entries[i].RawLength)), i);
            return Activator.CreateInstance(textType, value, Span(0, 200), segments)!;
        }
        object? Map(object text, int start, int length, ref int cursor, ref int budget, CancellationToken token = default)
        {
            object[] args = [text, Span(start, length), 200, token, cursor, budget];
            var result = mapper.Invoke(null, args); cursor = (int)args[4]; budget = (int)args[5]; return result;
        }
        var checks = new List<string>();
        foreach (var (name, text, start, length) in new[] {
            ("missing", Text("javascript:x"), 0, 12),
            ("gap", Text("javascript:x", (0, 4, 10, 4), (4, 8, 20, 8)), 0, 12),
            ("unsafe-entity-cut", Text("xy", (0, 2, 10, 6)), 1, 1),
            ("decoded-gap", Text("javascript:x", (0, 4, 10, 4), (5, 7, 14, 7)), 0, 12)
        })
        {
            var cursor = 0; var budget = 32;
            Check(Map(text, start, length, ref cursor, ref budget) is null, "Unprovable candidate Deferred: " + name);
            checks.Add(name);
        }
        var pair = Text("javascript:x javascript:y", (0, 4, 10, 4), (4, 8, 20, 8), (13, 12, 50, 12));
        var pairCursor = 0; var pairBudget = 32;
        Check(Map(pair, 0, 12, ref pairCursor, ref pairBudget) is null, "First candidate raw gap Deferred");
        var second = Map(pair, 13, 12, ref pairCursor, ref pairBudget);
        Check(second is not null && (int)spanType.GetProperty("Start")!.GetValue(second)! == 50
            && (int)spanType.GetProperty("Length")!.GetValue(second)! == 12, "Later independent exact candidate preserved");
        checks.Add("candidate-local-deferred");
        var entity = Text("xy", (0, 2, 10, 6)); var entityCursor = 0; var entityBudget = 32;
        Check(Map(entity, 0, 2, ref entityCursor, ref entityBudget) is not null, "Whole entity segment maps");
        var exhaustedCursor = 0; var exhausted = 0;
        Check(Map(entity, 0, 2, ref exhaustedCursor, ref exhausted) is null, "Mapping budget stays Deferred");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancelledCursor = 0; var cancelledBudget = 32;
        try { Map(entity, 0, 2, ref cancelledCursor, ref cancelledBudget, cancelled.Token); throw new InvalidOperationException("Mapping ignored cancellation"); }
        catch (TargetInvocationException e) when (e.InnerException is OperationCanceledException) { checks.Add("mapping-cancellation"); }
        var scanner = assembly.GetType("LithoSharp.Quality.SrcsetUrlScanner")!.GetMethod("ReadCandidates", BindingFlags.NonPublic | BindingFlags.Static)!;
        var cancelledCandidates = (System.Collections.IEnumerable)scanner.Invoke(null, ["x.png 1x", cancelled.Token])!;
        try { cancelledCandidates.GetEnumerator().MoveNext(); throw new InvalidOperationException("Packed scanner ignored cancellation"); }
        catch (OperationCanceledException) { checks.Add("scanner-cancellation"); }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No forbidden runtime loads");
        File.WriteAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "srcset-deferred.json"),
            JsonSerializer.Serialize(new { status = "PASS", dynamic = expressions.Length, partial = partial.Length,
                lookalike = true, mapping = checks, entireEntity = true, exhaustedBudget = true },
                new JsonSerializerOptions { WriteIndented = true }));
    }
}
