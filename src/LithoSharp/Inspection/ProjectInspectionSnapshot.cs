using System.Text.Json;
using LithoSharp.Documentation;

namespace LithoSharp.Inspection;

/// <summary>project context内のsource→route候補の1件を表します。</summary>
/// <remarks>同sourceが複数公開routeを持つ場合は複数候補として返し、勝手に一件を選びません。</remarks>
public sealed class ProjectRouteCandidate
{
    /// <summary>候補を作成します。</summary>
    public ProjectRouteCandidate(
        string sourcePath,
        string publicPath,
        string projectId,
        string collection,
        string version,
        string locale,
        DocumentPublicationState publication)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        if (!Enum.IsDefined(publication)) throw new ArgumentOutOfRangeException(nameof(publication));
        SourcePath = sourcePath;
        PublicPath = publicPath;
        ProjectId = projectId;
        Collection = collection;
        Version = version;
        Locale = locale;
        Publication = publication;
    }

    /// <summary>variant入力内の相対source pathを取得します。</summary>
    public string SourcePath { get; }

    /// <summary>公開URLのpath部分を取得します。</summary>
    public string PublicPath { get; }

    /// <summary>project識別子を取得します。</summary>
    public string ProjectId { get; }

    /// <summary>collectionを取得します。</summary>
    public string Collection { get; }

    /// <summary>versionを取得します。</summary>
    public string Version { get; }

    /// <summary>localeを取得します。</summary>
    public string Locale { get; }

    /// <summary>公開状態を取得します。</summary>
    public DocumentPublicationState Publication { get; }
}

/// <summary>project context付き検査のroute解決状態を表します。</summary>
public enum DocumentProjectStatus
{
    /// <summary>contextなしの構文解析のみ。Routeは呼び出し側の指定をそのまま返します。</summary>
    NoContext = 0,
    /// <summary>候補が1件に定まりました。</summary>
    Resolved = 1,
    /// <summary>候補が複数あり、一件に定めません。Routeは <see langword="null"/> です。</summary>
    Ambiguous = 2,
    /// <summary>context内に候補がありません。Routeは <see langword="null"/> です。</summary>
    Unresolved = 3,
    /// <summary>front matterがdraftのため公開されません。Routeは <see langword="null"/> です。</summary>
    Draft = 4,
}

/// <summary>明示操作で取得したproject contextの不変snapshotを表します。</summary>
/// <remarks>
/// 実projectの評価（factory実行やMSBuild）はこの型を作りません。取得済みの結果だけを保持し、
/// 未保存解析へ入力として渡します。contextがなければ構文解析のみとし、不足を成功として隠しません。
/// </remarks>
public sealed class ProjectInspectionSnapshot
{
    internal ProjectInspectionSnapshot(
        string projectId,
        long projectGeneration,
        string coreVersion,
        string collection,
        string language,
        string schemaName,
        string version,
        string locale,
        IReadOnlyList<ProjectRouteCandidate> routes,
        DateTimeOffset acquiredAt)
    {
        ProjectId = projectId;
        ProjectGeneration = projectGeneration;
        CoreVersion = coreVersion;
        Collection = collection;
        Language = language;
        SchemaName = schemaName;
        Version = version;
        Locale = locale;
        var routeSnapshot = routes.ToArray();
        if (routeSnapshot.Any(route => !string.Equals(route.ProjectId, projectId, StringComparison.Ordinal)))
            throw new ArgumentException("Every route candidate must belong to the snapshot project.", nameof(routes));
        Routes = Array.AsReadOnly(routeSnapshot);
        AcquiredAt = acquiredAt;
    }

    /// <summary>project識別子を取得します。</summary>
    public string ProjectId { get; }

    /// <summary>project generationを取得します。</summary>
    public long ProjectGeneration { get; }

    /// <summary>snapshot取得時のCore versionを取得します。</summary>
    public string CoreVersion { get; }

    /// <summary>対象のcollectionを取得します。</summary>
    public string Collection { get; }

    /// <summary>対象の言語（markdown等）を取得します。</summary>
    public string Language { get; }

    /// <summary>front matter schema名を取得します。組み込みは document/post、利用者定義は型名です。</summary>
    public string SchemaName { get; }

    /// <summary>対象のversionを取得します。</summary>
    public string Version { get; }

    /// <summary>対象のlocaleを取得します。</summary>
    public string Locale { get; }

    /// <summary>公開route候補を取得します。下書き・未buildは含みません。</summary>
    public IReadOnlyList<ProjectRouteCandidate> Routes { get; }

    /// <summary>取得時点を取得します。</summary>
    public DateTimeOffset AcquiredAt { get; }

