using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

// The CFG owns values; the original semantic parent chain proves only sink identity.
internal sealed class GenerationOptionsAnalysis
{
    internal static readonly DiagnosticDescriptor Rule = new("LSA1005", "Invalid site generation option",
        "Every known value of '{0}' violates the SiteGenerator.GenerateWithOptionsAsync runtime contract",
        "LithoSharp", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/src/LithoSharp.Analyzers/README.md#lsa1005");
    private readonly IMethodSymbol method;
    private readonly IMethodSymbol constructor;
    private readonly IPropertySymbol property;

    private GenerationOptionsAnalysis(IMethodSymbol method, IMethodSymbol constructor, IPropertySymbol property)
    { this.method = method; this.constructor = constructor; this.property = property; }

    internal static GenerationOptionsAnalysis? Create(Compilation compilation)
    {
        var generator = compilation.GetTypeByMetadataName("LithoSharp.SiteGenerator");
        var options = compilation.GetTypeByMetadataName("LithoSharp.SiteGenerationOptions");
        var result = compilation.GetTypeByMetadataName("LithoSharp.SiteGenerationResult");
        var site = compilation.GetTypeByMetadataName("LithoSharp.Configuration.SiteSettings");
        var post = compilation.GetTypeByMetadataName("LithoSharp.Content.MarkdownPost");
        var customization = compilation.GetTypeByMetadataName("LithoSharp.SiteCustomization");
        var owned = new[] { generator, options, result, site, post, customization };
        if (generator is null || options is not { Arity: 0, IsSealed: true }
            || owned.Any(type => type is null || type.ContainingAssembly.Identity.Name != "LithoSharp"
                || type.Locations.Any(location => location.IsInSource)
                || !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, generator.ContainingAssembly))) return null;
        var task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
        var list = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1");
        var token = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
        if (task is null || list is null || token is null) return null;
        var signature = new ITypeSymbol[] { site!, list.Construct(post!), compilation.GetSpecialType(SpecialType.System_String),
            compilation.GetSpecialType(SpecialType.System_Boolean), customization!, options, token };
        var methods = generator.GetMembers("GenerateWithOptionsAsync").OfType<IMethodSymbol>().Where(candidate =>
            candidate.MethodKind == MethodKind.Ordinary && !candidate.IsStatic && candidate.Arity == 0
            && candidate.DeclaredAccessibility == Accessibility.Public && !candidate.ReturnsByRef && !candidate.ReturnsByRefReadonly
            && SymbolEqualityComparer.Default.Equals(candidate.ReturnType, task.Construct(result!))
            && candidate.Parameters.Length == 7 && candidate.Parameters.All(parameter => parameter.RefKind == RefKind.None && !parameter.IsOptional)
            && candidate.Parameters.Select((parameter, index) => SymbolEqualityComparer.Default.Equals(parameter.Type, signature[index])).All(same => same)).ToArray();
        var constructors = options.InstanceConstructors.Where(candidate => candidate.DeclaredAccessibility == Accessibility.Public
            && candidate.Parameters.Length == 0).ToArray();
        var properties = options.GetMembers("MaxDegreeOfParallelism").OfType<IPropertySymbol>().Where(candidate =>
            !candidate.IsStatic && !candidate.IsIndexer && !candidate.ReturnsByRef && !candidate.ReturnsByRefReadonly
            && candidate.DeclaredAccessibility == Accessibility.Public && candidate.Type.SpecialType == SpecialType.System_Int32
            && candidate.GetMethod?.DeclaredAccessibility == Accessibility.Public
            && candidate.SetMethod is { IsInitOnly: true, DeclaredAccessibility: Accessibility.Public }).ToArray();
        return methods.Length == 1 && constructors.Length == 1 && properties.Length == 1
            ? new(methods[0].OriginalDefinition, constructors[0].OriginalDefinition, properties[0]) : null;
    }

    internal void Analyze(OperationBlockAnalysisContext context, IOperation operation, Func<IOperation, StaticValue> evaluate)
    {
        if (operation is not ISimpleAssignmentOperation { IsRef: false, Target: IPropertyReferenceOperation target } assignment
            || !SymbolEqualityComparer.Default.Equals(target.Property, property)) return;
        var model = context.Compilation.GetSemanticModel(operation.Syntax.SyntaxTree);
        if (model.GetOperation(operation.Syntax, context.CancellationToken) is not ISimpleAssignmentOperation original
            || original.Parent is not IObjectOrCollectionInitializerOperation initializer
            || initializer.Parent is not IObjectCreationOperation creation
            || !SymbolEqualityComparer.Default.Equals(creation.Constructor?.OriginalDefinition, constructor)
            || original.Target is not IPropertyReferenceOperation originalTarget
            || !SymbolEqualityComparer.Default.Equals(originalTarget.Property, property)) return;
        IOperation owner = creation;
        while (owner.Parent is IParenthesizedOperation
            || owner.Parent is IConversionOperation { IsImplicit: true, Conversion.IsIdentity: true }) owner = owner.Parent;
        if (owner.Parent is not IArgumentOperation { ArgumentKind: ArgumentKind.Explicit, Parameter.Ordinal: 5 } argument
            || argument.Parent is not IInvocationOperation call
            || !SymbolEqualityComparer.Default.Equals(call.TargetMethod.OriginalDefinition, method)
            || StaticValueFlow.HasBindingError(call)) return;
        // Never evaluate the original syntax or the final invocation state. In a split
        // CFG a missing/invalidated capture stays Unknown at this assignment.
        var value = evaluate(assignment.Value);
        if (value.Values is not { Length: > 0 } values || !values.All(candidate => candidate is int number && number < 1)) return;
        var properties = ImmutableDictionary<string, string?>.Empty.Add("staticState", "Known")
            .Add("candidateCount", values.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("origin", "original-initializer-value-utf16-span").Add("optionsArgumentOrdinal", "5");
        var expression = original.Syntax is AssignmentExpressionSyntax syntax ? syntax.Right : original.Value.Syntax;
        context.ReportDiagnostic(Diagnostic.Create(Rule, expression.GetLocation(), properties, property.Name));
    }
}
