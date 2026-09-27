using LithoSharp.Build;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    private const string DefaultBuildCacheDirectoryName = ".lithosharp";

    /// <summary>
    /// Reports how many files and bytes the incremental cache holds for one output. The cache is
    /// never cleaned automatically; use <see cref="ClearCacheAsync"/> to reclaim one output's
    /// partition while no build is running for it.
    /// </summary>
    public static SiteBuildCacheUsage MeasureCache(string outputRoot, SiteGenerationOptions? options = null)
    {
        var partition = ResolveCachePartition(outputRoot, options);
        return MeasureCachePartition(partition);
    }

    /// <summary>
    /// Removes the incremental cache partition owned by one output and reports the reclaimed usage.
    /// The published output and other outputs' cache partitions are never touched.
    /// </summary>
    public static SiteBuildCacheUsage ClearCache(string outputRoot, SiteGenerationOptions? options = null)
    {
        var partition = ResolveCachePartition(outputRoot, options);
        var usage = MeasureCachePartition(partition);
        if (Directory.Exists(partition))
        {
            Directory.Delete(partition, recursive: true);
        }

        return usage;
    }

    private static string ResolveCachePartition(string outputRoot, SiteGenerationOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        var fullOutput = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var parentRoot = Path.GetDirectoryName(fullOutput);
        var outputName = Path.GetFileName(fullOutput);
        if (parentRoot is null || string.IsNullOrEmpty(outputName))
        {
            throw new InvalidOperationException(
                "The file system root cannot be used as the output directory.");
        }

        if (!Directory.Exists(parentRoot))
        {
            throw new DirectoryNotFoundException(
                $"The output parent directory '{parentRoot}' does not exist.");
        }

        var ownershipScope = OutputTransaction.CreateOwnershipScope(parentRoot, outputName);
        var cacheRoot = Path.GetFullPath(options?.BuildCacheDirectory
            ?? Path.Combine(parentRoot, DefaultBuildCacheDirectoryName));
        return Path.Combine(cacheRoot, OutputTransaction.CreateOutputIdentity(ownershipScope));
    }

    private static SiteBuildCacheUsage MeasureCachePartition(string partition)
    {
        if (!Directory.Exists(partition))
        {
            return new SiteBuildCacheUsage(partition, 0, 0);
        }

        var files = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(partition, "*", SearchOption.AllDirectories))
        {
            files++;
            bytes += new FileInfo(file).Length;
        }

        return new SiteBuildCacheUsage(partition, files, bytes);
    }
}
