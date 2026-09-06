using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using YamlDotNet.Serialization.NamingConventions;

namespace LithoSharp.Generators;

internal sealed class MemberModel
{
    public MemberModel(ISymbol symbol, ITypeSymbol type)
    {
        Symbol = symbol;
        Type = type;
        var alias = GeneratorModel.GetYamlAttribute(symbol, "YamlDotNet.Serialization.YamlMemberAttribute")
            ?.NamedArguments.FirstOrDefault(a => a.Key == "Alias").Value.Value as string;
        YamlName = (string.IsNullOrEmpty(alias) ? UnderscoredNamingConvention.Instance.Apply(symbol.Name) : alias!).Normalize(NormalizationForm.FormC);
        AllowsNull = GeneratorModel.AllowsNull(type);
        Required = symbol is IPropertySymbol p ? p.IsRequired : symbol is IFieldSymbol f && f.IsRequired;
        // ponytail: constructor and metadata defaults are unknown here; the runtime binder validates them.
        HasDefault = type.IsValueType || symbol.DeclaringSyntaxReferences.Length == 0
            || symbol.ContainingType.InstanceConstructors.Any(constructor => constructor.Parameters.Length == 0 && !constructor.IsImplicitlyDeclared)
            || symbol.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax() switch
        {
            PropertyDeclarationSyntax property => HasInitializer(property.Initializer)
                || property.ExpressionBody is not null || property.AccessorList?.Accessors.Any(accessor => accessor.Body is not null || accessor.ExpressionBody is not null) == true,
            VariableDeclaratorSyntax field => HasInitializer(field.Initializer),
            _ => false
        });
    }

    private static bool HasInitializer(EqualsValueClauseSyntax? initializer)
    {
        var expression = initializer?.Value;
        while (expression is PostfixUnaryExpressionSyntax suppression) expression = suppression.Operand;
        return expression is not null && !expression.IsKind(SyntaxKind.NullLiteralExpression)
            && !expression.IsKind(SyntaxKind.DefaultLiteralExpression) && expression is not DefaultExpressionSyntax;
    }

    public ISymbol Symbol { get; }
    public string Name => Symbol.Name;
    public string YamlName { get; }
    public ITypeSymbol Type { get; }
    public bool AllowsNull { get; }
    public bool Required { get; }
    public bool HasDefault { get; }
}

internal static class GeneratorModel
{
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static string Display(ITypeSymbol type) => type.ToDisplayString(TypeFormat);
    public static string Identifier(string name) => SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;
    public static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
    public static bool AllowsNull(ITypeSymbol type) =>
        type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
        || type.IsReferenceType && type.NullableAnnotation != NullableAnnotation.NotAnnotated;

    public static IReadOnlyList<MemberModel> GetMembers(INamedTypeSymbol type)
    {
        var properties = new List<MemberModel>();
        var fields = new List<MemberModel>();
        var propertySlots = new List<IPropertySymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic) continue;
                if (member is IPropertySymbol property)
                {
                    // Reflection hides a base property by signature before filtering its accessors or YAML attributes.
                    // Private properties participate only when declared on the reflected type.
                    if (property.DeclaredAccessibility == Accessibility.Private
                        && !SymbolEqualityComparer.Default.Equals(current, type)) continue;
                    if (propertySlots.Any(slot => SamePropertySignature(slot, property))) continue;
                    propertySlots.Add(property);
                    if (!property.IsIndexer && property.SetMethod?.DeclaredAccessibility == Accessibility.Public
                        && GetYamlAttribute(property, "YamlDotNet.Serialization.YamlIgnoreAttribute") is null)
                        properties.Add(new MemberModel(property, property.Type));
                }
                else if (member is IFieldSymbol field && field.DeclaredAccessibility == Accessibility.Public
                    && !field.IsReadOnly && !field.IsConst
                    && GetYamlAttribute(field, "YamlDotNet.Serialization.YamlIgnoreAttribute") is null)
                    fields.Add(new MemberModel(field, field.Type));
            }
        }
        return properties.Concat(fields).ToArray();
    }

    private static bool SamePropertySignature(IPropertySymbol left, IPropertySymbol right) =>
        left.Name == right.Name && SymbolEqualityComparer.Default.Equals(left.Type, right.Type)
        && left.Parameters.Length == right.Parameters.Length
        && left.Parameters.Zip(right.Parameters, (a, b) =>
            a.RefKind == b.RefKind && SymbolEqualityComparer.Default.Equals(a.Type, b.Type)).All(equal => equal);

    internal static AttributeData? GetYamlAttribute(ISymbol member, string metadataName)
    {
        for (ISymbol? current = member; current is not null;
             current = current is IPropertySymbol property ? property.OverriddenProperty : null)
        {
            var attribute = current.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == metadataName);
            if (attribute is not null) return attribute;
        }
        return null;
    }

    public static bool HasRequiredMembers(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.GetMembers().Any(member => member is IPropertySymbol { IsRequired: true }
                or IFieldSymbol { IsRequired: true })) return true;
        return false;
    }

    public static bool IsScalar(ITypeSymbol type) => type.TypeKind == TypeKind.Enum
        || type.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_String
        || type.SpecialType == SpecialType.System_Object
        || type.ToDisplayString().TrimEnd('?') is "System.Guid" or "System.DateTime" or "System.DateTimeOffset"
            or "System.DateOnly" or "System.TimeOnly" or "System.TimeSpan" or "System.Uri";

    public static bool IsList(INamedTypeSymbol type) => type.IsGenericType
        && type.ContainingNamespace.ToDisplayString() == "System.Collections.Generic"
        && type.Name is "List" or "IList" or "IReadOnlyList" or "ICollection" or "IReadOnlyCollection" or "IEnumerable";

    public static bool IsDictionary(INamedTypeSymbol type) => type.IsGenericType
        && type.ContainingNamespace.ToDisplayString() == "System.Collections.Generic"
        && type.Name is "Dictionary" or "IDictionary" or "IReadOnlyDictionary"
        && type.TypeArguments[0].SpecialType == SpecialType.System_String;
}
