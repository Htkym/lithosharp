using LithoSharp.Diagnostics;

namespace LithoSharp.Quality;

/// <summary>サイト品質検査の設定です。</summary>
public sealed record SiteQualityOptions
{
    /// <summary>品質検査の設定を作成します。</summary>
    /// <param name="failureThreshold">失敗とみなす最小重大度。</param>
    /// <param name="checkOrphans">孤立ページを検査するかどうか。</param>
    /// <param name="externalLinks">外部リンク検査の設定。</param>
    /// <exception cref="ArgumentOutOfRangeException">重大度が未定義です。</exception>
    public SiteQualityOptions(
        SiteDiagnosticSeverity failureThreshold = SiteDiagnosticSeverity.Error,
        bool checkOrphans = true,
        ExternalLinkCheckOptions? externalLinks = null)
    {
        Internal.StaticApiGuards.ValidateFailureThreshold((int)failureThreshold);
        FailureThreshold = failureThreshold;
        CheckOrphans = checkOrphans;
        ExternalLinks = externalLinks;
    }

    /// <summary>この重大度以上の診断を失敗として扱います。</summary>
    public SiteDiagnosticSeverity FailureThreshold { get; }
    /// <summary>孤立ページを検査するかどうかを取得します。</summary>
    public bool CheckOrphans { get; }
    /// <summary>外部リンク検査の設定を取得します。</summary>
    public ExternalLinkCheckOptions? ExternalLinks { get; }
}

/// <summary>外部リンク検査の設定です。</summary>
public sealed record ExternalLinkCheckOptions
{
    /// <summary>外部リンク検査の設定を作成します。</summary>
    /// <param name="cacheFilePath">リンクキャッシュのファイルパス。</param>
    /// <param name="cacheDuration">キャッシュの有効期間。</param>
    /// <param name="minimumRequestInterval">リクエスト間隔の下限。</param>
    /// <param name="requestTimeout">リクエストのタイムアウト。</param>
    /// <exception cref="ArgumentNullException">キャッシュパスが null です。</exception>
    /// <exception cref="ArgumentException">キャッシュパスが空です。</exception>
    /// <exception cref="ArgumentOutOfRangeException">期間が負、タイムアウトがゼロ、またはタイマーの上限を超えています。</exception>
    public ExternalLinkCheckOptions(
        string cacheFilePath,
        TimeSpan? cacheDuration = null,
        TimeSpan? minimumRequestInterval = null,
        TimeSpan? requestTimeout = null)
    {
        Internal.StaticApiGuards.ValidateExternalCacheFilePath(cacheFilePath);
        CacheFilePath = cacheFilePath;
        CacheDuration = cacheDuration ?? TimeSpan.FromHours(24);
        MinimumRequestInterval = minimumRequestInterval ?? TimeSpan.FromMilliseconds(500);
        RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        Internal.StaticApiGuards.ValidateExternalCacheDuration(CacheDuration.Ticks);
        Internal.StaticApiGuards.ValidateExternalTimerDuration(MinimumRequestInterval.Ticks, nameof(minimumRequestInterval), allowZero: true);
        Internal.StaticApiGuards.ValidateExternalTimerDuration(RequestTimeout.Ticks, nameof(requestTimeout), allowZero: false);
    }

    /// <summary>リンクキャッシュのファイルパスを取得します。</summary>
    public string CacheFilePath { get; }
    /// <summary>キャッシュの有効期間を取得します。</summary>
    public TimeSpan CacheDuration { get; }
    /// <summary>リクエスト間隔の下限を取得します。</summary>
    public TimeSpan MinimumRequestInterval { get; }
    /// <summary>リクエストのタイムアウトを取得します。</summary>
    public TimeSpan RequestTimeout { get; }

}
