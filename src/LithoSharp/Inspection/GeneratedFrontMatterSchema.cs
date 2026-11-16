using System.Collections;
using System.Text.Json;
using LithoSharp.Content;
using LithoSharp.Diagnostics;

namespace LithoSharp.Inspection;

// Consumes only SchemaEmitter's versioned subset; it does not resolve external schemas or CLR types.
internal sealed class GeneratedFrontMatterSchema
{
    internal const string Version = "1.0";
    private readonly JsonElement document;

    private GeneratedFrontMatterSchema(JsonElement document)
    {
        this.document = document;
        Fields = Describe(document, new HashSet<string>(StringComparer.Ordinal));
    }

    internal IReadOnlyList<FrontMatterFieldInfo> Fields { get; }

    internal static GeneratedFrontMatterSchema Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > 4 * 1024 * 1024)
            throw new ArgumentException("The generated schema exceeds its input limit.", nameof(json));
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "x-lithosharp-schemaVersion") != Version
                || !root.TryGetProperty("$defs", out var definitions) || definitions.ValueKind != JsonValueKind.Object
                || Text(root, "$ref") is null)
                throw new ArgumentException("A supported generated LithoSharp schema is required.", nameof(json));
            var result = new GeneratedFrontMatterSchema(root.Clone());
            var rootSchema = result.Resolve(root);
            if (Text(rootSchema, "type") != "object" || !rootSchema.TryGetProperty("properties", out var rootFields)
                || rootFields.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("The generated front matter schema root must describe a DTO mapping.", nameof(json));
            foreach (var definition in definitions.EnumerateObject())
                result.CheckContract(definition.Value, 0);
            return result;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Invalid generated schema JSON: " + exception.Message, nameof(json));
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new ArgumentException("Unsupported generated schema structure: " + exception.Message, nameof(json));
        }
    }

    private void CheckContract(JsonElement schema, int depth)
    {
        if (depth > 128 || schema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Unsupported generated schema structure.");
        if (Text(schema, "$ref") is not null)
            _ = Resolve(schema);
        if (Text(schema, "x-lithosharp-clrType") is { } scalar && ScalarType(scalar) is null)
            throw new ArgumentException("Unsupported generated scalar type: " + scalar);
        if (Text(schema, "x-lithosharp-enumUnderlyingType") is { } underlying
            && underlying is not ("System.SByte" or "System.Byte" or "System.Int16" or "System.UInt16"
                or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64"))
            throw new ArgumentException("Unsupported generated enum representation.");
        if (schema.TryGetProperty("x-lithosharp-enumNames", out var names)
            && (names.ValueKind != JsonValueKind.Array || names.EnumerateArray().Any(name => name.ValueKind != JsonValueKind.String)))
            throw new ArgumentException("Unsupported generated enum choices.");
        if (schema.TryGetProperty("x-lithosharp-requiredWhen", out var conditions)
            && (conditions.ValueKind != JsonValueKind.Array || conditions.EnumerateArray().Any(condition =>
                string.IsNullOrWhiteSpace(Text(condition, "field")) || Text(condition, "equals") is null)))
            throw new ArgumentException("Unsupported generated conditional requirement.");
        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name == "properties")
                foreach (var field in property.Value.EnumerateObject())
                    CheckContract(field.Value, depth + 1);
            else if (property.Name is "items" or "not" || property.Name == "additionalProperties" && property.Value.ValueKind == JsonValueKind.Object)
                CheckContract(property.Value, depth + 1);
            else if (property.Name == "anyOf")
                foreach (var alternative in property.Value.EnumerateArray())
                    CheckContract(alternative, depth + 1);
        }
    }

    internal IReadOnlyList<SiteDiagnostic> Validate(IReadOnlyDictionary<string, object?> values,
        SiteSourceLocation? rootLocation, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<SiteDiagnostic>();
        ValidateObject(document, values, rootLocation, diagnostics, cancellationToken);
        return diagnostics;
    }

    private void ValidateObject(JsonElement schema, IReadOnlyDictionary<string, object?> values,
        SiteSourceLocation? location, List<SiteDiagnostic> diagnostics, CancellationToken token)
    {
        schema = Resolve(schema);
        if (!schema.TryGetProperty("properties", out var properties))
            return;
        var required = Required(schema);
        foreach (var property in properties.EnumerateObject())
        {
            token.ThrowIfCancellationRequested();
            if (property.Value.TryGetProperty("x-lithosharp-requiredWhen", out var conditions))
                foreach (var condition in conditions.EnumerateArray().Select(condition =>
                    (Field: Text(condition, "field") ?? "", Expected: Text(condition, "equals") ?? "")).Distinct())
                    if (values.TryGetValue(condition.Field, out var discriminator)
                        && GeneratedContentBindingContext.ConditionalMatches(discriminator, condition.Expected)
                        && (!values.TryGetValue(property.Name, out var conditionalValue)
                            || GeneratedContentBindingContext.ConditionalMissing(conditionalValue)))
                        diagnostics.Add(Error(ContentFrontMatterDiagnosticIds.ConditionalRequiredField,
                            $"Field '{property.Name}' is required when '{condition.Field}' equals '{condition.Expected}'.",
                            values is LocatedYamlMapping conditionalMapping && values.ContainsKey(property.Name)
                                ? conditionalMapping.GetValueLocation(property.Name) : location));
            if (!values.TryGetValue(property.Name, out var value))
            {
                if (required.Contains(property.Name))
                    diagnostics.Add(Error(ContentFrontMatterDiagnosticIds.MissingRequiredField,
                        $"Required front matter field '{property.Name}' is missing.", location));
                continue;
            }
            ValidateValue(property.Value, value, property.Name,
                values is LocatedYamlMapping mapping ? mapping.GetValueLocation(property.Name) : location, diagnostics, token);
        }
        if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
            foreach (var name in values.Keys.Where(name => !properties.TryGetProperty(name, out _)))
                diagnostics.Add(Error(ContentFrontMatterDiagnosticIds.UnknownField, $"Unknown front matter field '{name}'.",
                    values is LocatedYamlMapping mapping ? mapping.GetKeyLocation(name) : location));
    }

    private void ValidateValue(JsonElement declared, object? input, string path, SiteSourceLocation? fallback,
        List<SiteDiagnostic> diagnostics, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var location = LocatedYamlValue.GetLocation(input) ?? fallback;
        input = LocatedYamlValue.Unwrap(input);
        if (input is null)
        {
            if (!AllowsNull(declared))
                diagnostics.Add(Error(ContentFrontMatterDiagnosticIds.NullNotAllowed, $"Front matter field '{path}' does not allow null.", location));
            return;
        }
        var schema = Resolve(declared);
        if (schema.TryGetProperty("x-lithosharp-enumNames", out var choices))
        {
            var text = input as string;
            var names = choices.EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
            var underlying = ScalarType(Text(schema, "x-lithosharp-enumUnderlyingType") ?? "");
            if (text is not null && (text.Split(',').All(name => names.Contains(name.Trim()))
                || underlying is not null && ContentScalarConversion.TryConvertScalar(text, underlying, out _)))
                return;
        }
        else if (Text(schema, "type") == "array")
        {
            if (input is IEnumerable sequence && input is not string && input is not IReadOnlyDictionary<string, object?>)
            {
                var index = 0;
                foreach (var item in sequence)
                {
                    ValidateValue(schema.GetProperty("items"), item, $"{path}[{index}]",
                        input is LocatedYamlSequence located ? located.GetItemLocation(index) : location, diagnostics, token);
                    index++;
                }
                return;
            }
        }
        else if (Text(schema, "type") == "object")
        {
            if (input is IReadOnlyDictionary<string, object?> mapping)
            {
                if (schema.TryGetProperty("properties", out _))
                    ValidateObject(schema, mapping, location, diagnostics, token);
                else if (schema.TryGetProperty("additionalProperties", out var valueSchema) && valueSchema.ValueKind == JsonValueKind.Object)
                    foreach (var pair in mapping)
                        ValidateValue(valueSchema, pair.Value, $"{path}.{pair.Key}",
                            mapping is LocatedYamlMapping located ? located.GetValueLocation(pair.Key) : location, diagnostics, token);
                return;
            }
        }
        else if (Text(schema, "x-lithosharp-clrType") is { } typeName)
        {
            if (input is not IEnumerable || input is string)
                if (ScalarType(typeName) is { } type && ContentScalarConversion.TryConvertScalar(input, type, out _))
                    return;
        }
        else if (!schema.TryGetProperty("type", out _) && !schema.TryGetProperty("anyOf", out _))
            return;
        diagnostics.Add(Error(ContentFrontMatterDiagnosticIds.InvalidValue,
            $"Front matter field '{path}' does not match its declared static type.", location));
    }

    internal JsonElement Root => document;

    internal JsonElement Resolve(JsonElement schema)
    {
        if (schema.TryGetProperty("anyOf", out var alternatives)
            && alternatives.EnumerateArray().Any(value => Text(value, "type") == "null"))
            schema = alternatives.EnumerateArray().First(value => Text(value, "type") != "null");
        if (Text(schema, "$ref") is not { } reference)
            return schema;
        if (!reference.StartsWith("#/$defs/", StringComparison.Ordinal))
            throw new ArgumentException("Only generated local schema references are supported.");
        var key = Uri.UnescapeDataString(reference[8..]).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        if (!document.GetProperty("$defs").TryGetProperty(key, out var definition))
            throw new ArgumentException("The generated schema has an unresolved definition.");
        return definition;
    }

    private sealed class MetadataBudget { internal int Remaining = 4096; }

    internal IReadOnlyList<FrontMatterFieldInfo> Describe(JsonElement declared, HashSet<string>? visited = null) =>
        DescribeCore(declared, visited ?? new HashSet<string>(StringComparer.Ordinal), new MetadataBudget(), 0);

    private IReadOnlyList<FrontMatterFieldInfo> DescribeCore(JsonElement declared, HashSet<string> visited, MetadataBudget budget, int depth)
    {
        if (depth >= 64 || budget.Remaining <= 0)
            return [];
        var reference = Text(declared, "$ref");
        if (reference is null && declared.TryGetProperty("anyOf", out var alternatives)
            && alternatives.EnumerateArray().Any(value => Text(value, "type") == "null"))
            reference = Text(alternatives.EnumerateArray().First(value => Text(value, "type") != "null"), "$ref");
        if (reference is not null && !visited.Add(reference))
            return [];
        try
        {
            var schema = Resolve(declared);
            if (!schema.TryGetProperty("properties", out var properties))
                return [];
            var required = Required(schema);
            return Array.AsReadOnly(properties.EnumerateObject().Select(property =>
            {
                if (--budget.Remaining < 0)
                    return null;
                var value = Resolve(property.Value);
                var kind = Text(value, "type") ?? "any";
                var item = kind == "array" ? Resolve(value.GetProperty("items"))
                    : kind == "object" && !value.TryGetProperty("properties", out _) && value.TryGetProperty("additionalProperties", out var extra)
                        && extra.ValueKind == JsonValueKind.Object ? Resolve(extra) : value;
                var dictionary = kind == "object" && !value.TryGetProperty("properties", out _) && value.TryGetProperty("additionalProperties", out _);
                var enumeration = value.TryGetProperty("x-lithosharp-enumNames", out var names);
                return new FrontMatterFieldInfo(property.Name, enumeration ? "enum" : dictionary ? "dictionary" : Text(value, "format") ?? kind,
                    kind == "array" || dictionary ? Text(item, "format") ?? Text(item, "type")
                        ?? (item.TryGetProperty("x-lithosharp-enumNames", out _) ? "enum" : "any") : null,
                    required.Contains(property.Name), AllowsNull(property.Value),
                    enumeration ? names.EnumerateArray().Select(name => name.GetString()!).ToArray() : [],
                    property.Value.TryGetProperty("deprecated", out var obsolete) && obsolete.ValueKind == JsonValueKind.True,
                    null, Text(property.Value, "description"), DescribeCore(kind == "array" ? value.GetProperty("items")
                        : dictionary ? value.GetProperty("additionalProperties") : property.Value, visited, budget, depth + 1),
                    property.Value.TryGetProperty("x-lithosharp-buildRequired", out var build) && build.ValueKind == JsonValueKind.True);
            }).OfType<FrontMatterFieldInfo>().ToArray());
        }
        finally
        {
            if (reference is not null)
                visited.Remove(reference);
        }
    }

    internal static bool AllowsNull(JsonElement schema) => !schema.TryGetProperty("type", out _) && !schema.TryGetProperty("not", out _)
        && !schema.TryGetProperty("$ref", out _) && !schema.TryGetProperty("anyOf", out _)
        || Text(schema, "type") == "null"
        || schema.TryGetProperty("anyOf", out var alternatives) && alternatives.EnumerateArray().Any(value => Text(value, "type") == "null");

    private static HashSet<string> Required(JsonElement schema) => schema.TryGetProperty("required", out var values)
        ? values.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal) : new(StringComparer.Ordinal);

    internal static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static SiteDiagnostic Error(string id, string message, SiteSourceLocation? location) =>
        new(id, SiteDiagnosticSeverity.Error, message, location);

    private static Type? ScalarType(string name) => name switch
    {
        "System.String" => typeof(string),
        "System.Boolean" => typeof(bool),
        "System.Char" => typeof(char),
        "System.SByte" => typeof(sbyte),
        "System.Byte" => typeof(byte),
        "System.Int16" => typeof(short),
        "System.UInt16" => typeof(ushort),
        "System.Int32" => typeof(int),
        "System.UInt32" => typeof(uint),
        "System.Int64" => typeof(long),
        "System.UInt64" => typeof(ulong),
        "System.Single" => typeof(float),
        "System.Double" => typeof(double),
        "System.Decimal" => typeof(decimal),
        "System.DateTime" => typeof(DateTime),
        "System.DateTimeOffset" => typeof(DateTimeOffset),
        "System.DateOnly" => typeof(DateOnly),
        "System.TimeOnly" => typeof(TimeOnly),
        "System.TimeSpan" => typeof(TimeSpan),
        "System.Guid" => typeof(Guid),
        "System.Uri" => typeof(Uri),
        _ => null,
    };
}
