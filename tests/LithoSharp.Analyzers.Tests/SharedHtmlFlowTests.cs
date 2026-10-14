using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task SharedStaticHtmlInputsKeepCommonOriginalUrlSpans()
    {
        var cases = new (string Name, string Body, string Original)[]
        {
            ("const-local", "const string html=\"<a href='javascript:bad'>X</a>\"; LithoSharp.Html.UnsafeRaw(html);", "javascript:bad"),
            ("field-const-chain", "var html=Markup; LithoSharp.Html.UnsafeRaw(html);", "javascript:field"),
            ("local-alias", "var x=\"<img src='javascript:bad'>\"; var html=x; LithoSharp.Html.UnsafeRaw(html);", "javascript:bad"),
            ("local-reassign", "var html=\"<p>Safe</p>\"; html=\"<img src='javascript:bad'>\"; LithoSharp.Html.UnsafeRaw(html);", "javascript:bad"),
            ("branch-common", "var prefix=b ? \"<div>\" : \"<section>\"; var html=prefix+\"<a href='javascript:bad'>X</a>\"; LithoSharp.Html.UnsafeRaw(html);", "javascript:bad"),
            ("equal-value-common", "const string bad=\"<a href='javascript:bad'>X</a>\"; var html=b?bad:bad; LithoSharp.Html.UnsafeRaw(html);", "javascript:bad"),
            ("string-hole", "const string url=\"javascript:bad\"; var html=$\"<a href='{url}'>X</a>\"; LithoSharp.Html.UnsafeRaw(html);", "javascript:bad"),
            ("string-hole-srcset", "const string url=\"javascript:bad\"; var html=$\"<img srcset='{url} 1x'>\"; LithoSharp.Html.UnsafeRaw(html);", "javascript:bad"),
            ("normal-text", "var title=\"T\"; var html=$\"{{x}}<a href='java&#x73;cript:bad'>{title}</a>\"; LithoSharp.Html.UnsafeRaw(html);", "java&#x73;cript:bad"),
            ("verbatim-text", "var title=\"T\"; var html=$@\"<a href=\"\"javascript:bad\"\">{title}</a>\"; LithoSharp.Html.UnsafeRaw(html);", "javascript:bad"),
            ("raw-text", "var title=\"T\"; var html=$\"\"\"<a href=\"javascript:bad\">{title}</a>\"\"\"; LithoSharp.Html.UnsafeRaw(html);", "javascript:bad"),
            ("raw-multiline", "var title=\"T\"; var html=$$\"\"\"\n    <a href=\"javascript:bad\">{{title}}</a>\n    \"\"\"; LithoSharp.Html.UnsafeRaw(html);", "javascript:bad"),
            ("decoded-escape", "var html=\"<img src='\\u006aavascript:bad'>\"; LithoSharp.Html.UnsafeRaw(html);", "\\u006aavascript:bad"),
            ("before-later-write", "var html=\"<img src='javascript:bad'>\"; LithoSharp.Html.UnsafeRaw(html+(html=\"<p>Safe</p>\"));", "javascript:bad"),
            ("repeat-origin-in-value", "const string html=\"<img src='javascript:bad'>\"; LithoSharp.Html.UnsafeRaw(html+html);", "javascript:bad"),
            ("different-sinks-union", "const string html=\"<img src='javascript:bad'>\"; LithoSharp.Html.UnsafeRaw(html); LithoSharp.Html.UnsafeRaw(b?html:unknown);", "javascript:bad")
        };
        var proof = new List<object>();
        foreach (var (name, body, original) in cases)
        {
            var source = "// 😀 prefix\nclass C { const string Url=\"javascript:field\"; const string Markup=\"<a href='\"+Url+\"'>X</a>\"; void M(bool b,string unknown) { " + body + " } }";
            var compilation = Compile(source, true, name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Shared HTML fixture binds: " + name);
            var diagnostics = (await Analyze(compilation)).Where(d => d.Id == "LSA1105").ToArray();
            Check(diagnostics.Length == 1, "One proven common origin: " + name + " " + string.Join(";", diagnostics.Select(d => d.ToString())));
            var diagnostic = diagnostics[0];
            var expected = new TextSpan(source.IndexOf(original, StringComparison.Ordinal), original.Length);
            Check(diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Location.SourceSpan == expected,
                "Exact original URL: " + name);
            Check(diagnostic.AdditionalLocations.Count >= 1 && diagnostic.AdditionalLocations.All(location =>
                source.Substring(location.SourceSpan.Start, location.SourceSpan.Length).Contains("html", StringComparison.Ordinal)
                || name == "field-const-chain"), "Usage locations: " + name);
            proof.Add(new { name, source, start = expected.Start, length = expected.Length,
                related = diagnostic.AdditionalLocations.Select(l => new { l.SourceSpan.Start, l.SourceSpan.Length }).ToArray() });
        }

        const string definitions = "static class Shared { public const string Html=\"<img src='javascript:shared'>\"; }";
        var users = Enumerable.Range(0, 20).Select(i => ("u" + i.ToString("D2") + ".cs",
            "class U" + i + " { void M() { LithoSharp.Html.UnsafeRaw(Shared.Html); } }")).ToArray();
        var first = MultiFile(definitions, users);
        var one = (await Analyze(first)).Single(d => d.Id == "LSA1105");
        Check(one.Location.SourceTree!.FilePath == "defs.cs" && one.AdditionalLocations.Count == 16
            && one.AdditionalLocations.Select(l => l.SourceTree!.FilePath).SequenceEqual(users.Take(16).Select(u => u.Item1)),
            "Cross-file deterministic bounded related locations");
        var reversed = MultiFile(definitions, users.Reverse().ToArray());
        var two = (await Analyze(reversed)).Single(d => d.Id == "LSA1105");
        Check(one.AdditionalLocations.Select(l => (l.SourceTree!.FilePath, l.SourceSpan))
            .SequenceEqual(two.AdditionalLocations.Select(l => (l.SourceTree!.FilePath, l.SourceSpan))), "Order independent of block processing");
        var suppressed = MultiFile("#pragma warning disable LSA1105\n" + definitions, users);
        Check(!(await Analyze(suppressed)).Any(d => d.Id == "LSA1105"), "Primary definition pragma respected");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No Core runtime/user code execution");
        File.WriteAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "shared-html-inputs.json"), JsonSerializer.Serialize(
            new { status = "PASS", cases = proof, crossFileRelated = 16, deterministic = true, primaryPragma = true },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task SharedHtmlProofBoundariesNeverReuseUnavailableOrigins()
    {
        var bodies = new (string Name, string Body)[]
        {
            ("different-origins-same-value", "var html=b?\"<img src='javascript:bad'>\":\"<img src='javascript:bad'>\"; LithoSharp.Html.UnsafeRaw(html);"),
            ("different-invalid-origins", "var html=b?\"<img src='javascript:a'>\":\"<img src='javascript:z'>\"; LithoSharp.Html.UnsafeRaw(html);"),
            ("mixed-valid", "const string bad=\"<img src='javascript:bad'>\"; var html=b?bad:\"<p>Safe</p>\"; LithoSharp.Html.UnsafeRaw(html);"),
            ("unknown-branch", "const string bad=\"<img src='javascript:bad'>\"; var html=b?bad:unknown; LithoSharp.Html.UnsafeRaw(html);"),
            ("getter-escape", "var html=\"<img src='javascript:bad'>\"; var x=Getter; LithoSharp.Html.UnsafeRaw(html);"),
            ("call-escape", "var html=\"<img src='javascript:bad'>\"; Touch(); LithoSharp.Html.UnsafeRaw(html);"),
            ("ref-alias", "var html=\"<img src='javascript:bad'>\"; ref var alias=ref html; alias=\"<p>Safe</p>\"; LithoSharp.Html.UnsafeRaw(html);"),
            ("deconstruction", "var html=\"<img src='javascript:bad'>\"; var other=\"<p>Safe</p>\"; (html,other)=(other,html); LithoSharp.Html.UnsafeRaw(html);"),
            ("deconstruction-capture", "var html=\"<img src='javascript:bad'>\"; var other=\"<p>Safe</p>\"; (html,other)=(\"<p>Safe</p>\",other); var alias=html; LithoSharp.Html.UnsafeRaw(alias);"),
            ("ref-argument", "var html=\"<img src='javascript:bad'>\"; Set(ref html); LithoSharp.Html.UnsafeRaw(html);"),
            ("loop", "var html=\"<img src='javascript:bad'>\"; while(b) { html=unknown; } LithoSharp.Html.UnsafeRaw(html);"),
            ("exception", "var html=\"<img src='javascript:bad'>\"; try {} finally {html=unknown;} LithoSharp.Html.UnsafeRaw(html);"),
            ("captured-write", "var html=\"<img src='javascript:bad'>\"; void Change(){html=unknown;} Change(); LithoSharp.Html.UnsafeRaw(html);"),
            ("readonly-field", "LithoSharp.Html.UnsafeRaw(Field);"),
            ("numeric-hole", "var html=$\"<img src='javascript:bad'>{n}\"; LithoSharp.Html.UnsafeRaw(html);"),
            ("format-hole", "var x=\"X\"; var html=$\"<img src='javascript:bad'>{x,3}\"; LithoSharp.Html.UnsafeRaw(html);"),
            ("cross-origin-url", "var x=\"java\"; var html=\"<img src='\"+x+\"script:bad'>\"; LithoSharp.Html.UnsafeRaw(html);"),
            ("partial", "var html=\"<img src='javascript:bad'><template shadowrootmode=open>X</template>\"; LithoSharp.Html.UnsafeRaw(html);")
        };
        var proof = new List<object>();
        foreach (var (name, body) in bodies)
        {
            var source = "class C { static readonly string Field=\"<img src='javascript:bad'>\"; static string Getter=>throw new System.Exception(\"GETTER_SENTINEL\"); "
                + "static void Touch()=>throw new System.Exception(\"CALL_SENTINEL\"); static void Set(ref string x)=>throw new System.Exception(\"REF_SENTINEL\"); "
                + "void M(bool b,int n,string unknown) { " + body + " } }";
            var compilation = Compile(source, true, name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Boundary binds: " + name);
            Check(!(await Analyze(compilation)).Any(d => d.Id == "LSA1105"), "Missing proof cannot become Error: " + name);
            proof.Add(new { name, definiteUrlErrors = 0 });
        }
        // Scalar values still deduplicate to one string, but all 65 different source origins must survive or become Unavailable.
        var alternatives = Enumerable.Range(0, 65).Select(i => i < 64 ? "n==" + i + "?\"<img src='javascript:bad'>\":" : "\"<img src='javascript:bad'>\"");
        var manySource = "class C { void M(int n) { var html=" + string.Join("", alternatives) + "; LithoSharp.Html.UnsafeRaw(html); } }";
        var manyCompilation = Compile(manySource, true);
        Check(!manyCompilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Alternative budget fixture binds");
        Check(!(await Analyze(manyCompilation)).Any(d => d.Id == "LSA1105"), "Never truncate origins to first proof");

        // One same-value alternative has metadata-only origin. It must not disappear after Distinct.
        var external = Compile("public static class External { public const string Html=\"<img src='javascript:bad'>\"; }", false).WithAssemblyName("OriginlessConstants");
        using var bytes = new MemoryStream();
        Check(external.Emit(bytes).Success, "Emit metadata-only constant without loading it");
        var missing = Compile("class C { void M(bool b) { const string local=\"<img src='javascript:bad'>\"; var html=b?local:External.Html; LithoSharp.Html.UnsafeRaw(html); } }", true)
            .AddReferences(MetadataReference.CreateFromImage(bytes.ToArray()));
        Check(!missing.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Metadata origin alternative binds");
        Check(!(await Analyze(missing)).Any(d => d.Id == "LSA1105"), "Metadata proof missing survives equal-value join");

        // A proven sink stays reported even if another use of that same source has no common violation.
        const string independent = "class C { void M(bool b) { const string html=\"<img src='javascript:bad'>\"; LithoSharp.Html.UnsafeRaw(html); LithoSharp.Html.UnsafeRaw(b?html:\"<p>Safe</p>\"); } }";
        Check((await Analyze(Compile(independent, true))).Count(d => d.Id == "LSA1105") == 1, "No cross-sink intersection");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await Analyze(Compile(independent, true), cancellation.Token); throw new InvalidOperationException("Cancelled shared HTML returned a result"); }
        catch (OperationCanceledException) { }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No runtime/user sentinel execution");
        File.WriteAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "shared-html-boundaries.json"), JsonSerializer.Serialize(
            new { status = "PASS", cases = proof, alternativeBudget = 65, metadataOriginMissing = true, perSinkUnion = true, cancellation = true },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static CSharpCompilation MultiFile(string definitions, (string File, string Source)[] users)
    {
        var initial = Compile(definitions, true, "defs.cs");
        return initial.AddSyntaxTrees(users.Select(u => CSharpSyntaxTree.ParseText(u.Source,
            new CSharpParseOptions(LanguageVersion.CSharp13), u.File)));
    }
}
