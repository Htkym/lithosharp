using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace LithoSharp.Generators;

internal static class StaticYamlValidation
{
    private static readonly System.Reflection.MethodInfo? DateOnlyParser = DateTimeParser("System.DateOnly");
    private static readonly System.Reflection.MethodInfo? TimeOnlyParser = DateTimeParser("System.TimeOnly");
    private static readonly DiagnosticDescriptor StrictYaml = new("LSG005", "Invalid static content YAML", "{0}", "LithoSharp", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor Conversion = new("LSG006", "Invalid static content value", "{0}", "LithoSharp", DiagnosticSeverity.Error, true);

    internal static void Validate(string path, string markdown, INamedTypeSymbol frontMatter, Action<Diagnostic> report)
    {
        var start = FindFrontMatter(markdown);
        if (start is null)
        {
            report(Diagnostic.Create(StrictYaml, Location(path, markdown, 0, Math.Min(3, markdown.Length)), "Markdown front matter is required."));
            return;
        }
        if (!start.Value.Valid)
        {
            report(Diagnostic.Create(StrictYaml, Location(path, markdown, start.Value.Offset, 3), "Front matter is not closed."));
            return;
        }
        try
        {
            var parser = new Parser(new StringReader(start.Value.Yaml));
            var depth = 0;
            while (parser.MoveNext())
            {
                if (parser.Current is AnchorAlias || parser.Current is NodeEvent nodeEvent && !nodeEvent.Anchor.IsEmpty)
                {
                    report(Diagnostic.Create(StrictYaml, Location(path, markdown, start.Value.Offset, 1), "YAML aliases and anchors are not supported."));
                    return;
                }
                if (parser.Current is MappingStart or SequenceStart && ++depth > 64)
                {
                    report(Diagnostic.Create(StrictYaml, Location(path, markdown, start.Value.Offset, 1), "YAML nesting is too deep."));
                    return;
                }
                if (parser.Current is MappingEnd or SequenceEnd) depth--;
            }
            var stream = new YamlStream();
            stream.Load(new StringReader(start.Value.Yaml));
            if (stream.Documents.Count != 1)
            {
                report(Diagnostic.Create(StrictYaml, Location(path, markdown, start.Value.Offset, 1), "Front matter must contain exactly one YAML document."));
                return;
            }
            ValidateNode(path, markdown, start.Value.Offset, stream.Documents[0].RootNode, frontMatter, report, string.Empty);
        }
        catch (NotSupportedException error)
        {
            report(Diagnostic.Create(StaticContentGenerator.InvalidDeclaration, Location(path, markdown, start.Value.Offset, 1), error.Message));
        }
        catch (Exception error) when (error is YamlException or ArgumentException)
        {
            report(Diagnostic.Create(StrictYaml, Location(path, markdown, start.Value.Offset, 1), error.Message));
        }
    }

    private static void ValidateNode(string path, string source, int offset, YamlNode node, ITypeSymbol type, Action<Diagnostic> report, string field)
    {
        if (field.Count(static character => character == '.') > 64)
        {
            report(Diagnostic.Create(StrictYaml, NodeLocation(path, source, offset, node), "YAML nesting is too deep."));
            return;
        }
        if (node is YamlMappingNode mapping)
        {
            var members = GeneratorModel.GetMembers((INamedTypeSymbol)type);
            var byName = members.ToDictionary(member => member.YamlName, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in mapping.Children)
            {
                if (pair.Key is not YamlScalarNode key || IsNull(key))
                {
                    report(Diagnostic.Create(StrictYaml, NodeLocation(path, source, offset, pair.Key), "YAML mapping keys must be strings."));
                    continue;
                }
                var name = (key.Value ?? string.Empty).Normalize(NormalizationForm.FormC);
                if (!seen.Add(name))
                    report(Diagnostic.Create(StrictYaml, NodeLocation(path, source, offset, key), $"Duplicate YAML field '{name}'."));
                if (!byName.TryGetValue(name, out var member))
                {
                    report(Diagnostic.Create(StrictYaml, NodeLocation(path, source, offset, key), $"Unknown YAML field '{name}'."));
                    continue;
                }
                ValidateValue(path, source, offset, pair.Value, member.Type, member.AllowsNull, report, field.Length == 0 ? name : field + "." + name);
            }
            foreach (var member in members)
            {
                if (!seen.Contains(member.YamlName) && (member.Required || !member.AllowsNull && !member.HasDefault))
                    report(Diagnostic.Create(Conversion, NodeLocation(path, source, offset, node), $"Required YAML field '{member.YamlName}' is missing."));
            }
            return;
        }
        report(Diagnostic.Create(Conversion, NodeLocation(path, source, offset, node), $"Expected a mapping for '{field}'."));
    }

    private static void ValidateValue(string path, string source, int offset, YamlNode node, ITypeSymbol type, bool allowsNull, Action<Diagnostic> report, string field)
    {
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            type = nullable.TypeArguments[0];
        if (node is YamlScalarNode scalar && IsNull(scalar))
        {
            if (!allowsNull)
                report(Diagnostic.Create(Conversion, NodeLocation(path, source, offset, node), $"Field '{field}' does not allow null."));
            return;
        }
        if (type is IArrayTypeSymbol array)
        {
            ValidateSequence(path, source, offset, node, array.ElementType, report, field);
            return;
        }
        if (type.SpecialType == SpecialType.System_Object)
        {
            ValidateUntypedNode(path, source, offset, node, report);
            return;
        }
        if (type is INamedTypeSymbol named)
        {
            if (GeneratorModel.IsList(named))
            {
                ValidateSequence(path, source, offset, node, named.TypeArguments[0], report, field);
                return;
            }
            if (GeneratorModel.IsDictionary(named))
            {
                if (node is not YamlMappingNode dictionary)
                {
                    Invalid(path, source, offset, node, report, field, "Expected a mapping.");
                    return;
                }
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pair in dictionary.Children)
                {
                    if (pair.Key is not YamlScalarNode key || IsNull(key) || !keys.Add((key.Value ?? string.Empty).Normalize(NormalizationForm.FormC)))
                        report(Diagnostic.Create(StrictYaml, NodeLocation(path, source, offset, pair.Key), "Dictionary keys must be unique non-null strings."));
                    ValidateValue(path, source, offset, pair.Value, named.TypeArguments[1], GeneratorModel.AllowsNull(named.TypeArguments[1]), report, field);
                }
                return;
            }
        }
        if (type.TypeKind == TypeKind.Enum)
        {
            var names = type.GetMembers().OfType<IFieldSymbol>().Where(static field => field.HasConstantValue).Select(static field => field.Name).ToArray();
            if (node is not YamlScalarNode enumScalar || !(ScalarValid(enumScalar.Value, ((INamedTypeSymbol)type).EnumUnderlyingType!)
                || enumScalar.Value is not null && enumScalar.Value.Split(',').All(part => names.Contains(part.Trim(), StringComparer.Ordinal))))
                Invalid(path, source, offset, node, report, field, "Invalid enum value.");
            return;
        }
        if (!GeneratorModel.IsScalar(type))
        {
            ValidateNode(path, source, offset, node, type, report, field);
            return;
        }
        if (node is not YamlScalarNode value || !ScalarValid(value.Value, type))
            Invalid(path, source, offset, node, report, field, "Value cannot be converted to the declared type.");
    }

