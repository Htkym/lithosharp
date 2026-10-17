using LithoSharp.Diagnostics;

namespace LithoSharp.Build;

public sealed class SiteBuildPlanValidationResult
{
    internal SiteBuildPlanValidationResult(
        SiteBuildPlan? plan,
        IReadOnlyList<SiteDiagnostic> diagnostics)
    {
        Plan = plan;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public SiteBuildPlan? Plan { get; }

    public bool IsValid => Plan is not null;

    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
}

public sealed class SiteBuildPlanValidationException : InvalidOperationException
{
    internal SiteBuildPlanValidationException(IReadOnlyList<SiteDiagnostic> diagnostics)
        : base($"Site build plan validation failed with {diagnostics.Count} diagnostic(s).")
    {
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
}
