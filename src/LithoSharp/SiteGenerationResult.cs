using LithoSharp.Build;
using LithoSharp.Quality;

namespace LithoSharp;

/// <summary>
/// 静的サイトの生成結果です。
/// </summary>
/// <param name="OutputDirectory">出力ディレクトリ。</param>
/// <param name="PostCount">公開条件を満たして生成された Markdown 投稿数。</param>
/// <param name="GeneratedFiles">再利用した成果物を含む、今回公開したファイルの一覧。</param>
public sealed record SiteGenerationResult(
    string OutputDirectory,
    int PostCount,
    IReadOnlyList<string> GeneratedFiles)
{
    /// <summary>この生成で検証した不変のビルド計画です。</summary>
    public SiteBuildPlan BuildPlan { get; init; } = SiteBuildPlan.Create([]);

    /// <summary>出力確定前に収集した品質診断です。検査が無効な場合は空です。</summary>
    public SiteQualityReport QualityReport { get; init; } = new();

    /// <summary>今回の生成を決定的に要約したビルドレポートです。</summary>
    public SiteBuildReport BuildReport { get; init; } = new(
        DateTimeOffset.UnixEpoch,
        "Production",
        "Unknown");

    /// <summary>従来の位置指定メンバーだけを使って、生成結果が等しいかどうかを判定します。</summary>
    /// <param name="other">比較する生成結果。</param>
    /// <returns>従来の位置指定メンバーがすべて等しい場合は <see langword="true"/>。</returns>
    public bool Equals(SiteGenerationResult? other) =>
        other is not null
        && StringComparer.Ordinal.Equals(OutputDirectory, other.OutputDirectory)
        && PostCount == other.PostCount
        && EqualityComparer<IReadOnlyList<string>>.Default.Equals(
            GeneratedFiles,
            other.GeneratedFiles);

    /// <summary>従来の位置指定メンバーだけに基づくハッシュコードを返します。</summary>
    /// <returns>出力ディレクトリ、投稿数、生成ファイル一覧から計算したハッシュコード。</returns>
    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(OutputDirectory),
            PostCount,
            EqualityComparer<IReadOnlyList<string>>.Default.GetHashCode(GeneratedFiles));
}
