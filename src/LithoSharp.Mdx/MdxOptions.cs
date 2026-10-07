namespace LithoSharp.Mdx;

/// <summary>Explicit build-only MDX execution and dependency settings.</summary>
public sealed record MdxOptions
{
    /// <summary>Creates settings for a project and a restored worker installation.</summary>
    public MdxOptions(string projectDirectory, string? workerDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ProjectDirectory = Path.GetFullPath(projectDirectory);
        WorkerDirectory = Path.GetFullPath(workerDirectory ?? Path.Combine(Path.GetDirectoryName(typeof(MdxOptions).Assembly.Location)!, "worker"));
    }
    /// <summary>The root containing trusted source and its node_modules.</summary>
    public string ProjectDirectory { get; }
    /// <summary>The worker directory containing worker.mjs and its explicitly restored dependencies.</summary>
    public string WorkerDirectory { get; }
    /// <summary>The Node executable. It is never installed or downloaded during build.</summary>
    public string NodeExecutable { get; init; } = "node";
    /// <summary>Maximum time for one worker request, including startup.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>Maximum UTF-8 message size in bytes.</summary>
    public int MaximumMessageBytes { get; init; } = 128 * 1024 * 1024;
    /// <summary>Enables reuse only when all site code depends on declared inputs and fixed public data.</summary>
    public bool Cacheable { get; init; }
    /// <summary>Explicit environment values available to trusted worker code; never sent to browsers.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    /// <summary>Additional source inputs used by code that reads files dynamically.</summary>
    public IReadOnlyList<string> DeclaredInputFiles { get; init; } = [];
    /// <summary>Explicit Remark or Rehype modules, in execution order. Their dynamic file inputs must be declared.</summary>
    public IReadOnlyList<MdxPlugin> Plugins { get; init; } = [];
    /// <summary>An optional project-relative module exporting component overrides. Defaults remain available for wrapping.</summary>
    public string? ComponentsModule { get; init; }
    /// <summary>Either page for compatibility or selective for explicit islands and static pages.</summary>
    public string Hydration { get; init; } = "page";
    /// <summary>Component names explicitly known to produce static output without browser behavior.</summary>
    public IReadOnlyList<string> StaticComponents { get; init; } = [];
    /// <summary>Explicit public URLs for xref links, keyed by exact API or document identity.</summary>
    public IReadOnlyDictionary<string, SiteUrl> CrossReferences { get; init; } = new Dictionary<string, SiteUrl>();
}

/// <summary>An explicitly trusted compiler plugin and its serializable configuration.</summary>
public sealed record MdxPlugin(string Stage, string Module, System.Text.Json.JsonElement Options);

/// <summary>Measured worker work for the most recent successful preparation.</summary>
public sealed record MdxBuildMetrics
{
    /// <summary>The number of Node workers started for this preparation.</summary>
    public int WorkerStarts { get; init; }
    /// <summary>Worker elapsed time for this preparation; zero for a bridge cache hit.</summary>
    public double WorkerMilliseconds { get; init; }
    /// <summary>Server bundling time, including MDX compilation.</summary>
    public double ServerBundleMilliseconds { get; init; }
    /// <summary>Server module loading and React rendering time.</summary>
    public double RenderMilliseconds { get; init; }
    /// <summary>Browser bundling time.</summary>
    public double BrowserBundleMilliseconds { get; init; }
    /// <summary>Node's heap-used snapshot after compilation; zero when no worker response was needed.</summary>
    public long NodeHeapUsedBytes { get; init; }
    /// <summary>The number of MDX modules compiled.</summary>
    public int CompiledModules { get; init; }
    /// <summary>The number of pages rendered by React.</summary>
    public int RenderedPages { get; init; }
    /// <summary>The number of browser entries bundled.</summary>
    public int BundledPages { get; init; }
    /// <summary>The number of interactive entries processed by browser bundling in this worker request; zero when a validated browser cache avoids bundling.</summary>
    public int RebundledPages { get; init; }
    /// <summary>The page ids counted by <see cref="RebundledPages"/>; empty on a cache hit.</summary>
    public IReadOnlyList<string> RebundledPageIds { get; init; } = [];
    /// <summary>Whether a validated preparation cache avoided worker execution.</summary>
    public bool CacheHit { get; init; }
    /// <summary>Observed work across all worker attempts, including validation retries and partial failures.</summary>
    [System.Text.Json.Serialization.JsonInclude]
    internal MdxWorkerWorkMetrics Work { get; init; } = new();
}
/// <summary>Observed worker work across every request attempted by one preparation.</summary>
/// <remarks>Failure reports may be partial. These counts are separate from the final-success metrics and unique page inventory.</remarks>
internal sealed record MdxWorkerWorkMetrics
{
    /// <summary>Bridge SendAsync attempts, including startup, validation retries and transport failures.</summary>
    public int RequestAttempts { get; init; }
    /// <summary>Workers actually started during this preparation, including failed requests.</summary>
    public int WorkerStarts { get; init; }
    /// <summary>Bridge wall time spent awaiting all requests, including startup and cleanup.</summary>
    public double RequestMilliseconds { get; init; }
    /// <summary>True only if every attempted request supplied a complete compiler-work report. This does not imply build success.</summary>
    public bool HasCompleteReports { get; init; } = true;
    /// <summary>Reported completed MDX compilations, summed across requests; a failure report may omit unfinished work.</summary>
    public long CompiledModules { get; init; }
    /// <summary>Reported completed React renders, summed across requests; a failure report may omit unfinished work.</summary>
    public long RenderedPages { get; init; }
    /// <summary>Reported MDX compiler calls, including calls that failed.</summary>
    public long MdxCompileInvocations { get; init; }
    /// <summary>Reported React renderer calls, including calls that failed.</summary>
    public long RenderInvocations { get; init; }
    /// <summary>Reported esbuild API calls for all platforms, including failed or exploratory calls.</summary>
    public long EsbuildInvocations { get; init; }
    /// <summary>Reported browser-platform esbuild calls, including page, shared-runtime and live-runtime builds.</summary>
    public long BrowserBuildInvocations { get; init; }
    /// <summary>Reported interactive page entry submissions to browser builds; repeated submissions count again.</summary>
    public long BrowserEntryBuildAttempts { get; init; }
    /// <summary>Reported compiler elapsed time across requests, excluding worker protocol/startup/cleanup.</summary>
    public double WorkerMilliseconds { get; init; }
    /// <summary>Reported server-build elapsed time, including nested live-runtime work; failures may report only settled work.</summary>
    public double ServerBundleMilliseconds { get; init; }
    /// <summary>Reported shared-runtime/page browser-build elapsed time; nested live runtime is reported separately.</summary>
    public double BrowserBundleMilliseconds { get; init; }
    /// <summary>Reported nested live-runtime build elapsed time, which overlaps its containing server/browser phase.</summary>
    public double LiveRuntimeMilliseconds { get; init; }
    /// <summary>Reported module loading and rendering elapsed time across requests, including failed render phases.</summary>
    public double RenderMilliseconds { get; init; }
    /// <summary>Reported compiler-plugin bundle elapsed time across requests.</summary>
    public double PluginBundleMilliseconds { get; init; }
}
