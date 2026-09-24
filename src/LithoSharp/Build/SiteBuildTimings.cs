namespace LithoSharp.Build;

/// <summary>
/// 1回の生成における段階タイミングと検証件数です。計測は加算APIとして提供し、
/// <see cref="LithoSharp.SiteGenerationOptions.CollectTimings"/> が有効なときだけ記録します。
/// </summary>
/// <remarks>
/// 値は同一process内の経過ミリ秒です。並列実行の区間はwall timeとして加算され、
/// CPU時間ではありません。段階の合計が <see cref="TotalMilliseconds"/> を超える説明には
/// 使わず、差はunattributedとして扱います。
/// </remarks>
public sealed class SiteBuildTimings
{
    internal SiteBuildTimings(
        long planMilliseconds,
        long transactionMilliseconds,
        long cacheLoadMilliseconds,
        long executionMilliseconds,
        long verificationMilliseconds,
        long qualityMilliseconds,
        long commitMilliseconds,
        long totalMilliseconds,
        int verifiedArtifactCount)
    {
        PlanMilliseconds = planMilliseconds;
        TransactionMilliseconds = transactionMilliseconds;
        CacheLoadMilliseconds = cacheLoadMilliseconds;
        ExecutionMilliseconds = executionMilliseconds;
        VerificationMilliseconds = verificationMilliseconds;
        QualityMilliseconds = qualityMilliseconds;
        CommitMilliseconds = commitMilliseconds;
        TotalMilliseconds = totalMilliseconds;
        VerifiedArtifactCount = verifiedArtifactCount;
    }

    /// <summary>入力の正規化、計画構築、出力transaction作成前までの時間を取得します。</summary>
    public long PlanMilliseconds { get; }

    /// <summary>出力transactionの作成時間を取得します。既存出力をstagingへ複製する時間を含みます。</summary>
    public long TransactionMilliseconds { get; }

    /// <summary>ビルドcache manifestの読み込み時間を取得します。</summary>
    public long CacheLoadMilliseconds { get; }

    /// <summary>ノード実行(cache判定、key計算、描画、書き込み)の時間を取得します。</summary>
    public long ExecutionMilliseconds { get; }

    /// <summary><see cref="ExecutionMilliseconds"/> のうち、再利用成果物の読み取りとhash検証の時間を取得します。</summary>
    public long VerificationMilliseconds { get; }

    /// <summary>品質検査と出力manifest・所有権準備の時間を取得します。</summary>
    public long QualityMilliseconds { get; }

    /// <summary>stagingの公開(commit)時間を取得します。</summary>
    public long CommitMilliseconds { get; }

    /// <summary>生成呼び出し全体の経過時間を取得します。</summary>
    public long TotalMilliseconds { get; }

    /// <summary>読み取りとhash検証を行った再利用成果物の件数を取得します。</summary>
    public int VerifiedArtifactCount { get; }
}
