using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LithoSharp;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;

using LithoSharp.Performance;

const int defaultPageCount = 100;
var arguments = BenchmarkArguments.Parse(args);
var runSite = arguments.Mode is "site" or "all";
var runEngine = arguments.Mode is "engine" or "all";
var pageCount = arguments.PageCount ?? defaultPageCount;
if (pageCount is not (100 or 1_000 or 10_000))
{
    throw new ArgumentException("The page count must be 100, 1000, or 10000.", nameof(args));
}

var outputPath = Path.GetFullPath(arguments.OutputPath ?? Path.Combine(
    Environment.CurrentDirectory,
    "artifacts",
    "performance-baselines",
    $"baseline-{pageCount}.json"));
var outputDirectory = Path.GetDirectoryName(outputPath)
    ?? throw new InvalidOperationException("The result output path must include a directory.");
Directory.CreateDirectory(outputDirectory);

var engineOutputPath = Path.GetFullPath(arguments.EngineOutputPath ?? Path.Combine(
    outputDirectory,
    $"engine-{pageCount}.json"));
Directory.CreateDirectory(Path.GetDirectoryName(engineOutputPath)!);

if (runEngine)
{
    var engineResult = EngineBenchmark.Run(
        pageCount,
        arguments.Warmup,
        arguments.Iterations,
        args);
    await File.WriteAllTextAsync(
        engineOutputPath,
        JsonSerializer.Serialize(engineResult, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    Console.WriteLine($"Wrote engine benchmark result to {engineOutputPath}");
}

if (!runSite)
{
    return;
}

var corpusDirectory = Path.Combine(outputDirectory, $"corpus-{pageCount}");
var siteDirectory = Path.Combine(outputDirectory, $"site-{pageCount}");
CorpusGenerator.Generate(corpusDirectory, pageCount);
var posts = await new MarkdownPostReader().ReadAllAsync(corpusDirectory);
if (posts.Count != pageCount)
{
    throw new InvalidOperationException($"Expected {pageCount} Markdown posts, but read {posts.Count}.");
}

var site = new SiteSettings
{
    Title = "LithoSharp Performance Corpus",
    Description = "Deterministic performance measurement corpus.",
    BaseUrl = "https://example.test/",
    Language = "en",
    Author = "LithoSharp",
    TimeZone = "UTC"
};
var customization = new SiteCustomization
{
    Template = new BlogSiteTemplate(),
    GenerateLlmsTxt = true
};
var options = new SiteGenerationOptions
{
    BuildTimestamp = CorpusGenerator.BuildTimestamp
};

var process = Process.GetCurrentProcess();
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
process.Refresh();
var workingSetBefore = process.WorkingSet64;
var allocatedBefore = GC.GetTotalAllocatedBytes(true);
using var workingSetMonitor = new WorkingSetMonitor(process, TimeSpan.FromMilliseconds(10));
workingSetMonitor.Start();
var stopwatch = Stopwatch.StartNew();
long allocatedAfter;
long workingSetAfter;
SiteGenerationResult result;
try
{
    result = await new SiteGenerator().GenerateWithOptionsAsync(
        site,
        posts,
        siteDirectory,
        clean: true,
        customization,
        options,
        CancellationToken.None);
    allocatedAfter = GC.GetTotalAllocatedBytes(true);
    stopwatch.Stop();
    process.Refresh();
    workingSetAfter = process.WorkingSet64;
}
finally
{
    stopwatch.Stop();
    await workingSetMonitor.StopAsync();
}

var generatedFileCount = Directory.EnumerateFiles(siteDirectory, "*", SearchOption.AllDirectories)
    .Count(path => Path.GetFileName(path) != ".lithosharp-output-manifest.json");
if (generatedFileCount != result.GeneratedFiles.Count)
{
    throw new InvalidOperationException(
        $"Generator reported {result.GeneratedFiles.Count} files, but {generatedFileCount} files exist on disk.");
}

var expectedGeneratedFileCount = pageCount + 11;
if (generatedFileCount != expectedGeneratedFileCount)
{
    throw new InvalidOperationException(
        $"Expected {expectedGeneratedFileCount} generated files for the Blog template, but found {generatedFileCount}.");
}

var measurement = new BenchmarkResult
{
    SchemaVersion = 3,
    Benchmark = "site-generation-workloads",
    Smoke = arguments.Smoke,
    Corpus = new CorpusMetadata
    {
        PageCount = pageCount,
        RootDirectory = corpusDirectory,
        BuildTimestamp = CorpusGenerator.BuildTimestamp
    },
    Measurement = new Measurement
    {
        CleanBuildElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        AllocatedBytes = Math.Max(0, allocatedAfter - allocatedBefore),
        GenerationStartWorkingSetBytes = workingSetBefore,
        GenerationEndWorkingSetBytes = workingSetAfter,
        GenerationPeakWorkingSetBytes = Math.Max(workingSetAfter, Math.Max(workingSetBefore, workingSetMonitor.PeakWorkingSetBytes)),
        WorkingSetSamplingIntervalMilliseconds = workingSetMonitor.SamplingIntervalMilliseconds,
        GeneratedFileCount = generatedFileCount
    },
    Environment = EnvironmentMetadata.Create(),
    // Site workloads execute once per process; --warmup/--iterations apply to
    // engine workloads only. The provenance records the actual site execution
    // counts so the record matches the measurement. Official comparison
    // repeats whole processes (at least 5 per size); see README.
    Provenance = ProvenanceMetadata.Create(args, warmup: 0, iterations: 1, corpusDirectory),
};

var workloads = new List<WorkloadMeasurement>
{
    WorkloadMeasurement.From("clean", stopwatch.Elapsed.TotalMilliseconds, allocatedAfter - allocatedBefore,
        workingSetBefore, workingSetAfter, workingSetMonitor.PeakWorkingSetBytes,
        result, generatedFileCount)
};

async Task<(WorkloadMeasurement Measurement, SiteGenerationResult Result)> MeasureWorkloadAsync(
    string name,
    IReadOnlyList<MarkdownPost> workloadPosts,
    SiteSettings workloadSite,
    bool clean,
    SiteBuildPlan? previousPlan,
    SiteCustomization? workloadCustomization = null)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    process.Refresh();
    var startWorkingSet = process.WorkingSet64;
    var startAllocated = GC.GetTotalAllocatedBytes(true);
    using var monitor = new WorkingSetMonitor(process, TimeSpan.FromMilliseconds(10));
    monitor.Start();
    var timer = Stopwatch.StartNew();
    SiteGenerationResult workloadResult;
    long endAllocated;
    long endWorkingSet;
    try
    {
        workloadResult = await new SiteGenerator().GenerateWithOptionsAsync(
            workloadSite, workloadPosts, siteDirectory, clean, workloadCustomization ?? customization,
            new SiteGenerationOptions { BuildTimestamp = CorpusGenerator.BuildTimestamp, PreviousBuildPlan = previousPlan },
            CancellationToken.None);
        endAllocated = GC.GetTotalAllocatedBytes(true);
        timer.Stop();
        process.Refresh();
        endWorkingSet = process.WorkingSet64;
    }
    finally
    {
        timer.Stop();
        await monitor.StopAsync();
    }
    var artifactCount = Directory.EnumerateFiles(siteDirectory, "*", SearchOption.AllDirectories)
        .Count(path => Path.GetFileName(path) != ".lithosharp-output-manifest.json");
    return (
        WorkloadMeasurement.From(name, timer.Elapsed.TotalMilliseconds,
            Math.Max(0, endAllocated - startAllocated),
            startWorkingSet, endWorkingSet, Math.Max(startWorkingSet, Math.Max(endWorkingSet, monitor.PeakWorkingSetBytes)),
            workloadResult, artifactCount),
        workloadResult);
}

var noOpRun = await MeasureWorkloadAsync("no-op", posts, site, clean: false, result.BuildPlan);
workloads.Add(noOpRun.Measurement);
var changedPost = Path.Combine(corpusDirectory, "section-000", "page-00001.md");
File.AppendAllText(changedPost, Environment.NewLine + "Changed for the single-page workload.", Encoding.UTF8);
var changedPosts = await new MarkdownPostReader().ReadAllAsync(corpusDirectory);
var singlePageRun = await MeasureWorkloadAsync("single-page-change", changedPosts, site, clean: false, noOpRun.Result.BuildPlan);
workloads.Add(singlePageRun.Measurement);
var layoutCustomization = customization with
{
    Theme = new SiteThemeOptions
    {
        ThemeColor = "#7c3aed"
    }
};
var layoutRun = await MeasureWorkloadAsync(
    "layout-change",
    changedPosts,
    site,
    clean: false,
    singlePageRun.Result.BuildPlan,
    layoutCustomization);
workloads.Add(layoutRun.Measurement);
measurement.Workloads = workloads;

if (noOpRun.Result.BuildReport.Invalidations.Count != 0)
{
    throw new InvalidOperationException("The no-op workload must not invalidate any nodes.");
}
if (noOpRun.Result.BuildReport.CacheMissCount != 0)
    throw new InvalidOperationException("The no-op workload must reuse every cacheable node.");

var expectedLayoutRenderingNodes = layoutRun.Result.BuildPlan.Nodes.Count(node =>
    node.Inputs.Any(input => input.Key == "theme.themeColor"));
if (layoutRun.Result.BuildReport.Invalidations.Count != expectedLayoutRenderingNodes)
{
    throw new InvalidOperationException(
        $"The layout workload invalidated {layoutRun.Result.BuildReport.Invalidations.Count} nodes; " +
        $"expected the {expectedLayoutRenderingNodes} rendering nodes that consume theme.themeColor.");
}

if (arguments.Smoke
    && (measurement.Measurement.GenerationPeakWorkingSetBytes < measurement.Measurement.GenerationStartWorkingSetBytes
        || measurement.Measurement.GenerationPeakWorkingSetBytes < measurement.Measurement.GenerationEndWorkingSetBytes
        || measurement.Measurement.WorkingSetSamplingIntervalMilliseconds <= 0
        || measurement.Workloads.Any(workload =>
            workload.PeakWorkingSetBytes < workload.StartWorkingSetBytes
            || workload.PeakWorkingSetBytes < workload.EndWorkingSetBytes)))
{
    throw new InvalidOperationException("Smoke validation found an invalid generation working-set measurement.");
}

await File.WriteAllTextAsync(
    outputPath,
    JsonSerializer.Serialize(measurement, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

Console.WriteLine(JsonSerializer.Serialize(measurement.Measurement));
Console.WriteLine($"Wrote benchmark result to {outputPath}");

internal sealed class BenchmarkArguments
{
    public int? PageCount { get; private init; }
    public string? OutputPath { get; private init; }
    public bool Smoke { get; private init; }
    public string Mode { get; private init; } = "site";
    public int Warmup { get; private init; } = 1;
    public int Iterations { get; private init; } = 5;
    public string? EngineOutputPath { get; private init; }

    public static BenchmarkArguments Parse(string[] args)
    {
        int? pageCount = null;
        string? outputPath = null;
        var smoke = false;
        var mode = "site";
        var warmup = 1;
        var iterations = 5;
        string? engineOutput = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--size" when index + 1 < args.Length:
                    pageCount = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--output" when index + 1 < args.Length:
                    outputPath = args[++index];
                    break;
                case "--smoke":
                    smoke = true;
                    break;
                case "--mode" when index + 1 < args.Length:
                    mode = args[++index].ToLowerInvariant();
                    if (mode is not ("site" or "engine" or "all"))
                        throw new ArgumentException("The mode must be site, engine, or all.", nameof(args));
                    break;
                case "--warmup" when index + 1 < args.Length:
                    warmup = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--iterations" when index + 1 < args.Length:
                    iterations = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--engine-output" when index + 1 < args.Length:
                    engineOutput = args[++index];
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("Usage: dotnet run --project benchmarks/LithoSharp.Performance -- [--size 100|1000|10000] [--output path] [--smoke] [--mode site|engine|all] [--warmup N] [--iterations N] [--engine-output path]");
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument '{args[index]}'.");
            }
        }

        if (warmup < 0 || iterations < 1)
            throw new ArgumentException("Warmup must be >= 0 and iterations must be >= 1.", nameof(args));

        return new BenchmarkArguments { PageCount = pageCount, OutputPath = outputPath, Smoke = smoke, Mode = mode, Warmup = warmup, Iterations = iterations, EngineOutputPath = engineOutput };
    }
}

internal static class CorpusGenerator
{
    public static readonly DateTimeOffset BuildTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstPostDate = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Generate(string directory, int pageCount)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        for (var index = 1; index <= pageCount; index++)
        {
            var relativeDirectory = Path.Combine(directory, $"section-{(index - 1) / 100:D3}");
            Directory.CreateDirectory(relativeDirectory);
            var path = Path.Combine(relativeDirectory, $"page-{index:D5}.md");
            var date = FirstPostDate.AddDays(index - 1);
            var content = $"""
                ---
                title: "Benchmark page {index:D5}"
                date: {date:yyyy-MM-ddTHH:mm:ssZ}
                summary: "Deterministic benchmark page {index:D5}."
                tags:
                  - benchmark
                  - section-{(index - 1) / 100:D3}
                ---

                # Benchmark page {index:D5}

                This is deterministic Markdown content for page {index:D5}.

                - Stable list item A
                - Stable list item B
                """;
            File.WriteAllText(path, content, Encoding.UTF8);
        }
    }
}

