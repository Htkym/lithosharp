namespace LithoSharp;

public sealed class ToolingCapabilityInfo
{
    public ToolingCapabilityInfo(string name, string maturity, string schemaVersion, string description, IReadOnlyList<string> scope)
    {
        Name = name;
        Maturity = maturity;
        SchemaVersion = schemaVersion;
        Description = description;
        Scope = scope;
    }

    public string Name { get; }

    public string Maturity { get; }

    public string SchemaVersion { get; }

    public string Description { get; }

    public IReadOnlyList<string> Scope { get; }
}

public sealed class ToolingContractInfo
{
    public ToolingContractInfo(string name, string maturity, string schemaVersion, string description)
    {
        Name = name;
        Maturity = maturity;
        SchemaVersion = schemaVersion;
        Description = description;
    }

    public string Name { get; }

    public string Maturity { get; }

    public string SchemaVersion { get; }

    public string Description { get; }
}

public sealed class ToolingIdentityInfo
{
    public ToolingIdentityInfo(string name, string version)
    {
        Name = name;
        Version = version;
    }

    public string Name { get; }

    public string Version { get; }
}

public sealed class ToolingProjectInfo
{
    public ToolingProjectInfo(bool resolved, string? coreVersion, string reason)
    {
        Resolved = resolved;
        CoreVersion = coreVersion;
        Reason = reason;
    }

    public bool Resolved { get; }

    public string? CoreVersion { get; }

    public string Reason { get; }
}

public sealed class ToolingCapabilitiesReport
{
    public ToolingCapabilitiesReport(
        string schemaVersion,
        bool success,
        int exitCode,
        ToolingIdentityInfo tool,
        ToolingIdentityInfo core,
        ToolingProjectInfo project,
        IReadOnlyList<ToolingContractInfo> contracts,
        IReadOnlyList<ToolingCapabilityInfo> capabilities,
        string? error = null)
    {
        SchemaVersion = schemaVersion;
        Success = success;
        ExitCode = exitCode;
        Tool = tool;
        Core = core;
        Project = project;
        Contracts = contracts;
        Capabilities = capabilities;
        Error = error;
    }

    public string SchemaVersion { get; }

    public bool Success { get; }

    public int ExitCode { get; }

    public ToolingIdentityInfo Tool { get; }

    public ToolingIdentityInfo Core { get; }

    public ToolingProjectInfo Project { get; }

    public IReadOnlyList<ToolingContractInfo> Contracts { get; }

    public IReadOnlyList<ToolingCapabilityInfo> Capabilities { get; }

    public string? Error { get; }
}

/// <summary>Detect support through advertised capability names, not versions or logs. Unknown names are ignored; missing required capabilities disable the corresponding feature with an explanation.</summary>
public static class ToolingCapabilities
{
    public const string DocumentInspection = "document-inspection";

    public const string VersionedSnapshot = "versioned-snapshot";

    public const string ServeShutdown = "serve-shutdown";

    public const string SourceRouteLookup = "source-route-lookup";

    public const string StageSyntax = "syntax";

    public const string StageFrontMatter = "front-matter";

    public const string StageProjectResolution = "project-resolution";

    public const string StageGeneratedOutput = "generated-output";

    public const string StageRuntime = "runtime";

    public const string LanguageMarkdown = "markdown";

    public const string LanguageMdx = "mdx";

    public static IReadOnlyList<ToolingCapability> All { get; } =
    [
        new ToolingCapability(
            DocumentInspection, ToolingContractMaturity.Stable, ToolingContracts.CurrentSchemaVersion,
            "Unsaved document inspection. The syntax and front matter stages run without a project; project-resolution, generated-output and runtime stages require an explicit project operation.",
            [LanguageMarkdown, LanguageMdx, StageSyntax, StageFrontMatter, StageProjectResolution, StageGeneratedOutput, StageRuntime]),
        new ToolingCapability(
            VersionedSnapshot, ToolingContractMaturity.Stable, ToolingContracts.CurrentSchemaVersion,
            "Inspection snapshots carry the document version and project generation so stale results are discarded instead of published.",
            [StageSyntax, StageProjectResolution]),
        new ToolingCapability(
            ServeShutdown, ToolingContractMaturity.Stable, ToolingContracts.CurrentSchemaVersion,
            "Structured shutdown over stdin for the development server. Duplicate, in-flight and mid-startup shutdown requests are idempotent.",
            []),
        new ToolingCapability(
            SourceRouteLookup, ToolingContractMaturity.Stable, ToolingContracts.CurrentSchemaVersion,
            "Source to route lookup that keeps project, collection and variant identity and returns every candidate route instead of guessing one.",
            [StageProjectResolution]),
    ];

    public static bool TryGet(string name, out ToolingCapability? capability)
    {
        ArgumentNullException.ThrowIfNull(name);
        capability = All.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        return capability is not null;
    }

    public static IReadOnlyList<string> Missing(IEnumerable<string> advertised, IEnumerable<string> required)
    {
        ArgumentNullException.ThrowIfNull(advertised);
        ArgumentNullException.ThrowIfNull(required);
        var available = new HashSet<string>(advertised.Where(static name => !string.IsNullOrWhiteSpace(name)), StringComparer.Ordinal);
        return [.. required.Where(name => !available.Contains(name))];
    }

    public static void Require(IEnumerable<string> advertised, IEnumerable<string> required)
    {
        var missing = Missing(advertised, required);
        if (missing.Count == 0) return;
        var supported = string.Join(", ", All.Select(static capability => capability.Name));
        throw new InvalidOperationException(
            $"The peer does not provide the required tooling capability '{string.Join("', '", missing)}'; "
            + $"disable the feature with this explanation instead of assuming it. Supported capabilities: {supported}.");
    }

    public static ToolingCapabilitiesReport CreateReport(
        ToolingIdentityInfo tool,
        ToolingIdentityInfo core,
        ToolingProjectInfo? project = null,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(core);
        return new ToolingCapabilitiesReport(
            ToolingContracts.CurrentSchemaVersion,
            error is null,
            error is null ? 0 : 1,
            tool,
            core,
            project ?? NotEvaluatedProject,
            [.. ToolingContracts.All.Select(static contract => new ToolingContractInfo(
                contract.Name, contract.Maturity.ToString(), contract.SchemaVersion, contract.Description))],
            [.. All.Select(static capability => new ToolingCapabilityInfo(
                capability.Name, capability.Maturity.ToString(), capability.SchemaVersion, capability.Description, capability.Scope))],
            error);
    }

    public static ToolingProjectInfo NotEvaluatedProject { get; } = new(
        false, null,
        "This command does not evaluate a project. Resolve project context with an explicit inspect or build operation.");
}
