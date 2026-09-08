using System.Text.Json;

namespace LithoSharp.Mdx;

/// <summary>Explicitly selected browser data validated against a bounded JSON schema.</summary>
/// <remarks>Supported schema keywords: type, properties, required, additionalProperties, items,
/// enum and description. Unsupported keywords fail rather than weakening validation.</remarks>
public sealed class MdxPublicData
{
    /// <summary>Validates and snapshots JSON. The root must be an object.</summary>
    /// <exception cref="ArgumentException">The schema is unsupported or the data does not satisfy it.</exception>
    public MdxPublicData(JsonElement value, JsonElement schema)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Public MDX props must be a JSON object.");
        Validate(value, schema, "$", 0);
        Value = value.Clone();
        Schema = schema.Clone();
    }
    /// <summary>Validated public data. Internal front matter is never added automatically.</summary>
    public JsonElement Value { get; }
    /// <summary>The explicit schema used to validate the public data.</summary>
    public JsonElement Schema { get; }
    /// <summary>An empty public data object.</summary>
    public static MdxPublicData Empty { get; } = new(JsonSerializer.SerializeToElement(new { }),
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }));

    private static void Validate(JsonElement value, JsonElement schema, string path, int depth)
    {
        if (depth > 32 || schema.ValueKind != JsonValueKind.Object) throw new ArgumentException($"Invalid schema at {path}.");
        foreach (var field in schema.EnumerateObject())
            if (field.Name is not ("type" or "properties" or "required" or "additionalProperties" or "items" or "enum" or "description"))
                throw new ArgumentException($"Unsupported schema keyword '{field.Name}' at {path}.");
        if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"A schema type is required at {path}.");
        var matches = type.GetString() switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => throw new ArgumentException($"Unsupported schema type at {path}.")
        };
        if (!matches) throw new ArgumentException($"Public data has the wrong type at {path}.");
        if (schema.TryGetProperty("enum", out var choices)
            && (choices.ValueKind != JsonValueKind.Array || !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(choice, value))))
            throw new ArgumentException($"Public data is not an allowed value at {path}.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object
                || !schema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False)
                throw new ArgumentException($"Public object schemas must explicitly declare properties and prohibit additional properties at {path}.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name) || field.Name is "__proto__" or "prototype" or "constructor"
                    || !properties.TryGetProperty(field.Name, out var propertySchema))
                    throw new ArgumentException($"Unexpected public field '{field.Name}' at {path}.");
                Validate(field.Value, propertySchema, path + "." + field.Name, depth + 1);
            }
            if (schema.TryGetProperty("required", out var required))
            {
                if (required.ValueKind != JsonValueKind.Array) throw new ArgumentException($"Invalid required schema at {path}.");
                foreach (var name in required.EnumerateArray())
                    if (name.ValueKind != JsonValueKind.String || !names.Contains(name.GetString()!))
                        throw new ArgumentException($"A required public field is missing at {path}.");
            }
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (!schema.TryGetProperty("items", out var items)) throw new ArgumentException($"Array item schema is required at {path}.");
            var index = 0;
            foreach (var item in value.EnumerateArray()) Validate(item, items, $"{path}[{index++}]", depth + 1);
        }
    }
}
