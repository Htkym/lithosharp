using System.Text.Json;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;

namespace LithoSharp.Documentation;

public sealed partial class DocumentSemanticRuleSet
{
    public string ToJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = "1.0",
        rules = Rules.Select(rule => new
        {
            kind = rule.Kind.ToString(),
            field = rule.Field,
            conditionField = rule.ConditionField,
            expectedValue = rule.ExpectedValue,
            sameVersion = rule.SameVersion,
            sameLocale = rule.SameLocale,
            publishedTarget = rule.PublishedTarget,
            publishedSourceOnly = rule.PublishedSourceOnly
        })
    }, DocumentSemanticJson.Options);
    public static DocumentSemanticRuleSet ParseJson(string json) => DocumentSemanticJson.Read(() => ParseRules(json));
    private static DocumentSemanticRuleSet ParseRules(string json)
    {
        using var parsed = DocumentSemanticJson.Parse(json);
        var root = parsed.RootElement;
        DocumentSemanticJson.Version(root);
        return new(root.GetProperty("rules").EnumerateArray().Select(item => DocumentSemanticJson.String(item, "kind") switch
        {
            "RequiredWhen" => DocumentSemanticRule.RequiredWhen(DocumentSemanticJson.String(item, "field"), DocumentSemanticJson.String(item, "conditionField"), DocumentSemanticJson.String(item, "expectedValue")),
            "RequiredWhenPublished" => DocumentSemanticRule.RequiredWhenPublished(DocumentSemanticJson.String(item, "field")),
            "Reference" => DocumentSemanticRule.Reference(DocumentSemanticJson.String(item, "field"), item.GetProperty("sameVersion").GetBoolean(), item.GetProperty("sameLocale").GetBoolean(), item.GetProperty("publishedTarget").GetBoolean(), item.GetProperty("publishedSourceOnly").GetBoolean()),
            _ => throw new ArgumentException("Unknown document semantic rule kind.", nameof(json)),
        }));
    }
}

public sealed partial class DocumentSemanticSnapshot
{
    /// <summary>Captures evidence for explicit editor context. This operation does not execute a project.</summary>
    public string ToJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = "1.0",
        isComplete = IsComplete,
        completeScopes = CompleteScopes,
        documents = Documents.Select(document => new
        {
            key = document.Key,
            sourcePath = document.SourcePath,
            values = DocumentSemanticJson.Value(document.Values),
            publication = document.Publication.ToString(),
            isComplete = document.IsComplete,
            locations = document.Locations
        }),
        routes = Catalog.Pages.Select(page => new
        {
            key = page.Key,
            sourcePath = page.SourcePath,
            publicPath = page.Route.PublicPath,
            outputPath = page.Route.RelativeOutputPath
        })
    }, DocumentSemanticJson.Options);
    public static DocumentSemanticSnapshot ParseJson(string json) => DocumentSemanticJson.Read(() => ParseSnapshot(json));
    private static DocumentSemanticSnapshot ParseSnapshot(string json)
    {
        using var parsed = DocumentSemanticJson.Parse(json);
        var root = parsed.RootElement;
        DocumentSemanticJson.Version(root);
        var documents = root.GetProperty("documents").EnumerateArray().Select(item =>
        {
            if (!Enum.TryParse<DocumentSemanticPublication>(DocumentSemanticJson.String(item, "publication"), out var publication) || !Enum.IsDefined(publication))
                throw new ArgumentException("Unknown document publication evidence.", nameof(json));
            var locations = item.GetProperty("locations").EnumerateObject().ToDictionary(pair => pair.Name, pair => DocumentSemanticJson.Location(pair.Value), StringComparer.Ordinal);
            return new DocumentSemanticDocument(DocumentSemanticJson.Key(item.GetProperty("key")), DocumentSemanticJson.String(item, "sourcePath"), item.GetProperty("values").EnumerateObject().ToDictionary(pair => pair.Name, pair => (object?)pair.Value, StringComparer.Ordinal), publication, item.GetProperty("isComplete").GetBoolean(), locations);
        }).ToArray();
        var pages = root.GetProperty("routes").EnumerateArray().Select(item => new DocumentPage(DocumentSemanticJson.Key(item.GetProperty("key")), DocumentSemanticJson.String(item, "sourcePath"), new(), DocumentSemanticJson.Route(DocumentSemanticJson.String(item, "publicPath"), DocumentSemanticJson.String(item, "outputPath")))).ToArray();
        var completeScopes = root.GetProperty("completeScopes");
        var scopes = completeScopes.ValueKind != JsonValueKind.Null ? completeScopes.EnumerateArray().Select(item => new DocumentSemanticScope(DocumentSemanticJson.String(item, "Collection"), DocumentSemanticJson.String(item, "Version"), DocumentSemanticJson.String(item, "Locale"))).ToArray() : null;
        return new(new DocumentCatalog(pages), documents, root.GetProperty("isComplete").GetBoolean(), scopes);
    }
}

