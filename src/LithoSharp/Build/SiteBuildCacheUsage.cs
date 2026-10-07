namespace LithoSharp.Build;

/// <summary>Files and bytes held by the incremental cache partition of a single output.</summary>
public sealed class SiteBuildCacheUsage
{
    /// <summary>Creates a cache usage snapshot.</summary>
    /// <param name="cacheDirectory">The cache partition directory that was measured or cleared.</param>
    /// <param name="fileCount">The number of files in the cache partition.</param>
    /// <param name="totalBytes">The total size of the cache partition files in bytes.</param>
    public SiteBuildCacheUsage(string cacheDirectory, int fileCount, long totalBytes)
    {
        CacheDirectory = cacheDirectory;
        FileCount = fileCount;
        TotalBytes = totalBytes;
    }

    /// <summary>The cache partition directory that was measured or cleared.</summary>
    public string CacheDirectory { get; }
    /// <summary>The number of files in the cache partition.</summary>
    public int FileCount { get; }
    /// <summary>The total size of the cache partition files in bytes.</summary>
    public long TotalBytes { get; }
}
