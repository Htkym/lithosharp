using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LithoSharp.Documentation;

internal static class OpenApiReferenceReader
{
    internal static ApiMember[] Read(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("openapi", out var version)
            || version.ValueKind != JsonValueKind.String || !version.GetString()!.StartsWith("3.", StringComparison.Ordinal))
            throw new ArgumentException("OpenAPI 3 JSON is required.");

        JsonElement Reference(string reference)
        {
            if (!reference.StartsWith('#'))
                throw new ArgumentException("External OpenAPI reference '" + reference + "' must be bundled explicitly before generation; external APIs are not fetched.");
            if (reference == "#")
                return root;
            var fragment = reference[1..];
            for (var index = 0; index < fragment.Length; index++)
                if (fragment[index] == '%' && (index + 2 >= fragment.Length || !Uri.IsHexDigit(fragment[++index]) || !Uri.IsHexDigit(fragment[++index])))
                    throw new ArgumentException("Invalid OpenAPI JSON pointer: " + reference);
            fragment = Uri.UnescapeDataString(fragment);
            if (!fragment.StartsWith('/'))
                throw new ArgumentException("OpenAPI local reference requires a JSON pointer: " + reference);
            var tokens = new List<string>();
            foreach (var encoded in fragment[1..].Split('/'))
            {
                var token = new StringBuilder();
                for (var index = 0; index < encoded.Length; index++)
                {
                    if (encoded[index] != '~')
                        token.Append(encoded[index]);
                    else if (++index < encoded.Length && encoded[index] is '0' or '1')
                        token.Append(encoded[index] == '0' ? '~' : '/');
                    else
                        throw new ArgumentException("Invalid OpenAPI JSON pointer: " + reference);
                }
                tokens.Add(token.ToString());
            }
            var current = root;
            foreach (var token in tokens)
            {
                if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(token, out var property))
                    current = property;
                else if (current.ValueKind == JsonValueKind.Array && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var item)
                    && (token.Length == 1 || token[0] != '0') && item < current.GetArrayLength())
                    current = current[item];
                else
                    throw new ArgumentException("Unresolved OpenAPI reference: " + reference);
            }
            return current;
        }

        string ChildKind(string kind, string name) => kind.EndsWith("Map", StringComparison.Ordinal) ? kind[..^3] : (kind, name) switch
        {
            ("Root", "paths" or "webhooks") => "PathMap",
            ("Root", "components") => "Components",
            ("Components", "schemas") => "SchemaMap",
            ("Components", "parameters" or "headers") => "ParameterMap",
            ("Components", "responses") => "ResponseMap",
            ("Components", "requestBodies") => "RequestBodyMap",
            ("Components", "examples") => "ExampleMap",
            ("Components", "links") => "LinkMap",
            ("Components", "callbacks") => "CallbackMap",
            ("Components", "pathItems") => "PathMap",
            ("Components", "securitySchemes") => "SecuritySchemeMap",
            ("Path", "get" or "put" or "post" or "delete" or "options" or "head" or "patch" or "trace") => "Operation",
            ("Path" or "Operation", "parameters") => "Parameter",
            ("Path" or "Operation", "servers") => "Server",
            ("Operation", "requestBody") => "RequestBody",
            ("Operation", "responses") => "ResponseMap",
            ("Operation", "callbacks") => "CallbackMap",
            ("Callback", _) => "Path",
            ("Parameter" or "Media", "schema") => "Schema",
            ("Parameter" or "Media", "examples") => "ExampleMap",
            ("Parameter" or "RequestBody" or "Response", "content") => "MediaMap",
            ("Response", "headers") => "ParameterMap",
            ("Response", "links") => "LinkMap",
            ("Schema", "properties" or "patternProperties" or "$defs" or "definitions" or "dependentSchemas") => "SchemaMap",
            ("Schema", "items" or "additionalProperties" or "unevaluatedProperties" or "unevaluatedItems" or "propertyNames" or "contains" or "not" or "if" or "then" or "else" or "allOf" or "anyOf" or "oneOf" or "prefixItems" or "contentSchema") => "Schema",
            _ => "Data"
        };

        bool StructuralSchema(JsonElement schema) => schema.EnumerateObject().Any(property => !property.Name.StartsWith("x-", StringComparison.Ordinal)
            && property.Name is not ("$ref" or "$id" or "$schema" or "$anchor" or "$dynamicAnchor" or "$comment" or "title" or "description" or "default" or "deprecated" or "readOnly" or "writeOnly" or "examples" or "example"));

        void Validate(JsonElement element, string kind)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                        throw new ArgumentException("Duplicate OpenAPI key: " + property.Name);
                    if (kind == "Schema" && property.Name is "$id" or "$dynamicRef")
                        throw new ArgumentException("Unsupported OpenAPI schema keyword " + property.Name + ": schema resource scopes and dynamic references are not supported; supply a document-local schema without these keywords.");
                    if (kind != "Data" && !kind.EndsWith("Map", StringComparison.Ordinal) && property.Name == "$ref")
                    {
                        if (property.Value.ValueKind != JsonValueKind.String)
                            throw new ArgumentException("OpenAPI $ref must be a string.");
                        Reference(property.Value.GetString()!);
                        Dereference(element, kind, kind == "Schema");
                    }
                    Validate(property.Value, ChildKind(kind, property.Name));
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray())
                    Validate(item, kind);
        }
        Validate(root, "Root");

        JsonElement Dereference(JsonElement element, string context, bool structuralSchema = false)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("$ref", out var reference))
            {
                if (structuralSchema && StructuralSchema(element))
                    break;
                if (reference.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("OpenAPI $ref must be a string.");
                var id = reference.GetString()!;
                if (!visited.Add(id))
                    throw new ArgumentException("Cyclic OpenAPI reference in " + context + ": " + id);
                element = Reference(id);
            }
            return element;
        }

        JsonElement Object(JsonElement element, string context)
        {
            var value = Dereference(element, context);
            if (value.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("OpenAPI " + context + " must be an object.");
            if (!element.TryGetProperty("$ref", out _))
                return value;
            var sources = new List<JsonElement>();
            while (element.TryGetProperty("$ref", out var reference))
            {
                sources.Add(element);
                element = Reference(reference.GetString()!);
            }
            var fields = value.EnumerateObject().ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
            foreach (var source in sources.AsEnumerable().Reverse())
                foreach (var field in source.EnumerateObject().Where(field => field.Name != "$ref"))
                {
                    if (context.StartsWith("path ", StringComparison.Ordinal) && fields.ContainsKey(field.Name))
                        throw new ArgumentException("Ambiguous OpenAPI path reference sibling: " + field.Name + " in " + context);
                    if (context.StartsWith("path ", StringComparison.Ordinal) || field.Name is "summary" or "description")
                        fields[field.Name] = field.Value;
                }
            return JsonSerializer.SerializeToElement(fields);
        }

        string Text(JsonElement element, string name, string fallback = "", bool required = false)
        {
            if (!element.TryGetProperty(name, out var value))
                return required ? throw new ArgumentException("OpenAPI " + name + " is required.") : fallback;
            if (value.ValueKind != JsonValueKind.String)
                throw new ArgumentException("OpenAPI " + name + " must be a string.");
            return value.GetString()!;
        }

        bool Flag(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
                return false;
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("OpenAPI " + name + " must be a boolean.");
            return value.GetBoolean();
        }

        string Schema(JsonElement schema, HashSet<string> active)
        {
            if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return "<p>" + (schema.GetBoolean() ? "Any value is allowed." : "No value is allowed.") + "</p>";
            if (schema.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("OpenAPI schema must be an object or boolean.");
            var html = new StringBuilder();
            var format = Text(schema, "format");
            var nullable = Flag(schema, "nullable");
            if (Flag(schema, "deprecated"))
                html.Append("<p>Deprecated schema.</p>");
            if (schema.TryGetProperty("$ref", out var reference))
            {
                var id = reference.GetString()!;
                // Structural recursion is displayable; a cycle consisting only of references is not.
                Dereference(schema, "schema", structuralSchema: true);
                html.Append("<p>Reference: <code>").Append(Html.Encode(id)).Append("</code></p>");
                if (!active.Add(id))
                    html.Append("<p>Recursive schema reference.</p>");
                else
                {
                    html.Append(Schema(Reference(id), active));
                    active.Remove(id);
                }
            }
            if (schema.TryGetProperty("type", out var type))
            {
                if (type.ValueKind != JsonValueKind.String && (type.ValueKind != JsonValueKind.Array || type.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)))
                    throw new ArgumentException("OpenAPI schema type must be a string or an array of strings.");
                var types = type.ValueKind == JsonValueKind.String ? new[] { type.GetString()! } : type.EnumerateArray().Select(value => value.GetString()!).ToArray();
                if (types.Length == 0 || types.Distinct(StringComparer.Ordinal).Count() != types.Length
                    || types.Any(value => value is not ("array" or "boolean" or "integer" or "number" or "object" or "string" or "null")))
                    throw new ArgumentException("OpenAPI schema type must contain distinct JSON schema type names.");
                html.Append("<p>Type: <code>").Append(Html.Encode(string.Join(" | ", types))).Append("</code>");
                if (format.Length > 0)
                    html.Append(" (format: <code>").Append(Html.Encode(format)).Append("</code>)");
                if (nullable)
                    html.Append("; nullable");
                html.Append("</p>");
            }
            else
            {
                if (format.Length > 0)
                    html.Append("<p>Format: <code>").Append(Html.Encode(format)).Append("</code></p>");
                if (nullable)
                    html.Append("<p>nullable: <code>true</code></p>");
            }
            var description = Text(schema, "description");
            if (description.Length > 0)
                html.Append("<p>").Append(Html.Encode(description)).Append("</p>");
            foreach (var name in new[] { "enum", "const", "default", "example" })
                if (schema.TryGetProperty(name, out var value))
                {
                    if (name == "enum" && value.ValueKind != JsonValueKind.Array)
                        throw new ArgumentException("OpenAPI schema enum must be an array.");
                    html.Append("<p>").Append(Html.Encode(name)).Append(": <code>").Append(Html.Encode(value.GetRawText())).Append("</code></p>");
                }
            if (schema.TryGetProperty("items", out var items))
                html.Append("<div><strong>Items</strong>").Append(Schema(items, active)).Append("</div>");
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (schema.TryGetProperty("required", out var requiredNames))
            {
                if (requiredNames.ValueKind != JsonValueKind.Array || requiredNames.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                    throw new ArgumentException("OpenAPI schema required must be an array of property names.");
                required.UnionWith(requiredNames.EnumerateArray().Select(value => value.GetString()!));
                if (required.Count != requiredNames.GetArrayLength())
                    throw new ArgumentException("OpenAPI schema required property names must be distinct.");
                if (required.Count > 0)
                    html.Append("<p>Required properties: <code>").Append(Html.Encode(string.Join(", ", required))).Append("</code></p>");
            }
            if (schema.TryGetProperty("properties", out var properties))
            {
                if (properties.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("OpenAPI schema properties must be an object.");
                html.Append("<dl>");
                foreach (var property in properties.EnumerateObject())
                    html.Append("<dt><code>").Append(Html.Encode(property.Name)).Append("</code>").Append(required.Contains(property.Name) ? " (required)" : " (optional)")
                        .Append("</dt><dd>").Append(Schema(property.Value, active)).Append("</dd>");
                html.Append("</dl>");
            }
            foreach (var name in new[] { "allOf", "anyOf", "oneOf" })
                if (schema.TryGetProperty(name, out var alternatives))
                {
                    if (alternatives.ValueKind != JsonValueKind.Array)
                        throw new ArgumentException("OpenAPI schema " + name + " must be an array.");
                    html.Append("<div><strong>").Append(name).Append("</strong><ul>");
                    foreach (var alternative in alternatives.EnumerateArray())
                        html.Append("<li>").Append(Schema(alternative, active)).Append("</li>");
                    html.Append("</ul></div>");
                }
            if (schema.TryGetProperty("additionalProperties", out var additional))
                html.Append("<div><strong>Additional properties</strong>").Append(Schema(additional, active)).Append("</div>");
            foreach (var field in schema.EnumerateObject().Where(field => field.Name is not ("$ref" or "type" or "format" or "nullable" or "deprecated" or "description"
                or "enum" or "const" or "default" or "example" or "items" or "required" or "properties" or "allOf" or "anyOf" or "oneOf" or "additionalProperties")))
                html.Append("<dl><dt>").Append(Html.Encode(field.Name)).Append("</dt><dd><code>").Append(Html.Encode(field.Value.GetRawText())).Append("</code></dd></dl>");
            return html.Length == 0 ? "<p>Schema constraints are not specified.</p>" : html.ToString();
        }

        string Content(JsonElement owner, bool required = false)
        {
            if (!owner.TryGetProperty("content", out var content))
                return required ? throw new ArgumentException("OpenAPI request body content is required.") : "";
            if (content.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("OpenAPI content must be an object.");
            var html = new StringBuilder();
            foreach (var media in content.EnumerateObject())
            {
                var value = Object(media.Value, "media type");
                html.Append("<div><h3>").Append(Html.Encode(media.Name)).Append("</h3>");
                html.Append(value.TryGetProperty("schema", out var schema) ? Schema(schema, new(StringComparer.Ordinal)) : "<p>Schema is not specified.</p>");
                if (value.TryGetProperty("example", out var example))
                    html.Append("<p>Example: <code>").Append(Html.Encode(example.GetRawText())).Append("</code></p>");
                html.Append("</div>");
            }
            return html.Length == 0 ? "<p>Media types are not specified.</p>" : html.ToString();
        }

        (string Contract, string Documentation, string Obsolete) Fingerprints(JsonElement operation)
        {
            var documentation = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var obsolete = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            void Write(Utf8JsonWriter writer, JsonElement element, string kind, string path, HashSet<string> active)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    if (kind != "Data" && !kind.EndsWith("Map", StringComparison.Ordinal) && element.TryGetProperty("$ref", out var referenceValue)
                        && (kind != "Schema" || element.EnumerateObject().Count() == 1))
                    {
                        var reference = referenceValue.GetString()!;
                        if (active.Add(reference))
                        {
                            Write(writer, kind == "Schema" ? Reference(reference) : Object(element, kind == "Path" ? "path fingerprint" : kind), kind, path, active);
                            active.Remove(reference);
                        }
                        else
                        {
                            writer.WriteStartObject();
                            writer.WriteString("$ref", "recursive:" + reference);
                            writer.WriteEndObject();
                        }
                        return;
                    }
                    writer.WriteStartObject();
                    foreach (var field in element.EnumerateObject().OrderBy(field => field.Name, StringComparer.Ordinal))
                    {
                        var fieldPath = path + "/" + field.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
                        if (kind != "Data" && !kind.EndsWith("Map", StringComparison.Ordinal)
                            && (field.Name is "description" or "summary" or "title" or "example" or "examples" or "externalDocs" || (kind == "Operation" && field.Name == "tags")))
                        {
                            using var data = new MemoryStream();
                            using (var dataWriter = new Utf8JsonWriter(data))
                                Write(dataWriter, field.Value, ChildKind(kind, field.Name), fieldPath, active);
                            documentation[fieldPath] = Encoding.UTF8.GetString(data.ToArray());
                            continue;
                        }
                        if (kind != "Data" && !kind.EndsWith("Map", StringComparison.Ordinal) && field.Name == "deprecated")
                        {
                            if (Flag(element, "deprecated"))
                                obsolete[fieldPath] = true;
                            continue;
                        }
                        writer.WritePropertyName(field.Name);
                        if (kind != "Data" && !kind.EndsWith("Map", StringComparison.Ordinal) && field.Name == "$ref")
                        {
                            var reference = field.Value.GetString()!;
                            if (active.Add(reference))
                            {
                                Write(writer, Reference(reference), kind, path, active);
                                active.Remove(reference);
                            }
                            else
                                writer.WriteStringValue("recursive:" + reference);
                        }
                        else
                            Write(writer, field.Value, ChildKind(kind, field.Name), fieldPath, active);
                    }
                    writer.WriteEndObject();
                }
                else if (element.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                        Write(writer, item, kind, path + "/" + (index++).ToString(CultureInfo.InvariantCulture), active);
                    writer.WriteEndArray();
                }
                else
                    element.WriteTo(writer);
            }
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Write(writer, operation, "Operation", "", []);
            return (Encoding.UTF8.GetString(stream.ToArray()), JsonSerializer.Serialize(documentation), JsonSerializer.Serialize(obsolete));
        }

        var result = new List<ApiMember>();
        if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("OpenAPI paths must be an object.");
        foreach (var path in paths.EnumerateObject())
        {
            if (path.Name.StartsWith("x-", StringComparison.Ordinal))
                continue;
            var pathItem = Object(path.Value, "path " + path.Name);
            foreach (var operation in pathItem.EnumerateObject().Where(property => property.Name is "get" or "put" or "post" or "delete" or "options" or "head" or "patch" or "trace"))
            {
                var value = Object(operation.Value, "operation");
                var id = "O:" + operation.Name.ToUpperInvariant() + " " + path.Name;
                var title = Text(value, "summary", id[2..]);
                var description = Text(value, "description");
                var html = new StringBuilder("<p><code>" + Html.Encode(id[2..]) + "</code></p><p>" + Html.Encode(description) + "</p>");
                var parameters = new Dictionary<(string Name, string Location), JsonElement>();
                foreach (var scope in new[] { pathItem, value })
                {
                    if (!scope.TryGetProperty("parameters", out var scoped))
                        continue;
                    if (scoped.ValueKind != JsonValueKind.Array)
                        throw new ArgumentException("OpenAPI parameters must be an array.");
                    var declared = new HashSet<(string, string)>();
                    foreach (var item in scoped.EnumerateArray())
                    {
                        var parameter = Object(item, "parameter");
                        var key = (Text(parameter, "name"), Text(parameter, "in"));
                        if (key.Item1.Length == 0 || key.Item2 is not ("query" or "header" or "path" or "cookie"))
                            throw new ArgumentException("OpenAPI parameter requires a name and a query, header, path or cookie location.");
                        if (!declared.Add(key))
                            throw new ArgumentException("Duplicate OpenAPI parameter: " + key.Item1 + " in " + key.Item2);
                        if (key.Item2 == "path" && !Flag(parameter, "required"))
                            throw new ArgumentException("OpenAPI path parameter must be required: " + key.Item1);
                        var hasSchema = parameter.TryGetProperty("schema", out _);
                        var hasContent = parameter.TryGetProperty("content", out var parameterContent);
                        if (hasSchema == hasContent)
                            throw new ArgumentException("OpenAPI parameter requires exactly one of schema or content: " + key.Item1);
                        if (hasContent && (parameterContent.ValueKind != JsonValueKind.Object || parameterContent.EnumerateObject().Count() != 1))
                            throw new ArgumentException("OpenAPI parameter content must contain exactly one media type: " + key.Item1);
                        parameters[key] = parameter;
                    }
                }
                if (parameters.Count > 0)
                {
                    html.Append("<section><h2>Parameters</h2><dl>");
                    foreach (var (key, parameter) in parameters)
                    {
                        html.Append("<dt><code>").Append(Html.Encode(key.Name)).Append("</code> (").Append(Html.Encode(key.Location))
                            .Append(Flag(parameter, "required") ? ", required" : ", optional").Append(Flag(parameter, "deprecated") ? ", deprecated" : "").Append(")</dt><dd><p>")
                            .Append(Html.Encode(Text(parameter, "description"))).Append("</p>");
                        html.Append(parameter.TryGetProperty("schema", out var schema) ? Schema(schema, new(StringComparer.Ordinal)) : Content(parameter));
                        html.Append("</dd>");
                    }
                    html.Append("</dl></section>");
                }
                if (value.TryGetProperty("requestBody", out var request))
                {
                    var body = Object(request, "request body");
                    html.Append("<section><h2>Request body</h2><p>").Append(Flag(body, "required") ? "Required" : "Optional")
                        .Append("</p><p>").Append(Html.Encode(Text(body, "description"))).Append("</p>").Append(Content(body, required: true)).Append("</section>");
                }
                html.Append("<section><h2>Responses</h2>");
                if (!value.TryGetProperty("responses", out var responses))
                    html.Append("<p>Responses are not specified.</p>");
                else
                {
                    if (responses.ValueKind != JsonValueKind.Object || !responses.EnumerateObject().Any(response => !response.Name.StartsWith("x-", StringComparison.Ordinal)))
                        throw new ArgumentException("OpenAPI operation responses must be a nonempty object: " + id);
                    foreach (var response in responses.EnumerateObject())
                    {
                        if (response.Name.StartsWith("x-", StringComparison.Ordinal))
                            continue;
                        var responseValue = Object(response.Value, "response " + response.Name);
                        html.Append("<div><h3>").Append(Html.Encode(response.Name)).Append("</h3><p>").Append(Html.Encode(Text(responseValue, "description", required: true)))
                            .Append("</p>").Append(Content(responseValue)).Append("</div>");
                    }
                }
                html.Append("</section>");
                var effective = value.EnumerateObject().ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);
                if (parameters.Count > 0)
                    effective["parameters"] = JsonSerializer.SerializeToElement(parameters.OrderBy(parameter => parameter.Key.Location, StringComparer.Ordinal)
                        .ThenBy(parameter => parameter.Key.Name, StringComparer.Ordinal).Select(parameter => parameter.Value).ToArray());
                foreach (var name in new[] { "servers", "security" })
                    if (!effective.ContainsKey(name))
                    {
                        if (name == "servers" && pathItem.TryGetProperty(name, out var pathValue))
                            effective[name] = pathValue;
                        else if (root.TryGetProperty(name, out var rootValue))
                            effective[name] = rootValue;
                    }
                var fingerprints = Fingerprints(JsonSerializer.SerializeToElement(effective));
                result.Add(new(id, title, description, html.ToString(), Flag(value, "deprecated"))
                { ContractContent = fingerprints.Contract, DocumentationContent = fingerprints.Documentation, ObsoleteContent = fingerprints.Obsolete, ParsedBodyHtml = html.ToString() });
            }
        }
        return result.ToArray();
    }
}
