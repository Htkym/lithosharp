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
            if (args.Length < 3 || args[1] != "docusaurus") throw new CliUsageException("Usage: migrate docusaurus <source> [--output <directory>] [--expected-routes <file>] [--base-url <url>] [--default-locale <locale>]");
            string? output = null;
            string? expectedRoutes = null;
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
                    default: throw new CliUsageException($"Unknown option '{args[index]}'.");
                }
            }

            return await MigrateAsync(args[2], output, expectedRoutes, new(baseUrl, defaultLocale), cancellationToken);
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
        string source, string? output, string? expectedRoutesFile, DocusaurusMigrationOptions options, CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? expectedRoutes = null;
        if (expectedRoutesFile is not null)
        {
            try
            {
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(expectedRoutesFile, cancellationToken));
                if (json.RootElement.ValueKind != JsonValueKind.Array
                    || json.RootElement.EnumerateArray().Any(element => element.ValueKind != JsonValueKind.String))
                    throw new CliUsageException("The expected-routes file must be a JSON array of strings.");
                expectedRoutes = json.RootElement.EnumerateArray().Select(element => element.GetString()!).ToArray();
            }
            catch (JsonException exception)
            {
                throw new CliUsageException("The expected-routes file must be a JSON array of strings: " + exception.Message);
            }
        }

        var report = output is null
            ? DocusaurusMigrationReport.Analyze(source, expectedRoutes, options, cancellationToken)
            : await DocusaurusMigrationReport.ConvertAsync(source, Path.GetFullPath(output), expectedRoutes, options, cancellationToken);
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
            },
            exitCode = report.ExitCode,
            nextSteps = new[] { "Create a lithosharp-mdx project.", "Copy trusted content, components and assets to the project.", "Configure explicit collections, variants, author profiles and sidebars.", "Restore pinned dependencies explicitly, then run check to validate strict front matter and MDX." },
        };
        Console.WriteLine(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return report.ExitCode;
    }
}
