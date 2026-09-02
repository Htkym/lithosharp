namespace LithoSharp;

using LithoSharp.Content;

/// <summary>
/// 1 回の静的サイト生成を制御するオプションです。
/// </summary>
public sealed record SiteGenerationOptions
{
    /// <summary>
    /// 生成成果物と公開判定に使うビルド時刻です。値は UTC に正規化されます。
    /// 未指定時は <c>SOURCE_DATE_EPOCH</c>、現在の UTC 時刻の順に使用します。
    /// </summary>
    public DateTimeOffset? BuildTimestamp { get; init; }

    /// <summary>
    /// front matter の <c>environments</c> と照合する環境名です。
    /// 既定値は <c>Production</c> です。
    /// </summary>
    public string EnvironmentName { get; init; } = "Production";

    /// <summary>今回の生成へ接続する型付きコンテンツコレクションを取得または設定します。</summary>
    public IReadOnlyList<SiteContentCollection> ContentCollections { get; init; } = [];

    /// <summary>差分無効化を計算する前回のビルド計画です。</summary>
    public LithoSharp.Build.SiteBuildPlan? PreviousBuildPlan { get; init; }
}
