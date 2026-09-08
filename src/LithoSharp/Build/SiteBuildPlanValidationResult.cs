using LithoSharp.Diagnostics;

namespace LithoSharp.Build;

/// <summary>ビルド計画の検証結果を表します。</summary>
public sealed class SiteBuildPlanValidationResult
{
    internal SiteBuildPlanValidationResult(
        SiteBuildPlan? plan,
        IReadOnlyList<SiteDiagnostic> diagnostics)
    {
        Plan = plan;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    /// <summary>検証済みのビルド計画を取得します。エラーがある場合は <see langword="null"/> です。</summary>
    public SiteBuildPlan? Plan { get; }

    /// <summary>エラー診断がなく、検証済み計画を取得できる場合は <see langword="true"/> を取得します。</summary>
    public bool IsValid => Plan is not null;

    /// <summary>安定した順序に並べられた診断を取得します。</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
}

/// <summary>ビルド計画にエラー診断が含まれる場合にスローされる例外です。</summary>
public sealed class SiteBuildPlanValidationException : InvalidOperationException
{
    internal SiteBuildPlanValidationException(IReadOnlyList<SiteDiagnostic> diagnostics)
        : base($"Site build plan validation failed with {diagnostics.Count} diagnostic(s).")
    {
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    /// <summary>検証で収集された診断を取得します。</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
}