internal sealed class BenchmarkResult
{
    public int SchemaVersion { get; init; }
    public string Benchmark { get; init; } = string.Empty;
    public bool Smoke { get; init; }
    public CorpusMetadata Corpus { get; init; } = new();
    public Measurement Measurement { get; init; } = new();
    public EnvironmentMetadata Environment { get; init; } = new();
    public IReadOnlyList<WorkloadMeasurement> Workloads { get; set; } = [];
    public ProvenanceMetadata? Provenance { get; init; }
    public MeasurementScope? Scope { get; init; } = MeasurementScope.SiteDefault;
}

internal sealed class CorpusMetadata
{
    public int PageCount { get; init; }
    public string RootDirectory { get; init; } = string.Empty;
    public DateTimeOffset BuildTimestamp { get; init; }
}

internal sealed class Measurement
{
    public double CleanBuildElapsedMilliseconds { get; init; }
    public long AllocatedBytes { get; init; }
    public long GenerationStartWorkingSetBytes { get; init; }
    public long GenerationEndWorkingSetBytes { get; init; }
    public long GenerationPeakWorkingSetBytes { get; init; }
    public double WorkingSetSamplingIntervalMilliseconds { get; init; }
    public int GeneratedFileCount { get; init; }
}

internal sealed class WorkloadMeasurement
{
    public string Name { get; init; } = string.Empty;
    public double ElapsedMilliseconds { get; init; }
    public long AllocatedBytes { get; init; }
    public long StartWorkingSetBytes { get; init; }
    public long EndWorkingSetBytes { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public int GeneratedNodeCount { get; init; }
    public int InvalidatedNodeCount { get; init; }
    public int ArtifactCount { get; init; }

    public static WorkloadMeasurement From(
        string name, double elapsed, long allocated, long start, long end, long peak,
        SiteGenerationResult result, int artifacts) => new()
    {
        Name = name,
        ElapsedMilliseconds = elapsed,
        AllocatedBytes = Math.Max(0, allocated),
        StartWorkingSetBytes = start,
        EndWorkingSetBytes = end,
        PeakWorkingSetBytes = Math.Max(peak, end),
        GeneratedNodeCount = result.BuildReport.CacheMissCount,
        InvalidatedNodeCount = result.BuildReport.Invalidations.Count,
        ArtifactCount = artifacts
    };
}

internal sealed class WorkingSetMonitor(Process process, TimeSpan samplingInterval) : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private Task? monitoringTask;

