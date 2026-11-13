using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LithoSharp.Inspection;

/// <summary>Generated static constraints for an explicitly acquired project revision and exact input membership.</summary>
public sealed class FrontMatterSchemaSnapshot
{
    private FrontMatterSchemaSnapshot(string projectId, long generation, string projectHash, string collection,
        string schemaJson, string[] inputPaths)
    {
        ProjectId = projectId;
        ProjectGeneration = generation;
        ProjectHash = projectHash;
        Collection = collection;
        SchemaJson = schemaJson;
        Schema = GeneratedFrontMatterSchema.Parse(schemaJson);
        InputPaths = Array.AsReadOnly(inputPaths);
        SchemaHash = Hash(schemaJson);
        InputSetHash = Hash(string.Join("\n", inputPaths));
    }

    public string SchemaVersion => GeneratedFrontMatterSchema.Version;
    public string ProjectId { get; }
    public long ProjectGeneration { get; }
    public string ProjectHash { get; }
    public string Collection { get; }
    public string SchemaJson { get; }
    public string SchemaHash { get; }
    public string InputSetHash { get; }
    public IReadOnlyList<string> InputPaths { get; }
    public IReadOnlyList<FrontMatterFieldInfo> Fields => Schema.Fields;
    /// <summary>Constructors, setters, dynamic defaults and custom validators still require a build.</summary>
    public bool RequiresBuildValidation => true;
    internal GeneratedFrontMatterSchema Schema { get; }

    public static FrontMatterSchemaSnapshot Create(string projectId, long projectGeneration, string projectHash,
        string collection, string schemaJson, IEnumerable<string> inputPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentNullException.ThrowIfNull(inputPaths);
        if (projectGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(projectGeneration));
        if (!IsHash(projectHash))
            throw new ArgumentException("A sha256 project acquisition hash is required.", nameof(projectHash));
        var paths = inputPaths.Select(NormalizePath).Order(StringComparer.Ordinal).ToArray();
        if (paths.Distinct(Content.ContentInputPolicy.PathComparer).Count() != paths.Length)
            throw new ArgumentException("Schema input paths must not collide.", nameof(inputPaths));
        return new FrontMatterSchemaSnapshot(projectId, projectGeneration, projectHash, collection, schemaJson, paths);
    }

    internal bool Contains(string sourcePath) => Path.IsPathRooted(sourcePath)
        && InputPaths.Contains(NormalizePath(sourcePath), PathComparer);

    internal static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = path.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (!Path.IsPathRooted(path) || path.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("Schema membership requires absolute paths without dot segments.", nameof(path));
        return path;
    }

    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static string Hash(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static bool IsHash(string? value) => value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal object JsonValue() => new
    {
        schemaVersion = SchemaVersion,
        projectId = ProjectId,
        projectGeneration = ProjectGeneration,
        projectHash = ProjectHash,
        collection = Collection,
        schemaJson = SchemaJson,
        schemaHash = SchemaHash,
        inputSetHash = InputSetHash,
        inputPaths = InputPaths,
    };

    internal static FrontMatterSchemaSnapshot Parse(JsonElement value)
    {
        string Text(string name) => GeneratedFrontMatterSchema.Text(value, name)
            ?? throw new ArgumentException("The schema snapshot omits '" + name + "'.");
        if (Text("schemaVersion") != GeneratedFrontMatterSchema.Version)
            throw new ArgumentException("Unsupported front matter schema snapshot version.");
        if (!value.TryGetProperty("projectGeneration", out var generation) || generation.ValueKind != JsonValueKind.Number || !generation.TryGetInt64(out var revision)
            || !value.TryGetProperty("inputPaths", out var paths) || paths.ValueKind != JsonValueKind.Array
            || paths.EnumerateArray().Any(path => path.ValueKind != JsonValueKind.String))
            throw new ArgumentException("Invalid schema snapshot generation or input membership.");
        var result = Create(Text("projectId"), revision, Text("projectHash"), Text("collection"), Text("schemaJson"),
            paths.EnumerateArray().Select(path => path.GetString()!));
        if (result.SchemaHash != Text("schemaHash") || result.InputSetHash != Text("inputSetHash"))
            throw new ArgumentException("The schema snapshot content does not match its hashes.");
        return result;
    }
}
