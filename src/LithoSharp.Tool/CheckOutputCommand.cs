using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using LithoSharp.Build;
using System.Text;
using System.Text.Json;

namespace LithoSharp.Tool;

internal static class CheckOutputCommand
{
    private const string Usage = "Usage: lithosharp check-output --output <existing-directory> --base-url <published-url> [--format text|json|sarif] [--site-id <identity> [--baseline <published-manifest> | --initialize-baseline <new-manifest>] [--require-publication-compatibility] [--exclude-output <exact-file-path>] [--allow-removals <url-to-reason.json>]]";

    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var exclusions = new List<string>();
        var required = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--require-publication-compatibility")
            {
                if (required)
                    throw new CliUsageException(Usage);
                required = true;
                continue;
            }
            if (args[index] == "--exclude-output")
            {
                if (++index >= args.Length)
                    throw new CliUsageException(Usage);
                exclusions.Add(args[index]);
                continue;
            }
            var key = args[index] switch
            {
                "--output" or "-o" => "output",
                "--base-url" => "base-url",
                "--format" or "-f" => "format",
                "--site-id" => "site-id",
                "--baseline" => "baseline",
                "--initialize-baseline" => "initialize-baseline",
                "--allow-removals" => "allow-removals",
                _ => throw new CliUsageException(Usage),
            };
            if (++index >= args.Length || !values.TryAdd(key, args[index]))
                throw new CliUsageException(Usage);
        }
        if (!values.TryGetValue("output", out var output) || !values.TryGetValue("base-url", out var baseUrl))
            throw new CliUsageException(Usage);
        var initialize = values.ContainsKey("initialize-baseline");
        var compare = required || values.ContainsKey("baseline");
        if (initialize && compare || (initialize || compare) != values.ContainsKey("site-id")
            || values.ContainsKey("allow-removals") && !compare || exclusions.Count != 0 && !initialize && !compare)
            throw new CliUsageException(Usage);
        required = compare;
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
        SiteQualityReport report;
        try
        {
            report = initialize || compare
                ? await GeneratedSiteInspector.InspectPublicationAsync(output, baseUrl, values["site-id"], exclusions, cancellationToken).ConfigureAwait(false)
                : await GeneratedSiteInspector.InspectAsync(output, baseUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (compare && exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            report = SitePublicationCompatibility.NotCompared($"Current output could not be inspected; no publication comparison was performed: {exception.Message}", required);
        }
        if (compare && report.PublicationCompatibility is null)
        {
            IReadOnlyDictionary<string, string> allowances = new Dictionary<string, string>(StringComparer.Ordinal);
            SiteQualityReport? unavailable = null;
            if (report.PublicationManifest is not null && values.TryGetValue("allow-removals", out var allowanceFile))
            {
                try
                {
                    allowances = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        await SitePublicationCompatibility.ReadTextAsync(allowanceFile, cancellationToken).ConfigureAwait(false))
                        ?? throw new JsonException("Removal allowances must be a URL-to-reason object.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                    or ArgumentException or InvalidOperationException or NotSupportedException or System.Security.SecurityException
                    or System.ComponentModel.Win32Exception)
                {
                    unavailable = SitePublicationCompatibility.NotCompared($"Removal allowances could not be read; no publication comparison was performed: {exception.Message}", required);
                }
            }
            var compatibility = unavailable ?? (report.PublicationManifest is { } manifest
                ? await SitePublicationCompatibility.CompareAsync(manifest, values.GetValueOrDefault("baseline"), allowances, required, cancellationToken).ConfigureAwait(false)
                : SitePublicationCompatibility.NotCompared("Current output failed inspection; no publication comparison was performed.", required));
            report = new SiteQualityReport(report.Diagnostics.Concat(compatibility.Diagnostics))
            {
                HtmlParseCount = report.HtmlParseCount,
                PublicationCompatibility = compatibility.PublicationCompatibility,
                PublicationCompatibilityRequired = required,
            };
        }
        if (initialize && report.PublicationManifest is { } candidate)
        {
            var target = Path.GetFullPath(values["initialize-baseline"]);
            GeneratedSiteInspector.EnsureSafeAncestors(Path.GetDirectoryName(target)!);
            var relative = Path.GetRelativePath(output, target);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new CliUsageException("The published baseline must be stored outside the inspected output directory.");
            await InitializeBaselineAsync(target, candidate.ToJson(), cancellationToken).ConfigureAwait(false);
            report = new SiteQualityReport(report.Diagnostics.Concat([
                new SiteDiagnostic("LSPUB001", SiteDiagnosticSeverity.Info,
                    $"Initialized the explicitly selected published baseline '{target}'. No comparison was performed. Update published baselines only after successful publication.")]))
            { PublicationCompatibility = SitePublicationCompatibilityState.NotCompared };
        }
        var banner = initialize ? $"Output inspection with explicit baseline initialization (no generation): {output}"
            : $"Read-only output inspection (no generation): {output}";
        (format == SiteDiagnosticFormat.Text ? Console.Out : Console.Error).WriteLine(banner);
        Console.WriteLine(report.Format(format));
        var failed = report.Diagnostics.Any(diagnostic => diagnostic.Severity >= SiteDiagnosticSeverity.Error);
        if (format == SiteDiagnosticFormat.Text)
            Console.WriteLine(failed ? "Output inspection failed." : "Output inspection succeeded.");
        return failed ? 1 : 0;
    }

    private static async Task InitializeBaselineAsync(string target, string json, CancellationToken token)
    {
        var parent = Path.GetDirectoryName(target)!;
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var created = false;
        try
        {
            GeneratedSiteInspector.EnsureSafeAncestors(parent);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                created = true;
                BuildInputFingerprint.VerifyOpenedContainedFile(parent, temporary, stream.SafeFileHandle);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(json), token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                GeneratedSiteInspector.EnsureSafeAncestors(parent);
                BuildInputFingerprint.VerifyOpenedContainedFile(parent, temporary, stream.SafeFileHandle);
            }
            token.ThrowIfCancellationRequested();
            GeneratedSiteInspector.EnsureSafeAncestors(parent);
            File.Move(temporary, target, overwrite: false);
            created = false;
        }
        finally
        {
            if (created)
            {
                GeneratedSiteInspector.EnsureSafeAncestors(parent);
                File.Delete(temporary);
            }
        }
    }
}
