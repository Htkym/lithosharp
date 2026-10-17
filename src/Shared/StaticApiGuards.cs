using System;
using System.Linq;
using LithoSharp.Diagnostics;

namespace LithoSharp.Internal;

// Compiled privately into the analyzer: only pure runtime validation is shared.
internal static class StaticApiGuards
{
    internal static void ValidateAbsoluteUrl(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (value.Any(char.IsControl) || value.Any(char.IsWhiteSpace) || value.IndexOf('\\') >= 0
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.IsWellFormedOriginalString())
            throw new UriFormatException("The URL must be an absolute, well-formed HTTP or HTTPS URL.");
    }

    internal static void ValidateFailureThreshold(int failureThreshold)
    {
        if (!Enum.IsDefined(typeof(SiteDiagnosticSeverity), failureThreshold))
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));
    }

    internal const long MaximumTimerTicks = 42949672940000L;
    internal const long MaximumRequestTimeoutTicks = (long)int.MaxValue * TimeSpan.TicksPerMillisecond;

    internal static void ValidateExternalCacheFilePath(string cacheFilePath)
    {
        if (cacheFilePath is null) throw new ArgumentNullException(nameof(cacheFilePath));
        if (string.IsNullOrWhiteSpace(cacheFilePath))
            throw new ArgumentException("A cache file path must not be empty.", nameof(cacheFilePath));
    }

    internal static void ValidateExternalCacheDuration(long ticks)
    {
        if (ticks < 0) throw new ArgumentOutOfRangeException("cacheDuration");
    }

    internal static void ValidateExternalTimerDuration(long ticks, string name, bool allowZero)
    {
        if ((!allowZero && ticks <= 0) || (allowZero && ticks < 0) || ticks > MaximumTimerTicks)
            throw new ArgumentOutOfRangeException(name);
    }

    internal static void ValidateExternalRequestTimeout(long ticks)
    {
        if (ticks <= 0 || ticks > MaximumRequestTimeoutTicks)
            throw new ArgumentOutOfRangeException("requestTimeout");
    }
}
