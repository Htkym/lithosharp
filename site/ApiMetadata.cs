using LithoSharp.Documentation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class ApiMetadata
{
    internal static readonly string[] Packages = ["LithoSharp", "LithoSharp.Mdx", "LithoSharp.Images", "LithoSharp.Testing"];

    internal sealed record Snapshot(CSharpCompilation Compilation, IReadOnlyDictionary<string, IReadOnlyList<ISymbol>> Assemblies);

    internal static Snapshot Read(string assemblyDirectory)
    {
        var paths = (((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator) ?? [])
            .Concat(Packages.Select(package => Path.Combine(assemblyDirectory, package + ".dll")))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var references = paths.ToDictionary(path => path, path => MetadataReference.CreateFromFile(path), StringComparer.OrdinalIgnoreCase);
        var compilation = CSharpCompilation.Create("WebsitePublicApi", references: references.Values,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var assemblies = Packages.ToDictionary(package => package, package =>
        {
            var reference = references[Path.Combine(assemblyDirectory, package + ".dll")];
            var assembly = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(reference)!;
            return (IReadOnlyList<ISymbol>)Namespace(assembly.GlobalNamespace).OrderBy(symbol => symbol.GetDocumentationCommentId(), StringComparer.Ordinal).ToArray();
        }, StringComparer.Ordinal);
        return new(compilation, assemblies);
    }

    private static IEnumerable<ISymbol> Namespace(INamespaceSymbol space) => space.GetTypeMembers().SelectMany(Type)
        .Concat(space.GetNamespaceMembers().SelectMany(Namespace));

    private static IEnumerable<ISymbol> Type(INamedTypeSymbol type)
    {
        if (!Exported(type))
            yield break;
        yield return type;
        foreach (var member in type.GetMembers())
        {
            if (member is INamedTypeSymbol nested)
            {
                foreach (var child in Type(nested))
                    yield return child;
            }
            else if (Exported(member) && member.GetDocumentationCommentId() is not null
                && member is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise })
                yield return member;
        }
    }

    internal static bool Exported(ISymbol symbol) => symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
        && (symbol.ContainingType is null || Exported(symbol.ContainingType));

    private static readonly SymbolDisplayFormat Signature = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeAccessibility
            | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeConstantValue | SymbolDisplayMemberOptions.IncludeRef,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeExtensionThis,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword | SymbolDisplayKindOptions.IncludeMemberKeyword,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
    private static readonly SymbolDisplayFormat TypeName = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    internal static ApiSymbol Project(ISymbol symbol, ISet<string> included)
    {
        var parameters = symbol switch
        {
            IMethodSymbol parameterMethod => parameterMethod.Parameters.AsEnumerable(),
            IPropertySymbol indexedProperty => indexedProperty.Parameters.AsEnumerable(),
            INamedTypeSymbol { DelegateInvokeMethod: { } invoke } => invoke.Parameters.AsEnumerable(),
            _ => []
        };
        var inherited = InheritedDocumentationTarget(symbol);
        var inheritance = symbol is INamedTypeSymbol type
            ? type.Interfaces.Cast<INamedTypeSymbol>().Concat(type.BaseType is { } parent ? [parent] : []).Select(value => value.OriginalDefinition.GetDocumentationCommentId())
            : [];
        return new(symbol.GetDocumentationCommentId()!, symbol.ToDisplayString(Signature), symbol.DeclaredAccessibility switch
        {
            Accessibility.Protected => ApiVisibility.Protected,
            Accessibility.ProtectedOrInternal => ApiVisibility.ProtectedInternal,
            _ => ApiVisibility.Public
        })
        {
            Parameters = parameters.Select(parameter => new ApiParameter(parameter.Name, (parameter.IsParams ? "params " : "")
                + (parameter.RefKind switch { RefKind.None => "", RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "ref readonly " })
                + parameter.Type.ToDisplayString(TypeName))).ToArray(),
            ReturnType = symbol is IMethodSymbol { MethodKind: not MethodKind.Constructor and not MethodKind.StaticConstructor } method
                ? method.ReturnType.ToDisplayString(TypeName) : symbol is IPropertySymbol property ? property.Type.ToDisplayString(TypeName) : null,
            GenericConstraints = (symbol is IMethodSymbol genericMethod ? genericMethod.TypeParameters.AsEnumerable()
                : symbol is INamedTypeSymbol genericType ? genericType.TypeParameters.AsEnumerable() : [])
                .Select(Constraints).Where(constraint => constraint is not null).Select(constraint => constraint!).ToArray(),
            Inheritance = inheritance.Where(id => id is not null && included.Contains(id)).Select(id => id!).Distinct(StringComparer.Ordinal).ToArray(),
            InheritDocId = inherited?.GetDocumentationCommentId(),
            ObsoleteMessage = symbol.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "System.ObsoleteAttribute")
                is { } obsolete ? obsolete.ConstructorArguments.Length == 0 ? "" : obsolete.ConstructorArguments[0].Value?.ToString() ?? "" : null
        };
    }

    internal static ISymbol? InheritedDocumentationTarget(ISymbol symbol)
    {
        var overridden = symbol switch
        {
            IMethodSymbol method => (ISymbol?)method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            INamedTypeSymbol type => type.BaseType,
            _ => null
        };
        if (overridden is not null)
            return overridden.OriginalDefinition;
        if (symbol.ContainingType is not { } containing)
            return null;
        var targets = containing.AllInterfaces.SelectMany(type => type.GetMembers())
            .Where(member => SymbolEqualityComparer.Default.Equals(containing.FindImplementationForInterfaceMember(member)?.OriginalDefinition, symbol.OriginalDefinition))
            .Select(member => member.OriginalDefinition)
            .GroupBy(member => member.GetDocumentationCommentId(), StringComparer.Ordinal).Select(group => group.First()).ToArray();
        return targets.Length == 1 ? targets[0] : null;
    }

    private static string? Constraints(ITypeParameterSymbol parameter)
    {
        var values = new List<string>();
        if (parameter.HasUnmanagedTypeConstraint) values.Add("unmanaged");
        else if (parameter.HasValueTypeConstraint) values.Add("struct");
        else if (parameter.HasReferenceTypeConstraint) values.Add(parameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
        if (parameter.HasNotNullConstraint) values.Add("notnull");
        values.AddRange(parameter.ConstraintTypes.Select(type => type.ToDisplayString(TypeName)));
        if (parameter.HasConstructorConstraint && !parameter.HasValueTypeConstraint) values.Add("new()");
        return values.Count == 0 ? null : "where " + parameter.Name + " : " + string.Join(", ", values);
    }
}
