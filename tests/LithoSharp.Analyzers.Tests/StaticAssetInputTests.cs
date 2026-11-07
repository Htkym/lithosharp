using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task AssetTransformInputsMatchRuntimeDeclarationIdentity()
    {
        var evidence = new List<object>();
        foreach (var (name, before, inputs, body, count, named) in new[]
        {
            ("both-apis-undeclared", "", "[source]", "ctx.GetUrl(missing); using var reader = ctx.OpenRead(missing);", 2, false),
            ("same-instance-alias", "var alias = source;", "[source]", "ctx.GetUrl(alias); using var reader = ctx.OpenRead(alias);", 0, false),
            ("same-id-distinct-instance", "", "[source]", "ctx.GetUrl(twin); using var reader = ctx.OpenRead(twin);", 2, false),
            ("named-argument-order", "", "new SiteAsset[] { source }", "ctx.GetUrl(missing); using var reader = ctx.OpenRead(missing);", 2, true),
            ("empty-declarations", "", "[]", "ctx.GetUrl(missing); using var reader = ctx.OpenRead(missing);", 2, false),
            ("all-known-absent", "", "[source]", "ctx.GetUrl(flag ? missing : twin);", 1, false),
        })
        {
            var source = AssetInputSource(before, inputs, body, named: named);
            var compilation = AssetInputCompilation(source);
            var diagnostics = await Analyze(compilation);
            var asset = diagnostics.Where(d => d.Id == "LSA1203").ToArray();
            Check(asset.Length == count && diagnostics.Length == count, "Exact asset diagnostics: " + name + " " + string.Join(";", diagnostics));
            var arguments = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(n => n.Expression.ToString() is "ctx.GetUrl" or "ctx.OpenRead").Select(n => n.ArgumentList.Arguments.Single().Expression.Span).ToArray();
            foreach (var d in asset)
            {
                Check(d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree?.FilePath == "canary.cs"
                    && arguments.Contains(d.Location.SourceSpan), "Original UTF16 argument span: " + name);
                Check(d.Properties["scope"] == "asset-transform-inputs" && d.Properties["scopeState"] == "Closed"
                    && d.Properties["candidateCount"] == (name == "all-known-absent" ? "2" : "1"), "Explicit scope and bounded identity proof");
                Check(d.AdditionalLocations.Count == 1, "Original declaration related location");
            }
            evidence.Add(new { name, source, diagnostics = diagnostics.Select(AssetInputDiagnostic).ToArray() });
        }
        var original = AssetInputSource("", "[source]", "ctx.GetUrl(missing);");
        var initial = AssetInputCompilation(original);
        Check((await Analyze(initial)).Count(d => d.Id == "LSA1203") == 1, "Unsaved initial source reports absence");
        var tree = initial.SyntaxTrees.Single();
        var updated = original.Replace("ctx.GetUrl(missing)", "ctx.GetUrl(source)", StringComparison.Ordinal);
        var fresh = initial.ReplaceSyntaxTree(tree, tree.WithChangedText(SourceText.From(updated)));
        Check(!(await Analyze(fresh)).Any(d => d.Id == "LSA1203"), "Unsaved buffer replaces the proof in the same package host");
        evidence.Add(new { name = "unsaved-update", original, updated, originalMissing = 1, updatedMissing = 0 });
        AssetInputEvidence("asset-inputs-known", evidence);
    }

    [Test]
    public async Task AssetTransformInputsDeferUnprovenScopes()
    {
        var evidence = new List<object>();
        foreach (var (name, before, inputs, body, after, outputs) in new[]
        {
            ("known-unknown-mixed", "", "[source]", "ctx.GetUrl(flag ? missing : unknown);", "", "[output]"),
            ("known-present-missing", "", "[source]", "ctx.GetUrl(flag ? source : missing);", "", "[output]"),
            ("opaque-inputs", "", "GetInputs()", "ctx.GetUrl(missing);", "", "[output]"),
            ("constructor-before-alias-mutation", "var entries = new[] { source }; var alias = entries; alias[0] = missing;", "entries", "ctx.GetUrl(missing);", "", "[output]"),
            ("later-argument-array-mutation", "var entries = new[] { source };", "entries", "ctx.GetUrl(missing);", "", "Later(entries, missing)"),
            ("later-argument-capture-mutation", "", "[source]", "ctx.GetUrl(missing);", "", "LaterRef(ref missing, source)"),
            ("after-constructor-reassignment", "", "[source]", "ctx.GetUrl(missing);", "missing = source;", "[output]"),
            ("branch-reassignment", "", "[source]", "ctx.GetUrl(missing);", "if (flag) missing = source;", "[output]"),
            ("other-closure", "", "[source]", "ctx.GetUrl(missing);", "Action change = () => missing = source;", "[output]"),
            ("ref-escape", "", "[source]", "ctx.GetUrl(missing);", "Escape(ref missing);", "[output]"),
            ("out-escape", "", "[source]", "ctx.GetUrl(missing);", "Take(out missing);", "[output]"),
            ("deconstruction", "", "[source]", "ctx.GetUrl(missing);", "(missing, source) = (source, missing);", "[output]"),
            ("loop-regeneration", "", "[source]", "ctx.GetUrl(missing);", "for (int i=0;i<2;i++) missing = new SiteAsset(\"late\",\"D:/site\",\"late.txt\",\"late.txt\");", "[output]"),
            ("handler-reassignment", "", "[source]", "ctx.GetUrl(missing); missing = source;", "", "[output]"),
            ("foreign-context", "", "[source]", "foreign.GetUrl(missing); using var reader = foreign.OpenRead(missing);", "", "[output]"),
            ("context-reassignment", "", "[source]", "ctx.GetUrl(missing); ctx = foreign;", "", "[output]"),
            ("context-escape", "", "[source]", "ctx.GetUrl(missing); Use(ctx);", "", "[output]"),
            ("nested-handler-outer-context", "", "[source]", "var inner = new SiteAssetTransform(\"inner\",\"v1\",[missing],[output],(innerContext,_) => { ctx.GetUrl(missing); return Task.CompletedTask; });", "", "[output]"),
            ("nested-local-function", "", "[source]", "void ReadLater() { ctx.GetUrl(missing); }", "", "[output]"),
            ("syntax-budget", string.Concat(Enumerable.Range(0,1100).Select(i => "int n" + i + "=0;")), "[source]", "ctx.GetUrl(missing);", "", "[output]"),
        })
        {
            var source = AssetInputSource(before, inputs, body, after, outputs);
            var diagnostics = await Analyze(AssetInputCompilation(source));
            Check(!diagnostics.Any(d => d.Id == "LSA1203"), "Unavailable is not missing: " + name + " " + string.Join(";", diagnostics));
            evidence.Add(new { name, source, diagnostics = diagnostics.Select(AssetInputDiagnostic).ToArray() });
        }
        var nested = AssetInputSource("", "[source]", "var inner = new SiteAssetTransform(\"inner\",\"v1\",[new SiteAsset(\"a\",\"D:/site\",\"a\",\"a\")],[output],(innerContext,_) => { innerContext.GetUrl(new SiteAsset(\"b\",\"D:/site\",\"b\",\"b\")); return Task.CompletedTask; });");
        var innerDiagnostics = await Analyze(AssetInputCompilation(nested));
        Check(innerDiagnostics.Count(d => d.Id == "LSA1203") == 1, "Nested handler uses only its own declaration scope");
        evidence.Add(new { name = "nested-own-scope", source = nested, diagnostics = innerDiagnostics.Select(AssetInputDiagnostic).ToArray() });
        var opaque = AssetInputSource("", "[source]", "ctx.GetUrl(missing);")
            .Replace("(ctx,token)=>{ctx.GetUrl(missing);return Task.CompletedTask;}", "Handler", StringComparison.Ordinal)
            .Replace("class C {", "class C { static Task Handler(SiteAssetTransformContext context,CancellationToken token) { context.GetUrl(new SiteAsset(\"opaque\",\"D:/site\",\"x\",\"x\")); return Task.CompletedTask; }", StringComparison.Ordinal);
        Check(!(await Analyze(AssetInputCompilation(opaque))).Any(d => d.Id == "LSA1203"), "Method-group handler is not an inline scope");
        evidence.Add(new { name = "opaque-handler", source = opaque });
        var bodyWithControl = "ctx.GetUrl(missing); LithoSharp.SiteUrl.FromAbsolute(\"file:///bad\");";
        var suppressed = AssetInputSource("", "[source]", "\n#pragma warning disable LSA1203\n" + bodyWithControl + "\n#pragma warning restore LSA1203\n");
        var pragma = await Analyze(AssetInputCompilation(suppressed));
        Check(!pragma.Any(d => d.Id == "LSA1203") && pragma.Count(d => d.Id == "LSA1001") == 1, "Original pragma leaves unrelated analyzer control");
        evidence.Add(new { name = "pragma", source = suppressed, diagnostics = pragma.Select(AssetInputDiagnostic).ToArray() });
        var standard = AssetInputCompilation(AssetInputSource("", "[source]", bodyWithControl));
        foreach (var severity in new[] { ReportDiagnostic.Warn, ReportDiagnostic.Suppress })
        {
            var diagnostics = await Analyze(standard.WithOptions(standard.Options.WithSpecificDiagnosticOptions(
                standard.Options.SpecificDiagnosticOptions.SetItem("LSA1203", severity))));
            Check(diagnostics.Count(d => d.Id == "LSA1203") == (severity == ReportDiagnostic.Warn ? 1 : 0)
                && diagnostics.Count(d => d.Id == "LSA1001") == 1, "Standard severity/suppression");
            if (severity == ReportDiagnostic.Warn)
                Check(diagnostics.Single(d => d.Id == "LSA1203").Severity == DiagnosticSeverity.Warning, "Standard Warning");
            evidence.Add(new { name = severity.ToString(), diagnostics = diagnostics.Select(AssetInputDiagnostic).ToArray() });
        }
        var lookalike = AssetInputSource("", "[source]", "ctx.GetUrl(missing);").Replace("using LithoSharp;", "using Local;", StringComparison.Ordinal) + """
            namespace Local {
              sealed class SiteAsset(string id,string root,string input,string output) { }
              sealed class SiteAssetOutput(string id,string output) { }
              sealed class SiteAssetTransform(string id,string fingerprint,IReadOnlyList<SiteAsset> inputs,IReadOnlyList<SiteAssetOutput> outputs,Func<SiteAssetTransformContext,CancellationToken,Task> handler,Func<CancellationToken,Task>? validateInputs=null) { }
              sealed class SiteAssetTransformContext { public object GetUrl(SiteAsset input)=>throw new Exception(); public System.IO.Stream OpenRead(SiteAsset input)=>throw new Exception(); }
            }
            """;
        Check(!(await Analyze(AssetInputCompilation(lookalike))).Any(d => d.Id == "LSA1203"), "Source lookalikes excluded");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            await Analyze(standard, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Check(cancelled, "Cancellation does not return absence proof");
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No Core/runtime facade/user execution");
        evidence.Add(new { name = "lookalike-and-cancellation", source = lookalike, cancelled, forbiddenRuntimeLoaded = false });
        AssetInputEvidence("asset-inputs-deferred", evidence);
    }

    private static CSharpCompilation AssetInputCompilation(string source)
    {
        var names = new[] { "System.Collections.dll", "System.Threading.dll", "System.Threading.Tasks.dll", "System.IO.dll" };
        var compilation = Compile(source, true).AddReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => names.Contains(Path.GetFileName(p))).Select(p => MetadataReference.CreateFromFile(p)));
        Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Asset fixture must bind: " + string.Join(";", compilation.GetDiagnostics()));
        return compilation;
    }
    private static string AssetInputSource(string before, string inputs, string body, string after = "", string outputs = "[output]", bool named = false) => """
        // 😀 original asset input position
        using System; using System.Threading; using System.Threading.Tasks; using System.Collections.Generic; using LithoSharp;
        class C {
          static IReadOnlyList<SiteAsset> GetInputs()=>throw new Exception();
          static IReadOnlyList<SiteAssetOutput> Later(SiteAsset[] inputs,SiteAsset replacement) { inputs[0]=replacement; return [new SiteAssetOutput("later","later.txt")]; }
          static IReadOnlyList<SiteAssetOutput> LaterRef(ref SiteAsset input,SiteAsset replacement) { input=replacement; return [new SiteAssetOutput("later","later.txt")]; }
          static void Escape(ref SiteAsset input) { } static void Take(out SiteAsset input) { input=null!; } static void Use(SiteAssetTransformContext context) { }
          void M(SiteAsset unknown,bool flag,SiteAssetTransformContext foreign) {
            var source = new SiteAsset("source","D:/site","a.txt","a.txt");
            var missing = new SiteAsset("missing","D:/site","b.txt","b.txt");
            var twin = new SiteAsset("source","D:/site","a.txt","a.txt");
            var output = new SiteAssetOutput("result","result.txt");
        """ + before + "\nvar transform = new SiteAssetTransform(" + (named
            ? "handler:(ctx,token)=>{" + body + "return Task.CompletedTask;}, outputs:" + outputs + ", inputs:" + inputs + ", implementationFingerprint:\"v1\", id:\"t\""
            : "\"t\",\"v1\"," + inputs + "," + outputs + ",(ctx,token)=>{" + body + "return Task.CompletedTask;}") + ");\n" + after + "\n} }";
    private static object AssetInputDiagnostic(Diagnostic d) => new
    {
        d.Id,
        severity = d.Severity.ToString(),
        file = d.Location.SourceTree?.FilePath,
        start = d.Location.SourceSpan.Start,
        length = d.Location.SourceSpan.Length,
        d.Properties
    };
    private static void AssetInputEvidence(string name, object cases) => File.WriteAllText(Path.Combine(Required("RA05_EVIDENCE_DIR"), name + ".json"),
        JsonSerializer.Serialize(new { status = "PASS", cases }, new JsonSerializerOptions { WriteIndented = true }));
}
