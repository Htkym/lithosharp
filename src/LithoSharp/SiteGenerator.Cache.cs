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
    {
        ValidateCachePathAncestry(outputRoot, options);
        return OutputTransaction.WithOutputLockAsync(outputRoot,
                () => MeasureCachePartition(ResolveCachePartition(outputRoot, options)))
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Removes the incremental cache partition owned by one output and reports the reclaimed usage.
    /// The published output and other outputs' cache partitions are never touched.
    /// </summary>
    public static SiteBuildCacheUsage ClearCache(string outputRoot, SiteGenerationOptions? options = null)
    {
        ValidateCachePathAncestry(outputRoot, options);
        return OutputTransaction.WithOutputLockAsync(outputRoot, () =>
        {
            var partition = ResolveCachePartition(outputRoot, options);
            var usage = MeasureCachePartition(partition);
            if (Directory.Exists(partition))
            {
                Directory.Delete(partition, recursive: true);
            }

            return usage;
        }).GetAwaiter().GetResult();
    }

    private static string ResolveCachePartition(string outputRoot, SiteGenerationOptions? options)
    {
        ValidateCachePathAncestry(outputRoot, options);
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
        if (ContainsCacheDirectory(fullOutput, cacheRoot) || ContainsCacheDirectory(cacheRoot, fullOutput)
            || options?.PublicDirectory is { } publicInput && ContainsCacheDirectory(Path.GetFullPath(publicInput), cacheRoot))
        {
            throw new ArgumentException("The build cache must not overlap output or be inside public input.", nameof(options));
        }

        var partition = Path.Combine(cacheRoot, OutputTransaction.CreateOutputIdentity(ownershipScope));
        ValidateCachePartitionPublicInput(partition, options);
        return partition;
    }

    private static void ValidateCachePartitionPublicInput(string partition, SiteGenerationOptions? options)
    {
        EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(partition)!, partition);
        if (options?.PublicDirectory is not { } publicDirectory) return;
        var publicRoot = Path.GetFullPath(publicDirectory);
        if (ContainsCacheDirectory(partition, publicRoot) || ContainsCacheDirectory(publicRoot, partition))
        {
            throw new ArgumentException("The build cache partition and public input directory must not overlap.", nameof(options));
        }
    }

    // Cache admission deliberately rejects case-only aliases even on case-sensitive
    // volumes. This conservative destructive boundary does not change route/path identity.
    private static bool ContainsCacheDirectory(string root, string path)
    {
        if (CachePathContains(root, path)) return true;
        return CachePathContains(CanonicalizeCacheBoundaryPath(root), CanonicalizeCacheBoundaryPath(path));
    }

    private static bool CachePathContains(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(root).Normalize(System.Text.NormalizationForm.FormD);
        path = Path.TrimEndingDirectorySeparator(path).Normalize(System.Text.NormalizationForm.FormD);
        return string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    // Resolve existing physical components without requiring prospective cache/output
    // suffixes to exist. A linked component is rejected rather than followed.
    private static string CanonicalizeCacheBoundaryPath(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.GetPathRoot(fullPath)!;
        EnsureContainedPathHasNoNameSurrogateReparsePoints(root, fullPath);
        var segments = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            var requested = Path.Combine(current, segments[index]);
            if (!Directory.Exists(requested))
            {
                for (; index < segments.Length; index++) current = Path.Combine(current, segments[index]);
                return current;
            }

            var entries = Directory.EnumerateDirectories(current).ToArray();
            var exact = entries.SingleOrDefault(entry =>
                string.Equals(Path.GetFileName(entry), segments[index], StringComparison.Ordinal));
            if (exact is not null)
            {
                current = exact;
                continue;
            }

            var name = segments[index].Normalize(System.Text.NormalizationForm.FormD);
            var aliases = entries.Where(entry => string.Equals(
                Path.GetFileName(entry).Normalize(System.Text.NormalizationForm.FormD), name,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (aliases.Length != 1)
                throw new InvalidOperationException($"Cache boundary path '{requested}' has an ambiguous physical identity.");
            current = aliases[0];
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static void ValidateCachePathAncestry(string outputRoot, SiteGenerationOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        var fullOutput = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var parentRoot = Path.GetDirectoryName(fullOutput);
        if (parentRoot is null || string.IsNullOrEmpty(Path.GetFileName(fullOutput)))
            throw new InvalidOperationException("The file system root cannot be used as the output directory.");
        var cacheRoot = Path.GetFullPath(options?.BuildCacheDirectory
            ?? Path.Combine(parentRoot, DefaultBuildCacheDirectoryName));
        EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(fullOutput)!, fullOutput);
        EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(cacheRoot)!, cacheRoot);
        if (options?.PublicDirectory is { } publicDirectory)
        {
            var publicRoot = Path.GetFullPath(publicDirectory);
            EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(publicRoot)!, publicRoot);
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
