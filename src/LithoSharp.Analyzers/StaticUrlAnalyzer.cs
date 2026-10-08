using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

/// <summary>Bootstrap analyzer for a proven non-HTTP(S) constant passed to SiteUrl.FromAbsolute.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StaticUrlAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor NonHttpUrl = new("LSA1001", "Unsupported absolute site URL scheme",
        "SiteUrl.FromAbsolute accepts only HTTP or HTTPS URLs; scheme '{0}' is not supported", "LithoSharp",
        DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(NonHttpUrl);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            start.CancellationToken.ThrowIfCancellationRequested();
            var type = start.Compilation.GetTypeByMetadataName("LithoSharp.SiteUrl");
            if (type?.ContainingAssembly.Identity.Name != "LithoSharp") return;
            var methods = type.GetMembers("FromAbsolute").OfType<IMethodSymbol>().Where(m => m.IsStatic
                && m.DeclaredAccessibility == Accessibility.Public && m.Arity == 0 && m.Parameters.Length == 1
                && m.Parameters[0].RefKind == RefKind.None && m.Parameters[0].Type.SpecialType == SpecialType.System_String
                && SymbolEqualityComparer.Default.Equals(m.ReturnType, type)).ToArray();
            if (methods.Length != 1) return;
            var method = methods[0];
            start.RegisterOperationAction(operation =>
            {
                operation.CancellationToken.ThrowIfCancellationRequested();
                var invocation = (IInvocationOperation)operation.Operation;
                if (!SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.OriginalDefinition, method)
                    || invocation.Arguments.Length != 1) return;
                var argument = invocation.Arguments[0].Value;
                if (!argument.ConstantValue.HasValue || argument.ConstantValue.Value is not string value
                    || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    || !value.StartsWith(uri.Scheme + ":", StringComparison.OrdinalIgnoreCase)
                    || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) return;
                operation.ReportDiagnostic(Diagnostic.Create(NonHttpUrl, argument.Syntax.GetLocation(), uri.Scheme));
            }, OperationKind.Invocation);
        });
    }
}
