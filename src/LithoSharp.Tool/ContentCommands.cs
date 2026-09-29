using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LithoSharp.Documentation;

namespace LithoSharp.Tool;

internal static class ContentCommands
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args[0] == "snapshot")
        {
            if (args.Length != 4) throw new CliUsageException("Usage: snapshot <source> <destination> <version>");
            await DocumentSnapshot.CreateAsync(args[1], args[2], args[3], cancellationToken);
            Console.WriteLine(Path.GetFullPath(args[2]));
            return 0;
        }
        if (args[0] == "restore-mdx")
        {
            if (args.Length < 2 || args.Length > 3) throw new CliUsageException("Specify an input directory.");
            var root = Path.GetFullPath(args[1]);
            if (args.Length == 3 && args[2] != "--allow-scripts") throw new CliUsageException("Only --allow-scripts is supported.");
            if (!File.Exists(Path.Combine(root, "package-lock.json")) || !File.Exists(Path.Combine(root, "worker.mjs")))
                throw new CliUsageException("Specify the packaged worker directory with its lockfile.");
            // npm.cmd is a Windows launcher; fixed arguments never contain user paths or shell syntax.
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "npm") { WorkingDirectory = root, UseShellExecute = false };
            if (OperatingSystem.IsWindows()) { start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("npm.cmd"); }
            start.ArgumentList.Add("ci"); start.ArgumentList.Add("--no-audit"); start.ArgumentList.Add("--no-fund");
            if (args.Length == 2) start.ArgumentList.Add("--ignore-scripts");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start npm.");
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); throw; }
            return process.ExitCode;
        }
        if (args[0] == "migrate-docusaurus")
        {
            if (args.Length != 2) throw new CliUsageException("Usage: migrate-docusaurus <source>");
            return await MigrateAsync(args[1], null, null, new(), cancellationToken);
        }
        if (args[0] == "migrate")
        {
            if (args.Length < 3 || args[1] != "docusaurus") throw new CliUsageException("Usage: migrate docusaurus <source> [--output <directory>] [--expected-routes <file>] [--base-url <url>] [--default-locale <locale>] [--source-version <version>] [--source-base-path <path>] [--compare-normalized-pages]");
            string? output = null;
            string? expectedRoutes = null;
            string? sourceVersion = null;
            string? sourceBasePath = null;
            var compareNormalizedPages = false;
            var baseUrl = "https://example.test/";
            var defaultLocale = "en";
            for (var index = 3; index < args.Length; index++)
            {
                string Next()
                {
                    if (++index >= args.Length) throw new CliUsageException($"Option '{args[index - 1]}' requires a value.");
                    return args[index];
                }

                switch (args[index])
                {
                    case "--output": output = Next(); break;
                    case "--expected-routes": expectedRoutes = Next(); break;
                    case "--base-url": baseUrl = Next(); break;
                    case "--default-locale": defaultLocale = Next(); break;
                    case "--source-version": sourceVersion = Next(); break;
                    case "--source-base-path": sourceBasePath = Next(); break;
                    case "--compare-normalized-pages": compareNormalizedPages = true; break;
                    default: throw new CliUsageException($"Unknown option '{args[index]}'.");
                }
            }

            if (expectedRoutes is null && (sourceVersion is not null || sourceBasePath is not null || compareNormalizedPages))
                throw new CliUsageException("Route comparison options require --expected-routes.");
            var migrationOptions = new DocusaurusMigrationOptions(baseUrl, defaultLocale)
            {
                CompareNormalizedPageSet = compareNormalizedPages,
            };
            return await MigrateAsync(args[2], output, expectedRoutes, migrationOptions, cancellationToken, sourceVersion, sourceBasePath);
        }
        if (args.Length < 2 || args.Length > 3) throw new CliUsageException("Specify an input directory.");
        var scanRoot = Path.GetFullPath(args[1]);
        if (args.Length != 2) throw new CliUsageException("This command accepts only an input directory.");
        var files = Directory.EnumerateFiles(scanRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(file => !Path.GetRelativePath(scanRoot, file).Split(Path.DirectorySeparatorChar).Any(segment => segment is "node_modules" or ".git" or "bin" or "obj"))
            .Where(file => Path.GetExtension(file) is ".md" or ".mdx" or ".js" or ".jsx" or ".ts" or ".tsx" or ".json").Order(StringComparer.Ordinal).ToArray();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files) texts.Add(Path.GetRelativePath(scanRoot, file).Replace('\\', '/'), await File.ReadAllTextAsync(file, cancellationToken));
        object report;
        if (args[0] == "extract-translations") report = texts.Values.SelectMany(TranslationCatalog.ExtractKeys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToDictionary(key => key, _ => "");
        else throw new CliUsageException($"Unknown command '{args[0]}'.");
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return 0;
    }

    private static async Task<int> MigrateAsync(
        string source,
        string? output,
        string? expectedRoutesFile,
        DocusaurusMigrationOptions options,
        CancellationToken cancellationToken,
        string? sourceVersionOverride = null,
        string? sourceBasePathOverride = null)
    {
        MigrationRouteOracle? routeOracle = null;
        if (expectedRoutesFile is not null)
        {
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(expectedRoutesFile, cancellationToken));
                routeOracle = ParseRouteOracle(json.RootElement);
            }
            catch (JsonException exception)
            {
                throw new CliUsageException("The expected-routes file must contain a route array or route-oracle object: " + exception.Message);
            }

            routeOracle = new MigrationRouteOracle(
                routeOracle.Routes,
                sourceVersionOverride ?? routeOracle.SourceVersion,
                sourceBasePathOverride ?? routeOracle.BasePath);
        }

        var report = output is null
            ? DocusaurusMigrationReport.AnalyzeWithOracle(source, routeOracle, options, cancellationToken)
            : await DocusaurusMigrationReport.ConvertWithOracleAsync(source, Path.GetFullPath(output), routeOracle, options, cancellationToken);
        var body = new
        {
            schemaVersion = report.SchemaVersion,
            dryRun = report.DryRun,
            wroteOutput = report.WroteOutput,
            destination = report.Destination,
            executedConfiguration = false,
            files = report.Files.Select(file => file.SourcePath).ToArray(),
            diagnostics = report.Files.SelectMany(file => file.Issues
                .Select(issue => new { file = file.SourcePath, line = issue.Location.Line, id = issue.Id, message = issue.Message })).ToArray(),
            verdicts = report.Files.Select(file => new
            {
                file = file.SourcePath,
                verdict = file.Verdict.ToString(),
                convertedFile = file.ConvertedPath,
                sourceFingerprint = file.SourceFingerprint,
                issues = file.Issues.Select(issue => new
                {
                    id = issue.Id,
                    severity = issue.Severity.ToString().ToLowerInvariant(),
                    issue.Message,
                    line = issue.Location.Line,
                    replacement = issue.Replacement,
                    manualStep = issue.ManualStep,
                }).ToArray(),
            }).ToArray(),
            suggestedActions = report.Files.SelectMany(file => file.SuggestedActions.Select(action => new
            {
                file = action.SourcePath,
                kind = action.Kind.ToString(),
                startLine = action.StartLine,
                endLine = action.EndLine,
                replacementText = action.ReplacementText,
                sourceFingerprint = action.SourceFingerprint,
                canApplyAutomatically = action.CanApplyAutomatically,
                applyCondition = action.ApplyCondition,
            })).ToArray(),
            componentChanges = report.ComponentChanges.Select(change => new
            {
                file = change.SourcePath,
                change.Component,
                kind = change.Kind.ToString(),
                functionalEquivalence = change.FunctionalEquivalence.ToString(),
                canApplyAutomatically = change.CanApplyAutomatically,
                change.Note,
            }).ToArray(),
            manifest = new
            {
                variants = report.Manifest.Variants.Select(variant => new
                {
                    variant.Version,
                    variant.Locale,
                    inputDirectory = variant.InputDirectory,
                    suggestedRoutePrefix = variant.SuggestedRoutePrefix,
                }).ToArray(),
                versionedSidebars = report.Manifest.VersionedSidebars,
                blogAuthors = report.Manifest.BlogAuthors.Select(author => new { id = author.Id, name = author.Name }).ToArray(),
                manualSteps = report.Manifest.ManualSteps,
                unsupported = report.Manifest.UnsupportedNotes,
            },
            routes = new
            {
                actual = report.ConvertedRoutes.Select(route => route.Route).ToArray(),
                missing = report.MissingRoutes,
                extra = report.ExtraRoutes,
                comparison = new
                {
                    rawStatus = report.RouteComparison.RawStatus.ToString(),
                    sourceRouteCount = report.RouteComparison.SourceRouteCount,
                    targetRouteCount = report.RouteComparison.TargetRouteCount,
                    excludedRouteCount = report.RouteComparison.ExcludedRouteCount,
                    duplicateSourceRouteCount = report.RouteComparison.DuplicateSourceRouteCount,
                    report.RouteComparison.SourceVersion,
                    report.RouteComparison.SourceHash,
                    report.RouteComparison.RouteOracleHash,
                    exclusionRules = report.RouteComparison.ExclusionRules.Select(rule => new
                    {
                        category = rule.Category.ToString(),
                        rule.Count,
                        rule.Reason,
                    }).ToArray(),
                    locales = report.RouteComparison.Locales.Select(locale => new { locale.Locale, locale.RouteCount }).ToArray(),
                    normalizedPageSet = new
                    {
                        status = report.RouteComparison.NormalizedPageSet.Status.ToString(),
                        sourcePageCount = report.RouteComparison.NormalizedPageSet.SourcePageCount,
                        normalizedSourcePageCount = report.RouteComparison.NormalizedPageSet.NormalizedSourcePageCount,
                        targetPageCount = report.RouteComparison.NormalizedPageSet.TargetPageCount,
                        normalizedTargetPageCount = report.RouteComparison.NormalizedPageSet.NormalizedTargetPageCount,
                        missing = report.RouteComparison.NormalizedPageSet.MissingRoutes,
                        extra = report.RouteComparison.NormalizedPageSet.ExtraRoutes,
                        rules = report.RouteComparison.NormalizedPageSet.Rules,
                        report.RouteComparison.NormalizedPageSet.Reason,
                    },
                },
            },
            exitCode = report.ExitCode,
            nextSteps = new[] { "Create a lithosharp-mdx project.", "Copy trusted content, components and assets to the project.", "Configure explicit collections, variants, author profiles and sidebars.", "Restore pinned dependencies explicitly, then run check to validate strict front matter and MDX." },
        };
        Console.WriteLine(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return report.ExitCode;
    }

    private static MigrationRouteOracle ParseRouteOracle(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.EnumerateArray().Any(element => element.ValueKind != JsonValueKind.String))
                throw new CliUsageException("A legacy expected-routes file must be a JSON array of strings.");
            return MigrationRouteOracle.FromPaths(root.EnumerateArray().Select(element => element.GetString()!).ToArray());
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("routes", out var routeArray)
            || routeArray.ValueKind != JsonValueKind.Array)
            throw new CliUsageException("An expected-routes oracle must be a JSON array of strings or an object with a routes array.");

        var routes = new List<MigrationRouteOracleEntry>();
        foreach (var route in routeArray.EnumerateArray())
        {
            if (route.ValueKind != JsonValueKind.Object
                || !route.TryGetProperty("path", out var pathValue)
                || pathValue.ValueKind != JsonValueKind.String)
                throw new CliUsageException("Each route-oracle entry must have a string path field.");

            var category = MigrationRouteCategory.Unclassified;
            if (route.TryGetProperty("kind", out var kindValue))
            {
                if (kindValue.ValueKind != JsonValueKind.String || !TryParseRouteCategory(kindValue.GetString()!, out category))
                    throw new CliUsageException("A route kind must be document, categoryIndex, blogIndex, blogAuthor, blogTag, blogArchive, blogPagination, unclassified, or other.");
            }

            routes.Add(new MigrationRouteOracleEntry(
                pathValue.GetString()!,
                category,
                OptionalString(route, "locale"),
                OptionalString(route, "reason")));
        }

        try
        {
            return new MigrationRouteOracle(routes, OptionalString(root, "sourceVersion"), OptionalString(root, "basePath"));
        }
        catch (ArgumentException exception)
        {
            throw new CliUsageException("The route oracle contains conflicting route metadata: " + exception.Message);
        }
    }

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new CliUsageException($"Route-oracle field '{propertyName}' must be a string or null.");
        return value.GetString();
    }

    private static bool TryParseRouteCategory(string value, out MigrationRouteCategory category)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        foreach (var candidate in Enum.GetValues<MigrationRouteCategory>())
        {
            var name = new string(candidate.ToString().Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            if (!string.Equals(normalized, name, StringComparison.Ordinal)) continue;
            category = candidate;
            return true;
        }

        category = default;
        return false;
    }
}
