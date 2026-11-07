using System.Text.Json;
using LithoSharp.Documentation;

namespace LithoSharp.Inspection;

/// <summary>Multiple routes for the same source remain separate candidates; no route is guessed.</summary>
public sealed class ProjectRouteCandidate
{
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
        if (!Enum.IsDefined(publication))
            throw new ArgumentOutOfRangeException(nameof(publication));
        SourcePath = sourcePath;
        PublicPath = publicPath;
        ProjectId = projectId;
        Collection = collection;
        Version = version;
        Locale = locale;
        Publication = publication;
    }

    public string SourcePath { get; }

    public string PublicPath { get; }

    public string ProjectId { get; }

    public string Collection { get; }

    public string Version { get; }

    public string Locale { get; }

    public DocumentPublicationState Publication { get; }
}

public enum DocumentProjectStatus
{
    /// <summary>Syntax-only; Route retains the caller-supplied value.</summary>
    NoContext = 0,
    Resolved = 1,
    /// <summary>Multiple candidates; Route is null.</summary>
    Ambiguous = 2,
    /// <summary>No candidate in the context; Route is null.</summary>
    Unresolved = 3,
    /// <summary>Draft content is unpublished; Route is null.</summary>
    Draft = 4,
}

/// <summary>Stores explicitly acquired results; construction does not run MSBuild or a factory.</summary>
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

    public string ProjectId { get; }

    public long ProjectGeneration { get; }

    public string CoreVersion { get; }

    public string Collection { get; }

    public string Language { get; }

    public string SchemaName { get; }

    public string Version { get; }

    public string Locale { get; }

    public IReadOnlyList<ProjectRouteCandidate> Routes { get; }

    public DateTimeOffset AcquiredAt { get; }

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
