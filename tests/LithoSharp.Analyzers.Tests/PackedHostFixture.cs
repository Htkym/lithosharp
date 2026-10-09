using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    private static readonly Lazy<PackageArtifact> Packed = new(PackageArtifact.Open);

    private static CSharpCompilation Compile(string source, bool core, string file = "canary.cs")
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll" };
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(System.IO.Path.PathSeparator)
            .Where(p => names.Contains(System.IO.Path.GetFileName(p))).Select(p => MetadataReference.CreateFromFile(p)).Cast<MetadataReference>().ToList();
        if (core) references.Add(MetadataReference.CreateFromFile(Required("RA01_CORE_METADATA")));
        return CSharpCompilation.Create("CanaryUser", new[] { CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(LanguageVersion.CSharp13), file) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(CSharpCompilation compilation, CancellationToken cancellation = default)
    {
        var diagnostics = await compilation.WithAnalyzers(Packed.Value.Analyzers).GetAnalyzerDiagnosticsAsync(cancellation);
        Check(!diagnostics.Any(d => d.Id is "AD0001" or "CS8032" or "CS9057"), "Analyzer crash/load failure is never warning0 success");
        return diagnostics;
    }

    private static bool Forbidden(string name) => name is "LithoSharp" or "Syntamark" or "AngleSharp" or "SkiaSharp" or "Node"
        || name.StartsWith("Microsoft.Build", StringComparison.Ordinal) || name.Contains("Workspaces", StringComparison.Ordinal);
    private static string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
        ? value : throw new InvalidOperationException("Explicit canary input missing: " + key);
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed record PackageArtifact(string Path, string Version, string Sha256, Assembly Assembly, ImmutableArray<DiagnosticAnalyzer> Analyzers)
    {
        internal static PackageArtifact Open()
        {
            var path = Required("RA01_NUPKG");
            var bytes = File.ReadAllBytes(path);
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var dlls = zip.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToArray();
            Check(dlls.Select(e => e.FullName).Order().SequenceEqual(new[] {
                "analyzers/dotnet/cs/LithoSharp.Analyzers.dll", "analyzers/dotnet/cs/YamlDotNet.dll" }.Order()),
                "Pack owns analyzer and selected YAML dependency, no runtime/Roslyn payload");
            Check(zip.GetEntry("analyzers/dotnet/cs/LithoSharp.Analyzers.xml") is not null, "XML payload");
            Check(!zip.Entries.Any(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)), "No consumer runtime DLL");
            using var nuspecStream = zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
            var nuspec = XDocument.Load(nuspecStream);
            Check(!nuspec.Descendants().Any(e => e.Name.LocalName == "dependency"), "No transitive runtime dependency groups");
            var id = nuspec.Descendants().Single(e => e.Name.LocalName == "id").Value;
            var version = nuspec.Descendants().Single(e => e.Name.LocalName == "version").Value;
            Check(id == "LithoSharp.Analyzers" && version == Required("RA01_EXPECTED_PACKAGE_VERSION"), "Actual package ID/version");
            var payload = System.IO.Path.Combine(Required("RA01_PAYLOAD_DIR"), "LithoSharp.Analyzers.dll");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(payload)!);
            foreach (var dll in dlls)
                using (var source = dll.Open())
                using (var target = File.Create(System.IO.Path.Combine(Required("RA01_PAYLOAD_DIR"), System.IO.Path.GetFileName(dll.FullName)))) source.CopyTo(target);
            var reference = new AnalyzerFileReference(payload, new Loader());
            var failed = false;
            reference.AnalyzerLoadFailed += (_, _) => failed = true;
            var analyzers = reference.GetAnalyzers(LanguageNames.CSharp);
            Check(!failed && analyzers.Length == 1 && analyzers[0].GetType().FullName == "LithoSharp.Analyzers.StaticUrlAnalyzer", "Actual packed analyzer discovery; load errors fail closed");
            using var contractStream = zip.GetEntry("static-value-contract.json")!.Open();
            using var contract = JsonDocument.Parse(contractStream);
            var flow = analyzers[0].GetType().Assembly.GetType("LithoSharp.Analyzers.StaticValueFlow")!;
            foreach (var (key, field) in new[] { ("candidates", "MaxCandidates"), ("blocksPerGraph", "MaxBlocks"),
                ("operationsPerGraph", "MaxOperations"), ("evaluationDepth", "MaxDepth"),
                ("stringUtf16Length", "MaxStringLength"), ("nestedGraphsPerRoot", "MaxNestedGraphs") })
                Check(contract.RootElement.GetProperty("budgets").GetProperty(key).GetInt32()
                    == (int)flow.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!,
                    "Packed flow budget manifest must match executable analyzer: " + key);
            foreach (var (key, type, field) in new[] { ("sourceUtf16Length", "HtmlTemplateAnalysis", "MaxTemplateLength"),
                ("decodedUtf16Length", "HtmlTemplateAnalysis", "MaxTemplateLength"),
                ("holes", "HtmlTemplateAnalysis", "MaxHoles"), ("nameLength", "HtmlContextCursor", "MaxNameLength") })
                Check(contract.RootElement.GetProperty("htmlTemplates").GetProperty("budgets").GetProperty(key).GetInt32()
                    == (int)analyzers[0].GetType().Assembly.GetType("LithoSharp.Analyzers." + type)!
                        .GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!,
                    "Packed HTML budget manifest matches executable analyzer: " + key);
            var literal = analyzers[0].GetType().Assembly.GetType("LithoSharp.Analyzers.HtmlLiteralAnalysis")!;
            foreach (var (key, field) in new[] { ("decodedUtf16Length", "MaxInputLength"), ("sourceUtf16Length", "MaxSourceLength"),
                ("nodes", "MaxNodes"), ("depth", "MaxDepth"), ("operations", "MaxOperations") })
                Check(contract.RootElement.GetProperty("literalHtml").GetProperty("budgets").GetProperty(key).GetInt32()
                    == (int)literal.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!,
                    "Packed literal HTML budget manifest matches executable analyzer: " + key);
            return new(path, version, Sha(bytes), analyzers[0].GetType().Assembly, analyzers);
        }
    }

    private sealed class Loader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }
        public Assembly LoadFromPath(string fullPath) => AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
    }
}
