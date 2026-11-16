using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LithoSharp.Diagnostics;
using LithoSharp.OpenKnowledge;
using LithoSharp.Quality;

namespace LithoSharp.Tool;

internal static class OkfCommand
{
    private const string Usage = "Usage: lithosharp okf <inspect|validate|export> --input <bundle-directory> [--format text|json|sarif] [--as-of <ISO-8601-datetime-with-offset>] [--strict-lint] [--base-url <published-url>] [--max-files <count>] [--max-discovery-entries <count>] [--max-file-bytes <bytes>] [--max-bundle-bytes <bytes>] [--output <new-export-directory>]";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args is ["--help" or "-h" or "help"]
            || args is ["inspect" or "validate" or "export", "--help" or "-h"])
        {
            Console.WriteLine(Usage);
            return 0;
        }
        var options = Parse(args);
        var asOf = options.Read.AsOf ?? DateTimeOffset.UtcNow;
        var readOptions = new OkfReadOptions
        {
            AsOf = asOf,
            StrictLint = options.Read.StrictLint,
            MaxFiles = options.Read.MaxFiles,
            MaxDiscoveryEntries = options.Read.MaxDiscoveryEntries,
            MaxFileBytes = options.Read.MaxFileBytes,
            MaxBundleBytes = options.Read.MaxBundleBytes,
            BaseUrl = options.Read.BaseUrl,
        };
        OkfBundle? bundle = null;
        var exported = false;
        try
        {
            bundle = await OkfBundle.ReadAsync(options.Input, readOptions, cancellationToken).ConfigureAwait(false);
            var canceled = cancellationToken.IsCancellationRequested
                || bundle.Diagnostics.Any(static diagnostic => diagnostic.Category == "Canceled");
            if (!canceled && options.Command == "export")
            {
                await bundle.ExportPreservedAsync(options.Output!, cancellationToken).ConfigureAwait(false);
                exported = true;
            }
            var summary = Summarize(bundle, options, exported, canceled ? "Canceled" : bundle.IsComplete ? "Completed" : "Incomplete");
            if (canceled)
                summary = summary with { Success = false };
            return Print(summary,
                bundle.Diagnostics, options.Format, canceled ? 130 : bundle.IsSuccess ? 0 : 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var summary = bundle is null ? EmptySummary(options, asOf) : Summarize(bundle, options, exported, "Canceled");
            return Print(summary with { State = "Canceled", Success = false, Conformance = "NotAssessed" },
                (bundle?.Diagnostics ?? []).Append(new("LSOKF900", SiteDiagnosticSeverity.Error,
                    "The OKF operation was canceled; validation or export was not completed.", new SiteSourceLocation(options.Input), "Canceled", null)),
                options.Format, 130);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            var summary = bundle is null ? EmptySummary(options, asOf) : Summarize(bundle, options, exported, "Failed");
            return Print(summary with { State = "Failed", Success = false },
                (bundle?.Diagnostics ?? []).Append(new("LSOKF901", SiteDiagnosticSeverity.Error,
                    exception.Message, new SiteSourceLocation(options.Output ?? options.Input), "Io", null)), options.Format, 1);
        }
    }

    private static Options Parse(string[] args)
    {
        if (args[0] is not ("inspect" or "validate" or "export"))
            throw new CliUsageException(Usage);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var strictLint = false;
        for (var index = 1; index < args.Length; index++)
        {
            if (args[index] == "--strict-lint")
            {
                if (strictLint)
                    throw new CliUsageException("Option '--strict-lint' was specified more than once.\n" + Usage);
                strictLint = true;
                continue;
            }
            var name = args[index] switch
            {
                "--input" => "input",
                "--output" or "-o" => "output",
                "--format" or "-f" => "format",
                "--as-of" => "as-of",
                "--base-url" => "base-url",
                "--max-files" => "max-files",
                "--max-discovery-entries" => "max-discovery-entries",
                "--max-file-bytes" => "max-file-bytes",
                "--max-bundle-bytes" => "max-bundle-bytes",
                _ => throw new CliUsageException($"Unknown OKF option '{args[index]}'.\n{Usage}"),
            };
            if (++index >= args.Length || !values.TryAdd(name, args[index]))
                throw new CliUsageException($"Option '--{name}' requires one value.\n{Usage}");
        }
        if (!values.TryGetValue("input", out var input) || string.IsNullOrWhiteSpace(input)
            || (args[0] == "export") != values.ContainsKey("output"))
            throw new CliUsageException(Usage);
        var format = values.GetValueOrDefault("format", "text");
        if (format is not ("text" or "json" or "sarif"))
            throw new CliUsageException("OKF format must be text, json or sarif.\n" + Usage);
        DateTimeOffset? asOf = null;
        if (values.TryGetValue("as-of", out var time))
        {
            if (!OkfMetadata.TryParseOffsetDateTime(time, out var parsed))
                throw new CliUsageException("OKF --as-of requires an ISO 8601 datetime with an explicit offset and exact DateTimeOffset representation.\n" + Usage);
            asOf = parsed;
        }
        var defaults = new OkfReadOptions();
        var maxFiles = Positive("max-files", defaults.MaxFiles);
        if (maxFiles > int.MaxValue)
            throw new CliUsageException("OKF --max-files exceeds the supported count.\n" + Usage);
        var maxEntries = Positive("max-discovery-entries", defaults.MaxDiscoveryEntries);
        if (maxEntries > int.MaxValue)
            throw new CliUsageException("OKF --max-discovery-entries exceeds the supported count.\n" + Usage);
        var baseUrl = values.GetValueOrDefault("base-url");
        if (baseUrl is not null && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0))
            throw new CliUsageException("OKF --base-url requires an absolute HTTP or HTTPS URL without credentials, query or fragment.\n" + Usage);
        try
        {
            var output = values.GetValueOrDefault("output");
            if (output is not null && string.IsNullOrWhiteSpace(output))
                throw new ArgumentException("The output path must not be empty.");
            return new(args[0], Path.GetFullPath(input), output is null ? null : Path.GetFullPath(output), format,
                new OkfReadOptions
                {
                    AsOf = asOf,
                    StrictLint = strictLint,
                    MaxFiles = (int)maxFiles,
                    MaxDiscoveryEntries = (int)maxEntries,
                    MaxFileBytes = Positive("max-file-bytes", defaults.MaxFileBytes),
                    MaxBundleBytes = Positive("max-bundle-bytes", defaults.MaxBundleBytes),
                    BaseUrl = baseUrl,
                });
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CliUsageException($"Invalid OKF path: {exception.Message}\n{Usage}");
        }

        long Positive(string name, long fallback)
        {
            if (!values.TryGetValue(name, out var value))
                return fallback;
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
                throw new CliUsageException($"OKF --{name} requires a positive integer.\n{Usage}");
            return number;
        }
    }

    private static Summary Summarize(OkfBundle bundle, Options options, bool exported, string state)
    {
        return new(options.Command, bundle.InputRoot, bundle.SpecificationVersion, bundle.DeclaredVersion,
            bundle.AsOf, state, bundle.IsComplete, bundle.SnapshotComplete, bundle.Conformance.ToString(),
            bundle.IsSuccess, options.Read.StrictLint, bundle.Files.Count,
            bundle.Documents.Select(static document => new DocumentSummary(document.Path, document.Kind.ToString(), document.ConceptId,
                document.IsAvailable, document.Metadata?.Type, document.Metadata?.Status,
                document.Metadata?.TrustTier.ToString(), document.Metadata?.Freshness.ToString())).ToArray(), exported, exported ? options.Output : null);
    }

    private static Summary EmptySummary(Options options, DateTimeOffset asOf) =>
        new(options.Command, options.Input, "0.2", null, asOf, "Incomplete", false, false, "NotAssessed", false,
            options.Read.StrictLint, 0, [], false, null);

    private static int Print(Summary summary, IEnumerable<SiteDiagnostic> diagnostics, string format, int exitCode)
    {
        var report = new SiteQualityReport(diagnostics);
        if (format == "text")
        {
            Console.WriteLine($"OKF {summary.Command}: {summary.State}; conformance: {summary.Conformance}; success: {summary.Success}.");
            Console.WriteLine($"Supported specification: {summary.SpecificationVersion}; declared: {summary.DeclaredVersion ?? "not declared"}; as-of: {summary.AsOf:O}.");
            Console.WriteLine($"Files: {summary.FileCount}; documents: {summary.Documents.Length}; strict lint: {summary.StrictLint}.");
            if (summary.Command == "inspect")
                foreach (var document in summary.Documents)
                    Console.WriteLine($"  {document.Path} [{document.Kind}] available: {document.Available}; type: {document.Type ?? "unavailable"}; trust: {document.TrustTier ?? "unavailable"}; freshness: {document.Freshness ?? "unavailable"}.");
            var text = report.Format(SiteDiagnosticFormat.Text);
            if (text.Length != 0)
                Console.WriteLine(text);
            if (summary.Exported)
                Console.WriteLine($"Preserved original bundle bytes in '{summary.Output}'.");
        }
        else
        {
            var document = JsonNode.Parse(report.Format(format == "json" ? SiteDiagnosticFormat.Json : SiteDiagnosticFormat.Sarif))!.AsObject();
            var summaryNode = JsonSerializer.SerializeToNode(summary, JsonOptions);
            if (format == "json")
                document["okf"] = summaryNode;
            else
            {
                var run = document["runs"]![0]!.AsObject();
                run["properties"] ??= new JsonObject();
                run["properties"]!["okf"] = summaryNode;
            }
            Console.WriteLine(document.ToJsonString(JsonOptions));
        }
        return exitCode;
    }

    private sealed record Options(string Command, string Input, string? Output, string Format, OkfReadOptions Read);
    private sealed record Summary(string Command, string InputRoot, string SpecificationVersion, string? DeclaredVersion,
        DateTimeOffset AsOf, string State, bool Complete, bool SnapshotComplete, string Conformance, bool Success, bool StrictLint,
        int FileCount, DocumentSummary[] Documents, bool Exported, string? Output);
    private sealed record DocumentSummary(string Path, string Kind, string? ConceptId, bool Available,
        string? Type, string? Status, string? TrustTier, string? Freshness);
}
