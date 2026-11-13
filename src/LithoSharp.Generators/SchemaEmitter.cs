using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace LithoSharp.Generators;

internal static class SchemaEmitter
{
    public static string Emit(INamedTypeSymbol root, IReadOnlyList<INamedTypeSymbol> objects) =>
        "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"x-lithosharp-schemaVersion\":\"1.0\",\"$ref\":" + Quote(Reference(root))
        + ",\"$defs\":{" + string.Join(",", objects.Select(type => Quote(Key(type)) + ":" + Object(type))) + "}}";

    private static string Object(INamedTypeSymbol type)
    {
        var members = GeneratorModel.GetMembers(type);
        return "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{"
            + string.Join(",", members.Select(member => Quote(member.YamlName) + ":" + Member(member)))
            + "},\"required\":[" + string.Join(",", members.Where(member => member.Required || (!member.AllowsNull && !member.HasDefault))
                .Select(member => Quote(member.YamlName))) + "]}";
    }

    private static string Member(MemberModel member)
    {
        var schema = Value(member.Type);
        var description = Description(member.Symbol);
        var deprecated = member.Symbol.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute");
        return schema.Substring(0, schema.Length - 1)
            + (schema.Length == 2 ? "" : ",")
            + (description is null ? "" : "\"description\":" + Quote(description) + ",")
            + (deprecated is null ? "" : "\"deprecated\":true,")
            + "\"x-lithosharp-requiredWhen\":[" + string.Join(",", member.RequiredWhen.Select(rule =>
                "{\"field\":" + Quote(rule.Field) + ",\"equals\":" + Quote(rule.EqualsValue) + "}")) + "]"
            + ",\"x-lithosharp-buildRequired\":" + (!member.Required && member.HasDefault && !member.AllowsNull ? "true" : "false") + "}";
    }

    private static string? Description(ISymbol symbol)
    {
        var attribute = symbol.GetAttributes().FirstOrDefault(value =>
            value.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute");
        if (attribute?.ConstructorArguments.FirstOrDefault().Value is string description)
            return description;
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var summary = XDocument.Load(reader).Root?.Element("summary")?.Value;
            return string.IsNullOrWhiteSpace(summary) ? null : string.Join(" ", summary!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        catch (XmlException)
        {
            return null;
        }
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
        if (type.SpecialType == SpecialType.System_Object)
            return GeneratorModel.AllowsNull(type) ? "{}" : "{\"not\":{\"type\":\"null\"}}";
        if (type is IArrayTypeSymbol array)
            return "{\"type\":\"array\",\"items\":" + Value(array.ElementType) + "}";
        if (type is INamedTypeSymbol list && GeneratorModel.IsList(list))
            return "{\"type\":\"array\",\"items\":" + Value(list.TypeArguments[0]) + "}";
        if (type is INamedTypeSymbol dictionary && GeneratorModel.IsDictionary(dictionary))
            return "{\"type\":\"object\",\"additionalProperties\":" + Value(dictionary.TypeArguments[1]) + "}";
        if (type.TypeKind == TypeKind.Enum)
        {
            var names = string.Join("|", type.GetMembers().OfType<IFieldSymbol>().Where(field => field.HasConstantValue).Select(field => field.Name));
            return "{\"anyOf\":[{\"type\":\"string\",\"pattern\":"
                + Quote("^\\s*(?:" + names + ")(?:\\s*,\\s*(?:" + names + "))*\\s*$")
                + "},{\"type\":\"integer\"}],\"x-lithosharp-enumNames\":["
                + string.Join(",", type.GetMembers().OfType<IFieldSymbol>().Where(field => field.HasConstantValue).Select(field => Quote(field.Name)))
                + "],\"x-lithosharp-enumUnderlyingType\":" + Quote(ClrName(((INamedTypeSymbol)type).EnumUnderlyingType!)) + "}";
        }
        if (!GeneratorModel.IsScalar(type))
            return "{\"$ref\":" + Quote(Reference(type)) + "}";
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
        return "{\"type\":" + Quote(scalar) + ",\"x-lithosharp-clrType\":" + Quote(ClrName(type))
            + (format is null ? string.Empty : ",\"format\":" + Quote(format)) + "}";
    }

    private static string ClrName(ITypeSymbol type) => type.SpecialType == SpecialType.None
        ? type.ToDisplayString().TrimEnd('?') : type.SpecialType.ToString().Replace('_', '.');

    private static string Key(ITypeSymbol type) => GeneratorModel.Display(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
    private static string Reference(ITypeSymbol type) => "#/$defs/" + Uri.EscapeDataString(Key(type).Replace("~", "~0").Replace("/", "~1"));

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var character in value)
        {
            if (character is '"' or '\\')
                result.Append('\\').Append(character);
            else if (character < ' ')
                result.Append("\\u").Append(((int)character).ToString("x4"));
            else
                result.Append(character);
        }
        return result.Append('"').ToString();
    }
}
