using LithoSharp.Diagnostics;
using LithoSharp.Quality;

namespace LithoSharp.Tool;

internal static class CheckOutputCommand
{
    private const string Usage = "Usage: lithosharp check-output --output <existing-directory> --base-url <published-url> [--format text|json|sarif]";

    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index] switch
            {
                "--output" or "-o" => "output",
                "--base-url" => "base-url",
                "--format" or "-f" => "format",
                _ => throw new CliUsageException(Usage),
            };
            if (++index >= args.Length || !values.TryAdd(key, args[index]))
                throw new CliUsageException(Usage);
        }
        if (!values.TryGetValue("output", out var output) || !values.TryGetValue("base-url", out var baseUrl))
            throw new CliUsageException(Usage);
        var format = values.GetValueOrDefault("format", "text") switch
        {
            "text" => SiteDiagnosticFormat.Text,
            "json" => SiteDiagnosticFormat.Json,
            "sarif" => SiteDiagnosticFormat.Sarif,
            _ => throw new CliUsageException(Usage),
        };
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(output);
            output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output));
            if (Path.GetDirectoryName(output) is null)
                throw new ArgumentException("The output directory must not be a file system root.");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliUsageException($"Invalid --output: {exception.Message}\n{Usage}");
        }
        try
        {
            _ = SiteUrl.ForDirectory(string.Empty, baseUrl);
            if (new Uri(baseUrl).UserInfo.Length != 0)
                throw new UriFormatException("The base URL must not contain credentials.");
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            throw new CliUsageException($"Invalid --base-url: {exception.Message}\n{Usage}");
        }
        var report = await GeneratedSiteInspector.InspectAsync(output, baseUrl, cancellationToken).ConfigureAwait(false);
        var banner = $"Read-only output inspection (no generation): {output}";
        (format == SiteDiagnosticFormat.Text ? Console.Out : Console.Error).WriteLine(banner);
        Console.WriteLine(report.Format(format));
        var failed = report.Diagnostics.Any(diagnostic => diagnostic.Severity >= SiteDiagnosticSeverity.Error);
        if (format == SiteDiagnosticFormat.Text)
            Console.WriteLine(failed ? "Output inspection failed." : "Output inspection succeeded.");
        return failed ? 1 : 0;
    }
}
