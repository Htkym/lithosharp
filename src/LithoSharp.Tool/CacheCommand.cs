using System.Globalization;
using LithoSharp.Build;

namespace LithoSharp.Tool;

/// <summary>
/// Explicit cache reporting and reclamation. It never runs as part of a build, so a live cache
/// or another output's partition can never be removed by surprise.
/// </summary>
internal static class CacheCommand
{
    private const string Usage =
        "Usage: lithosharp cache <info|clean> -o <output-directory> [--cache-dir <directory>]";

    internal static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("info" or "clean"))
        {
            throw new CliUsageException(Usage);
        }

        var action = args[0];
        var options = CommandOptions.Parse(args[1..]);
        var output = options.Value("output") ?? throw new CliUsageException(Usage);
        var generation = new SiteGenerationOptions
        {
            BuildCacheDirectory = options.Value("cache-dir"),
        };
        var usage = action == "info"
            ? SiteGenerator.MeasureCache(output, generation)
            : SiteGenerator.ClearCache(output, generation);
        var files = usage.FileCount.ToString(CultureInfo.InvariantCulture);
        var bytes = usage.TotalBytes.ToString(CultureInfo.InvariantCulture);
        Console.WriteLine(action == "info"
            ? $"{files} cache file(s), {bytes} byte(s) in {usage.CacheDirectory}"
            : $"Removed {files} cache file(s) ({bytes} byte(s)) from {usage.CacheDirectory}");
        return 0;
    }
}