    private static void ValidateSequence(string path, string source, int offset, YamlNode node, ITypeSymbol element, Action<Diagnostic> report, string field)
    {
        if (node is not YamlSequenceNode sequence)
        {
            Invalid(path, source, offset, node, report, field, "Expected a sequence.");
            return;
        }
        foreach (var item in sequence.Children)
            ValidateValue(path, source, offset, item, element, GeneratorModel.AllowsNull(element), report, field);
    }

    private static void ValidateUntypedNode(string path, string source, int offset, YamlNode node, Action<Diagnostic> report)
    {
        if (node is YamlMappingNode mapping)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in mapping.Children)
            {
                if (pair.Key is not YamlScalarNode key || IsNull(key) || !keys.Add((key.Value ?? string.Empty).Normalize(NormalizationForm.FormC)))
                    report(Diagnostic.Create(StrictYaml, NodeLocation(path, source, offset, pair.Key), "Mapping keys must be unique non-null strings."));
                ValidateUntypedNode(path, source, offset, pair.Value, report);
            }
        }
        else if (node is YamlSequenceNode sequence)
            foreach (var item in sequence.Children) ValidateUntypedNode(path, source, offset, item, report);
    }

    private static bool ScalarValid(string? value, ITypeSymbol type)
    {
        if (value is null) return false;
        if (type.SpecialType == SpecialType.System_String || type.SpecialType == SpecialType.System_Object) return true;
        if (type.SpecialType == SpecialType.System_Boolean) return bool.TryParse(value, out _);
        if (type.SpecialType == SpecialType.System_Char) return value.Length == 1;
        if (type.SpecialType == SpecialType.System_SByte) return sbyte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        if (type.SpecialType == SpecialType.System_Byte) return byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        if (type.SpecialType == SpecialType.System_Int16) return short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        if (type.SpecialType == SpecialType.System_Int32) return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        if (type.SpecialType == SpecialType.System_UInt16) return ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        if (type.SpecialType == SpecialType.System_UInt32) return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        if (type.SpecialType == SpecialType.System_UInt64) return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        if (type.SpecialType == SpecialType.System_Int64) return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
        if (type.SpecialType == SpecialType.System_Single) return float.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _);
        if (type.SpecialType == SpecialType.System_Double) return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _);
        if (type.SpecialType == SpecialType.System_Decimal) return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
        if (type.SpecialType == SpecialType.System_DateTime) return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
        var name = type.ToDisplayString().TrimEnd('?');
        if (name is "System.DateOnly" or "System.TimeOnly")
        {
            var method = name == "System.DateOnly" ? DateOnlyParser : TimeOnlyParser;
            if (method is null) throw new NotSupportedException("Static DateOnly/TimeOnly validation requires a modern .NET compiler host. Use dotnet build.");
            return (bool)method.Invoke(null, new object?[] { value, CultureInfo.InvariantCulture, DateTimeStyles.None, null })!;
        }
        if (name == "System.DateTimeOffset") return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
        if (name == "System.TimeSpan") return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _);
        if (name == "System.Guid") return Guid.TryParseExact(value, "D", out _);
        if (name == "System.Uri") return Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _);
        return true;
    }

    private static bool IsNull(YamlScalarNode node) =>
        node.Style is ScalarStyle.Any or ScalarStyle.Plain
        && (string.Equals(node.Value, "null", StringComparison.OrdinalIgnoreCase) || node.Value == "~" || (node.Value ?? string.Empty).Length == 0);

    private static void Invalid(string path, string source, int offset, YamlNode node, Action<Diagnostic> report, string field, string message) =>
        report(Diagnostic.Create(Conversion, NodeLocation(path, source, offset, node), $"{field}: {message}"));

    private static (string Yaml, int Offset, bool Valid)? FindFrontMatter(string markdown)
    {
        var openingStart = markdown.Length > 0 && markdown[0] == '\uFEFF' ? 1 : 0;
        var firstEnd = markdown.IndexOf('\n');
        if (firstEnd < 0) firstEnd = markdown.Length;
        if (markdown.Substring(openingStart, firstEnd - openingStart).TrimEnd('\r') != "---") return null;
        if (firstEnd == markdown.Length) return (string.Empty, 0, false);
        var bodyStart = firstEnd + 1;
        for (var lineStart = bodyStart; lineStart < markdown.Length;)
        {
            var lineEnd = markdown.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = markdown.Length;
            if (markdown.Substring(lineStart, lineEnd - lineStart).TrimEnd('\r') == "---")
                return (markdown.Substring(bodyStart, lineStart - bodyStart), bodyStart, true);
            lineStart = lineEnd + 1;
        }
        return (markdown.Substring(bodyStart), bodyStart, false);
    }

    private static Location NodeLocation(string path, string source, int offset, YamlNode node)
    {
        var text = SourceText.From(source);
        var baseLine = text.Lines.GetLineFromPosition(Math.Min(offset, text.Length)).LineNumber;
        var yamlStart = text.Lines[Math.Min(baseLine + checked((int)node.Start.Line) - 1, text.Lines.Count - 1)].Start;
        return Location(path, source, yamlStart + checked((int)node.Start.Column) - 1, Math.Max(1, checked((int)(node.End.Column - node.Start.Column))));
    }

    private static System.Reflection.MethodInfo? DateTimeParser(string name)
    {
        var type = Type.GetType(name);
        return type?.GetMethod("TryParse", new[] { typeof(string), typeof(IFormatProvider), typeof(DateTimeStyles), type.MakeByRefType() });
    }

    private static Location Location(string path, string source, int offset, int length)
    {
        var text = SourceText.From(source);
        var safe = Math.Min(Math.Max(offset, 0), text.Length);
        return Microsoft.CodeAnalysis.Location.Create(path, new TextSpan(safe, Math.Min(length, text.Length - safe)), new LinePositionSpan(text.Lines.GetLinePosition(safe), text.Lines.GetLinePosition(Math.Min(text.Length, safe + length))));
    }
}
