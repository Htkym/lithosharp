namespace LithoSharp;

using LithoSharp.Content;
using LithoSharp.Quality;
using LithoSharp.Routing;

/// <summary>
/// 1 回の静的サイト生成を制御するオプションです。
/// </summary>
public sealed record SiteGenerationOptions
{
    /// <summary>Optional output path prefixes for a subset build. Other owned outputs and shared chunks are retained.</summary>
    /// <remarks>A subset requires an existing full output and clean=false. Shared aggregate files outside the scope are retained; include context-specific search partitions in the scope.</remarks>
    public IReadOnlyList<string> OutputScope { get; init; } = [];
    /// <summary>Opt-in trusted build preparation. Ordinary sites do not run external processors.</summary>
    public IReadOnlyList<LithoSharp.Build.ISiteBuildExtension> Extensions { get; init; } = [];

    /// <summary>Prepared assets whose paths are preserved, for example bundled JavaScript chunks.</summary>
    public IReadOnlyList<SiteGeneratedAsset> GeneratedAssets { get; init; } = [];

    /// <summary>Maximum simultaneous renders. The default is one; extensions remain serial unless they declare thread safety.</summary>
    public int MaxDegreeOfParallelism { get; init; } = 1;

    /// <summary>Build cache root outside output and public input. Null uses a sibling .lithosharp directory, partitioned by output identity.</summary>
    public string? BuildCacheDirectory { get; init; }

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

    /// <summary>今回の生成へ接続する静的資産です。</summary>
    public IReadOnlyList<SiteAsset> Assets { get; init; } = [];

    /// <summary>Transforms with declared source assets and output files.</summary>
    public IReadOnlyList<SiteAssetTransform> AssetTransforms { get; init; } = [];

    /// <summary>Optional directory whose regular files are copied using their original relative paths. It must not overlap the output directory or contain caches.</summary>
    public string? PublicDirectory { get; init; }

    /// <summary>Optional transform cache directory outside the output directory. Null disables the disk cache.</summary>
    public string? AssetCacheDirectory { get; init; }

    /// <summary>通常の成果物として生成するリダイレクトです。</summary>
    public IReadOnlyList<SiteRedirect> Redirects { get; init; } = [];

    /// <summary>出力確定前の品質検査です。未指定の場合は検査もネットワーク通信も行いません。</summary>
    public SiteQualityOptions? Quality { get; init; }

    /// <summary>最終 HTML の局所 facts を出力別の有限 cache で再利用するかどうかを取得または設定します。品質検査が有効な場合だけ使用し、参照先と品質規則は毎回検査します。既定値は true です。</summary>
    public bool HtmlFactsCacheEnabled { get; init; } = true;

    /// <summary>
    /// 段階タイミングを <see cref="SiteGenerationResult.Timings"/> へ記録するかどうかを取得または設定します。
    /// 既定は無効で、無効時は結果が <see langword="null"/> になります。
    /// </summary>
    public bool CollectTimings { get; init; }
}
