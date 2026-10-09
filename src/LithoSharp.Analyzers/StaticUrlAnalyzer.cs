using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using LithoSharp.Internal;
using LithoSharp.Routing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace LithoSharp.Analyzers;

/// <summary>Reports proven misuse of registered LithoSharp APIs without executing user code.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StaticUrlAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Url = Rule("LSA1001", "Invalid absolute site URL");
    private static readonly DiagnosticDescriptor Route = Rule("LSA1002", "Invalid site route");
    private static readonly DiagnosticDescriptor Path = Rule("LSA1003", "Invalid relative output path");
    private static readonly DiagnosticDescriptor Option = Rule("LSA1004", "Invalid site quality option");

    private static DiagnosticDescriptor Rule(string id, string title) => new(id, title,
        "Every known value of '{0}' violates the {1} runtime contract", "LithoSharp",
        DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://github.com/Htkym/lithosharp/blob/feature/2.0.0/src/LithoSharp.Analyzers/README.md#" + id.ToLowerInvariant());

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Url, Route, Path, Option);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var sinks = RegisterSinks(start.Compilation);
            if (sinks.Count == 0) return;
            start.RegisterOperationBlockAction(block => StaticValueFlow.Analyze(block, (operation, evaluate) =>
            {
                block.CancellationToken.ThrowIfCancellationRequested();
                var method = operation is IInvocationOperation call ? call.TargetMethod
                    : (operation as IObjectCreationOperation)?.Constructor;
                if (method is null || !sinks.TryGetValue(method.OriginalDefinition, out var rules)) return;
                var arguments = operation is IInvocationOperation invocation ? invocation.Arguments
                    : ((IObjectCreationOperation)operation).Arguments;
                if (StaticValueFlow.HasBindingError(operation)) return;
                foreach (var rule in rules)
                {
                    var argument = arguments.FirstOrDefault(a => a.Parameter?.Ordinal == rule.Ordinal);
                    if (argument is null || argument.IsImplicit) continue;
                    var values = evaluate(argument.Value);
                    if (values.Values is null || !values.Values.All(rule.Invalid)) continue;
                    var properties = ImmutableDictionary<string, string?>.Empty
                        .Add("staticState", "Known").Add("candidateCount", values.Values.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .Add("origin", "original-argument-utf16-span");
                    block.ReportDiagnostic(Diagnostic.Create(rule.Descriptor, argument.Value.Syntax.GetLocation(),
                        properties, argument.Parameter!.Name, method.ContainingType.Name + "." + method.Name));
                }
            }));
        });
    }

    private static Dictionary<IMethodSymbol, List<Sink>> RegisterSinks(Compilation compilation)
    {
        var result = new Dictionary<IMethodSymbol, List<Sink>>(SymbolEqualityComparer.Default);
        void Register(string metadataName, string member, string[] parameterTypes, params Sink[] rules)
        {
            var type = compilation.GetTypeByMetadataName(metadataName);
            if (type?.ContainingAssembly.Identity.Name != "LithoSharp" || type.Locations.Any(l => l.IsInSource)) return;
            foreach (var method in type.GetMembers(member).OfType<IMethodSymbol>().Where(m => m.Arity == 0
                && m.DeclaredAccessibility == Accessibility.Public && m.Parameters.Length == parameterTypes.Length
                && m.Parameters.All(p => p.RefKind == RefKind.None)))
            {
                if (!method.Parameters.Select(p => p.Type.ToDisplayString()).SequenceEqual(parameterTypes)) continue;
                if (member != ".ctor" && (!method.IsStatic || !SymbolEqualityComparer.Default.Equals(method.ReturnType, type))) continue;
                result.Add(method.OriginalDefinition, rules.ToList());
            }
        }
        var strings = new[] { "string", "string?" };
        Register("LithoSharp.SiteUrl", "FromAbsolute", new[] { "string" }, new Sink(0, Url, v => Reject(() => StaticApiGuards.ValidateAbsoluteUrl((string)v!))));
        foreach (var type in new[] { "LithoSharp.SiteUrl", "LithoSharp.Routing.SiteRoute" })
        {
            Register(type, "ForFile", strings, new Sink(0, Route, v => Reject(() => SiteRoute.ForFile((string)v!))),
                new Sink(1, Route, v => Reject(() => SiteRoute.ForFile("valid.html", (string?)v))));
            Register(type, type.EndsWith("SiteUrl", StringComparison.Ordinal) ? "ForDirectory" : "ForDirectoryIndex", strings,
                new Sink(0, Route, v => Reject(() => SiteRoute.ForDirectoryIndex((string)v!))),
                new Sink(1, Route, v => Reject(() => SiteRoute.ForDirectoryIndex("", (string?)v))));
        }
        Register("LithoSharp.SiteAssetOutput", ".ctor", new[] { "string", "string" },
            new Sink(1, Path, v => Reject(() => SiteRoute.NormalizeRelativeOutputPath((string)v!))));
        Register("LithoSharp.Quality.SiteQualityOptions", ".ctor",
            new[] { "LithoSharp.Diagnostics.SiteDiagnosticSeverity", "bool", "LithoSharp.Quality.ExternalLinkCheckOptions?" },
            new Sink(0, Option, v => v is int severity && Reject(() => StaticApiGuards.ValidateFailureThreshold(severity))));
        return result;
    }

    private static bool Reject(Action guard)
    {
        try { guard(); return false; }
        catch (ArgumentException) { return true; }
        catch (UriFormatException) { return true; }
    }

    private sealed class Sink
    {
        internal Sink(int ordinal, DiagnosticDescriptor descriptor, Func<object?, bool> invalid)
        { Ordinal = ordinal; Descriptor = descriptor; Invalid = invalid; }
        internal int Ordinal { get; }
        internal DiagnosticDescriptor Descriptor { get; }
        internal Func<object?, bool> Invalid { get; }
    }
}
