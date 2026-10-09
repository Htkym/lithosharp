using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using LithoSharp.HtmlPortability;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

if (args.Length != 6) throw new ArgumentException("nupkg, version, corpus, runtime-proof, Core metadata and output directory are required.");
var packagePath = Path.GetFullPath(args[0]); var output = Path.GetFullPath(args[5]);
Directory.CreateDirectory(output);
bool Forbidden(string name) => name is "LithoSharp" or "Syntamark" or "AngleSharp" or "SkiaSharp" or "Node"
    || name.StartsWith("Microsoft.Build", StringComparison.Ordinal) || name.Contains("Workspaces", StringComparison.Ordinal);
string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
using (var zip = ZipFile.OpenRead(packagePath))
{
    Check(zip.Entries.Where(e => e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .All(e => e.FullName is "analyzers/dotnet/cs/LithoSharp.Analyzers.dll" or "analyzers/dotnet/cs/YamlDotNet.dll"), "Unexpected package DLL dependency");
    using var nuspec = zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
    var metadata = XDocument.Load(nuspec);
    Check(metadata.Descendants().Single(e => e.Name.LocalName == "version").Value == args[1], "Candidate version mismatch");
    Check(!metadata.Descendants().Any(e => e.Name.LocalName == "dependency"), "Unexpected runtime package dependencies");
    foreach (var name in new[] { "LithoSharp.Analyzers.dll", "YamlDotNet.dll" })
        if (zip.GetEntry("analyzers/dotnet/cs/" + name) is { } entry) entry.ExtractToFile(Path.Combine(output, name), overwrite: false);
}
var dll = Path.Combine(output, "LithoSharp.Analyzers.dll");
var loader = new PackedLoader(output);
var reference = new AnalyzerFileReference(dll, loader);
var loadFailed = false; reference.AnalyzerLoadFailed += (_, _) => loadFailed = true;
var analyzers = reference.GetAnalyzers(LanguageNames.CSharp);
Check(!loadFailed && analyzers.Length == 1, "Packed analyzer discovery failed");
var assembly = analyzers[0].GetType().Assembly;
Check(assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName == ".NETStandard,Version=v2.0", "Portable target mismatch");
Check(assembly.GetReferencedAssemblies().All(a => !Forbidden(a.Name!)), "Forbidden runtime dependency in analyzer metadata");
Check(typeof(CSharpCompilation).Assembly.GetName().Version == new Version(4, 14, 0, 0), "Actual Roslyn must be 4.14");
var observations = HtmlBridgeObservations.Inputs(args[2]).Select(input => new { input.Id, facts = HtmlBridgeObservations.Observe(assembly, input) }).ToArray();
using var expected = JsonDocument.Parse(File.ReadAllText(args[3]));
var actual = JsonSerializer.SerializeToElement(observations);
Check(JsonElement.DeepEquals(expected.RootElement, actual), "Runtime and packed compiler host observations differ");
using var cancel = new CancellationTokenSource(); cancel.Cancel();
var cancelled = false;
try { HtmlBridgeObservations.Parse(assembly, new("cancel", "<a href='x'>X</a>"), cancel.Token); }
catch (OperationCanceledException) { cancelled = true; }
Check(cancelled, "Cancelled host parse returned a result");
const string source = "// 😀 UTF16\nclass C { void M() { LithoSharp.SiteUrl.FromAbsolute(\"file:///tmp/private\"); } }";
var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll" };
var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
    .Where(p => names.Contains(Path.GetFileName(p))).Select(p => MetadataReference.CreateFromFile(p)).Cast<MetadataReference>().ToList();
refs.Add(MetadataReference.CreateFromFile(Path.GetFullPath(args[4]))); // metadata only; never CLR-load Core.
var compilation = CSharpCompilation.Create("Canary", new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp13), "canary.cs") },
    refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
Check(!compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Canary failed to bind actual Core metadata");
var diagnostics = await compilation.WithAnalyzers(analyzers).GetAnalyzerDiagnosticsAsync();
Check(diagnostics.Length == 1 && diagnostics[0].Id == "LSA1001" && diagnostics[0].Severity == DiagnosticSeverity.Error
    && diagnostics[0].Location.SourceSpan.Start == source.IndexOf("\"file:///tmp/private\"", StringComparison.Ordinal)
    && diagnostics[0].Location.SourceSpan.Length == "\"file:///tmp/private\"".Length
    && diagnostics[0].Location.SourceTree?.FilePath == "canary.cs", "Compiler host canary failed or crashed");
var loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name!).Order().ToArray();
Check(!loaded.Any(Forbidden), "Forbidden runtime assembly loaded into compiler host");
File.WriteAllText(Path.Combine(output, "host-observations.json"), JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(Path.Combine(output, "host-proof.json"), JsonSerializer.Serialize(new {
    status = "PASS", cases = observations.Length, package = packagePath, packageVersion = args[1], packageSha256 = Sha(File.ReadAllBytes(packagePath)),
    analyzerSha256 = Sha(File.ReadAllBytes(dll)), targetFramework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName,
    roslyn = typeof(CSharpCompilation).Assembly.FullName, analyzerReferences = assembly.GetReferencedAssemblies().Select(a => a.FullName).ToArray(),
    loadedAssemblies = loaded, forbiddenLoaded = loaded.Where(Forbidden).ToArray(), runtimeHostIdentical = true, cancellation = "propagated",
    canary = new { diagnostics[0].Id, start = diagnostics[0].Location.SourceSpan.Start, length = diagnostics[0].Location.SourceSpan.Length },
    coreMetadataSha256 = Sha(File.ReadAllBytes(args[4])) }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Packed HTML bridge PASS: {observations.Length} runtime/host cases; Roslyn 4.14 canary; forbidden runtime loads 0.");

sealed class PackedLoader(string directory) : IAnalyzerAssemblyLoader
{
    public void AddDependencyLocation(string fullPath) { }
    public Assembly LoadFromPath(string fullPath)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) => name.Name == "YamlDotNet"
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "YamlDotNet.dll")) : null;
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
    }
}
