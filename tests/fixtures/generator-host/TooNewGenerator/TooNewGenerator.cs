using Microsoft.CodeAnalysis;

namespace LithoSharp.Fixtures.GeneratorHost;

/// <summary>
/// Fixture-only generator with a real PE reference to a deliberately future Roslyn identity.
/// SDK updates must not turn this negative compatibility case into a positive case.
/// Do not reference this project from the solution, tests or packages.
/// </summary>
[Generator]
public sealed class TooNewGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
    }
}
