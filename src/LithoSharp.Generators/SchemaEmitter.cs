using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace LithoSharp.Generators;

internal static class SchemaEmitter
{
    public static string Emit(INamedTypeSymbol root, IReadOnlyList<INamedTypeSymbol> objects) =>
        "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"$ref\":" + Quote(Reference(root))
        + ",\"$defs\":{" + string.Join(",", objects.Select(type => Quote(Key(type)) + ":" + Object(type))) + "}}";

    private static string Object(INamedTypeSymbol type)
    {
        var members = GeneratorModel.GetMembers(type);
        return "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{"
            + string.Join(",", members.Select(member => Quote(member.YamlName) + ":" + Value(member.Type)))
            + "},\"required\":[" + string.Join(",", members.Where(member => member.Required || (!member.AllowsNull && !member.HasDefault))
                .Select(member => Quote(member.YamlName))) + "]}";
    }

    private static string Value(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return "{\"anyOf\":[" + Value(nullable.TypeArguments[0]) + ",{\"type\":\"null\"}]}";
        var schema = NonNull(type);
        return type.IsReferenceType && GeneratorModel.AllowsNull(type) && type.SpecialType != SpecialType.System_Object
            ? "{\"anyOf\":[" + schema + ",{\"type\":\"null\"}]}" : schema;
    }

    private static string NonNull(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_Object) return GeneratorModel.AllowsNull(type) ? "{}" : "{\"not\":{\"type\":\"null\"}}";
        if (type is IArrayTypeSymbol array) return "{\"type\":\"array\",\"items\":" + Value(array.ElementType) + "}";
        if (type is INamedTypeSymbol list && GeneratorModel.IsList(list))
            return "{\"type\":\"array\",\"items\":" + Value(list.TypeArguments[0]) + "}";
        if (type is INamedTypeSymbol dictionary && GeneratorModel.IsDictionary(dictionary))
            return "{\"type\":\"object\",\"additionalProperties\":" + Value(dictionary.TypeArguments[1]) + "}";
        if (type.TypeKind == TypeKind.Enum)
        {
            var names = string.Join("|", type.GetMembers().OfType<IFieldSymbol>().Where(field => field.HasConstantValue).Select(field => field.Name));
            return "{\"anyOf\":[{\"type\":\"string\",\"pattern\":"
                + Quote("^\\s*(?:" + names + ")(?:\\s*,\\s*(?:" + names + "))*\\s*$")
                + "},{\"type\":\"integer\"}]}";
        }
        if (!GeneratorModel.IsScalar(type)) return "{\"$ref\":" + Quote(Reference(type)) + "}";
        var scalar = type.SpecialType switch
        {
            SpecialType.System_Boolean => "boolean",
            SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 => "integer",
            SpecialType.System_Decimal or SpecialType.System_Single or SpecialType.System_Double => "number",
            _ => "string"
        };
        var format = type.ToDisplayString().TrimEnd('?') switch
        {
            "System.DateTime" or "System.DateTimeOffset" => "date-time",
            "System.DateOnly" => "date",
            "System.TimeOnly" => "time",
            "System.Guid" => "uuid",
            "System.Uri" => "uri-reference",
            _ => null
        };
        return "{\"type\":" + Quote(scalar) + (format is null ? string.Empty : ",\"format\":" + Quote(format)) + "}";
    }

    private static string Key(ITypeSymbol type) => GeneratorModel.Display(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
    private static string Reference(ITypeSymbol type) => "#/$defs/" + Uri.EscapeDataString(Key(type).Replace("~", "~0").Replace("/", "~1"));

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var character in value)
        {
            if (character is '"' or '\\') result.Append('\\').Append(character);
            else if (character < ' ') result.Append("\\u").Append(((int)character).ToString("x4"));
            else result.Append(character);
        }
        return result.Append('"').ToString();
    }
}
