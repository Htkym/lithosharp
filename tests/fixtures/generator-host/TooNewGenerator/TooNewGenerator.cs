using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LithoSharp.Fixtures.GeneratorHost;

/// <summary>
/// Deliberately invalid fixture: this generator references Microsoft.CodeAnalysis 5.9.0,
/// which is newer than the compiler of the documented minimum SDK. It exists only so that
/// eng/Test-CompatibilityGate.ps1 can prove that the host compatibility check detects it.
/// Do not reference this project from the solution, tests or packages.
/// </summary>
[Generator]
public sealed class TooNewGenerator : IIncrementalGenerator
{
    private static readonly LanguageVersion CompatibleCSharpVersion = LanguageVersion.Latest;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        _ = CompatibleCSharpVersion;
    }
}
