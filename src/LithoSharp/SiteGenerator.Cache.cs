using LithoSharp.Build;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    private const string DefaultBuildCacheDirectoryName = ".lithosharp";

    /// <summary>
    /// Reports how many files and bytes the incremental cache holds for one output. The cache is
    /// never cleaned automatically; use <see cref="ClearCache"/> to reclaim one output's
    /// partition. Cache operations share the output lock with builds.
    /// </summary>
    public static SiteBuildCacheUsage MeasureCache(string outputRoot, SiteGenerationOptions? options = null)
        => OutputTransaction.WithOutputLockAsync(outputRoot,
                () => MeasureCachePartition(ResolveCachePartition(outputRoot, options)))
            .GetAwaiter().GetResult();

    /// <summary>
    /// Removes the incremental cache partition owned by one output and reports the reclaimed usage.
    /// The published output and other outputs' cache partitions are never touched.
    /// </summary>
    public static SiteBuildCacheUsage ClearCache(string outputRoot, SiteGenerationOptions? options = null)
        => OutputTransaction.WithOutputLockAsync(outputRoot, () =>
        {
            var partition = ResolveCachePartition(outputRoot, options);
            var usage = MeasureCachePartition(partition);
            if (Directory.Exists(partition))
            {
                Directory.Delete(partition, recursive: true);
            }

            return usage;
        }).GetAwaiter().GetResult();

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
        if (ContainsDirectory(fullOutput, cacheRoot) || ContainsDirectory(cacheRoot, fullOutput)
            || options?.PublicDirectory is { } publicInput && ContainsDirectory(Path.GetFullPath(publicInput), cacheRoot))
        {
            throw new ArgumentException("The build cache must not overlap output or be inside public input.", nameof(options));
        }

        var partition = Path.Combine(cacheRoot, OutputTransaction.CreateOutputIdentity(ownershipScope));
        ValidateCachePartitionPublicInput(partition, options);
        return partition;
    }

    private static void ValidateCachePartitionPublicInput(string partition, SiteGenerationOptions? options)
    {
        if (options?.PublicDirectory is not { } publicDirectory) return;
        var publicRoot = Path.GetFullPath(publicDirectory);
        if (ContainsDirectory(partition, publicRoot) || ContainsDirectory(publicRoot, partition))
        {
            throw new ArgumentException("The build cache partition and public input directory must not overlap.", nameof(options));
        }
    }

    private static SiteBuildCacheUsage MeasureCachePartition(string partition)
    {
        if (!Directory.Exists(partition))
        {
            return new SiteBuildCacheUsage(partition, 0, 0);
        }

        EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(partition)!, partition);
        var files = 0;
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(partition);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                EnsureNotNameSurrogateReparsePoint(entry, attributes);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    files++;
                    bytes += new FileInfo(entry).Length;
                }
            }
        }

        return new SiteBuildCacheUsage(partition, files, bytes);
    }
}
