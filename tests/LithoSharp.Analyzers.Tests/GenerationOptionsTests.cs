using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task GenerationParallelismUsesInitializerSnapshotsAndOriginalLocations()
    {
        var proof = new List<object>();
        foreach (var (name, body) in new[]
        {
            ("zero", GenerationCall("new Options { MaxDegreeOfParallelism = 0 }")),
            ("negative", GenerationCall("new Options { MaxDegreeOfParallelism = -1 }")),
            ("later-initializer", "int n;" + GenerationCall("new Options { MaxDegreeOfParallelism = (n=0), EnvironmentName = Set(ref n,1) }")),
            ("named-options-before-call", "int n;g.GenerateWithOptionsAsync(options:new Options { MaxDegreeOfParallelism=(n=0) },customization:Touch(ref n,1),site:site,posts:posts,outputDirectory:\"out\",clean:false,cancellationToken:default);"),
            ("snapshot-before-later-cfg-split", "int n;g.GenerateWithOptionsAsync(options:new Options { MaxDegreeOfParallelism=(n=0) },customization:flag?null:Touch(ref n,1),site:site,posts:posts,outputDirectory:\"out\",clean:false,cancellationToken:default);"),
        })
        {
            var source = GenerationSource(body);
            var compilation = Compile(source, true, name + ".cs");
            GenerationBinds(compilation, name);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == 1, name + ": " + string.Join("; ", diagnostics));
            var initializer = compilation.SyntaxTrees.Single().GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Single(node => node.Left.ToString() == "MaxDegreeOfParallelism").Right;
            var diagnostic = diagnostics[0];
            Check(diagnostic.Id == "LSA1005" && diagnostic.Severity == DiagnosticSeverity.Error
                && diagnostic.Location.SourceTree == initializer.SyntaxTree && diagnostic.Location.SourceSpan == initializer.Span,
                "New general options rule and original initializer UTF16 span: " + name);
            Check(diagnostic.Properties["staticState"] == "Known" && diagnostic.Properties["candidateCount"] == "1"
                && diagnostic.Properties["optionsArgumentOrdinal"] == "5", "Assignment snapshot and exact ordinal: " + name);
            proof.Add(new { name, source, diagnostics = diagnostics.Select(GenerationDiagnostic).ToArray() });
        }
        var standard = Compile(GenerationSource(GenerationCall("new Options { MaxDegreeOfParallelism=0 }")), true);
        foreach (var severity in new[] { ReportDiagnostic.Warn, ReportDiagnostic.Suppress })
        {
            var diagnostics = await Analyze(standard.WithOptions(standard.Options.WithSpecificDiagnosticOptions(
                new Dictionary<string, ReportDiagnostic> { ["LSA1005"] = severity })));
            Check(severity == ReportDiagnostic.Warn
                ? diagnostics.Length == 1 && diagnostics[0].Severity == DiagnosticSeverity.Warning : diagnostics.Length == 0,
                "Standard severity/suppression: " + severity);
        }
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => Forbidden(assembly.GetName().Name!)), "Core and user code were not executed");
        ManifestEvidence("generation-options-known", proof);
    }

    [Test]
    public async Task GenerationParallelismDefersUnknownAliasesLookalikesAndEarlierEffects()
    {
        var proof = new List<object>();
        foreach (var (name, body) in new[]
        {
            ("one", GenerationCall("new Options { MaxDegreeOfParallelism=1 }")),
            ("maximum", GenerationCall("new Options { MaxDegreeOfParallelism=int.MaxValue }")),
            ("omitted-one", GenerationCall("new Options()")),
            ("options-null", GenerationCall("null!")),
            ("unknown-getter", GenerationCall("new Options { MaxDegreeOfParallelism=CountGetter }")),
            ("constructor-invalidates-local", "int n=0;" + GenerationCall("new Options { MaxDegreeOfParallelism=n }")),
            ("earlier-initializer-call", "int n=0;" + GenerationCall("new Options { EnvironmentName=Set(ref n,0), MaxDegreeOfParallelism=n }")),
            ("named-getter-before-options", "int n=0;g.GenerateWithOptionsAsync(customization:CustomizationGetter,options:new Options {MaxDegreeOfParallelism=n},site:site,posts:posts,outputDirectory:\"out\",clean:false,cancellationToken:default);"),
            ("named-call-before-options", "int n=0;g.GenerateWithOptionsAsync(customization:Touch(ref n,0),options:new Options {MaxDegreeOfParallelism=n},site:site,posts:posts,outputDirectory:\"out\",clean:false,cancellationToken:default);"),
            ("valid-before-later-mutation", "int n;g.GenerateWithOptionsAsync(options:new Options {MaxDegreeOfParallelism=(n=1)},customization:Touch(ref n,0),site:site,posts:posts,outputDirectory:\"out\",clean:false,cancellationToken:default);"),
            ("mixed-cfg", GenerationCall("new Options { MaxDegreeOfParallelism=flag?0:1 }")),
            ("split-capture-not-recovered", "int n=0;" + GenerationCall("new Options { MaxDegreeOfParallelism=flag?n:n }")),
            ("options-alias", "var options=new Options {MaxDegreeOfParallelism=0};" + GenerationCall("options")),
            ("with", GenerationCall("new Options() with {MaxDegreeOfParallelism=0}")),
            ("other-overload", "GenerateWithOptionsAsync(new Options {MaxDegreeOfParallelism=0});"),
        })
        {
            var source = GenerationSource(body);
            var compilation = Compile(source, true, name + ".cs");
            GenerationBinds(compilation, name);
            var diagnostics = await Analyze(compilation);
            Check(diagnostics.Length == 0, name + ": " + string.Join("; ", diagnostics));
            proof.Add(new { name, source, diagnostics = diagnostics.Select(GenerationDiagnostic).ToArray() });
        }
        const string fake = "namespace LithoSharp { public sealed class SiteGenerator { public System.Threading.Tasks.Task<SiteGenerationResult> GenerateWithOptionsAsync(Configuration.SiteSettings site,System.Collections.Generic.IReadOnlyList<Content.MarkdownPost> posts,string outputDirectory,bool clean,SiteCustomization? customization,SiteGenerationOptions options,System.Threading.CancellationToken cancellationToken)=>throw new System.Exception(\"FAKE_SENTINEL\"); } }";
        var fakeSource = fake + GenerationSource(GenerationCall("new Options {MaxDegreeOfParallelism=0}"));
        // Namespace-local usings are necessary after the preceding namespace declaration.
        fakeSource = fakeSource.Replace("using Options=", "namespace Consumer { using Options=", StringComparison.Ordinal) + "}";
        var shadow = Compile(fakeSource, true, "source-lookalike.cs");
        GenerationBinds(shadow, "source-lookalike");
        Check((await Analyze(shadow)).Length == 0, "Source-defined SiteGenerator is not the Core metadata method");
        proof.Add(new { name = "source-lookalike", source = fakeSource });
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => Forbidden(assembly.GetName().Name!)), "No getter/call/constructor or Core runtime execution");
        ManifestEvidence("generation-options-deferred", proof);
    }

    private static string GenerationCall(string options) => "g.GenerateWithOptionsAsync(site,posts,\"out\",false,null," + options + ",default);";
    private static string GenerationSource(string body) => """
        using Options=LithoSharp.SiteGenerationOptions;
        // 😀 original UTF16 source
        class Calls {
            static int CountGetter=>throw new System.Exception("GETTER_SENTINEL");
            static LithoSharp.SiteCustomization? CustomizationGetter=>throw new System.Exception("CUSTOMIZATION_SENTINEL");
            static string Set(ref int value,int replacement){value=replacement;throw new System.Exception("SET_SENTINEL");}
            static LithoSharp.SiteCustomization? Touch(ref int value,int replacement){value=replacement;throw new System.Exception("CALL_SENTINEL");}
            static void GenerateWithOptionsAsync(Options options){}
            void M(bool flag,LithoSharp.SiteGenerator g,LithoSharp.Configuration.SiteSettings site,System.Collections.Generic.IReadOnlyList<LithoSharp.Content.MarkdownPost> posts) {
        """.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n" + body + "\r\n} }";
    private static void GenerationBinds(Microsoft.CodeAnalysis.CSharp.CSharpCompilation compilation, string name) =>
        Check(!compilation.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
            name + " binds actual Core metadata: " + string.Join("; ", compilation.GetDiagnostics()));
    private static object GenerationDiagnostic(Diagnostic diagnostic) => new
    {
        diagnostic.Id, severity = diagnostic.Severity.ToString(), diagnostic.Location.SourceSpan.Start,
        diagnostic.Location.SourceSpan.Length, expression = diagnostic.Location.SourceTree?.GetText().ToString(diagnostic.Location.SourceSpan), diagnostic.Properties
    };
}
