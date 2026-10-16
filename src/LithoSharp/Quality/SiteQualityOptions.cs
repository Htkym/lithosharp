using LithoSharp.Diagnostics;

namespace LithoSharp.Quality;

public sealed record SiteQualityOptions
{
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

    public SiteDiagnosticSeverity FailureThreshold { get; }
    public bool CheckOrphans { get; }
    public ExternalLinkCheckOptions? ExternalLinks { get; }
}

public sealed record ExternalLinkCheckOptions
{
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

    public string CacheFilePath { get; }
    public TimeSpan CacheDuration { get; }
    public TimeSpan MinimumRequestInterval { get; }
    public TimeSpan RequestTimeout { get; }

}
