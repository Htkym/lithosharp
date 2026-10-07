namespace LithoSharp;

/// <summary>1つのcapabilityの適用範囲を表します。</summary>
/// <remarks>
/// <see cref="Scope"/> は、そのcapabilityが対象にする言語・解析段階・操作の名前です。
/// 未知のscope値は利用側で無視し、既知のscopeだけで機能判定します。
/// </remarks>
public sealed class ToolingCapabilityInfo
{
    /// <summary>wire表現を作成します。</summary>
    public ToolingCapabilityInfo(string name, string maturity, string schemaVersion, string description, IReadOnlyList<string> scope)
    {
        Name = name;
        Maturity = maturity;
        SchemaVersion = schemaVersion;
        Description = description;
        Scope = scope;
    }

    /// <summary>capability名を取得します。</summary>
    public string Name { get; }

    /// <summary>成熟度を取得します。</summary>
    public string Maturity { get; }

    /// <summary>schema versionを取得します。</summary>
    public string SchemaVersion { get; }

    /// <summary>説明を取得します。</summary>
    public string Description { get; }

    /// <summary>対象にする言語・解析段階・操作の名前を取得します。</summary>
    public IReadOnlyList<string> Scope { get; }
}

/// <summary>1つのTooling契約のwire表現を表します。</summary>
public sealed class ToolingContractInfo
{
    /// <summary>wire表現を作成します。</summary>
    public ToolingContractInfo(string name, string maturity, string schemaVersion, string description)
    {
        Name = name;
        Maturity = maturity;
        SchemaVersion = schemaVersion;
        Description = description;
    }

    /// <summary>契約名を取得します。</summary>
    public string Name { get; }

    /// <summary>成熟度を取得します。</summary>
    public string Maturity { get; }

    /// <summary>schema versionを取得します。</summary>
    public string SchemaVersion { get; }

    /// <summary>説明を取得します。</summary>
    public string Description { get; }
}

/// <summary>tool・Core・Language Serverなどの実装identityを表します。</summary>
/// <remarks>
/// toolに同梱されたCore、projectが参照するCore、Language Serverに同梱されたCoreは
/// 別のrecordとして扱います。未解決の値を別のidentityで埋めてはいけません。
/// </remarks>
public sealed class ToolingIdentityInfo
{
    /// <summary>identityを作成します。</summary>
    public ToolingIdentityInfo(string name, string version)
    {
        Name = name;
        Version = version;
    }

    /// <summary>実装名を取得します。</summary>
    public string Name { get; }

    /// <summary>実装versionを取得します。</summary>
    public string Version { get; }
}

/// <summary>project contextの解決状態を表します。</summary>
public sealed class ToolingProjectInfo
{
    /// <summary>解決状態を作成します。</summary>
    /// <param name="resolved">project contextを解決済みかどうか。</param>
    /// <param name="coreVersion">解決済みの場合のproject参照Core version。未解決では <see langword="null"/>。</param>
    /// <param name="reason">未解決の理由、または解決方法の説明。</param>
    public ToolingProjectInfo(bool resolved, string? coreVersion, string reason)
    {
        Resolved = resolved;
        CoreVersion = coreVersion;
        Reason = reason;
    }

    /// <summary>project contextを解決済みかどうかを取得します。</summary>
    public bool Resolved { get; }

    /// <summary>project参照Core versionを取得します。未解決では <see langword="null"/> です。</summary>
    public string? CoreVersion { get; }

    /// <summary>未解決の理由、または解決方法の説明を取得します。</summary>
    public string Reason { get; }
}

/// <summary>capability commandのwire契約を表します。</summary>
/// <remarks>
/// 1.xでは加算的にfieldを追加します。未知fieldは無視し、未知のcapability名は
/// 存在しないものとして扱います。schema versionはmajor一致で互換です。
/// </remarks>
public sealed class ToolingCapabilitiesReport
{
    /// <summary>reportを作成します。</summary>
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

    /// <summary>reportのschema versionを取得します。</summary>
    public string SchemaVersion { get; }

    /// <summary>成功したかどうかを取得します。</summary>
    public bool Success { get; }

    /// <summary>終了codeを取得します。</summary>
    public int ExitCode { get; }

    /// <summary>toolのidentityを取得します。</summary>
    public ToolingIdentityInfo Tool { get; }

    /// <summary>toolに同梱されたCoreのidentityを取得します。</summary>
    public ToolingIdentityInfo Core { get; }

    /// <summary>project contextの解決状態を取得します。</summary>
    public ToolingProjectInfo Project { get; }

    /// <summary>既知のTooling契約を取得します。</summary>
    public IReadOnlyList<ToolingContractInfo> Contracts { get; }

