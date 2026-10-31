using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task SharedHtmlTemplatesKeepOriginalTypedHoleContexts()
    {
        var proof = new List<object>();
        var rows = new (string Name, string Body, string Rule, string Hole)[]
        {
            ("local-alias", "var html=$\"<script>{Html.Encode(x)}</script>\"; var alias=html; Html.UnsafeRaw(alias);", "LSA1104", "Html.Encode(x)"),
            ("string-concat", "var part=$\"{Html.Encode(x)}\"; var html=\"<style>\"+part+\"</style>\"; Html.UnsafeRaw(html);", "LSA1104", "Html.Encode(x)"),
            ("encoded-alias", "var encoded=Html.Encode(x); var html=$\"<p onclick='{encoded}'>\"; Html.UnsafeRaw(html);", "LSA1104", "encoded"),
            ("fragment-string", "var html=$\"<p title='{Html.UnsafeRaw(\"<b/>\").ToHtmlString()}'>\"; Html.UnsafeRaw(html);", "LSA1102", "Html.UnsafeRaw(\"<b/>\").ToHtmlString()"),
            ("fragment-alias", "var raw=Html.UnsafeRaw(\"<b/>\"); var rendered=raw.ToHtmlString(); var html=$\"<p title='{rendered}'>\"; Html.UnsafeRaw(html);", "LSA1102", "rendered"),
            ("common-shape-unknown-scalar", "var encoded=Html.Encode(x); var prefix=b?\"<script>\":\"<script type='x'>\"; var part=$\"{encoded}\"; var html=prefix+part+\"</script>\"; Html.UnsafeRaw(html);", "LSA1104", "encoded"),
            ("branch-same-origin", "var html=$\"<p {x}>\"; var alias=b?html:html; Html.UnsafeRaw(alias);", "LSA1103", "x"),
            ("if-captures", "var part=$\"{x}\"; string html; if(b) html=\"<p \"+part+\">\"; else html=\"<div \"+part+\">\"; Html.UnsafeRaw(html);", "LSA1103", "x"),
            ("source-evaluation-order", "var html=$\"<p {x}>\"; Html.UnsafeRaw(html+(html=\"safe\"));", "LSA1103", "x"),
            ("union-sinks", "var html=$\"<p {x}>\"; if(b) Html.UnsafeRaw(html); else Html.UnsafeRaw(b?html:x);", "LSA1103", "x"),
            ("verbatim", "var html=$@\"😀<p {x}>\"; Html.UnsafeRaw(html);", "LSA1103", "x"),
            ("raw-multiline", "var html=$$\"\"\"\n    😀<p {{x}}>\n    \"\"\"; Html.UnsafeRaw(html);", "LSA1103", "x"),
            ("escaped", "var html=$\"\\u003cp {x}>\"; Html.UnsafeRaw(html);", "LSA1103", "x"),
        };
        foreach (var row in rows)
        {
            var source = ContextSource(row.Body);
            var compilation = Compile(source, true, row.Name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Shared context binds: " + row.Name + string.Join(";", compilation.GetDiagnostics()));
            var diagnostics = await Analyze(compilation);
            var definite = diagnostics.Where(d => d.Id is "LSA1102" or "LSA1103" or "LSA1104").ToArray();
            Check(definite.Length == 1 && definite[0].Id == row.Rule, "Common context: " + row.Name + " " + string.Join(";", diagnostics));
            var diagnostic = definite[0];
            var hole = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<InterpolationSyntax>().Single(h => h.Expression.ToString() == row.Hole);
            Check(diagnostic.Location.SourceSpan == hole.Expression.Span && diagnostic.Location.SourceTree == hole.SyntaxTree, "Original hole: " + row.Name);
            Check(diagnostic.AdditionalLocations.Count >= 1 && diagnostic.AdditionalLocations.Count <= 16, "Related sink bound");
            Check(diagnostics.Any(d => d.Id == "LSA1101"), "Shape proof never upgrades Unknown scalar: " + row.Name);
            proof.Add(new
            {
                row.Name,
                source,
                diagnostic.Id,
                diagnostic.Properties,
                primary = diagnostic.Location.SourceSpan,
                related = diagnostic.AdditionalLocations.Select(l => l.SourceSpan).ToArray()
            });
        }
        // The 256-hole contract must survive the independent URL proof's 64-piece cap.
        var many = ContextSource("var html=$\"" + string.Concat(Enumerable.Repeat("<script>{Html.Encode(x)}</script>", 256)) + "\"; Html.UnsafeRaw(html);");
        Check((await Analyze(Compile(many, true))).Count(d => d.Id == "LSA1104") == 256, "Direct 256 encoded holes remain supported");
        const string origin = "var html=$\"<p {x}>\";";
        var branches = string.Concat(Enumerable.Range(0, 20).Select(i => "case " + i + ": Html.UnsafeRaw(html); break;"));
        var bounded = ContextSource(origin + "switch(n){" + branches + "}");
        var ordered = (await Analyze(Compile(bounded, true, "bounded.cs"))).Single(d => d.Id == "LSA1103");
        Check(ordered.AdditionalLocations.Count == 16 && ordered.AdditionalLocations.Select(l => l.SourceSpan.Start).SequenceEqual(ordered.AdditionalLocations.Select(l => l.SourceSpan.Start).Order()), "Deterministic bounded sink union");
        Check(!(await Analyze(Compile("#pragma warning disable LSA1103\n" + bounded, true))).Any(d => d.Id == "LSA1103"), "Primary pragma");
        var warning = Compile(ContextSource(origin + "Html.UnsafeRaw(html);"), true).WithOptions(Compile("", true).Options.WithSpecificDiagnosticOptions(
            new Dictionary<string, ReportDiagnostic> { { "LSA1103", ReportDiagnostic.Warn } }));
        Check((await Analyze(warning)).Single(d => d.Id == "LSA1103").Severity == DiagnosticSeverity.Warning, "Severity override");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No Core/user runtime execution");
        File.WriteAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "shared-template-contexts.json"), JsonSerializer.Serialize(new { status = "PASS", cases = proof, maxHoles = 256, related = 16, primaryPragma = true }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task SharedHtmlTemplateUnknownOriginsAndEscapesStayDeferred()
    {
        var rows = new (string Name, string Body)[]
        {
            ("missing-shape-unknown-scalar","var encoded=Html.Encode(x); var part=$\"<script>{encoded}</script>\"; var html=b?part:x; Html.UnsafeRaw(html);"),
            ("different-hole-origins","var html=b?$\"<p {x}>\":$\"<p {x}>\"; Html.UnsafeRaw(html);"),
            ("one-safe-context","var part=$\"{x}\"; var prefix=b?\"<p \":\"<p>\"; Html.UnsafeRaw(prefix+part);"),
            ("different-contexts","var part=$\"{x}\"; var prefix=b?\"<\":\"<p \"; Html.UnsafeRaw(prefix+part);"),
            ("raw-object-type-name","var html=$\"<p title='{Html.UnsafeRaw(\"<b/>\")}'>\"; Html.UnsafeRaw(html);"),
            ("interface-unknown","var html=$\"<p title='{content}'>\"; Html.UnsafeRaw(html);"),
            ("unknown-prefix","var html=$\"{x}<script>{Html.Encode(x)}</script>\"; Html.UnsafeRaw(html);"),
            ("call-escape","var html=$\"<p {x}>\"; Touch(); Html.UnsafeRaw(html);"),
            ("getter-escape","var html=$\"<p {x}>\"; var a=Getter; Html.UnsafeRaw(html);"),
            ("deconstruction","var html=$\"<p {x}>\"; var other=x; (html,other)=(other,html); Html.UnsafeRaw(html);"),
            ("ref-alias","var html=$\"<p {x}>\"; ref var alias=ref html; alias=x; Html.UnsafeRaw(html);"),
            ("ref-argument","var html=$\"<p {x}>\"; Set(ref html); Html.UnsafeRaw(html);"),
            ("loop","var html=$\"<p {x}>\"; while(b) html=x; Html.UnsafeRaw(html);"),
            ("finally","var html=$\"<p {x}>\"; try{}finally{html=x;} Html.UnsafeRaw(html);"),
            ("capture","var html=$\"<p {x}>\"; void Change(){html=x;} Change(); Html.UnsafeRaw(html);"),
            ("format-tag-direct","Html.UnsafeRaw($\"<{x:tag}>\");"),
            ("alignment-attribute-direct","Html.UnsafeRaw($\"<p {x,10}='v'>\");"),
            ("format-unquoted-shared","var html=$\"<p a={x:value}>\"; Html.UnsafeRaw(html);"),
            ("alignment-then-hole","var html=$\"<p {x,10} {x}>\"; Html.UnsafeRaw(html);"),
            ("hole-budget","var html=$\""+string.Concat(Enumerable.Repeat("<p {x}>",257))+"\"; Html.UnsafeRaw(html);"),
            ("literal-budget","var html=$\"<p {x}>"+new string('a',65536)+"\"; Html.UnsafeRaw(html);"),
            ("alternative-budget","var prefix=(b?\"<p \":\"<div \")+(b?\"\":\"a\")+(b?\"\":\"b\")+(b?\"\":\"c\"); var part=$\"{x}>\"; Html.UnsafeRaw(prefix+part);"),
        };
        var proof = new List<object>();
        foreach (var row in rows)
        {
            var source = ContextSource(row.Body);
            var compilation = Compile(source, true, row.Name + ".cs");
            Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Deferred context binds: " + row.Name + string.Join(";", compilation.GetDiagnostics()));
            var diagnostics = await Analyze(compilation);
            Check(!diagnostics.Any(d => d.Id is "LSA1102" or "LSA1103" or "LSA1104"), "No unavailable shape proof: " + row.Name + string.Join(";", diagnostics));
            if (row.Name == "missing-shape-unknown-scalar")
                Check(diagnostics.Any(d => d.Id == "LSA1101"), "Missing shape remains scalar Unknown");
            proof.Add(new { row.Name, source, ids = diagnostics.Select(d => d.Id).ToArray() });
        }
        var cancelled = false;
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                await Analyze(Compile(ContextSource("Html.UnsafeRaw($\"<p {x}>\");"), true), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
        }
        Check(cancelled, "Cancellation never returns prefix diagnostics");
        File.WriteAllText(Path.Combine(Required("RA04B_EVIDENCE_DIR"), "shared-template-deferred.json"), JsonSerializer.Serialize(new { status = "PASS", cases = proof, cancelled }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ContextSource(string body) => "using LithoSharp; class C { static string Getter=>throw new System.Exception(); static void Touch()=>throw new System.Exception(); static void Set(ref string x)=>throw new System.Exception(); void M(bool b,string x,int n,IHtmlContent content){" + body + "} }";
}
