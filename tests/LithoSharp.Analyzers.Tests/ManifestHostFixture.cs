using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    private const string ManifestModel = """
        using LithoSharp.Content;
        public sealed class Front { public string Title { get; set; } = ""; public int Count { get; set; } }
        [StaticContentCollection(typeof(Front), typeof(string), "guides", Site = "docs", Variant = "ja", EmitStaticSiteManifest = true)]
        public static partial class Guides { }
        """;
    private sealed record ManifestInput(string Path = "D:/site/intro.md", string? Content = "---\ntitle: Hello\n---\nBody",
        string Id = "intro", string Route = "intro/", string Collection = "guides", string Site = "docs", string Variant = "ja");
    private sealed class ManifestText(ManifestInput input) : AdditionalText
    {
        public ManifestInput Input { get; } = input;
        public override string Path => Input.Path;
        public override SourceText? GetText(CancellationToken cancellationToken = default) => Input.Content is null ? null : SourceText.From(Input.Content);
    }
    private sealed class ManifestOptions(Dictionary<string, string> values) : AnalyzerConfigOptions
    { public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!); }
    private sealed class ManifestProvider(string root = "D:/site", string profile = "default", string target = "net10.0") : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => new ManifestOptions(new()
        {
            ["build_property.MSBuildProjectDirectory"] = root,
            ["build_property.TargetFramework"] = target,
            ["build_property.Configuration"] = "Release",
            ["build_property.LithoSharpAnalysisProfile"] = profile
        });
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new ManifestOptions([]);
        public override AnalyzerConfigOptions GetOptions(AdditionalText text)
        {
            var input = ((ManifestText)text).Input;
            return new ManifestOptions(new()
            {
                ["build_metadata.AdditionalFiles.LithoSharpCollection"] = input.Collection,
                ["build_metadata.AdditionalFiles.LithoSharpId"] = input.Id,
                ["build_metadata.AdditionalFiles.LithoSharpRoute"] = input.Route,
                ["build_metadata.AdditionalFiles.LithoSharpSite"] = input.Site,
                ["build_metadata.AdditionalFiles.LithoSharpVariant"] = input.Variant
            });
        }
    }
    private static readonly Lazy<ImmutableArray<ISourceGenerator>> ManifestGenerators = new(LoadManifestGenerators);
    private static ImmutableArray<ISourceGenerator> LoadManifestGenerators()
    {
        _ = Packed.Value; // Extract the same YAML dependency before loading the actual generator package.
        using var zip = ZipFile.OpenRead(Required("RA05_GENERATOR_NUPKG"));
        using (var yaml = zip.GetEntry("analyzers/dotnet/cs/YamlDotNet.dll")!.Open())
        using (var bytes = new MemoryStream())
        {
            yaml.CopyTo(bytes);
            var dependency = Path.Combine(Required("RA01_PAYLOAD_DIR"), "YamlDotNet.dll");
            Check(File.ReadAllBytes(dependency).SequenceEqual(bytes.ToArray()), "Same actual YAML package dependency");
            System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency);
        }
        var dll = zip.GetEntry("analyzers/dotnet/cs/LithoSharp.Generators.dll")!;
        var path = Path.Combine(Required("RA01_PAYLOAD_DIR"), "LithoSharp.Generators.dll");
        using (var source = dll.Open()) using (var target = File.Create(path)) source.CopyTo(target);
        var reference = new AnalyzerFileReference(path, new Loader());
        var generators = reference.GetGenerators(LanguageNames.CSharp);
        Check(generators.Length == 1, "Actual packed generator discovery");
        return generators;
    }
    private static (CSharpCompilation Compilation, ImmutableArray<Diagnostic> Diagnostics) ManifestGenerate(string source,
        ManifestInput[]? inputs = null, ManifestProvider? provider = null, MetadataReference? dependency = null)
    {
        var compilation = Compile(source, true).AddReferences(((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) == "System.Collections.dll").Select(p => MetadataReference.CreateFromFile(p)));
        if (dependency is not null) compilation = compilation.AddReferences(dependency);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(ManifestGenerators.Value, (inputs ?? [new()]).Select(input => new ManifestText(input)),
            (CSharpParseOptions)compilation.SyntaxTrees.First().Options, provider ?? new());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        Check(!diagnostics.Any(d => d.Id is "CS8785" or "AD0001"), "Generator crash is never Deferred: " + string.Join("\n", diagnostics));
        var generatedText = string.Join("\n", output.SyntaxTrees.Where(tree => tree.FilePath != "canary.cs").Select(tree => tree.ToString()));
        var evidence = Path.Combine(Required("RA05_EVIDENCE_DIR"), "host-generated", Sha(System.Text.Encoding.UTF8.GetBytes(source + generatedText)));
        Directory.CreateDirectory(evidence);
        File.WriteAllText(Path.Combine(evidence, "original.cs"), source);
        File.WriteAllText(Path.Combine(evidence, "inputs.json"), JsonSerializer.Serialize(inputs ?? [new()], new JsonSerializerOptions { WriteIndented = true }));
        foreach (var tree in output.SyntaxTrees.Where(tree => tree.FilePath != "canary.cs"))
            File.WriteAllText(Path.Combine(evidence, Path.GetFileName(tree.FilePath)), tree.ToString());
        return ((CSharpCompilation)output, diagnostics);
    }
    private static async Task<ImmutableArray<Diagnostic>> ManifestAnalyze(CSharpCompilation compilation,
        ManifestInput[]? inputs = null, ManifestProvider? provider = null)
    {
        var diagnostics = await compilation.WithAnalyzers(Packed.Value.Analyzers,
            new AnalyzerOptions((inputs ?? [new()]).Select(input => (AdditionalText)new ManifestText(input)).ToImmutableArray(), provider ?? new())).GetAnalyzerDiagnosticsAsync();
        Check(!diagnostics.Any(d => d.Id is "AD0001" or "CS8032" or "CS9057"), "Packed manifest analyzer must not crash");
        return diagnostics;
    }
    private static void ManifestEvidence(string name, object cases) => File.WriteAllText(Path.Combine(Required("RA05_EVIDENCE_DIR"), name + ".json"),
        JsonSerializer.Serialize(new { status = "PASS", cases }, new JsonSerializerOptions { WriteIndented = true }));
    private static object ManifestDiagnostic(Diagnostic d) => new
    {
        d.Id,
        severity = d.Severity.ToString(),
        file = d.Location.SourceTree?.FilePath,
        start = d.Location.SourceSpan.Start,
        length = d.Location.SourceSpan.Length,
        d.Properties
    };

}