    public long PeakWorkingSetBytes { get; private set; }
    public double SamplingIntervalMilliseconds => samplingInterval.TotalMilliseconds;

    public void Start()
    {
        process.Refresh();
        PeakWorkingSetBytes = process.WorkingSet64;
        monitoringTask = MonitorAsync(cancellation.Token);
    }

    public async Task StopAsync()
    {
        if (monitoringTask is null)
        {
            return;
        }

        await cancellation.CancelAsync();
        await monitoringTask.ConfigureAwait(false);
        monitoringTask = null;
    }

    public void Dispose() => cancellation.Dispose();

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                process.Refresh();
                PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, process.WorkingSet64);
                await Task.Delay(samplingInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            process.Refresh();
            PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, process.WorkingSet64);
        }
    }
}

internal sealed class EnvironmentMetadata
{
    public string Runtime { get; init; } = string.Empty;
    public string FrameworkDescription { get; init; } = string.Empty;
    public string OSDescription { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public string ProcessArchitecture { get; init; } = string.Empty;
    public string SdkVersion { get; init; } = string.Empty;
    public string Commit { get; init; } = string.Empty;

    public static EnvironmentMetadata Create() => new()
    {
        Runtime = Environment.Version.ToString(),
        FrameworkDescription = RuntimeInformation.FrameworkDescription,
        OSDescription = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.OSArchitecture.ToString(),
        ProcessorCount = Environment.ProcessorCount,
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        SdkVersion = ProvenanceMetadata.CurrentSdkVersion,
        Commit = ProvenanceMetadata.CurrentCommit,
    };
}

internal sealed record MeasurementScope(string Measured, string OutOfScope, string WorkingSetNote)
{
    public static MeasurementScope SiteDefault => new(
        "Blog-template pipeline: clean, no-op, single-page-change, layout-change with elapsed, allocation, sampled working set, generated/invalidated nodes, artifacts.",
        "Engine micro-benchmarks (parse-only, render-only, parse+render, analyze) are in engine-*.json. No timing thresholds enforced. MDX/Node excluded.",
        "GenerationPeakWorkingSetBytes is the max sampled WorkingSet64 during generation at 10ms intervals, not the OS process-lifetime peak.");

