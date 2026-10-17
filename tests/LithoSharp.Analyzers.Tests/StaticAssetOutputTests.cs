using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task AssetTransformOutputsMatchRuntimeDeclarationIdentity()
    {
        Check(typeof(CSharpCompilation).Assembly.GetName().Version == new Version(4,14,0,0), "Actual packed host Roslyn4.14");
        var evidence = new List<object>();
        foreach (var (name, before, inputs, outputs, body, expectedInput, expectedOutput, named) in new[]
        {
            ("undeclared-output", "", "[source]", "[output]", "ctx.WriteAsync(missing, default, token);", 0, 1, false),
            ("same-instance-alias", "var alias = output;", "[source]", "[output]", "ctx.WriteAsync(alias, default, token);", 0, 0, false),
            ("same-id-distinct-instance", "", "[source]", "[output]", "ctx.WriteAsync(twin, default, token);", 0, 1, false),
            ("named-ordinals", "", "[source]", "new SiteAssetOutput[] { output }", "ctx.WriteAsync(cancellationToken:token, bytes:default, output:missing);", 0, 1, true),
            ("all-known-absent", "", "[source]", "[output]", "ctx.WriteAsync(flag ? missing : twin, default, token);", 0, 1, false),
            ("membership-only-repeated-declared-write", "", "[source]", "[output]", "ctx.WriteAsync(output, default, token); ctx.WriteAsync(output, default, token);", 0, 0, false),
            ("membership-only-unwritten", "", "[source]", "[output]", "", 0, 0, false),
            ("unknown-inputs-known-outputs", "", "GetInputs()", "[output]", "ctx.GetUrl(missingInput); ctx.WriteAsync(missing, default, token);", 0, 1, false),
            ("known-inputs-unknown-outputs", "", "[source]", "GetOutputs()", "ctx.GetUrl(missingInput); ctx.WriteAsync(missing, default, token);", 1, 0, false),
        })
        {
            var source = AssetOutputSource(before, inputs, outputs, body, named: named);
            var diagnostics = await Analyze(AssetInputCompilation(source));
            CheckOutputScopes(source, diagnostics, expectedInput, expectedOutput, name);
            if (name == "all-known-absent") Check(diagnostics.Single().Properties["candidateCount"] == "2", "Finite output candidates");
            evidence.Add(new { name, source, diagnostics = diagnostics.Select(AssetInputDiagnostic).ToArray() });
        }
        var original = AssetOutputSource("", "[source]", "[output]", "ctx.WriteAsync(missing, default, token);");
        var initial = AssetInputCompilation(original);var beforeUpdate = await Analyze(initial);
        CheckOutputScopes(original, beforeUpdate, 0, 1, "unsaved-before");
        var updated = original.Replace("ctx.WriteAsync(missing, default, token)", "ctx.WriteAsync(output, default, token)", StringComparison.Ordinal);
        var tree=initial.SyntaxTrees.Single();var fresh=initial.ReplaceSyntaxTree(tree,tree.WithChangedText(SourceText.From(updated)));
        var afterUpdate = await Analyze(fresh);CheckOutputScopes(updated, afterUpdate, 0, 0, "unsaved-after");
        evidence.Add(new { name="unsaved-update", original, updated, before=beforeUpdate.Select(AssetInputDiagnostic).ToArray(), after=afterUpdate.Select(AssetInputDiagnostic).ToArray() });
        AssetInputEvidence("asset-outputs-known",evidence);
    }

    [Test]
    public async Task AssetTransformOutputsDeferUnprovenScopes()
    {
        var evidence = new List<object>();
        foreach (var (name,before,inputs,outputs,body,after,validate,expectedInput,expectedOutput) in new[]
        {
            ("known-unknown-mixed","","[source]","[output]","ctx.WriteAsync(flag ? missing : unknown, default, token);","","null",0,0),
            ("present-missing-mixed","","[source]","[output]","ctx.WriteAsync(flag ? output : missing, default, token);","","null",0,0),
            ("constructor-before-array-alias-mutation","var entries=new[]{output}; var alias=entries; alias[0]=missing;","[source]","entries","ctx.WriteAsync(missing, default, token);","","null",0,0),
            ("later-argument-array-mutation","var entries=new[]{output};","[source]","entries","ctx.WriteAsync(missing, default, token);","","Later(entries,missing)",0,0),
            ("later-argument-capture-mutation","","[source]","[output]","ctx.WriteAsync(missing, default, token);","","LaterRef(ref missing,output)",0,0),
            ("after-constructor-capture","","[source]","[output]","ctx.WriteAsync(missing, default, token);","missing=output;","null",0,0),
            ("other-closure","","[source]","[output]","ctx.WriteAsync(missing, default, token);","Action change=()=>missing=output;","null",0,0),
            ("ref-escape","","[source]","[output]","ctx.WriteAsync(missing, default, token);","Escape(ref missing);","null",0,0),
            ("out-escape","","[source]","[output]","ctx.WriteAsync(missing, default, token);","Take(out missing);","null",0,0),
            ("deconstruction","","[source]","[output]","ctx.WriteAsync(missing, default, token);","(missing,output)=(output,missing);","null",0,0),
            ("loop-regeneration","","[source]","[output]","ctx.WriteAsync(missing, default, token);","for(int i=0;i<2;i++) missing=new SiteAssetOutput(\"later\",\"later.txt\");","null",0,0),
            ("handler-reassignment","","[source]","[output]","ctx.WriteAsync(missing, default, token); missing=output;","","null",0,0),
            ("foreign-context","","[source]","[output]","foreign.WriteAsync(missing, default, token);","","null",0,0),
            ("context-reassignment","","[source]","[output]","ctx.WriteAsync(missing, default, token); ctx=foreign;","","null",0,0),
            ("context-escape","","[source]","[output]","ctx.WriteAsync(missing, default, token); Use(ctx);","","null",0,0),
            ("nested-handler-outer-context","","[source]","[output]","var inner=new SiteAssetTransform(\"inner\",\"v1\",[],[missing],(innerContext,innerToken)=>{ctx.WriteAsync(missing,default,innerToken);return Task.CompletedTask;});","","null",0,0),
            ("nested-local-function","","[source]","[output]","void WriteLater(){ctx.WriteAsync(missing,default,token);}","","null",0,0),
            ("unknown-input-capture-retains-output","","[source]","[output]","ctx.GetUrl(missingInput); ctx.WriteAsync(missing,default,token);","source=new SiteAsset(\"late\",\"D:/site\",\"late.txt\",\"late.txt\");","null",0,1),
            ("unknown-output-capture-retains-input","","[source]","[output]","ctx.GetUrl(missingInput); ctx.WriteAsync(missing,default,token);","output=new SiteAssetOutput(\"late\",\"late.txt\");","null",1,0),
            ("shared-output-array-retains-input","var entries=new[]{output};","[source]","entries","ctx.GetUrl(missingInput); ctx.WriteAsync(missing,default,token);","","null",1,0),
            ("shared-input-array-retains-output","var entries=new[]{source};","entries","[output]","ctx.GetUrl(missingInput); ctx.WriteAsync(missing,default,token);","","null",0,1),
            ("syntax-budget",string.Concat(Enumerable.Range(0,1100).Select(i=>"int n"+i+"=0;")),"[source]","[output]","ctx.WriteAsync(missing,default,token);","","null",0,0),
        })
        {
            var source=AssetOutputSource(before,inputs,outputs,body,after,validate);
            var diagnostics=await Analyze(AssetInputCompilation(source));
            CheckOutputScopes(source,diagnostics,expectedInput,expectedOutput,name);
            evidence.Add(new{name,source,diagnostics=diagnostics.Select(AssetInputDiagnostic).ToArray()});
        }
        var nested=AssetOutputSource("","[source]","[output]","var inner=new SiteAssetTransform(\"inner\",\"v1\",[],[new SiteAssetOutput(\"a\",\"a.txt\")],(innerContext,innerToken)=>{innerContext.WriteAsync(new SiteAssetOutput(\"b\",\"b.txt\"),default,innerToken);return Task.CompletedTask;});");
        var own=await Analyze(AssetInputCompilation(nested));CheckOutputScopes(nested,own,0,1,"nested-own-scope");
        evidence.Add(new{name="nested-own-scope",source=nested,diagnostics=own.Select(AssetInputDiagnostic).ToArray()});
        var bodyWithControl="ctx.WriteAsync(missing,default,token); LithoSharp.SiteUrl.FromAbsolute(\"file:///bad\");";
        var suppressed=AssetOutputSource("","[source]","[output]","\n#pragma warning disable LSA1203\n"+bodyWithControl+"\n#pragma warning restore LSA1203\n");
        var pragma=await Analyze(AssetInputCompilation(suppressed));
        Check(!pragma.Any(d=>d.Id=="LSA1203")&&pragma.Count(d=>d.Id=="LSA1001")==1,"Output pragma retains unrelated control");
        evidence.Add(new{name="pragma",source=suppressed,diagnostics=pragma.Select(AssetInputDiagnostic).ToArray()});
        var standard=AssetInputCompilation(AssetOutputSource("","[source]","[output]",bodyWithControl));
        foreach(var severity in new[]{ReportDiagnostic.Warn,ReportDiagnostic.Suppress})
        {
            var diagnostics=await Analyze(standard.WithOptions(standard.Options.WithSpecificDiagnosticOptions(standard.Options.SpecificDiagnosticOptions.SetItem("LSA1203",severity))));
            Check(diagnostics.Count(d=>d.Id=="LSA1203")== (severity==ReportDiagnostic.Warn?1:0)&&diagnostics.Count(d=>d.Id=="LSA1001")==1,"Standard output severity/suppression");
            if(severity==ReportDiagnostic.Warn)Check(diagnostics.Single(d=>d.Id=="LSA1203").Severity==DiagnosticSeverity.Warning,"Output standard Warning");
            evidence.Add(new{name=severity.ToString(),diagnostics=diagnostics.Select(AssetInputDiagnostic).ToArray()});
        }
        var opaque=AssetOutputSource("","[source]","[output]","ctx.WriteAsync(missing,default,token);")
            .Replace("(ctx,token)=>{ctx.WriteAsync(missing,default,token);return Task.CompletedTask;}","Handler",StringComparison.Ordinal)
            .Replace("class C {","class C { static Task Handler(SiteAssetTransformContext context,CancellationToken token){context.WriteAsync(new SiteAssetOutput(\"opaque\",\"x.txt\"),default,token);return Task.CompletedTask;}",StringComparison.Ordinal);
        CheckOutputScopes(opaque,await Analyze(AssetInputCompilation(opaque)),0,0,"method-group-handler");
        evidence.Add(new{name="opaque-handler",source=opaque});
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>Forbidden(a.GetName().Name!)),"No runtime facade or user execution");
        evidence.Add(new{name="non-execution",forbiddenRuntimeLoaded=false});
        AssetInputEvidence("asset-outputs-deferred",evidence);
    }

    private static void CheckOutputScopes(string source,IEnumerable<Diagnostic> diagnostics,int inputs,int outputs,string name)
    {
        var all=diagnostics.ToArray();var rules=all.Where(d=>d.Id=="LSA1203").ToArray();
        Check(all.Length==inputs+outputs&&rules.Count(d=>d.Properties["scope"]=="asset-transform-inputs")==inputs
            &&rules.Count(d=>d.Properties["scope"]=="asset-transform-outputs")==outputs,"Independent exact scope counts: "+name+" "+string.Join(";",all.Select(d=>d.ToString())));
        var syntax=CSharpSyntaxTree.ParseText(source).GetRoot();
        foreach(var d in rules)
        {
            var scope=d.Properties["scope"]=="asset-transform-outputs"?"outputs":"inputs";
            var calls=syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(n=>n.Expression is MemberAccessExpressionSyntax access
                &&(scope=="outputs"?access.Name.Identifier.ValueText=="WriteAsync":access.Name.Identifier.ValueText is "GetUrl" or "OpenRead"));
            var spans=calls.Select(n=>(n.ArgumentList.Arguments.SingleOrDefault(a=>a.NameColon?.Name.Identifier.ValueText==(scope=="outputs"?"output":"input"))??n.ArgumentList.Arguments[0]).Expression.Span);
            Check(d.Severity==DiagnosticSeverity.Error&&d.Location.SourceTree?.FilePath=="canary.cs"&&spans.Contains(d.Location.SourceSpan),"Original output/input UTF16 span: "+name);
            Check(d.Properties["scopeState"]=="Closed"&&d.Properties["contract"]=="explicit-transform-"+scope+"/1"&&d.AdditionalLocations.Count==1,"Scope-specific contract/declaration: "+name);
        }
    }
    private static string AssetOutputSource(string before,string inputs,string outputs,string body,string after="",string validate="null",bool named=false)=>"""
        // 😀 original transform output position
        using System; using System.Threading; using System.Threading.Tasks; using System.Collections.Generic; using LithoSharp;
        class C {
          static IReadOnlyList<SiteAsset> GetInputs()=>throw new Exception();
          static IReadOnlyList<SiteAssetOutput> GetOutputs()=>throw new Exception();
          static Func<CancellationToken,Task>? Later(SiteAssetOutput[] values,SiteAssetOutput replacement){values[0]=replacement;return null;}
          static Func<CancellationToken,Task>? LaterRef(ref SiteAssetOutput value,SiteAssetOutput replacement){value=replacement;return null;}
          static void Escape(ref SiteAssetOutput value){} static void Take(out SiteAssetOutput value){value=null!;} static void Use(SiteAssetTransformContext context){}
          void M(SiteAsset unknownInput,SiteAssetOutput unknown,bool flag,SiteAssetTransformContext foreign){
            var source=new SiteAsset("source","D:/site","a.txt","a.txt");
            var missingInput=new SiteAsset("missing","D:/site","b.txt","b.txt");
            var output=new SiteAssetOutput("out","out.txt");
            var missing=new SiteAssetOutput("missing","missing.txt");
            var twin=new SiteAssetOutput("out","out.txt");
        """+before+"\nvar transform=new SiteAssetTransform("+(named
            ?"handler:(ctx,token)=>{"+body+"return Task.CompletedTask;},outputs:"+outputs+",inputs:"+inputs+",implementationFingerprint:\"v1\",id:\"t\",validateInputs:"+validate
            :"\"t\",\"v1\","+inputs+","+outputs+",(ctx,token)=>{"+body+"return Task.CompletedTask;},"+validate)+");\n"+after+"\n} }";
}
