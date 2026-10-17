using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task FixedHtmlReviewBoundariesStayInsideCoreAndDistinctPlans()
    {
        CSharpCompilation Generate(string source)
        {
            var compilation = Compile(source, true).AddReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => new[] { "System.Collections.dll", "System.Text.Encoding.Extensions.dll", "System.Memory.dll" }.Contains(Path.GetFileName(path)))
                .Select(path => MetadataReference.CreateFromFile(path)));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(ManifestGenerators.Value, [],
                (CSharpParseOptions)compilation.SyntaxTrees.First().Options, new ManifestProvider());
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
            Check(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), "Focus generator: " + string.Join(";", diagnostics));
            Check(!output.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Focus binds: " + string.Join(";", output.GetDiagnostics()));
            return (CSharpCompilation)output;
        }
        const string template = """
            using LithoSharp;
            using LithoSharp.Content;
            [FixedHtmlSite("https://fixed.example/sub/")]
            public static partial class FixedPages {
                [FixedHtmlPage("source.html")] private static IHtmlContent Source() => Html.UnsafeRaw("<a href='/sub/target.html#absent'>link</a>");
                [FixedHtmlPage("target.html")] private static IHtmlContent Target() => Html.UnsafeRaw(TARGET_LITERAL);
            }
            namespace System.Text {
                public sealed class UTF8Encoding {
                    public static int Calls;
                    public UTF8Encoding(bool bom, bool strict) { }
                    public byte[] GetBytes(string value) { Calls++; return Encoding.UTF8.GetBytes(value.Replace("id='present'", "id='absent'")); }
                }
            }
            namespace System {
                public static class Array {
                    public static int Calls;
                    public static Collections.Generic.IReadOnlyList<T> AsReadOnly<T>(T[] values) {
                        Calls++; throw new InvalidOperationException("consumer Array must not run");
                    }
                }
            }
            """;
        static string Source(string html) => template.Replace("TARGET_LITERAL", Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(html, true), StringComparison.Ordinal);
        var shadowSource = Source("<h1 id='present'>fixed</h1>");
        var shadow = Generate(shadowSource);
        var errors = (await ManifestAnalyze(shadow, [])).Where(d => d.Id == "LSA1202").ToArray();
        Check(errors.Length == 1 && errors[0].Properties["scopeState"] == "Closed" && errors[0].Properties["freshness"] == "Fresh", "Lookalike source must retain the actual fixed HTML missing proof");
        var owner = shadow.SyntaxTrees.Single(t => t.FilePath.EndsWith("FixedPages.FixedHtml.g.cs", StringComparison.Ordinal));
        Check(!owner.ToString().Contains("UTF8Encoding", StringComparison.Ordinal) && !owner.ToString().Contains("System.Array", StringComparison.Ordinal), "No consumer encoding/snapshot factory in emitted owner");
        var construction = owner.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>().Single();
        var bound = (Microsoft.CodeAnalysis.Operations.IObjectCreationOperation)shadow.GetSemanticModel(owner).GetOperation(construction)!;
        Check(bound.Constructor!.ContainingAssembly.Identity.Name == "LithoSharp" && !bound.Constructor.Locations.Any(l => l.IsInSource)
            && bound.Constructor.Parameters.Length == 3, "The emitted operation binds the actual Core HTML constructor");
        const string embedded = "<iframe srcdoc=\"&lt;script&gt;parent.document.body.id='absent'&lt;/script&gt;\"></iframe>";
        var iframe = Generate(Source(embedded));
        Check(!(await ManifestAnalyze(iframe, [])).Any(d => d.Id == "LSA1202"), "Executable embedded HTML is Deferred without browser execution");
        var factsType = Packed.Value.Assembly.GetType("LithoSharp.HtmlParsing.HtmlLiteralFacts")!;
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var facts = factsType.GetMethod("Parse", flags | System.Reflection.BindingFlags.Static)!.Invoke(null, [embedded, null, null, CancellationToken.None])!;
        var status = factsType.GetProperty("Status", flags | System.Reflection.BindingFlags.Instance)!.GetValue(facts)!.ToString();
        Check((bool)factsType.GetProperty("AllowsClosedAnchors", flags | System.Reflection.BindingFlags.Instance)!.GetValue(facts)! == false, "iframe/srcdoc private projection cannot certify Closed");
        const string identities = """
            using LithoSharp; using LithoSharp.Content;
            namespace A { [FixedHtmlSite("https://fixed.example/")] public static partial class B_C {
                [FixedHtmlPage("one.html")] private static IHtmlContent Page() => Html.UnsafeRaw("<h1 id='present'>one</h1>"); } }
            namespace A_B { [FixedHtmlSite("https://fixed.example/")] public static partial class C {
                [FixedHtmlPage("two.html")] private static IHtmlContent Page() => Html.UnsafeRaw("<h1 id='present'>two</h1>"); } }
            """;
        var distinct = Generate(identities);
        var hints = distinct.SyntaxTrees.Where(t => t.FilePath.EndsWith(".FixedHtml.g.cs", StringComparison.Ordinal)).Select(t => Path.GetFileName(t.FilePath)).Order().ToArray();
        Check(hints.SequenceEqual(new[] { "A.B_C.FixedHtml.g.cs", "A_B.C.FixedHtml.g.cs" }.Order()), "Exactly the two qualified identities have distinct hints");
        var directory = Path.Combine(Required("RA05_EVIDENCE_DIR"), "fixed-html-review-boundaries");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "shadow-source.cs"), shadowSource);
        File.WriteAllText(Path.Combine(directory, "shadow-owner.cs"), owner.ToString());
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { status = "PASS", shadowDiagnostic = errors.Select(ManifestDiagnostic),
            constructor = bound.Constructor.ToDisplayString(), consumerFactoriesInOwner = false, iframe = new { input = embedded, parseStatus = status, closed = false, diagnostics = 0 }, hints }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task FixedHtmlAnchorsRequireTheFreshExactGeneratedArtifactOwner()
    {
        static string Literal(string value) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, true);
        static string Site(string source, string target, string? targetExpression = null) => $$"""
            using LithoSharp;
            using LithoSharp.Content;
            [FixedHtmlSite("https://fixed.example/sub/")]
            public static partial class FixedPages {
                [FixedHtmlPage("source.html")]
                private static IHtmlContent Source() => Html.UnsafeRaw({{Literal(source)}});
                [FixedHtmlPage("target.html")]
                private static IHtmlContent Target() => Html.UnsafeRaw({{targetExpression ?? Literal(target)}});
                private static string DynamicHtml() => "<h1 id='absent'>dynamic</h1>";
            }
            """;
        CSharpCompilation Generate(string source)
        {
            var names = new[] { "System.Collections.dll", "System.Text.Encoding.Extensions.dll", "System.Memory.dll" };
            var compilation = Compile(source, true).AddReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Where(path => names.Contains(Path.GetFileName(path))).Select(path => MetadataReference.CreateFromFile(path)));
            GeneratorDriver driver = CSharpGeneratorDriver.Create(ManifestGenerators.Value, [],
                (CSharpParseOptions)compilation.SyntaxTrees.First().Options, new ManifestProvider());
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
            Check(!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), "Fixed plan generation: " + string.Join(";", diagnostics));
            Check(!output.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Fixed plan binds actual Core: " + string.Join(";", output.GetDiagnostics()));
            return (CSharpCompilation)output;
        }
        var evidence = new List<object>();
        async Task CheckCase(string name, CSharpCompilation compilation, int expected, ManifestProvider? provider = null)
        {
            var diagnostics = await ManifestAnalyze(compilation, [], provider);
            var anchors = diagnostics.Where(d => d.Id == "LSA1202").ToArray();
            Check(anchors.Length == expected, name + ": " + string.Join(";", diagnostics));
            if (expected != 0)
            {
                var original = compilation.SyntaxTrees.First(t => t.FilePath == "canary.cs").ToString();
                var href = "/sub/target.html#absent";
                Check(anchors[0].Severity == DiagnosticSeverity.Error && anchors[0].Location.SourceSpan.Start == original.IndexOf(href, StringComparison.Ordinal)
                    && anchors[0].Location.SourceSpan.Length == href.Length && anchors[0].AdditionalLocations.Count == 1
                    && anchors[0].Properties["scopeState"] == "Closed" && anchors[0].Properties["freshness"] == "Fresh", "Original UTF16 and target identity: " + name);
            }
            evidence.Add(new { name, expected, diagnostics = anchors.Select(ManifestDiagnostic).ToArray() });
        }
        const string missing = "<!doctype html><html><body>😀<a href='/sub/target.html#absent'>link</a></body></html>";
        const string target = "<!doctype html><html><body><h1 id='present'>target</h1></body></html>";
        await CheckCase("present decoded entity, one percent decode and plus remains plus", Generate(Site(
            "<a href='/sub/target.html#a%26b'>entity</a><a href='/sub/target.html#a%2526b'>once</a><a href='/sub/target.html#a+b'>plus</a>",
            "<h1 id='a&amp;b'>entity</h1><h2 id='a%26b'>once</h2><a name='a+b'>plus</a>")), 0);
        var source = Site(missing, target);
        var fresh = Generate(source);
        await CheckCase("fresh Closed definite missing", fresh, 1);
        await CheckCase("dynamic target remains Deferred", Generate(Site(missing, target, "DynamicHtml()")), 0);
        await CheckCase("incomplete depth-limited target remains Deferred", Generate(Site(missing, string.Concat(Enumerable.Repeat("<div>", 150)) + target)), 0);
        await CheckCase("script target remains Open", Generate(Site(missing, target + "<script>document.body.id='absent'</script>")), 0);
        await CheckCase("base-dependent source remains Deferred", Generate(Site("<base href='/else/'>" + missing, target)), 0);
        var originalTree = fresh.SyntaxTrees.First(t => t.FilePath == "canary.cs");
        await CheckCase("source fingerprint Stale", fresh.ReplaceSyntaxTree(originalTree,
            CSharpSyntaxTree.ParseText(source + "\n// changed compiler input", (CSharpParseOptions)originalTree.Options, originalTree.FilePath)), 0);
        await CheckCase("profile fingerprint Stale", fresh, 0, new ManifestProvider(profile: "changed"));
        var generated = fresh.SyntaxTrees.Single(t => t.FilePath.EndsWith(".FixedHtml.g.cs", StringComparison.Ordinal));
        await CheckCase("changed emitted owner is not trusted", fresh.ReplaceSyntaxTree(generated,
            CSharpSyntaxTree.ParseText(generated.ToString().Replace("@Target().ToHtmlString()", "@Source().ToHtmlString()", StringComparison.Ordinal),
                (CSharpParseOptions)generated.Options, generated.FilePath)), 0);
        await CheckCase("ordinary per-ID suppression", fresh.WithOptions(fresh.Options.WithSpecificDiagnosticOptions(
            fresh.Options.SpecificDiagnosticOptions.SetItem("LSA1202", ReportDiagnostic.Suppress))), 0);
        await CheckCase("original-source pragma", Generate("#pragma warning disable LSA1202\n" + source), 0);
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "Compiler host never loads Core runtime");
        var directory = Path.Combine(Required("RA05_EVIDENCE_DIR"), "fixed-html");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(new { status = "PASS", cases = evidence }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "missing-original.cs"), source);
        File.WriteAllText(Path.Combine(directory, "generated-owner.cs"), generated.ToString());
    }
}
