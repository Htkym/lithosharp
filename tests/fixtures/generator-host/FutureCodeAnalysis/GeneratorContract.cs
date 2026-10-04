// Fixture-only contract: the assembly identity deliberately exceeds real compiler versions.
// Never reference this project from product code or distribute its assembly.
namespace Microsoft.CodeAnalysis;

public interface IIncrementalGenerator
{
    void Initialize(IncrementalGeneratorInitializationContext context);
}

public readonly struct IncrementalGeneratorInitializationContext;

public sealed class GeneratorAttribute : System.Attribute;
