using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LithoSharp.Documentation;

namespace LithoSharp.Tests;

/// <summary>
/// T09: the C10 classification is reused through a public report with schema.
/// CLI and API return the same verdicts, aggregates and fix candidates, and a
/// future Quick Fix can apply engine-decided rewrites without reimplementing them.
/// </summary>
public sealed class MigrationReportTests
{
    private static readonly DocusaurusMigrationOptions Options = new("https://example.test/mig/", "en");

    private static async Task<string> WriteSourceAsync(string root)
    {
        var site = Path.Combine(root, "site");
        async Task Write(string relative, string text)
        {
            var path = Path.Combine(site, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        }

        await Write("docusaurus.config.js", "export default {};\n");
        await Write("docs/intro.md", "---\ntitle: Intro\n---\n\nIntro body.\n");
        await Write("docs/guide/README.md", "---\ntitle: Guide\n---\n\nGuide body.\n");
        await Write("docs/guide/custom.md", "---\ntitle: Custom\nslug: my-page\ncustom: 1\n---\n\nCustom body.\n");
        await Write("docs/linkfix.mdx", "---\ntitle: Linkfix\n---\nimport Link from '@docusaurus/Link';\n\n<Link to=\"/mig/docs/start/\">Start</Link>\n");
        await Write("blog/2024-01-02-hello.md", "---\ntitle: Hello\nauthors: [ada]\nsummary: Hi\n---\n\nHello body.\n");
        await Write("blog/authors.yml", "ada:\n  name: Ada Lovelace\n");
        await Write("src/components/Bad.js", "// @docusaurus/theme-foo\n");
        return site;
    }

    [Test]
    public async Task ReportExposesVerdictsLocationsAndCounts()
    {
        using var workspace = new TemporaryWorkspace();
        var site = await WriteSourceAsync(workspace.Root);
        var report = DocusaurusMigrationReport.Analyze(site, options: Options);

        await Assert.That(report.SchemaVersion).IsEqualTo("1.0");
        await Assert.That(report.DryRun).IsEqualTo(true);
        await Assert.That(report.WroteOutput).IsEqualTo(false);
        await Assert.That(report.Destination).IsNull();
        await Assert.That(report.Summary.TotalFiles).IsEqualTo(report.Files.Count);
        await Assert.That(report.Summary.Automatic + report.Summary.Convertible + report.Summary.ManualActionRequired + report.Summary.Unsupported)
            .IsEqualTo(report.Summary.TotalFiles);

        MigrationVerdict Verdict(string path) =>
            report.Files.Single(file => file.SourcePath == path).Verdict;
        await Assert.That(Verdict("docs/intro.md")).IsEqualTo(MigrationVerdict.Automatic);
        await Assert.That(Verdict("docs/guide/README.md")).IsEqualTo(MigrationVerdict.Convertible);
        await Assert.That(Verdict("docs/guide/custom.md")).IsEqualTo(MigrationVerdict.ManualActionRequired);
        await Assert.That(Verdict("docs/linkfix.mdx")).IsEqualTo(MigrationVerdict.Convertible);
        await Assert.That(Verdict("docusaurus.config.js")).IsEqualTo(MigrationVerdict.ManualActionRequired);
        await Assert.That(Verdict("src/components/Bad.js")).IsEqualTo(MigrationVerdict.Unsupported);
        await Assert.That(report.ExitCode).IsEqualTo(3);

        foreach (var file in report.Files)
        {
            await Assert.That(file.SourceFingerprint.Length).IsEqualTo(64);
            foreach (var issue in file.Issues)
            {
                await Assert.That(issue.Location.FilePath).IsEqualTo(file.SourcePath);
                await Assert.That(issue.Location.Line).IsNotNull();
            }
        }
    }

    [Test]
    public async Task SuggestedActionsReplayConversion()
    {
        using var workspace = new TemporaryWorkspace();
        var site = await WriteSourceAsync(workspace.Root);
        var destination = Path.Combine(workspace.Root, "out");
        var report = await DocusaurusMigrationReport.ConvertAsync(site, destination, options: Options);

        await Assert.That(report.DryRun).IsEqualTo(false);
        await Assert.That(report.WroteOutput).IsEqualTo(true);

        foreach (var file in report.Files.Where(file => file.SuggestedActions.Count > 0))
        {
            var automatic = file.Verdict is MigrationVerdict.Automatic or MigrationVerdict.Convertible;
            await Assert.That(file.SuggestedActions.All(action => action.CanApplyAutomatically == automatic)).IsEqualTo(true);
            foreach (var action in file.SuggestedActions)
            {
                await Assert.That(action.SourcePath).IsEqualTo(file.SourcePath);
                await Assert.That(action.SourceFingerprint).IsEqualTo(file.SourceFingerprint);
            }

            var source = await File.ReadAllBytesAsync(Path.Combine(site, file.SourcePath.Replace('/', Path.DirectorySeparatorChar)));
            var replayed = Replay(source, file.SuggestedActions);
            var converted = await File.ReadAllBytesAsync(Path.Combine(destination, (file.ConvertedPath ?? file.SourcePath).Replace('/', Path.DirectorySeparatorChar)));
            await Assert.That(replayed).IsEquivalentTo(converted);
        }

        MigrationFileReport ByPath(string path) => report.Files.Single(file => file.SourcePath == path);
        var readme = ByPath("docs/guide/README.md");
        await Assert.That(readme.SuggestedActions.Count).IsEqualTo(1);
        await Assert.That(readme.SuggestedActions[0].Kind).IsEqualTo(MigrationActionKind.InsertAfter);
        await Assert.That(readme.SuggestedActions[0].ReplacementText.Contains("slug:")).IsEqualTo(true);

        var linkfix = ByPath("docs/linkfix.mdx");
        await Assert.That(linkfix.SuggestedActions.Any(action =>
            action.Kind == MigrationActionKind.ReplaceLines && action.ReplacementText.Length == 0)).IsEqualTo(true);
        await Assert.That(linkfix.SuggestedActions.Any(action =>
            action.Kind == MigrationActionKind.ReplaceLines && action.ReplacementText.Contains("href="))).IsEqualTo(true);

        var hello = ByPath("blog/2024-01-02-hello.md");
        await Assert.That(hello.SuggestedActions.Count).IsEqualTo(1);
        await Assert.That(hello.SuggestedActions[0].ReplacementText.Contains("date:")).IsEqualTo(true);

        var authors = ByPath("blog/authors.yml");
        await Assert.That(authors.Verdict).IsEqualTo(MigrationVerdict.Convertible);
        await Assert.That(authors.SuggestedActions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AmbiguousAndStaleAreNotAutoApplicable()
    {
        using var workspace = new TemporaryWorkspace();
        var site = await WriteSourceAsync(workspace.Root);
        var report = DocusaurusMigrationReport.Analyze(site, options: Options);

        var custom = report.Files.Single(file => file.SourcePath == "docs/guide/custom.md");
        await Assert.That(custom.SuggestedActions.Count).IsGreaterThan(0);
        await Assert.That(custom.SuggestedActions.All(action => action.CanApplyAutomatically)).IsEqualTo(false);
        foreach (var action in custom.SuggestedActions)
        {
            await Assert.That(action.ApplyCondition.Contains("manual", StringComparison.OrdinalIgnoreCase)).IsEqualTo(true);
        }

        var source = await File.ReadAllBytesAsync(Path.Combine(site, "docs/guide/custom.md"));
        var action0 = custom.SuggestedActions[0];
        await Assert.That(action0.MatchesSource(source)).IsEqualTo(true);
        await Assert.That(action0.MatchesSourceText(Encoding.UTF8.GetString(source))).IsEqualTo(true);
        var edited = source.Concat([ (byte)'\n' ]).ToArray();
        await Assert.That(action0.MatchesSource(edited)).IsEqualTo(false);
    }

    [Test]
    public async Task NonCanonicalExpectedRoutesSurfaceExplicitly()
    {
        using var workspace = new TemporaryWorkspace();
        var site = await WriteSourceAsync(workspace.Root);
        var canonical = DocusaurusMigrationReport.Analyze(site, options: Options);
        var routes = canonical.ConvertedRoutes.Select(route => route.Route).ToArray();
        await Assert.That(routes.Length).IsGreaterThan(0);

        // Representation variance (missing trailing slash) never passes silently:
        // it surfaces as explicit missing/extra entries.
        var nonCanonical = routes.Select(route => route.TrimEnd('/')).ToArray();
        var compared = DocusaurusMigrationReport.Analyze(site, nonCanonical, Options);
        await Assert.That(compared.MissingRoutes.Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(nonCanonical.Order(StringComparer.Ordinal).ToArray());
        await Assert.That(compared.ExtraRoutes.Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(routes.Order(StringComparer.Ordinal).ToArray());
    }

    [Test]
    public async Task CliAndApiReturnSameVerdicts()
    {
        using var workspace = new TemporaryWorkspace();
        var site = await WriteSourceAsync(workspace.Root);
        var tool = FindTool();
        var expectedRoutesPath = Path.Combine(workspace.Root, "expected.json");
        await File.WriteAllTextAsync(expectedRoutesPath, "[\"/mig/docs/start/\", \"/mig/docs/missing/\"]");

        var api = DocusaurusMigrationReport.Analyze(site, ["/mig/docs/start/", "/mig/docs/missing/"], Options);
        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { tool, "migrate", "docusaurus", site, "--expected-routes", expectedRoutesPath, "--base-url", "https://example.test/mig/", "--default-locale", "en" },
        })!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Assert.That(stderr).IsEqualTo(string.Empty);
        await Assert.That(process.ExitCode).IsEqualTo(api.ExitCode);

        using var cli = JsonDocument.Parse(stdout);
        var root = cli.RootElement;
        await Assert.That(root.GetProperty("schemaVersion").GetString()).IsEqualTo(api.SchemaVersion);
        await Assert.That(root.GetProperty("exitCode").GetInt32()).IsEqualTo(api.ExitCode);

        var cliVerdicts = root.GetProperty("verdicts").EnumerateArray().ToDictionary(element => element.GetProperty("file").GetString()!);
        await Assert.That(cliVerdicts.Keys.ToHashSet(StringComparer.Ordinal))
            .IsEquivalentTo(api.Files.Select(file => file.SourcePath).ToHashSet(StringComparer.Ordinal));
        foreach (var file in api.Files)
        {
            var element = cliVerdicts[file.SourcePath];
            await Assert.That(element.GetProperty("verdict").GetString()).IsEqualTo(file.Verdict.ToString());
            await Assert.That(element.GetProperty("convertedFile").GetString()).IsEqualTo(file.ConvertedPath);
            var cliIssues = element.GetProperty("issues").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString() + "|" + item.GetProperty("message").GetString()
                    + "|" + item.GetProperty("line").GetInt32() + "|" + (item.GetProperty("replacement").GetString() ?? "")
                    + "|" + (item.GetProperty("manualStep").GetString() ?? "")).Order(StringComparer.Ordinal).ToArray();
            var apiIssues = file.Issues
                .Select(issue => issue.Id + "|" + issue.Message + "|" + issue.Location.Line + "|" + (issue.Replacement ?? "") + "|" + (issue.ManualStep ?? ""))
                .Order(StringComparer.Ordinal).ToArray();
            await Assert.That(cliIssues).IsEquivalentTo(apiIssues);
        }

        await Assert.That(root.GetProperty("routes").GetProperty("missing").EnumerateArray().Select(element => element.GetString()!).Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(api.MissingRoutes.Order(StringComparer.Ordinal).ToArray());
        await Assert.That(root.GetProperty("routes").GetProperty("extra").EnumerateArray().Select(element => element.GetString()!).Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(api.ExtraRoutes.Order(StringComparer.Ordinal).ToArray());

        var cliActions = root.GetProperty("suggestedActions").EnumerateArray().ToArray();
        var apiActions = api.Files.SelectMany(file => file.SuggestedActions).ToArray();
        await Assert.That(cliActions.Length).IsEqualTo(apiActions.Length);
        foreach (var (cliAction, apiAction) in cliActions.Zip(apiActions))
        {
            await Assert.That(cliAction.GetProperty("file").GetString()).IsEqualTo(apiAction.SourcePath);
            await Assert.That(cliAction.GetProperty("kind").GetString()).IsEqualTo(apiAction.Kind.ToString());
            await Assert.That(cliAction.GetProperty("startLine").GetInt32()).IsEqualTo(apiAction.StartLine);
            await Assert.That(cliAction.GetProperty("sourceFingerprint").GetString()).IsEqualTo(apiAction.SourceFingerprint);
            await Assert.That(cliAction.GetProperty("canApplyAutomatically").GetBoolean()).IsEqualTo(apiAction.CanApplyAutomatically);
        }
    }

    private static byte[] Replay(byte[] source, IReadOnlyList<MigrationSuggestedAction> actions)
    {
        var text = Encoding.UTF8.GetString(source);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        foreach (var action in actions.Where(action => action.Kind == MigrationActionKind.ReplaceLines)
                     .OrderByDescending(action => action.StartLine).ThenBy(action => action.EndLine))
        {
            var replacement = action.ReplacementText.Length == 0
                ? []
                : action.ReplacementText.Split('\n').ToList();
            lines.RemoveRange(action.StartLine - 1, action.EndLine - action.StartLine + 1);
            lines.InsertRange(action.StartLine - 1, replacement);
        }
        foreach (var action in actions.Where(action => action.Kind == MigrationActionKind.InsertAfter))
        {
            lines.InsertRange(action.StartLine, action.ReplacementText.Split('\n'));
        }
        return Encoding.UTF8.GetBytes(string.Join(newline, lines));
    }

    private static string FindTool()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LithoSharp.slnx")))
        {
            directory = directory.Parent;
        }
        var tool = directory is null ? null : Path.Combine(directory.FullName, "src", "LithoSharp.Tool", "bin", "Release", "net10.0", "LithoSharp.Tool.dll");
        if (tool is null || !File.Exists(tool))
        {
            throw new InvalidOperationException("Build the Release tool before running migration CLI parity tests.");
        }
        return tool;
    }
}
