using LithoSharp.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace LithoSharp.Analyzers;

// A metadata-only migration notice. No DOM, getter or user code is executed.
internal static class TestingDomMigrationAnalysis
{
    internal static readonly DiagnosticDescriptor Rule = LegacyTestingDomContract.Rule;

    internal static void Register(CompilationStartAnalysisContext start)
    {
        LegacyTestingDomContract.Register(start);
    }
}