internal static class DocumentSemanticJson
{
    internal static readonly JsonSerializerOptions Options = new() { MaxDepth = 128 };
    internal static object? Value(object? value) => value switch
    {
        DocumentKey key => new
        {
            collection = key.Collection,
            version = key.Version,
            locale = key.Locale,
            id = key.Id
        },
        IReadOnlyDictionary<string, object?> mapping => mapping.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal),
        IReadOnlyList<object?> sequence => sequence.Select(Value).ToArray(),
        _ => value,
    };
    internal static T Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or NotSupportedException or FormatException or OverflowException)
        {
            throw new ArgumentException("Invalid semantic context JSON.", "json", error);
        }
    }

    internal static JsonDocument Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            return JsonDocument.Parse(json, new() { MaxDepth = 128 });
        }
        catch (JsonException error)
        {
            throw new ArgumentException("Invalid semantic context JSON.", nameof(json), error);
        }
    }

    internal static void Version(JsonElement root)
    {
        if (String(root, "schemaVersion") != "1.0")
            throw new ArgumentException("Unsupported semantic context schema version.");
    }

    internal static string String(JsonElement item, string name) => item.GetProperty(name).GetString() ?? throw new ArgumentException($"Semantic context requires '{name}'.");
    internal static DocumentKey Key(JsonElement item) => new(String(item, "Collection"), String(item, "Version"), String(item, "Locale"), String(item, "Id"));
    internal static SiteSourceLocation Location(JsonElement item)
    {
        int? Number(string name) => item.GetProperty(name).ValueKind == JsonValueKind.Null ? null : item.GetProperty(name).GetInt32();
        return new(String(item, "FilePath"), Number("Line"), Number("Column"), Number("EndLine"), Number("EndColumn"));
    }

    internal static SiteRoute Route(string publicPath, string outputPath)
    {
        var directory = outputPath == "index.html" || outputPath.EndsWith("/index.html", StringComparison.Ordinal);
        var path = directory ? outputPath[..^"index.html".Length].TrimEnd('/') : outputPath;
        var unbased = directory ? SiteRoute.ForDirectoryIndex(path) : SiteRoute.ForFile(path);
        if (!publicPath.EndsWith(unbased.PublicPath, StringComparison.Ordinal))
            throw new ArgumentException("Semantic route public and output paths disagree.");
        var prefix = publicPath[..^unbased.PublicPath.Length];
        var route = directory ? SiteRoute.ForDirectoryIndex(path, "https://semantic.invalid" + prefix + "/") : SiteRoute.ForFile(path, "https://semantic.invalid" + prefix + "/");
        if (route.PublicPath != publicPath || route.RelativeOutputPath != outputPath)
            throw new ArgumentException("Semantic route normalization changed its declared identity.");
        return route;
    }
}