    /// <summary>snapshotを作成します。</summary>
    public static ProjectInspectionSnapshot Create(
        string projectId,
        long projectGeneration,
        string collection,
        string language,
        string schemaName,
        string version,
        string locale,
        IEnumerable<ProjectRouteCandidate>? routes = null,
        DateTimeOffset? acquiredAt = null,
        string? coreVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        var routeSnapshot = routes?.ToArray() ?? [];
        if (routeSnapshot.Any(static route => route is null))
            throw new ArgumentException("Project route candidates must not contain null.", nameof(routes));
        return new ProjectInspectionSnapshot(
            projectId,
            projectGeneration,
            coreVersion ?? typeof(ProjectInspectionSnapshot).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            collection,
            language,
            schemaName,
            version,
            locale,
            routeSnapshot,
            acquiredAt ?? DateTimeOffset.UtcNow);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>snapshotをJSONにします。未解決fieldはnullで表します。</summary>
    public string ToJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = ToolingContracts.CurrentSchemaVersion,
        projectId = ProjectId,
        projectGeneration = ProjectGeneration,
        coreVersion = CoreVersion,
        collection = Collection,
        language = Language,
        schema = SchemaName,
        version = Version,
        locale = Locale,
        acquiredAt = AcquiredAt,
        routes = Routes.Select(route => new
        {
            sourcePath = route.SourcePath,
            publicPath = route.PublicPath,
            projectId = route.ProjectId,
            collection = route.Collection,
            version = route.Version,
            locale = route.Locale,
            publication = route.Publication.ToString(),
        }).ToArray(),
    }, JsonOptions);

    /// <summary>JSONからsnapshotを復元します。</summary>
    /// <exception cref="ArgumentException">JSONがsnapshotの形ではありません。</exception>
    public static ProjectInspectionSnapshot ParseJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The project snapshot is not valid JSON: " + exception.Message, nameof(json));
        }

        using (document)
        {
            var root = document.RootElement;
            string Required(string name)
            {
                if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(value.GetString()))
                {
                    throw new ArgumentException($"The project snapshot omits '{name}'.", nameof(json));
                }

                return value.GetString()!;
            }

            string schemaVersion =
                root.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.String
                    ? schema.GetString() ?? ""
                    : throw new ArgumentException("The project snapshot omits 'schemaVersion'.", nameof(json));
            if (!ToolingCompatibility.IsCompatible(schemaVersion))
            {
                throw new ArgumentException(
                    $"Unsupported project snapshot schema version '{schemaVersion}'; this client supports schema version '{ToolingContracts.CurrentSchemaVersion}'.",
                    nameof(json));
            }

            var routes = new List<ProjectRouteCandidate>();
            if (!root.TryGetProperty("routes", out var routesValue) || routesValue.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("The project snapshot omits the 'routes' array.", nameof(json));
            }

            foreach (var item in routesValue.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("Each project snapshot route must be an object.", nameof(json));

                string Field(string name) =>
                    item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString() ?? ""
                        : throw new ArgumentException($"The project snapshot route omits '{name}'.", nameof(json));
                var publication = DocumentPublicationState.Published;
                if (item.TryGetProperty("publication", out var publicationValue)
                    && (publicationValue.ValueKind != JsonValueKind.String
                        || !Enum.TryParse(publicationValue.GetString(), ignoreCase: false, out publication)
                        || !Enum.IsDefined(publication)))
                {
                    throw new ArgumentException("The project snapshot route has an unknown 'publication'.", nameof(json));
                }

                routes.Add(new ProjectRouteCandidate(
                    Field("sourcePath"), Field("publicPath"), Field("projectId"),
                    Field("collection"), Field("version"), Field("locale"), publication));
            }

            var generation = root.TryGetProperty("projectGeneration", out var generationValue)
                && generationValue.ValueKind == JsonValueKind.Number
                && generationValue.TryGetInt64(out var parsed)
                ? parsed
                : throw new ArgumentException("The project snapshot omits 'projectGeneration'.", nameof(json));
            var acquiredAt = root.TryGetProperty("acquiredAt", out var acquiredValue)
                && acquiredValue.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(acquiredValue.GetString(), out var timestamp)
                ? timestamp
                : DateTimeOffset.UtcNow;

            return new ProjectInspectionSnapshot(
                Required("projectId"),
                generation,
                root.TryGetProperty("coreVersion", out var core) && core.ValueKind == JsonValueKind.String
                    ? core.GetString() ?? "unknown" : "unknown",
                Required("collection"),
                root.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String
                    ? language.GetString() ?? "markdown" : "markdown",
                root.TryGetProperty("schema", out var schemaName) && schemaName.ValueKind == JsonValueKind.String
                    ? schemaName.GetString() ?? "document" : "document",
                Required("version"),
                Required("locale"),
                routes,
                acquiredAt);
        }
    }
}
