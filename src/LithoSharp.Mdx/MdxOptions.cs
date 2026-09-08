namespace LithoSharp.Mdx;

/// <summary>Explicit build-only MDX execution and dependency settings.</summary>
public sealed record MdxOptions
{
    /// <summary>Creates settings for a project and a restored worker installation.</summary>
    public MdxOptions(string projectDirectory, string? workerDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ProjectDirectory = Path.GetFullPath(projectDirectory);
        WorkerDirectory = Path.GetFullPath(workerDirectory ?? Path.Combine(AppContext.BaseDirectory, "worker"));
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
}

/// <summary>An explicitly trusted compiler plugin and its serializable configuration.</summary>
public sealed record MdxPlugin(string Stage, string Module, System.Text.Json.JsonElement Options);

/// <summary>Measured worker work for the most recent successful preparation.</summary>
public sealed record MdxBuildMetrics
{
    /// <summary>The number of MDX modules compiled.</summary>
    public int CompiledModules { get; init; }
    /// <summary>The number of pages rendered by React.</summary>
    public int RenderedPages { get; init; }
    /// <summary>The number of browser entries bundled.</summary>
    public int BundledPages { get; init; }
    /// <summary>Whether a validated preparation cache avoided worker execution.</summary>
    public bool CacheHit { get; init; }
}
