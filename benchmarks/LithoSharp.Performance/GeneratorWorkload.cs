using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace LithoSharp.Performance;

// Deliberately a header-scanning substage, not a C# compilation or full
// Generator throughput claim. Exercises the actual embedded Source host.
internal static class GeneratorWorkload
{
    internal static async Task RunAsync(BenchmarkArguments arguments, string[] rawArguments)
    {
        var generatorPath = Path.GetFullPath(arguments.GeneratorAssembly ?? "src/LithoSharp.Generators/bin/Release/netstandard2.0/LithoSharp.Generators.dll");
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(generatorPath);
        var adapter = assembly.GetType("LithoSharp.Generators.MarkdownSourceHostAdapter", throwOnError: true)!;
        var parse = adapter.GetMethod("ParseForCompiler", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new NotSupportedException("This Generator does not support the header-scan measurement.");
        var sourceVersion = assembly.GetType("Syntamark.Compilation.MdParserVersion")?.GetField("Value", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue();
        var count = arguments.PageCount ?? 100;
        var inputs = Enumerable.Range(0, count).Select(index => $"---\ntitle: Page {index}\n---\n# Body\n" + new string('x', 4096)).ToArray();
        var samples = new List<object>();
        for (var iteration = -arguments.Warmup; iteration < arguments.Iterations; iteration++)
        {
            var allocated = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            foreach (var input in inputs)
            {
                var result = parse.Invoke(null, [input, CancellationToken.None])!;
                if (result.GetType().GetProperty("Status", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(result)?.ToString() != "Complete")
                    throw new InvalidOperationException("Generator header scan was incomplete.");
            }
            timer.Stop();
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            if (iteration >= 0)
                samples.Add(new { iteration, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds, allocatedBytes = allocated });
        }
        var output = Path.GetFullPath(arguments.OutputPath ?? ".local/performance/generator.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            status = "completed",
            stage = "Generator embedded Source header scanning",
            scope = "ParseForCompiler through reflection, including invocation overhead; excludes schema binding, code emission, Roslyn and compilation. Not whole Generator time.",
            identity = BenchmarkIdentity.Capture("generator-header-scan"),
            generator = BenchmarkIdentity.AssemblyIdentity(assembly),
            embeddedSourceParserVersion = sourceVersion,
            sourceIdentityStatus = sourceVersion is null ? "unavailable" : "measured",
            profile = adapter.GetField("ProfileId", BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue(),
            corpus = new { count, bodyCharacters = 4096, sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", inputs)))) },
            provenance = ProvenanceMetadata.Create(rawArguments, arguments.Warmup, arguments.Iterations, null),
            raw = samples
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Wrote Generator header-scan measurement: {output}");
    }
}
