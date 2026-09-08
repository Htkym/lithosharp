using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace LithoSharp.Generators;

internal sealed class BinderEmitter
{
    private readonly List<ITypeSymbol> types = new();
    private readonly Dictionary<string, int> parsers = new(StringComparer.Ordinal);
    private readonly List<INamedTypeSymbol> objects = new();
    private readonly Dictionary<string, int> binders = new(StringComparer.Ordinal);

    public BinderEmitter(INamedTypeSymbol root) => AddType(root);
    public IReadOnlyList<INamedTypeSymbol> ObjectTypes => objects;

    private int AddType(ITypeSymbol type)
    {
        var key = GeneratorModel.Display(type);
        if (parsers.TryGetValue(key, out var existing)) return existing;
        var index = types.Count;
        parsers.Add(key, index);
        types.Add(type);
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            AddType(nullable.TypeArguments[0]);
        else if (type is IArrayTypeSymbol array && array.Rank == 1) AddType(array.ElementType);
        else if (GeneratorModel.IsScalar(type)) { }
        else if (type is INamedTypeSymbol list && GeneratorModel.IsList(list)) AddType(list.TypeArguments[0]);
        else if (type is INamedTypeSymbol dictionary && GeneratorModel.IsDictionary(dictionary)) AddType(dictionary.TypeArguments[1]);
        else if (type is INamedTypeSymbol model && model.TypeKind is TypeKind.Class or TypeKind.Struct
            && !model.IsAbstract && !model.IsAnonymousType && model.SpecialType == SpecialType.None)
        {
            var objectKey = GeneratorModel.Display(model.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
            if (!binders.ContainsKey(objectKey))
            {
                if (!model.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
                    throw new InvalidOperationException($"Front matter type '{model}' requires a public parameterless constructor.");
                var members = GeneratorModel.GetMembers(model);
                if (members.GroupBy(m => m.YamlName, StringComparer.Ordinal).Any(group => group.Count() > 1))
                    throw new InvalidOperationException($"Front matter type '{model}' has duplicate YAML member names.");
                if (members.Any(m => m.Symbol is IPropertySymbol property && property.GetMethod is null))
                    throw new InvalidOperationException($"Front matter type '{model}' has a property without a getter.");
                binders.Add(objectKey, objects.Count);
                objects.Add((INamedTypeSymbol)model.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
                foreach (var member in members) AddType(member.Type);
            }
        }
        else throw new InvalidOperationException($"Front matter member type '{type}' is not supported by static binding.");
        return index;
    }

    public string Emit(INamedTypeSymbol root)
    {
        var text = new StringBuilder();
        var rootName = GeneratorModel.Display(root);
        text.AppendLine($"private sealed class BinderImpl : global::LithoSharp.Content.IContentFrontMatterBinder<{rootName}>");
        text.AppendLine("{");
        text.AppendLine($"public global::LithoSharp.Content.ContentParseResult<{rootName}> Bind(global::System.Collections.Generic.IReadOnlyDictionary<string, object?> values, global::LithoSharp.Diagnostics.SiteSourceLocation? sourceLocation = null)");
        text.AppendLine("{");
        text.AppendLine("var binding = new global::LithoSharp.Content.GeneratedContentBindingContext(values, sourceLocation);");
        text.AppendLine("return binding.Complete(Bind_0(binding));");
        text.AppendLine("}");
        for (var index = 0; index < objects.Count; index++) EmitObject(text, objects[index], index);
        for (var index = 0; index < types.Count; index++) EmitParser(text, types[index], index);
        text.AppendLine("}");
        return text.ToString();
    }

    private void EmitObject(StringBuilder text, INamedTypeSymbol type, int index)
    {
        var name = GeneratorModel.Display(type);
        var members = GeneratorModel.GetMembers(type);
        var required = GeneratorModel.HasRequiredMembers(type);
        var constructor = type.InstanceConstructors.First(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
        if (required && !(type.IsValueType && constructor.IsImplicitlyDeclared))
        {
            text.AppendLine("[global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Constructor)]");
            text.AppendLine($"private static extern {name} Create_{index}();");
        }
        else text.AppendLine($"private static {name} Create_{index}() => {(required ? $"default({name})" : $"new {name}()")};");
        text.AppendLine($"private static {name} Bind_{index}(global::LithoSharp.Content.GeneratedContentBindingContext binding)");
        text.AppendLine("{");
        text.AppendLine($"var instance = Create_{index}();");
        text.AppendLine("foreach (var yamlName in binding.Values.Keys)");
        text.AppendLine("{");
        text.AppendLine("switch (yamlName)");
        text.AppendLine("{");
        for (var memberIndex = 0; memberIndex < members.Count; memberIndex++)
        {
            var member = members[memberIndex];
            var memberType = GeneratorModel.Display(member.Type);
            var local = $"value_{memberIndex}";
            text.AppendLine($"case {GeneratorModel.Literal(member.YamlName)}:");
            text.AppendLine($"if (binding.TryRead<{memberType}>(yamlName, default!, {Bool(member.Required)}, {Bool(member.AllowsNull)}, Parse_{parsers[memberType]}, out var {local}))");
            text.AppendLine("{");
            var setter = member.Symbol is IPropertySymbol property && property.SetMethod!.IsInitOnly
                ? $"Set_{index}_{memberIndex}({(type.IsValueType ? "ref " : "")}instance, {local});"
                : $"{MemberAccess(type, member)} = {local};";
            text.AppendLine("try { " + setter + " }");
            text.AppendLine("catch (global::System.Exception failure) when (failure is global::System.ArgumentException or global::System.FormatException or global::System.InvalidOperationException) { binding.Reject(yamlName, failure); }");
            text.AppendLine("}");
            text.AppendLine("break;");
        }
        text.AppendLine("}");
        text.AppendLine("}");
        for (var memberIndex = 0; memberIndex < members.Count; memberIndex++)
        {
            var member = members[memberIndex];
            var getter = member.Symbol is IPropertySymbol property && property.GetMethod!.DeclaredAccessibility != Accessibility.Public
                ? $"Get_{index}_{memberIndex}({(type.IsValueType ? "ref " : "")}instance)"
                : MemberAccess(type, member);
            text.AppendLine($"if (!binding.Values.ContainsKey({GeneratorModel.Literal(member.YamlName)})) binding.ValidateRequired({GeneratorModel.Literal(member.YamlName)}, {getter}, {Bool(member.Required)}, {Bool(member.AllowsNull)});");
            // Present names were registered by TryRead; absent names are registered by ValidateRequired.
        }
        text.AppendLine("binding.ValidateUnknownFields();");
        text.AppendLine("return instance;");
        text.AppendLine("}");
        for (var memberIndex = 0; memberIndex < members.Count; memberIndex++)
        {
            var member = members[memberIndex];
            if (member.Symbol is not IPropertySymbol property) continue;
            var owner = GeneratorModel.Display(property.ContainingType);
            var parameter = (type.IsValueType ? "ref " : string.Empty) + owner + " instance";
            if (property.SetMethod!.IsInitOnly)
            {
                text.AppendLine($"[global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = {GeneratorModel.Literal("set_" + member.Name)})]");
                text.AppendLine($"private static extern void Set_{index}_{memberIndex}({parameter}, {GeneratorModel.Display(member.Type)} value);");
            }
            if (property.GetMethod!.DeclaredAccessibility != Accessibility.Public)
            {
                text.AppendLine($"[global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = {GeneratorModel.Literal("get_" + member.Name)})]");
                text.AppendLine($"private static extern {GeneratorModel.Display(member.Type)} Get_{index}_{memberIndex}({parameter});");
            }
        }
    }

    private void EmitParser(StringBuilder text, ITypeSymbol type, int index)
    {
        var name = GeneratorModel.Display(type);
        text.AppendLine($"private static bool Parse_{index}(object input, global::LithoSharp.Content.GeneratedContentBindingContext context, out {name} value)");
        text.AppendLine("{");
        if (type.SpecialType != SpecialType.System_Object && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
            text.AppendLine($"if (input is {GeneratorModel.Display(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated))} existing) {{ value = existing; return true; }}");
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            text.AppendLine($"var success = Parse_{parsers[GeneratorModel.Display(nullable.TypeArguments[0])]}(input, context, out var underlying);");
            text.AppendLine("value = underlying; return success;");
        }
        else if (type is IArrayTypeSymbol array)
            text.AppendLine($"return context.TryArray<{GeneratorModel.Display(array.ElementType)}>(input, {Bool(GeneratorModel.AllowsNull(array.ElementType))}, Parse_{parsers[GeneratorModel.Display(array.ElementType)]}, out value);");
        else if (GeneratorModel.IsScalar(type))
            text.AppendLine($"return global::LithoSharp.Content.GeneratedContentBindingContext.{(type.TypeKind == TypeKind.Enum ? "TryParseEnum" : "TryParseScalar")}<{name}>(input, context, out value);");
        else if (type is INamedTypeSymbol list && GeneratorModel.IsList(list))
        {
            var element = list.TypeArguments[0];
            text.AppendLine($"var success = context.TryList<{GeneratorModel.Display(element)}>(input, {Bool(GeneratorModel.AllowsNull(element))}, Parse_{parsers[GeneratorModel.Display(element)]}, out var items);");
            text.AppendLine("value = items; return success;");
        }
        else if (type is INamedTypeSymbol dictionary && GeneratorModel.IsDictionary(dictionary))
        {
            var element = dictionary.TypeArguments[1];
            text.AppendLine($"var success = context.TryDictionary<{GeneratorModel.Display(element)}>(input, {Bool(GeneratorModel.AllowsNull(element))}, Parse_{parsers[GeneratorModel.Display(element)]}, out var items);");
            text.AppendLine("value = items; return success;");
        }
        else text.AppendLine($"return context.TryObject(input, Bind_{binders[GeneratorModel.Display(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated))]}, out value);");
        text.AppendLine("}");
    }

    private static string MemberAccess(INamedTypeSymbol type, MemberModel member) =>
        (SymbolEqualityComparer.Default.Equals(type, member.Symbol.ContainingType)
            ? "instance"
            : $"(({GeneratorModel.Display(member.Symbol.ContainingType)})instance)")
        + "." + GeneratorModel.Identifier(member.Name);

    private static string Bool(bool value) => value ? "true" : "false";
}