    public static MeasurementScope EngineDefault => new(
        "Litho engine: parse-only, render-only (pre-prepared render, no parse in timer), parse+render, and analyze (one parse with HTML, syntax, and semantics).",
        "Site pipeline, MDX/Node, OS peak working set, and timing thresholds are out of scope for this file. Link new-tab attributes need site context and are covered by site workloads.",
        "Engine files record elapsed and allocation per iteration with raw values, median, mean, variance, first-vs-warm. Working-set peak is site-level only.");
}

internal sealed record ProvenanceMetadata(
    string Commit,
    string SdkVersion,
    string OS,
    string Cpu,
    IReadOnlyDictionary<string, string> DependencyVersions,
    IReadOnlyList<string> Arguments,
    int Warmup,
    int Iterations,
    string? InputHash)
{
    /// <summary>
    /// The .NET SDK that built and runs this benchmark (<c>dotnet --version</c>).
    /// The assembly informational version (for example, <c>1.0.0+&lt;commit&gt;</c>)
    /// is a product version, not the SDK, so it is only a fallback here.
    /// </summary>
    public static string CurrentSdkVersion
    {
        get
        {
            try
            {
                var start = new ProcessStartInfo("dotnet", "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(start);
                if (process is not null)
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0 && output.Length > 0) return output;
                }
            }
            catch
            {
            }

            return System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? Environment.Version.ToString();
        }
    }

