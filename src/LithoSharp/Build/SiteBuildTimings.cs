namespace LithoSharp.Build;

/// <summary>Elapsed milliseconds, recorded only when CollectTimings is enabled; not CPU time.</summary>
/// <remarks>Verification sums worker intervals, overlaps execution and may exceed TotalMilliseconds.</remarks>
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

    /// <summary>Input normalization and plan construction, before output transaction creation.</summary>
    public long PlanMilliseconds { get; }

    public long TransactionMilliseconds { get; }

    public long CacheLoadMilliseconds { get; }

    /// <summary>Cache decisions, key calculation, rendering and writes.</summary>
    public long ExecutionMilliseconds { get; }

    /// <summary>Sum of worker elapsed time reading and hashing reused artifacts; overlaps execution.</summary>
    public long VerificationMilliseconds { get; }

    /// <summary>Quality checks and output manifest/ownership preparation.</summary>
    public long QualityMilliseconds { get; }

    public long CommitMilliseconds { get; }

    public long TotalMilliseconds { get; }

    public int VerifiedArtifactCount { get; }
}
