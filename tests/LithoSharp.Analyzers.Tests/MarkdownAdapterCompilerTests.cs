using System.Collections;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task MarkdownAdapterFrontMatterKeepsOriginalYamlOffsetsAndGeneratedOutput()
    {
        var source = ManifestModel + "\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/missing/\"); } }";
        var evidence = new List<object>();
        using var baseline = new PreviousMarkdownGenerator();
        foreach (var (name, content, invalid) in new[]
        {
            ("lf", "---\ntitle: Hello\ncount: 3\n---\n# Heading\n", false),
            ("bom-crlf", "\uFEFF---\r\ntitle: 😀\r\ncount: 3\r\n---\r\n# Heading\r\n", false),
            ("unsupported-body", "---\ntitle: Hello\n---\n<Component a={x} />\n<script>let x=1;</script>\n", false),
            ("schema-utf16-lf", "---\ntitle: 😀\nunknown: true\ncount: wrong\n---\nBody", true),
            ("schema-utf16-bom-crlf", "\uFEFF---\r\ntitle: 😀\r\nunknown: true\r\ncount: wrong\r\n---\r\nBody", true),
        })
        {
            ManifestInput[] inputs = [new(Content: content)];
            var current = ManifestGenerate(source, inputs);
            var previous = baseline.Generate(source, inputs);
            Check(GeneratedMarkdownSource(current.Compilation) == GeneratedMarkdownSource(previous.Compilation), "Generated output and embedded fingerprint unchanged: " + name);
            Check(current.Diagnostics.Select(MarkdownDiagnosticSignature).Order().SequenceEqual(previous.Diagnostics.Select(MarkdownDiagnosticSignature).Order()), "Existing YAML IDs/severity/spans/messages unchanged: " + name);
            var diagnostics = await ManifestAnalyze(current.Compilation, inputs);
            if (invalid)
            {
                var unknown = current.Diagnostics.Single(d=>d.Id=="LSG005");
                var conversion = current.Diagnostics.Single(d=>d.Id=="LSG006");
                Check(unknown.Location.SourceSpan == new TextSpan(content.IndexOf("unknown",StringComparison.Ordinal),7), "Unknown key full original UTF16 span: "+name);
                Check(conversion.Location.SourceSpan == new TextSpan(content.IndexOf("wrong",StringComparison.Ordinal),5), "Conversion full original UTF16 span: "+name);
                Check(!diagnostics.Any(d=>d.Id=="LSA1201"), "Schema error keeps lookup Open");
            }
            else
            {
                Check(current.Diagnostics.Length==0,"Valid frontmatter despite body Unsupported: "+name);
                var missing=diagnostics.Single(d=>d.Id=="LSA1201");
                Check(missing.Properties["currentFingerprint"]==missing.Properties["expectedFingerprint"],"Original input fingerprint fresh: "+name);
            }
            var scan = ParseCompilerMarkdown(content);
            var yaml = MarkdownProperty(scan,"YamlSpan")!;
            var start = (int)MarkdownProperty(yaml,"Start")!;var length=(int)MarkdownProperty(yaml,"Length")!;
            Check(content.Substring(start,length).StartsWith("title:",StringComparison.Ordinal) && !content.Substring(start,length).Contains("---",StringComparison.Ordinal),"YAML span excludes delimiter lines");
            Check((string)MarkdownProperty(scan,"ContractVersion")! == "frontmatter-scan/1", "Header-only scan contract");
            Check(MarkdownProperty(scan,"State")!.ToString()=="Closed" && MarkdownProperty(scan,"Status")!.ToString()=="Complete", "Known delimiters do not certify YAML/schema: "+name);
            Check((string)MarkdownProperty(scan,"ParserVersion")! == "1/6315e352f3539311c306929ba34ac3d77990e352c95cf7bfc05600c7dea50d7b", "Pinned parser identity");
            var options = CompilerMarkdownOptions();
            Check((string)MarkdownProperty(scan,"OptionsHash")! == (string)MarkdownProperty(options,"Hash")! && (string)MarkdownProperty(scan,"ProfileId")! == "lithosharp-markdown/1","Actual default MdOptions/profile hash");
            foreach(var property in new[]{"RawText","FrontMatter","RequestedParts","Coverage","Headings","Sections","Links","Fences","TextRegions","Root","UnsupportedSyntax","SourceId","ScopeId","SourceVersion"})
                Check(scan.GetType().GetProperty(property,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance) is null,"Header result carries no full-document projection: "+property);
            Check(scan.GetType().GetProperties(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Where(property=>property.Name.EndsWith("Hash",StringComparison.Ordinal)).All(property=>property.Name=="OptionsHash"),"Header result carries no document/body hash");
            Check(CompilerScanDiagnosticIds(scan)==string.Empty,"Body syntax does not produce header diagnostics: "+name);
            evidence.Add(new{name,content,yamlStart=start,yamlLength=length,generatedSha=Sha(System.Text.Encoding.UTF8.GetBytes(GeneratedMarkdownSource(current.Compilation))),generator=current.Diagnostics.Select(ManifestDiagnostic).ToArray(),analyzer=diagnostics.Select(ManifestDiagnostic).ToArray()});
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>Forbidden(a.GetName().Name!)),"Compiler host never loads Core or runtime Markdown facade");
        ManifestEvidence("adapter-frontmatter-output",evidence);
    }

    [Test]
    public async Task MarkdownAdapterUnavailableFrontMatterCannotClaimClosedManifest()
    {
        var source=ManifestModel+"\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/missing/\"); } }";
        var evidence=new List<object>();
        using var baseline = new PreviousMarkdownGenerator();
        foreach(var (name,content,id,compare) in new[]
        {
            ("missing","# No frontmatter","LSG005",true),
            ("unterminated-bom-crlf","\uFEFF---\r\ntitle: 😀\r\n","LSG005",true),
            ("header-only","---","LSG005",true),
            ("empty","---\n---\nBody","LSG005",true),
            ("invalid-yaml","---\ntitle: [\n---\nBody","LSG005",true),
            ("header-input-budget","---\ntitle: "+new string('a',1048576)+"\n---\nBody","LSG002",false),
            ("header-invalid-unicode","---\ntitle: \uD800\n---\nBody","LSG002",false),
        })
        {
            ManifestInput[] inputs=[new(Content:content)];
            var current=ManifestGenerate(source,inputs);
            Check(current.Diagnostics.Length>0 && current.Diagnostics.Any(d=>d.Id==id),"Unavailable/invalid frontmatter reports its owner: "+name+" "+string.Join(";",current.Diagnostics));
            Check(!GeneratedMarkdownSource(current.Compilation).Contains("StaticSiteManifest.Create",StringComparison.Ordinal),"No silent Closed after unavailable frontmatter: "+name);
            var diagnostics=await ManifestAnalyze(current.Compilation,inputs);
            Check(!diagnostics.Any(d=>d.Id=="LSA1201"),"Unavailable source cannot prove missing route: "+name);
            var scan=ParseCompilerMarkdown(content);
            if(name is "header-input-budget" or "header-invalid-unicode")
                CheckUnknownCompilerScan(scan,"Failed",name=="header-input-budget"?"LMD002":"LMD001",name);
            else if(name is "unterminated-bom-crlf" or "header-only")
            {
                Check(MarkdownProperty(scan,"State")!.ToString()=="Unterminated" && MarkdownProperty(scan,"Status")!.ToString()=="Partial" && CompilerScanDiagnosticIds(scan)=="LMD004","Confirmed EOF untermination: "+name);
                var failure=MarkdownProperty(scan,"FailureSpan")!;
                Check((int)MarkdownProperty(failure,"Start")! == content.Length && (int)MarkdownProperty(failure,"Length")! == 0 && (int)MarkdownProperty(scan,"FailureLine")! == (name=="header-only"?2:3),"Confirmed EOF retains original failure position: "+name);
            }
            else if(name is "empty" or "invalid-yaml")
                Check(MarkdownProperty(scan,"State")!.ToString()=="Closed" && MarkdownProperty(scan,"Status")!.ToString()=="Complete" && CompilerScanDiagnosticIds(scan)==string.Empty,"Closed delimiters still require strict YAML validation: "+name);
            if(name=="invalid-yaml")
                Check(current.Diagnostics.Single(d=>d.Id=="LSG005").Location.SourceSpan == new TextSpan(content.IndexOf("title",StringComparison.Ordinal),1),"Malformed YAML reports original YAML range");
            if(compare)
            {
                var previous=baseline.Generate(source,inputs);
                Check(current.Diagnostics.Select(MarkdownDiagnosticSignature).Order().SequenceEqual(previous.Diagnostics.Select(MarkdownDiagnosticSignature).Order()),"Known frontmatter errors retain diagnostics: "+name);
                Check(GeneratedMarkdownSource(current.Compilation)==GeneratedMarkdownSource(previous.Compilation),"Known frontmatter error generated output unchanged: "+name);
            }
            evidence.Add(new{name,utf16Length=content.Length,generator=current.Diagnostics.Select(ManifestDiagnostic).ToArray(),analyzer=diagnostics.Select(ManifestDiagnostic).ToArray()});
        }
        // Exhaust work while reading frontmatter, rather than treating a published prefix as Complete.
        const string limitedRaw="---\ntitle: Hello\n---\n";
        var limited=ParseCompilerMarkdown(limitedRaw,CompilerMarkdownOptions(scan:4));
        CheckUnknownCompilerScan(limited,"Partial","LMD003","scan-budget");
        var validator=CompilerMarkdownAssembly().GetType("LithoSharp.Generators.StaticYamlValidation")!;
        var collected=new List<Diagnostic>();
        var known=(bool)validator.GetMethod("Validate",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{"input.md",limitedRaw,limited,Compile(ManifestModel,true).GetTypeByMetadataName("Front")!,new Action<Diagnostic>(collected.Add),CancellationToken.None})!;
        Check(!known && collected.Count==0,"Unavailable state returns to caller without schema success");
        var adapter=validator.Assembly.GetType("LithoSharp.Generators.MarkdownSourceHostAdapter")!;
        foreach(var (name,parser,yaml) in new[]{("parser","1/"+new string('0',64),"YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e"),("yaml","1/6315e352f3539311c306929ba34ac3d77990e352c95cf7bfc05600c7dea50d7b","YamlDotNet, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null")})
        {
            var rejected=false;
            try{adapter.GetMethod("Verify",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{parser,"1.0","lithosharp-markdown/1","18.1.0",yaml,"18.1.0"});}
            catch(TargetInvocationException failure) when(failure.InnerException is InvalidOperationException){rejected=true;}
            Check(rejected,"Mismatched fixed identity rejected: "+name);
        }
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();var cancelled=false;
        try{ParseCompilerMarkdown("---\ntitle: Hello\n---\n",token:cancellation.Token);}
        catch(TargetInvocationException failure) when(failure.InnerException is OperationCanceledException){cancelled=true;}
        Check(cancelled,"Compiler adapter propagates cancellation");
        ManifestEvidence("adapter-frontmatter-boundaries",new{cases=evidence,scanUnavailable=true,identityRejected=true,cancelled});
    }

    [Test]
    public async Task MarkdownAdapterClosedHeaderIgnoresOversizedAndInvalidUnicodeBodies()
    {
        const string header="---\ntitle: Hello\n---\n";
        var source=ManifestModel+"\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/missing/\"); } }";
        var evidence=new List<object>();
        foreach(var (name,body,closed) in new[]{("oversized-body",new string('a',1048576),true),("invalid-unicode-body","\uD800",false)})
        {
            var content=header+body;
            foreach(var options in new object?[]{null,CompilerMarkdownOptions(scan:256)})
            {
                var scan=ParseCompilerMarkdown(content,options);
                Check(MarkdownProperty(scan,"State")!.ToString()=="Closed" && MarkdownProperty(scan,"Status")!.ToString()=="Complete" && CompilerScanDiagnosticIds(scan)==string.Empty,"Body does not consume header input/scan/Unicode budget: "+name);
                var range=MarkdownProperty(scan,"BodySpan")!;
                Check((int)MarkdownProperty(range,"Start")! == header.Length && (int)MarkdownProperty(range,"Length")! == body.Length,"Body remains an original raw range: "+name);
            }
            ManifestInput[] inputs=[new(Content:content)];
            var current=ManifestGenerate(source,inputs);
            Check(current.Diagnostics.Length==0,"Known valid header reports no YAML/schema or LSG002 diagnostics: "+name);
            Check(GeneratedMarkdownSource(current.Compilation).Contains("StaticSiteManifest.Create",StringComparison.Ordinal)==closed,"Manifest preserves strict whole-input fingerprint ownership: "+name);
            var diagnostics=await ManifestAnalyze(current.Compilation,inputs);
            Check(diagnostics.Any(d=>d.Id=="LSA1201")==closed,"Only a valid whole-input fingerprint certifies the route lookup: "+name);
            evidence.Add(new{name,bodyLength=body.Length,closed,generator=current.Diagnostics.Select(ManifestDiagnostic).ToArray(),analyzer=diagnostics.Select(ManifestDiagnostic).ToArray()});
        }
        ManifestEvidence("adapter-frontmatter-body-isolation",evidence);
    }

    private static object? MarkdownProperty(object value,string name)=>value.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(value);
    private static string CompilerScanDiagnosticIds(object scan)=>string.Join(",",((IEnumerable)MarkdownProperty(scan,"Diagnostics")!).Cast<object>().Select(diagnostic=>(string)MarkdownProperty(diagnostic,"Id")!));
    private static void CheckUnknownCompilerScan(object scan,string status,string diagnostic,string name)
    {
        Check(MarkdownProperty(scan,"State")!.ToString()=="Unknown" && MarkdownProperty(scan,"Status")!.ToString()==status && CompilerScanDiagnosticIds(scan)==diagnostic,"Unavailable header scan retains its cause: "+name);
        foreach(var property in new[]{"OpeningSpan","YamlSpan","ClosingSpan","BodySpan","HeaderSpan","FailureSpan","FailureLine"})
            Check(MarkdownProperty(scan,property) is null,"Unavailable scan does not claim a delimiter or EOF range: "+name+" "+property);
    }
    private static Assembly CompilerMarkdownAssembly()
    {
        _=ManifestGenerators.Value;
        return AssemblyLoadContext.Default.Assemblies.Single(a=>a.GetName().Name=="LithoSharp.Generators");
    }
    private static object CompilerMarkdownOptions(int scan=16777216)=>CompilerMarkdownAssembly().GetType("Syntamark.Compilation.MdOptions")!.GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single(constructor=>constructor.GetParameters().Length==6).Invoke(new object[]{1048576,131072,200,scan,"lithosharp-markdown/1",1});
    private static object ParseCompilerMarkdown(string raw,object? options=null,CancellationToken token=default)
    {
        var adapter=CompilerMarkdownAssembly().GetType("LithoSharp.Generators.MarkdownSourceHostAdapter")!;
        return options is null
            ? adapter.GetMethod("ParseForCompiler",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{raw,token})!
            : adapter.GetMethod("AnalyzeForCompiler",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object?[]{raw,options,"1/6315e352f3539311c306929ba34ac3d77990e352c95cf7bfc05600c7dea50d7b","1.0","lithosharp-markdown/1","18.1.0","YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e","18.1.0",token})!;
    }
    private static string GeneratedMarkdownSource(CSharpCompilation compilation)=>string.Join("\n",compilation.SyntaxTrees.Where(t=>t.FilePath!="canary.cs").OrderBy(t=>t.FilePath,StringComparer.Ordinal).Select(t=>t.ToString()));
    private static string MarkdownDiagnosticSignature(Diagnostic diagnostic)=>diagnostic.Id+"|"+diagnostic.Severity+"|"+diagnostic.GetMessage()+"|"+diagnostic.Location.GetLineSpan()+"|"+diagnostic.Location.SourceSpan;
    private sealed class PreviousMarkdownGenerator : AssemblyLoadContext,IDisposable
    {
        private readonly ISourceGenerator generator;
        internal PreviousMarkdownGenerator():base(isCollectible:true)
        {
            _=ManifestGenerators.Value;
            using var zip=ZipFile.OpenRead(Required("MD04_BASELINE_GENERATOR_NUPKG"));
            using var stream=zip.GetEntry("analyzers/dotnet/cs/LithoSharp.Generators.dll")!.Open();using var bytes=new MemoryStream();stream.CopyTo(bytes);bytes.Position=0;
            generator=((IIncrementalGenerator)Activator.CreateInstance(LoadFromStream(bytes).GetType("LithoSharp.Generators.StaticContentGenerator")!)!).AsSourceGenerator();
        }
        protected override Assembly? Load(AssemblyName name)=>Default.Assemblies.FirstOrDefault(a=>AssemblyName.ReferenceMatchesDefinition(a.GetName(),name));
        internal (CSharpCompilation Compilation,ImmutableArray<Diagnostic> Diagnostics) Generate(string source,ManifestInput[] inputs)
        {
            var compilation=Compile(source,true).AddReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Where(p=>Path.GetFileName(p)=="System.Collections.dll").Select(p=>MetadataReference.CreateFromFile(p)));
            GeneratorDriver driver=CSharpGeneratorDriver.Create([generator],inputs.Select(i=>new ManifestText(i)),(CSharpParseOptions)compilation.SyntaxTrees.First().Options,new ManifestProvider());
            driver.RunGeneratorsAndUpdateCompilation(compilation,out var output,out var diagnostics);
            Check(!diagnostics.Any(d=>d.Id=="CS8785"),"Frozen previous Generator baseline cannot crash");
            return ((CSharpCompilation)output,diagnostics);
        }
        public void Dispose()=>Unload();
    }
}
