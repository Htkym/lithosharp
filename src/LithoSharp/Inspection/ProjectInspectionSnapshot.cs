using System.Text.Json;
using LithoSharp.Content;
using LithoSharp.Documentation;
using LithoSharp.OpenKnowledge;

namespace LithoSharp.Inspection;

/// <summary>Static input classification acquired with the project; it makes no route or reference-closure claim.</summary>
public sealed class ProjectInputMetadata
{
    public ProjectInputMetadata(string sourcePath, ContentInputProfile inputProfile, ContentInputKind inputKind,
        string? inputRoot = null, string? inputPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!Enum.IsDefined(inputProfile))
            throw new ArgumentOutOfRangeException(nameof(inputProfile));
        if (!Enum.IsDefined(inputKind))
            throw new ArgumentOutOfRangeException(nameof(inputKind));
        if ((inputProfile == ContentInputProfile.Markdown) != (inputKind == ContentInputKind.Markdown))
            throw new ArgumentException("The input kind must belong to its declared input profile.", nameof(inputKind));
        (SourcePath, InputProfile, InputKind, InputRoot, InputPath) = (sourcePath, inputProfile, inputKind, inputRoot, inputPath);
    }
    public string SourcePath { get; }
    public ContentInputProfile InputProfile { get; }
    public ContentInputKind InputKind { get; }
    public string? InputRoot { get; }
    public string? InputPath { get; }
}

/// <summary>A captured OKF relation from the bundle reader, attached to its existing acquired source route.</summary>
public sealed class ProjectReferenceRelation
{
    public ProjectReferenceRelation(string? target, string? targetSourcePath, OkfLinkKind kind, bool isImage)
    {
        if (targetSourcePath is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(targetSourcePath);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        (Target, TargetSourcePath, Kind, IsImage) = (target, targetSourcePath, kind, isImage);
    }
    public string? Target { get; }
    public string? TargetSourcePath { get; }
    public OkfLinkKind Kind { get; }
    public bool IsImage { get; }
}

/// <summary>Multiple routes for the same source remain separate candidates; no route is guessed.</summary>
public sealed class ProjectRouteCandidate
{
    private ContentInputProfile inputProfile = ContentInputProfile.Markdown;
    private ContentInputKind inputKind = ContentInputKind.Markdown;
    private IReadOnlyList<ProjectReferenceRelation> relations = [];
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