    public static string CurrentCommit
    {
        get
        {
            try
            {
                var start = new ProcessStartInfo("git", "rev-parse HEAD")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(start);
                if (process is null) return "unknown";
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                return process.ExitCode == 0 && output.Length >= 7 ? output : "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }

    public static ProvenanceMetadata Create(string[] args, int warmup, int iterations, string? corpusDirectory)
    {
        string? inputHash = null;
        try
        {
            if (corpusDirectory is not null && Directory.Exists(corpusDirectory))
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                var files = Directory.EnumerateFiles(corpusDirectory, "*.md", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray();
                foreach (var file in files)
                {
                    var bytes = File.ReadAllBytes(file);
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }

                sha.TransformFinalBlock([], 0, 0);
                inputHash = Convert.ToHexStringLower(sha.Hash!);
            }
        }
        catch
        {
            inputHash = null;
        }

        return new ProvenanceMetadata(
            CurrentCommit,
            CurrentSdkVersion,
            RuntimeInformation.OSDescription,
            $"{RuntimeInformation.OSArchitecture}/{RuntimeInformation.ProcessArchitecture}x{Environment.ProcessorCount}",
            new Dictionary<string, string>
            {
                ["TargetFramework"] = "net10.0",
                ["Runtime"] = RuntimeInformation.FrameworkDescription,
            },
            args.ToArray(),
            warmup,
            iterations,
            inputHash);
    }
}

internal static class EngineBenchmark
{
    public sealed record EngineResult
    {
        public int SchemaVersion { get; init; } = 5;
        public string Benchmark { get; init; } = "litho-engine-workloads";
        public int PageCount { get; init; }
        public IReadOnlyList<LithoEngineWorkload.LithoCorpusMeasurement> LithoCorpora { get; init; } = [];
        public EnvironmentMetadata Environment { get; init; } = new();
        public ProvenanceMetadata? Provenance { get; init; }
        public MeasurementScope? Scope { get; init; } = MeasurementScope.EngineDefault;
    }

    public static EngineResult Run(int pageCount, int warmup, int iterations, string[] args) => new()
    {
        PageCount = pageCount,
        LithoCorpora = LithoEngineWorkload.Measure(warmup, iterations),
        Environment = EnvironmentMetadata.Create(),
        Provenance = ProvenanceMetadata.Create(args, warmup, iterations, corpusDirectory: null),
    };
}
