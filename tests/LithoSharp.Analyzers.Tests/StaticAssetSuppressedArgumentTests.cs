using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task AssetSuppressedArgumentsKeepKnownIdentityAndOriginalLocations()
    {
        var evidence = new List<object>();
        var forgiving = AssetSuppressionCalls("missingInput!", "missing!");
        foreach (var (name, before, body, inputCount, outputCount, candidates, named) in new[]
        {
            ("plain", "", AssetSuppressionCalls("missingInput", "missing"), 2, 1, "1", false),
            ("forgiving", "", "/*😀*/" + forgiving, 2, 1, "1", false),
            ("direct-new", "", AssetSuppressionCalls("new SiteAsset(\"extra\",\"D:/site\",\"x.txt\",\"x.txt\")!", "new SiteAssetOutput(\"extra\",\"x.txt\")!"), 2, 1, "1", false),
            ("named-parenthesized", "", "ctx.GetUrl(input:(missingInput!));ctx.OpenRead(input:(missingInput!));ctx.WriteAsync(bytes:default,output:(missing!),cancellationToken:token);", 2, 1, "1", true),
            ("existing-alias", "var aliasInput=missingInput;var aliasOutput=missing;", AssetSuppressionCalls("aliasInput!", "aliasOutput!"), 2, 1, "1", false),
            ("finite-all-absent", "var otherInput=new SiteAsset(\"extra\",\"D:/site\",\"x.txt\",\"x.txt\");", AssetSuppressionCalls("(flag?missingInput:otherInput)!", "(flag?missing:twin)!"), 2, 1, "2", false),
            ("pragma", "", "\n#pragma warning disable LSA1203\n" + forgiving + "\n#pragma warning restore LSA1203\n/*😀*/" + forgiving, 2, 1, "1", false),
            ("plain-and-forgiving-same-local", "", "ctx.GetUrl(missingInput);ctx.OpenRead(missingInput!);ctx.WriteAsync(missing,default,token);ctx.WriteAsync(missing!,default,token);", 2, 2, "1", false),
        })
        {
            // Baseline and suppression rows are separate Compilations; only the explicit
            // mixed-use regression intentionally shares plain/suppressed uses in one owner.
            var source = AssetSuppressionSource(before, "[source]", "[output]", body, named: named);
            var compilation = AssetInputCompilation(source);
            var diagnostics = await Analyze(compilation);
            AssetSuppressionCheck(compilation, diagnostics, inputCount, outputCount, name, candidates);
            if (name == "pragma")
                Check(diagnostics.All(d => d.Location.SourceSpan.Start > source.IndexOf("#pragma warning restore LSA1203", StringComparison.Ordinal)), "Only restored original argument locations survive pragma");
            evidence.Add(new { name, source, diagnostics = diagnostics.Select(AssetSuppressionDiagnostic).ToArray() });
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "No Core, getter or handler execution");
        AssetInputEvidence("asset-suppressed-known", evidence);
    }

    [Test]
    public async Task AssetSuppressedArgumentsPreserveWholeOwnerAndIndependentScopes()
    {
        var evidence = new List<object>();
        var both = "ctx.GetUrl(missingInput!);ctx.WriteAsync(missing!,default,token);";
        foreach (var (name, before, inputs, outputs, body, after, validate, inputCount, outputCount) in new[]
        {
            ("declared-same-instances", "", "[source]", "[output]", "ctx.GetUrl(source!);ctx.WriteAsync(output!,default,token);", "", "null", 0, 0),
            ("present-missing-mixed", "", "[source]", "[output]", "ctx.GetUrl((flag?source:missingInput)!);ctx.WriteAsync((flag?output:missing)!,default,token);", "", "null", 0, 0),
            ("unknown", "", "[source]", "[output]", "ctx.GetUrl(unknownInput!);ctx.WriteAsync(unknown!,default,token);", "", "null", 0, 0),
            ("null-is-not-instance-identity", "", "[source]", "[output]", "ctx.GetUrl(null!);ctx.WriteAsync(null!,default,token);", "", "null", 0, 0),
            ("getter", "", "[source]", "[output]", "ctx.GetUrl(GetterInput!);ctx.WriteAsync(GetterOutput!,default,token);", "", "null", 0, 0),
            ("input-forgiving-use-then-reassignment", "", "[source]", "[output]", both, "missingInput=source;", "null", 0, 1),
            ("output-forgiving-use-then-reassignment", "", "[source]", "[output]", both, "missing=output;", "null", 1, 0),
            ("input-forgiving-use-then-ref-escape", "", "[source]", "[output]", both, "EscapeInput(ref missingInput);", "null", 0, 1),
            ("output-forgiving-use-then-ref-escape", "", "[source]", "[output]", both, "Escape(ref missing);", "null", 1, 0),
            ("out-escape", "", "[source]", "[output]", both, "TakeInput(out missingInput);Take(out missing);", "null", 0, 0),
            ("other-closure", "", "[source]", "[output]", both, "Action observe=()=>{UseInput(missingInput);UseOutput(missing);};", "null", 0, 0),
            ("forgiving-escape-to-unpermitted-arguments", "", "[source]", "[output]", both, "UseInput(missingInput!);UseOutput(missing!);", "null", 0, 0),
            ("suppressed-alias-initializer-still-unsupported", "var aliasInput=missingInput!;var aliasOutput=missing!;", "[source]", "[output]", "ctx.GetUrl(aliasInput!);ctx.WriteAsync(aliasOutput!,default,token);", "", "null", 0, 0),
            ("ctx-forgiving-still-unavailable", "", "[source]", "[output]", "ctx!.GetUrl(missingInput!);ctx.WriteAsync(missing!,default,token);", "", "null", 0, 0),
            ("foreign-input-retains-owned-output", "", "[source]", "[output]", "foreign.GetUrl(missingInput!);ctx.WriteAsync(missing!,default,token);", "", "null", 0, 1),
            ("nested-context-still-unavailable", "", "[source]", "[output]", "void Later(){ctx.GetUrl(missingInput!);}ctx.WriteAsync(missing!,default,token);", "", "null", 0, 0),
            ("shared-input-array-retains-output", "var entries=new[]{source};", "entries", "[output]", both, "", "null", 0, 1),
            ("shared-output-array-retains-input", "var entries=new[]{output};", "[source]", "entries", both, "", "null", 1, 0),
            ("deconstruction", "", "[source]", "[output]", both, "(missingInput,source)=(source,missingInput);(missing,output)=(output,missing);", "null", 0, 0),
            ("later-constructor-ref-mutation-retains-input", "", "[source]", "[output]", both, "", "LaterRef(ref missing,output)", 1, 0),
            ("alias-forgiving-use-then-source-ref-escape", "var aliasInput=missingInput;", "[source]", "[output]", "ctx.GetUrl(aliasInput!);ctx.WriteAsync(missing!,default,token);", "EscapeInput(ref missingInput);", "null", 0, 1),
        })
        {
            var source = AssetSuppressionSource(before, inputs, outputs, body, after, validate);
            var compilation = AssetInputCompilation(source);
            var diagnostics = await Analyze(compilation);
            AssetSuppressionCheck(compilation, diagnostics, inputCount, outputCount, name);
            evidence.Add(new { name, source, diagnostics = diagnostics.Select(AssetSuppressionDiagnostic).ToArray() });
        }
        var controlSource = AssetSuppressionSource("var invalid=new SiteAssetOutput(\"bad\",\"../bad\");", "[source]", "[output]", both);
        var control = AssetInputCompilation(controlSource);
        var controlDiagnostics = await Analyze(control);
        AssetSuppressionCheck(control, controlDiagnostics, 1, 1, "path-owner", paths: 1);
        Check(controlDiagnostics.Single(d => d.Id == "LSA1003").GetMessage().Contains("SiteAssetOutput", StringComparison.Ordinal), "Path guard remains independently owned");
        var enabled = await Analyze(control.WithOptions(control.Options.WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["LSA9001"] = ReportDiagnostic.Error })));
        Check(controlDiagnostics.Select(AssetSuppressionSignature).Order().SequenceEqual(enabled.Select(AssetSuppressionSignature).Order()) && !enabled.Any(d => d.Id == "LSA9001"), "No LSA9001 scope expansion or diagnostic ownership change");
        evidence.Add(new { name = "path-and-policy-owners", source = controlSource, diagnostics = controlDiagnostics.Select(AssetSuppressionDiagnostic).ToArray(), optInDiagnostics = enabled.Select(AssetSuppressionDiagnostic).ToArray() });
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => Forbidden(a.GetName().Name!)), "Core, getters and handlers never execute");
        AssetInputEvidence("asset-suppressed-deferred", evidence);
    }

    private static string AssetSuppressionCalls(string input, string output) => "ctx.GetUrl(" + input + ");ctx.OpenRead(" + input + ");ctx.WriteAsync(" + output + ",default,token);";
    private static string AssetSuppressionSource(string before, string inputs, string outputs, string body, string after = "", string validate = "null", bool named = false) =>
        AssetOutputSource(before, inputs, outputs, body, after, validate, named).Replace("class C {", """
            class C {
              static SiteAsset GetterInput=>throw new Exception("INPUT_SENTINEL");
              static SiteAssetOutput GetterOutput=>throw new Exception("OUTPUT_SENTINEL");
              static void EscapeInput(ref SiteAsset value){}
              static void TakeInput(out SiteAsset value){value=null!;}
              static void UseInput(SiteAsset value){}
              static void UseOutput(SiteAssetOutput value){}
            """, StringComparison.Ordinal);
    private static void AssetSuppressionCheck(CSharpCompilation compilation, IEnumerable<Diagnostic> observations, int inputs, int outputs, string name, string? candidates = null, int paths = 0)
    {
        var diagnostics = observations.ToArray();
        var asset = diagnostics.Where(d => d.Id == "LSA1203").ToArray();
        Check(diagnostics.Length == inputs + outputs + paths && asset.Count(d => d.Properties["scope"] == "asset-transform-inputs") == inputs
            && asset.Count(d => d.Properties["scope"] == "asset-transform-outputs") == outputs && diagnostics.Count(d => d.Id == "LSA1003") == paths, "Exact independent scope counts: " + name + " " + string.Join(";", diagnostics.Select(d => d.ToString())));
        var tree = compilation.SyntaxTrees.Single();
        var text = tree.ToString();
        var syntax = tree.GetRoot();
        var declaration = syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Single(n => n.Type.ToString() == "SiteAssetTransform");
        foreach (var diagnostic in asset)
        {
            var output = diagnostic.Properties["scope"] == "asset-transform-outputs";
            var scope = output ? "outputs" : "inputs";
            var calls = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(n => n.Expression is MemberAccessExpressionSyntax access
                && (output ? access.Name.Identifier.ValueText == "WriteAsync" : access.Name.Identifier.ValueText is "GetUrl" or "OpenRead"));
            var expected = calls.Select(n => (n.ArgumentList.Arguments.SingleOrDefault(a => a.NameColon?.Name.Identifier.ValueText == (output ? "output" : "input")) ?? n.ArgumentList.Arguments[0]).Expression.Span);
            var declared = (declaration.ArgumentList!.Arguments.SingleOrDefault(a => a.NameColon?.Name.Identifier.ValueText == scope) ?? declaration.ArgumentList.Arguments[output ? 3 : 2]).Expression;
            var at = diagnostic.Location.SourceSpan.Start;
            var position = diagnostic.Location.GetLineSpan().StartLinePosition;
            Check(diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Location.SourceTree?.FilePath == "canary.cs" && expected.Contains(diagnostic.Location.SourceSpan)
                && position.Line == text[..at].Count(c => c == '\n') && position.Character == at - text.LastIndexOf('\n', at - 1) - 1, "Whole original argument UTF16 position: " + name);
            Check(diagnostic.Properties["scopeState"] == "Closed" && diagnostic.Properties["staticState"] == "Known" && diagnostic.Properties["contract"] == "explicit-transform-" + scope + "/1"
                && diagnostic.Properties["origin"] == "original-argument-utf16-span" && diagnostic.AdditionalLocations.Count == 1 && diagnostic.AdditionalLocations[0].SourceSpan == declared.Span,
                "Existing declaration/identity contract and related location: " + name);
            if (candidates is not null)
                Check(diagnostic.Properties["candidateCount"] == candidates, "Bounded identity count: " + name);
        }
    }
    private static string AssetSuppressionSignature(Diagnostic d) => d.Id + "|" + d.Severity + "|" + d.Location.SourceSpan + "|" + d.GetMessage();
    private static object AssetSuppressionDiagnostic(Diagnostic d) => new
    {
        d.Id,
        severity = d.Severity.ToString(),
        start = d.Location.SourceSpan.Start,
        length = d.Location.SourceSpan.Length,
        expression = d.Location.SourceTree?.GetText().ToString(d.Location.SourceSpan),
        related = d.AdditionalLocations.Select(l => new { l.SourceSpan.Start, l.SourceSpan.Length }).ToArray(),
        d.Properties
    };
}
