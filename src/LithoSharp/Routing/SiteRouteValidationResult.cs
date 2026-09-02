using LithoSharp.Diagnostics;

namespace LithoSharp.Routing;

/// <summary>サイトルート表の検証結果を表します。</summary>
public sealed class SiteRouteValidationResult
{
    internal SiteRouteValidationResult(IReadOnlyList<SiteDiagnostic> diagnostics)
    {
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        IsValid = !Diagnostics.Any(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error);
    }

    /// <summary>エラー診断がない場合は <see langword="true"/> を取得します。</summary>
    public bool IsValid { get; }

    /// <summary>安定した順序に並べられた診断を取得します。</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
}

/// <summary>サイトルート表にエラー診断が含まれる場合にスローされる例外です。</summary>
public sealed class SiteRouteValidationException : InvalidOperationException
{
    internal SiteRouteValidationException(IReadOnlyList<SiteDiagnostic> diagnostics)
        : base(BuildMessage(diagnostics))
    {
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    /// <summary>検証で収集された診断を取得します。</summary>
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