    /// <summary>The input policy explicitly captured during project acquisition.</summary>
    public ContentInputProfile InputProfile
    {
        get => inputProfile;
        init => inputProfile = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }
    public ContentInputKind InputKind
    {
        get => inputKind;
        init => inputKind = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }
    public string? InputRoot { get; init; }
    public string? InputPath { get; init; }
    public IReadOnlyList<ProjectReferenceRelation> Relations
    {
        get => relations;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var snapshot = value.ToArray();
            if (snapshot.Any(static relation => relation is null))
                throw new ArgumentException("Captured relations must not contain null.", nameof(value));
            relations = Array.AsReadOnly(snapshot);
        }
    }
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
        DateTimeOffset acquiredAt,
        string? projectHash = null,
        IReadOnlyList<FrontMatterSchemaSnapshot>? schemas = null,
        string markdownProfileId = MarkdownOptions.LegacyProfile,
        IReadOnlyList<MarkdownComponentSchema>? markdownComponents = null,
        DocumentSemanticSnapshot? semantics = null,
        IReadOnlyDictionary<string, DocumentSemanticRuleSet>? semanticRules = null,
        IReadOnlyList<ProjectInputMetadata>? inputMetadata = null)
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
        var classifications = inputMetadata?.ToArray() ?? routeSnapshot.Select(route => new ProjectInputMetadata(
            route.SourcePath, route.InputProfile, route.InputKind, route.InputRoot, route.InputPath)).ToArray();
        if (classifications.Any(static input => input is null))
            throw new ArgumentException("Input metadata must not contain null.", nameof(inputMetadata));
        foreach (var route in routeSnapshot)
            if (!classifications.Any(input => input.SourcePath == route.SourcePath && input.InputProfile == route.InputProfile
                && input.InputKind == route.InputKind && input.InputRoot == route.InputRoot && input.InputPath == route.InputPath))
                throw new ArgumentException("Route and static input metadata must come from the same acquired classification.", nameof(inputMetadata));
        InputMetadata = Array.AsReadOnly(classifications);
        AcquiredAt = acquiredAt;
        ProjectHash = projectHash;
        if (projectHash is not null && !FrontMatterSchemaSnapshot.IsHash(projectHash))
            throw new ArgumentException("Invalid project acquisition hash.", nameof(projectHash));
        new MarkdownOptions { ProfileId = markdownProfileId }.Validate();
        MarkdownProfileId = markdownProfileId;
        var schemaSnapshot = schemas?.ToArray() ?? [];
        if (schemaSnapshot.Any(schema => schema is null || schema.ProjectId != projectId
            || schema.ProjectGeneration != projectGeneration || schema.ProjectHash != projectHash))
            throw new ArgumentException("Every schema must match the acquired project identity, generation and hash.", nameof(schemas));
        Schemas = Array.AsReadOnly(schemaSnapshot);
        var componentSnapshot = markdownComponents?.ToArray() ?? [];
        if (componentSnapshot.Any(component => component is null)
            || componentSnapshot.Select(component => component.Name).Distinct(StringComparer.Ordinal).Count() != componentSnapshot.Length
            || componentSnapshot.Length != 0 && (markdownProfileId != MarkdownOptions.DocumentationProfile || projectHash is null))
            throw new ArgumentException("Component schemas require a unique, acquired documentation-profile registration set.", nameof(markdownComponents));
        MarkdownComponents = Array.AsReadOnly(componentSnapshot);
        Semantics = semantics;
        SemanticRules = new System.Collections.ObjectModel.ReadOnlyDictionary<string, DocumentSemanticRuleSet>(
            (semanticRules ?? new Dictionary<string, DocumentSemanticRuleSet>()).ToDictionary(pair =>
                string.IsNullOrWhiteSpace(pair.Key) ? throw new ArgumentException("Semantic rule collections must be named.", nameof(semanticRules)) : pair.Key.Normalize(),
                pair => pair.Value ?? throw new ArgumentException("Semantic rule sets must not be null.", nameof(semanticRules)), StringComparer.Ordinal));
        if ((Semantics is not null || SemanticRules.Count != 0) && projectHash is null)
            throw new ArgumentException("Semantic context requires the project acquisition hash.", nameof(projectHash));
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

    public IReadOnlyList<ProjectInputMetadata> InputMetadata { get; }

    public DateTimeOffset AcquiredAt { get; }

    public string? ProjectHash { get; }

    public IReadOnlyList<FrontMatterSchemaSnapshot> Schemas { get; }

    public string MarkdownProfileId { get; }

    public IReadOnlyList<MarkdownComponentSchema> MarkdownComponents { get; }

    public DocumentSemanticSnapshot? Semantics { get; }

    public IReadOnlyDictionary<string, DocumentSemanticRuleSet> SemanticRules { get; }

    /// <summary>Captures the profile selected by an explicit project operation.</summary>
    public ProjectInspectionSnapshot WithMarkdownProfile(string profileId) =>
        new(ProjectId, ProjectGeneration, CoreVersion, Collection, Language, SchemaName, Version, Locale,
            Routes, AcquiredAt, ProjectHash, Schemas, profileId, MarkdownComponents, Semantics, SemanticRules, InputMetadata);

    /// <summary>Attaches component metadata without loading component or binder instances.</summary>
    public ProjectInspectionSnapshot WithMarkdownComponents(string projectHash, IEnumerable<MarkdownComponentSchema> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return new ProjectInspectionSnapshot(ProjectId, ProjectGeneration, CoreVersion, Collection, Language, SchemaName,
            Version, Locale, Routes, AcquiredAt, projectHash, Schemas, MarkdownProfileId, components.ToArray(), Semantics, SemanticRules, InputMetadata);
    }

    /// <summary>Attaches declarative rules and captured catalog evidence from the same explicit acquisition.</summary>
    public ProjectInspectionSnapshot WithSemantics(string projectHash, DocumentSemanticSnapshot? semantics,
        IReadOnlyDictionary<string, DocumentSemanticRuleSet> rules) =>
        new(ProjectId, ProjectGeneration, CoreVersion, Collection, Language, SchemaName, Version, Locale,
            Routes, AcquiredAt, projectHash, Schemas, MarkdownProfileId, MarkdownComponents, semantics, rules, InputMetadata);

    /// <summary>Attaches static schemas acquired by the same explicit operation. Replaces the immutable project snapshot.</summary>
    public ProjectInspectionSnapshot WithSchemas(string projectHash, IEnumerable<FrontMatterSchemaSnapshot> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        if (!FrontMatterSchemaSnapshot.IsHash(projectHash))
            throw new ArgumentException("A sha256 project acquisition hash is required.", nameof(projectHash));
        return new ProjectInspectionSnapshot(ProjectId, ProjectGeneration, CoreVersion, Collection, Language,
            SchemaName, Version, Locale, Routes, AcquiredAt, projectHash, schemas.ToArray(), MarkdownProfileId,
            MarkdownComponents, Semantics, SemanticRules, InputMetadata);
    }

    internal FrontMatterSchemaSnapshot? SchemaFor(string sourcePath)
    {
        if (!Path.IsPathRooted(sourcePath))
            return null;
        var matches = Schemas.Where(schema => schema.Contains(sourcePath)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal ProjectInputMetadata? InputFor(string sourcePath)
    {
        var key = sourcePath.Replace('\\', '/').Normalize();
        var matches = InputMetadata.Where(input => string.Equals(input.SourcePath.Replace('\\', '/').Normalize(), key,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .DistinctBy(static input => (input.InputProfile, input.InputKind, input.InputRoot, input.InputPath)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal ProjectInspectionSnapshot ForScope(string collection, string version, string locale) =>
        new(ProjectId, ProjectGeneration, CoreVersion, collection, Language, SchemaName, version, locale,
            Routes, AcquiredAt, ProjectHash, Schemas, MarkdownProfileId, MarkdownComponents, Semantics, SemanticRules, InputMetadata);

    internal ProjectInspectionSnapshot WithoutStaticMetadata() =>
        new(ProjectId, ProjectGeneration, CoreVersion, Collection, Language, SchemaName, Version, Locale,
            Routes, AcquiredAt, markdownProfileId: MarkdownProfileId, inputMetadata: InputMetadata);

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
        projectHash = ProjectHash,
        schemas = Schemas.Select(schema => schema.JsonValue()).ToArray(),
        markdownProfileId = MarkdownProfileId,
        markdownComponents = MarkdownComponents.Select(component => new
        {
            name = component.Name,
            propsTypeName = component.PropsTypeName,
            schemaJson = component.SchemaJson,
            fingerprint = component.Fingerprint,
            semanticRulesJson = component.SemanticRulesJson,
        }).ToArray(),
        semanticSnapshot = Semantics is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(Semantics.ToJson()),
        semanticRules = SemanticRules.ToDictionary(pair => pair.Key, pair => JsonSerializer.Deserialize<JsonElement>(pair.Value.ToJson()), StringComparer.Ordinal),
        inputMetadata = InputMetadata.Select(input => new
        {
            sourcePath = input.SourcePath,
            inputProfile = input.InputProfile.ToString(),
            inputKind = input.InputKind.ToString(),
            inputRoot = input.InputRoot,
            inputPath = input.InputPath,
        }).ToArray(),
        routes = Routes.Select(route => new
        {
            sourcePath = route.SourcePath,
            publicPath = route.PublicPath,
            projectId = route.ProjectId,
            collection = route.Collection,
            version = route.Version,
            locale = route.Locale,
            publication = route.Publication.ToString(),
            inputProfile = route.InputProfile.ToString(),
            inputKind = route.InputKind.ToString(),
            inputRoot = route.InputRoot,
            inputPath = route.InputPath,
            relations = route.Relations.Select(relation => new
            {
                target = relation.Target,
                targetSourcePath = relation.TargetSourcePath,
                kind = relation.Kind.ToString(),
                isImage = relation.IsImage,
            }).ToArray(),
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
            if (root.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("The project snapshot must be an object.", nameof(json));
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

                var inputProfile = ContentInputProfile.Markdown;
                if (item.TryGetProperty("inputProfile", out var inputProfileValue)
                    && (inputProfileValue.ValueKind != JsonValueKind.String
                        || !Enum.TryParse(inputProfileValue.GetString(), false, out inputProfile) || !Enum.IsDefined(inputProfile)))
                    throw new ArgumentException("The project snapshot route has an unknown input profile.", nameof(json));
                var inputKind = ContentInputKind.Markdown;
                if (item.TryGetProperty("inputKind", out var inputKindValue)
                    && (inputKindValue.ValueKind != JsonValueKind.String
                        || !Enum.TryParse(inputKindValue.GetString(), false, out inputKind) || !Enum.IsDefined(inputKind)))
                    throw new ArgumentException("The project snapshot route has an unknown input kind.", nameof(json));
                var relations = new List<ProjectReferenceRelation>();
                if (item.TryGetProperty("relations", out var relationValues))
                {
                    if (relationValues.ValueKind != JsonValueKind.Array)
                        throw new ArgumentException("Captured route relations must be an array.", nameof(json));
                    foreach (var relation in relationValues.EnumerateArray())
                    {
                        if (relation.ValueKind != JsonValueKind.Object
                            || !relation.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String
                            || !Enum.TryParse<OkfLinkKind>(kindValue.GetString(), false, out var kind) || !Enum.IsDefined(kind)
                            || !relation.TryGetProperty("isImage", out var image) || image.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new ArgumentException("The project snapshot has an invalid captured relation.", nameof(json));
                        string? OptionalText(string name) => !relation.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null
                            : value.ValueKind == JsonValueKind.String ? value.GetString()
                            : throw new ArgumentException("A captured relation path must be a string or null.", nameof(json));
                        relations.Add(new ProjectReferenceRelation(OptionalText("target"), OptionalText("targetSourcePath"), kind, image.GetBoolean()));
                    }
                }
                routes.Add(new ProjectRouteCandidate(
                    Field("sourcePath"), Field("publicPath"), Field("projectId"),
                    Field("collection"), Field("version"), Field("locale"), publication)
                {
                    InputProfile = inputProfile,
                    InputKind = inputKind,
                    Relations = relations,
                    InputRoot = item.TryGetProperty("inputRoot", out var inputRoot) && inputRoot.ValueKind == JsonValueKind.String ? inputRoot.GetString() : null,
                    InputPath = item.TryGetProperty("inputPath", out var inputPath) && inputPath.ValueKind == JsonValueKind.String ? inputPath.GetString() : null,
                });
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
                acquiredAt,
                root.TryGetProperty("projectHash", out var projectHash) && projectHash.ValueKind == JsonValueKind.String
                    ? projectHash.GetString() : null,
                root.TryGetProperty("schemas", out var schemaSnapshots)
                    ? schemaSnapshots.ValueKind == JsonValueKind.Array
                        ? schemaSnapshots.EnumerateArray().Select(FrontMatterSchemaSnapshot.Parse).ToArray()
                        : throw new ArgumentException("The project schema snapshots must be an array.", nameof(json))
                    : [],
                root.TryGetProperty("markdownProfileId", out var profile) && profile.ValueKind == JsonValueKind.String
                    ? profile.GetString()! : MarkdownOptions.LegacyProfile,
                root.TryGetProperty("markdownComponents", out var components)
                    ? components.ValueKind == JsonValueKind.Array ? components.EnumerateArray().Select(component => new MarkdownComponentSchema(
                        GeneratedFrontMatterSchema.Text(component, "name") ?? "",
                        GeneratedFrontMatterSchema.Text(component, "propsTypeName") ?? "",
                        GeneratedFrontMatterSchema.Text(component, "schemaJson"),
                        GeneratedFrontMatterSchema.Text(component, "fingerprint") ?? "",
                        GeneratedFrontMatterSchema.Text(component, "semanticRulesJson"))).ToArray()
                        : throw new ArgumentException("Component schema snapshots must be an array.", nameof(json)) : [],
                root.TryGetProperty("semanticSnapshot", out var semantics) && semantics.ValueKind != JsonValueKind.Null
                    ? DocumentSemanticSnapshot.ParseJson(semantics.GetRawText()) : null,
                root.TryGetProperty("semanticRules", out var rules)
                    ? rules.ValueKind == JsonValueKind.Object ? rules.EnumerateObject().ToDictionary(pair => pair.Name,
                        pair => DocumentSemanticRuleSet.ParseJson(pair.Value.GetRawText()), StringComparer.Ordinal)
                        : throw new ArgumentException("Semantic rules must be an object.", nameof(json)) : null,
                ParseInputMetadata(root));
        }
    }

    private static IReadOnlyList<ProjectInputMetadata>? ParseInputMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("inputMetadata", out var values))
            return null;
        if (values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Input metadata must be an array.");
        return values.EnumerateArray().Select(input =>
        {
            if (input.ValueKind != JsonValueKind.Object
                || !input.TryGetProperty("sourcePath", out var source) || source.ValueKind != JsonValueKind.String
                || !input.TryGetProperty("inputProfile", out var profile) || profile.ValueKind != JsonValueKind.String
                || !Enum.TryParse<ContentInputProfile>(profile.GetString(), false, out var inputProfile) || !Enum.IsDefined(inputProfile)
                || !input.TryGetProperty("inputKind", out var kind) || kind.ValueKind != JsonValueKind.String
                || !Enum.TryParse<ContentInputKind>(kind.GetString(), false, out var inputKind) || !Enum.IsDefined(inputKind))
                throw new ArgumentException("Invalid acquired input metadata.");
            return new ProjectInputMetadata(source.GetString()!, inputProfile, inputKind,
                input.TryGetProperty("inputRoot", out var inputRoot) && inputRoot.ValueKind == JsonValueKind.String ? inputRoot.GetString() : null,
                input.TryGetProperty("inputPath", out var inputPath) && inputPath.ValueKind == JsonValueKind.String ? inputPath.GetString() : null);
        }).ToArray();
    }
}
