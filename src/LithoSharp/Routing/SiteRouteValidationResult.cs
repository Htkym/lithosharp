using LithoSharp.Diagnostics;

namespace LithoSharp.Routing;

public sealed class SiteRouteValidationResult
{
    internal SiteRouteValidationResult(IReadOnlyList<SiteDiagnostic> diagnostics)
    {
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        IsValid = !Diagnostics.Any(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error);
    }

    public bool IsValid { get; }

    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
}

public sealed class SiteRouteValidationException : InvalidOperationException
{
    internal SiteRouteValidationException(IReadOnlyList<SiteDiagnostic> diagnostics)
        : base(BuildMessage(diagnostics))
    {
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }

    private static string BuildMessage(IReadOnlyList<SiteDiagnostic> diagnostics)
    {
        var message = $"Site route validation failed with {diagnostics.Count} diagnostic(s).";
        if (diagnostics.Any(diagnostic =>
                diagnostic.Message.Contains("'common:", StringComparison.Ordinal)))
        {
            message += " A template output conflicts with a common artifact.";
        }

        return message;
    }
}