    /// <summary>提供するcapabilityを取得します。</summary>
    public IReadOnlyList<ToolingCapabilityInfo> Capabilities { get; }

    /// <summary>失敗時の説明を取得します。成功時は <see langword="null"/> です。</summary>
    public string? Error { get; }
}

/// <summary>1.xで提供する機能capabilityの一覧を表します。</summary>
/// <remarks>
/// capabilityは「機能の存在」をversion文字列や人間向けログの推測なしに判定するための契約です。
/// 必須capabilityが無い場合、利用側はその機能を説明付きで無効化します（
/// <see cref="Missing"/> と <see cref="Require"/> を参照）。未知のcapability名は無視します。
/// </remarks>
public static class ToolingCapabilities
{
    /// <summary>未保存文書のinspection。</summary>
    public const string DocumentInspection = "document-inspection";

    /// <summary>文書versionとproject generationを持つsnapshot。</summary>
    public const string VersionedSnapshot = "versioned-snapshot";

    /// <summary>stdin経由の構造化serve停止。</summary>
    public const string ServeShutdown = "serve-shutdown";

    /// <summary>sourceからrouteへの取得。</summary>
    public const string SourceRouteLookup = "source-route-lookup";

    /// <summary>構文解析段階。</summary>
    public const string StageSyntax = "syntax";

    /// <summary>front matter段階。</summary>
    public const string StageFrontMatter = "front-matter";

    /// <summary>project解決段階。</summary>
    public const string StageProjectResolution = "project-resolution";

    /// <summary>生成出力段階。</summary>
    public const string StageGeneratedOutput = "generated-output";

    /// <summary>実行時段階。</summary>
    public const string StageRuntime = "runtime";

    /// <summary>Markdown言語。</summary>
    public const string LanguageMarkdown = "markdown";

    /// <summary>MDX言語。</summary>
    public const string LanguageMdx = "mdx";

    /// <summary>全capabilityを取得します。</summary>
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

    /// <summary>名前からcapabilityを取得します。</summary>
    /// <param name="name">capability名。</param>
    /// <param name="capability">見つかったcapability。</param>
    /// <returns>既知のcapabilityの場合に <see langword="true"/> を返します。</returns>
    public static bool TryGet(string name, out ToolingCapability? capability)
    {
        ArgumentNullException.ThrowIfNull(name);
        capability = All.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        return capability is not null;
    }

    /// <summary>必須capabilityのうち、通知されていないものを返します。</summary>
    /// <param name="advertised">相手が通知したcapability名。未知の名前は無視します。</param>
    /// <param name="required">自前の機能が必要とするcapability名。</param>
    /// <returns>欠落している必須capability名。順序は <paramref name="required"/> の順です。</returns>
    public static IReadOnlyList<string> Missing(IEnumerable<string> advertised, IEnumerable<string> required)
    {
        ArgumentNullException.ThrowIfNull(advertised);
        ArgumentNullException.ThrowIfNull(required);
        var available = new HashSet<string>(advertised.Where(static name => !string.IsNullOrWhiteSpace(name)), StringComparer.Ordinal);
        return [.. required.Where(name => !available.Contains(name))];
    }

    /// <summary>必須capabilityを検証し、欠落があれば説明付きで失敗します。</summary>
    /// <param name="advertised">相手が通知したcapability名。</param>
    /// <param name="required">自前の機能が必要とするcapability名。</param>
    /// <exception cref="InvalidOperationException">必須capabilityが欠落しています。</exception>
    public static void Require(IEnumerable<string> advertised, IEnumerable<string> required)
    {
        var missing = Missing(advertised, required);
        if (missing.Count == 0) return;
        var supported = string.Join(", ", All.Select(static capability => capability.Name));
        throw new InvalidOperationException(
            $"The peer does not provide the required tooling capability '{string.Join("', '", missing)}'; "
            + $"disable the feature with this explanation instead of assuming it. Supported capabilities: {supported}.");
    }

    /// <summary>capability commandのreportを作成します。</summary>
    /// <param name="tool">toolのidentity。</param>
    /// <param name="core">toolに同梱されたCoreのidentity。</param>
    /// <param name="project">project contextの解決状態。省略時は未評価です。</param>
    /// <param name="error">失敗時の説明。成功時は <see langword="null"/>。</param>
    /// <returns>wire契約としてのreport。</returns>
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

    /// <summary>project contextを評価していないことを表します。</summary>
    /// <remarks>未解決のproject versionをtoolやCoreのversionで埋めてはいけません。</remarks>
    public static ToolingProjectInfo NotEvaluatedProject { get; } = new(
        false, null,
        "This command does not evaluate a project. Resolve project context with an explicit inspect or build operation.");
}
