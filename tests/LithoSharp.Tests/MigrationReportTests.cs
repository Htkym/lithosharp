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

    private sealed record LegacyCliRoutes(string[] Actual, string[] Missing, string[] Extra);

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
        await Assert.That(report.DryRun).IsTrue();
        await Assert.That(report.WroteOutput).IsFalse();
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

        await Assert.That(report.DryRun).IsFalse();
        await Assert.That(report.WroteOutput).IsTrue();

        foreach (var file in report.Files.Where(file => file.SuggestedActions.Count > 0))
        {
            var automatic = file.Verdict is MigrationVerdict.Automatic or MigrationVerdict.Convertible;
            await Assert.That(file.SuggestedActions.All(action => action.CanApplyAutomatically == automatic)).IsTrue();
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
        await Assert.That(readme.SuggestedActions[0].ReplacementText.Contains("slug:")).IsTrue();

        var linkfix = ByPath("docs/linkfix.mdx");
        await Assert.That(linkfix.SuggestedActions.Any(action =>
            action.Kind == MigrationActionKind.ReplaceLines && action.ReplacementText.Length == 0)).IsTrue();
        await Assert.That(linkfix.SuggestedActions.Any(action =>
            action.Kind == MigrationActionKind.ReplaceLines && action.ReplacementText.Contains("href="))).IsTrue();

        var hello = ByPath("blog/2024-01-02-hello.md");
        await Assert.That(hello.SuggestedActions.Count).IsEqualTo(1);
        await Assert.That(hello.SuggestedActions[0].ReplacementText.Contains("date:")).IsTrue();

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
        await Assert.That(custom.SuggestedActions.All(action => action.CanApplyAutomatically)).IsFalse();
        foreach (var action in custom.SuggestedActions)
        {
            await Assert.That(action.ApplyCondition.Contains("manual", StringComparison.OrdinalIgnoreCase)).IsTrue();
        }

        var source = await File.ReadAllBytesAsync(Path.Combine(site, "docs/guide/custom.md"));
        var action0 = custom.SuggestedActions[0];
        await Assert.That(action0.MatchesSource(source)).IsTrue();
        await Assert.That(action0.MatchesSourceText(Encoding.UTF8.GetString(source))).IsTrue();
        var edited = source.Concat([ (byte)'\n' ]).ToArray();
        await Assert.That(action0.MatchesSource(edited)).IsFalse();
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
    public async Task MissingOracleIsNotReportedAsAComparisonPass()
    {
        using var workspace = new TemporaryWorkspace();
        var site = await WriteSourceAsync(workspace.Root);
        var package = Path.Combine(site, "package.json");
        await File.WriteAllTextAsync(package, "{\"devDependencies\":{\"@docusaurus/core\":\"3.9.1\"}}\n");

        var report = DocusaurusMigrationReport.Analyze(site, options: Options);

        await Assert.That(report.RouteComparison.RawStatus).IsEqualTo(MigrationRouteComparisonStatus.NotCompared);
        await Assert.That(report.RouteComparison.SourceRouteCount).IsNull();
        await Assert.That(report.RouteComparison.ExcludedRouteCount).IsNull();
        await Assert.That(report.RouteComparison.NormalizedPageSet.Status).IsEqualTo(MigrationPageSetComparisonStatus.NotRequested);
        await Assert.That(report.RouteComparison.SourceHash.Length).IsEqualTo(64);
        await Assert.That(report.RouteComparison.SourceVersion).IsEqualTo("3.9.1");
        await Assert.That(report.MissingRoutes).IsEmpty();
        await Assert.That(report.ExtraRoutes).IsEmpty();

        await File.WriteAllTextAsync(package, "{\"devDependencies\":{\"@docusaurus/core\":\"3.10.2\"}}\n");
        var changedSource = DocusaurusMigrationReport.Analyze(site, options: Options);
        await Assert.That(changedSource.RouteComparison.SourceVersion).IsEqualTo("3.10.2");
        await Assert.That(changedSource.RouteComparison.SourceHash).IsNotEqualTo(report.RouteComparison.SourceHash);

        await File.WriteAllTextAsync(package, "[]\n");
        var malformedMetadata = DocusaurusMigrationReport.Analyze(site, options: Options);
        await Assert.That(malformedMetadata.RouteComparison.SourceVersion).IsNull();
    }

    [Test]
    public async Task NormalizedDocumentPagesStaySeparateFromExactWholeSiteRoutes()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs", "intro.md"), "---\ntitle: Intro\n---\n\nBody.\n");
        var oracle = new MigrationRouteOracle(
        [
            new("/legacy/docs/intro", MigrationRouteCategory.Document, "en"),
            new("/legacy/docs/intro", MigrationRouteCategory.Document, "en"),
            new("/legacy/docs/excluded/", MigrationRouteCategory.Document, "en", "Reserved route outside this comparison run."),
            new("/legacy/docs/category/", MigrationRouteCategory.CategoryIndex, "en"),
            new("/legacy/blog/", MigrationRouteCategory.BlogIndex, "en"),
            new("/legacy/blog/authors/ada/", MigrationRouteCategory.BlogAuthor, "en"),
            new("/legacy/blog/tags/csharp/", MigrationRouteCategory.BlogTag, "en"),
            new("/legacy/blog/archive/", MigrationRouteCategory.BlogArchive, "en"),
            new("/legacy/blog/page/2/", MigrationRouteCategory.BlogPagination, "en"),
            new("/legacy/search/", MigrationRouteCategory.Other, "en", "Search is a separate tool surface."),
        ], sourceVersion: "3.10.2", basePath: "/legacy/");

        var report = DocusaurusMigrationReport.AnalyzeWithOracle(
            site, oracle, Options with { CompareNormalizedPageSet = true });

        await Assert.That(report.RouteComparison.RawStatus).IsEqualTo(MigrationRouteComparisonStatus.Differences);
        await Assert.That(report.MissingRoutes).Contains("/legacy/docs/intro");
        await Assert.That(report.ExtraRoutes).Contains("/mig/docs/intro/");
        await Assert.That(report.RouteComparison.SourceRouteCount).IsEqualTo(9);
        await Assert.That(report.RouteComparison.TargetRouteCount).IsEqualTo(1);
        await Assert.That(report.RouteComparison.ExcludedRouteCount).IsEqualTo(8);
        await Assert.That(report.RouteComparison.DuplicateSourceRouteCount).IsEqualTo(1);
        await Assert.That(report.RouteComparison.SourceVersion).IsEqualTo("3.10.2");
        await Assert.That(report.RouteComparison.RouteOracleHash?.Length).IsEqualTo(64);
        await Assert.That(report.RouteComparison.ExclusionRules.Select(rule => rule.Category).ToArray())
            .IsEquivalentTo([
                MigrationRouteCategory.CategoryIndex,
                MigrationRouteCategory.BlogIndex,
                MigrationRouteCategory.BlogAuthor,
                MigrationRouteCategory.BlogTag,
                MigrationRouteCategory.BlogArchive,
                MigrationRouteCategory.BlogPagination,
                MigrationRouteCategory.Other,
                MigrationRouteCategory.Document,
            ]);
        await Assert.That(report.RouteComparison.ExclusionRules.Single(rule => rule.Category == MigrationRouteCategory.Other).Reason)
            .IsEqualTo("Search is a separate tool surface.");
        await Assert.That(report.RouteComparison.ExclusionRules.Single(rule => rule.Category == MigrationRouteCategory.Document).Reason)
            .IsEqualTo("Reserved route outside this comparison run.");
        await Assert.That(report.RouteComparison.Locales.Single().Locale).IsEqualTo("en");
        await Assert.That(report.RouteComparison.Locales.Single().RouteCount).IsEqualTo(9);

        var pages = report.RouteComparison.NormalizedPageSet;
        await Assert.That(pages.Status).IsEqualTo(MigrationPageSetComparisonStatus.Match);
        await Assert.That(pages.SourcePageCount).IsEqualTo(1);
        await Assert.That(pages.NormalizedSourcePageCount).IsEqualTo(1);
        await Assert.That(pages.TargetPageCount).IsEqualTo(1);
        await Assert.That(pages.NormalizedTargetPageCount).IsEqualTo(1);
        await Assert.That(pages.MissingRoutes).IsEmpty();
        await Assert.That(pages.ExtraRoutes).IsEmpty();
        await Assert.That(pages.Rules.Any(rule => rule.Contains("source base path", StringComparison.Ordinal))).IsTrue();
        await Assert.That(pages.Rules.Any(rule => rule.Contains("case-sensitive", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task NormalizedComparisonCanonicalizesEncodedUnicodeOnceButKeepsCaseDistinct()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs", "café.md"), "---\ntitle: Café\n---\n");
        await File.WriteAllTextAsync(Path.Combine(site, "docs", "start.md"), "---\ntitle: Start\n---\n");
        var oracle = new MigrationRouteOracle(
        [
            new("/legacy/docs/cafe\u0301", MigrationRouteCategory.Document, "fr"),
            new("/legacy/docs/START/", MigrationRouteCategory.Document, "fr"),
        ], sourceVersion: "3.10.2", basePath: "/legacy/");

        var report = DocusaurusMigrationReport.AnalyzeWithOracle(
            site, oracle, Options with { CompareNormalizedPageSet = true });

        var pages = report.RouteComparison.NormalizedPageSet;
        await Assert.That(pages.Status).IsEqualTo(MigrationPageSetComparisonStatus.Differences);
        await Assert.That(pages.MissingRoutes).IsEquivalentTo(["/docs/START/"]);
        await Assert.That(pages.ExtraRoutes).IsEquivalentTo(["/docs/start/"]);
        await Assert.That(pages.MissingRoutes.Any(route => route.Contains("caf", StringComparison.Ordinal))).IsFalse();
        await Assert.That(pages.Rules.Any(rule => rule.Contains("once", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task ConflictingDuplicateOracleClassificationIsRejected()
    {
        await Assert.That(() => new MigrationRouteOracle(
        [
            new("/docs/start/", MigrationRouteCategory.Document),
            new("/docs/start/", MigrationRouteCategory.CategoryIndex),
        ])).Throws<ArgumentException>();
    }

    [Test]
    public async Task ManualComponentChangesAreClassifiedAndNeverAutoApplied()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs", "components.mdx"),
            "---\ntitle: Components\n---\n" +
            "import ThemedImage from '@theme/ThemedImage';\n" +
            "import Zoom from 'react-medium-image-zoom';\n" +
            "import Video from './LiteYouTubeEmbed';\n" +
            "import Guide from './UpgradeGuide';\n" +
            "import Toggle from './ColorModeToggle';\n" +
            "import raw from 'raw-loader!./sample';\n" +
            "import Live from 'react-live';\n" +
            "import Custom from '@theme/CustomWidget';\n\n" +
            "<ThemedImage /><Zoom /><Video /><Guide /><Toggle /><Live /><Custom />\n");

        var report = DocusaurusMigrationReport.Analyze(site, options: Options);

        var file = report.Files.Single(item => item.SourcePath == "docs/components.mdx");
        await Assert.That(file.ComponentChanges.Select(change => change.Kind).Distinct().Order().ToArray())
            .IsEquivalentTo(Enum.GetValues<MigrationComponentChangeKind>().Order().ToArray());
        await Assert.That(file.ComponentChanges.Single(change => change.Component == "ThemedImage").Kind)
            .IsEqualTo(MigrationComponentChangeKind.AppearanceChanged);
        await Assert.That(file.ComponentChanges.Single(change => change.Component == "LiteYouTubeEmbed").Kind)
            .IsEqualTo(MigrationComponentChangeKind.InteractionChanged);
        await Assert.That(file.ComponentChanges.Single(change => change.Component == "UpgradeGuide").Kind)
            .IsEqualTo(MigrationComponentChangeKind.Staticized);
        await Assert.That(file.ComponentChanges.Single(change => change.Component == "react-live").Kind)
            .IsEqualTo(MigrationComponentChangeKind.Deleted);
        await Assert.That(file.ComponentChanges.All(change => !change.CanApplyAutomatically)).IsTrue();
        await Assert.That(file.ComponentChanges.Where(change => change.Kind != MigrationComponentChangeKind.Unverified)
            .All(change => change.FunctionalEquivalence == MigrationFunctionalEquivalence.NotEquivalent)).IsTrue();
        await Assert.That(file.ComponentChanges.Single(change => change.Component == "@theme/CustomWidget").FunctionalEquivalence)
            .IsEqualTo(MigrationFunctionalEquivalence.Unverified);
        await Assert.That(file.SuggestedActions.All(action => !action.CanApplyAutomatically)).IsTrue();
        await Assert.That(report.ComponentChanges.Count).IsEqualTo(file.ComponentChanges.Count);
        await Assert.That(file.SourceFingerprint.Length).IsEqualTo(64);
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
        var comparison = root.GetProperty("routes").GetProperty("comparison");
        await Assert.That(comparison.GetProperty("rawStatus").GetString()).IsEqualTo(api.RouteComparison.RawStatus.ToString());
        await Assert.That(comparison.GetProperty("sourceRouteCount").GetInt32()).IsEqualTo(api.RouteComparison.SourceRouteCount);
        await Assert.That(comparison.GetProperty("sourceHash").GetString()).IsEqualTo(api.RouteComparison.SourceHash);
        await Assert.That(comparison.GetProperty("routeOracleHash").GetString()).IsEqualTo(api.RouteComparison.RouteOracleHash);

        // New route comparison fields are additive to the old CLI routes object.
        var legacyRoutes = JsonSerializer.Deserialize<LegacyCliRoutes>(
            root.GetProperty("routes").GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Assert.That(legacyRoutes).IsNotNull();
        await Assert.That(legacyRoutes!.Missing).IsEquivalentTo(api.MissingRoutes);
        await Assert.That(legacyRoutes.Extra).IsEquivalentTo(api.ExtraRoutes);

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

    [Test]
    public async Task StructuredOracleCliAndApiUseTheSameNormalizedComparison()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs", "intro.md"), "---\ntitle: Intro\n---\n\nBody.\n");
        var oraclePath = Path.Combine(workspace.Root, "expected.json");
        const string oracleJson = """
            {
              "sourceVersion": "3.10.2",
              "basePath": "/legacy/",
              "routes": [
                { "path": "/legacy/docs/intro", "kind": "document", "locale": "en" },
                { "path": "/legacy/docs/category/", "kind": "categoryIndex", "locale": "en" }
              ]
            }
            """;
        await File.WriteAllTextAsync(oraclePath, oracleJson);
        var oracle = new MigrationRouteOracle(
        [
            new("/legacy/docs/intro", MigrationRouteCategory.Document, "en"),
            new("/legacy/docs/category/", MigrationRouteCategory.CategoryIndex, "en"),
        ], "3.10.2", "/legacy/");
        var options = new DocusaurusMigrationOptions("https://example.test/mig/", "en")
        {
            CompareNormalizedPageSet = true,
        };
        var api = DocusaurusMigrationReport.AnalyzeWithOracle(site, oracle, options);
        await Assert.That(api.ExitCode).IsEqualTo(0);
        var tool = FindTool();
        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                tool, "migrate", "docusaurus", site,
                "--expected-routes", oraclePath,
                "--base-url", "https://example.test/mig/",
                "--compare-normalized-pages",
            },
        })!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        await Assert.That(stderr).IsEqualTo(string.Empty);
        await Assert.That(process.ExitCode).IsEqualTo(api.ExitCode);
        using var cli = JsonDocument.Parse(stdout);
        var routeComparison = cli.RootElement.GetProperty("routes").GetProperty("comparison");
        await Assert.That(routeComparison.GetProperty("rawStatus").GetString()).IsEqualTo(api.RouteComparison.RawStatus.ToString());
        await Assert.That(routeComparison.GetProperty("sourceRouteCount").GetInt32()).IsEqualTo(api.RouteComparison.SourceRouteCount);
        await Assert.That(routeComparison.GetProperty("excludedRouteCount").GetInt32()).IsEqualTo(api.RouteComparison.ExcludedRouteCount);
        await Assert.That(routeComparison.GetProperty("sourceVersion").GetString()).IsEqualTo(api.RouteComparison.SourceVersion);
        await Assert.That(routeComparison.GetProperty("sourceHash").GetString()).IsEqualTo(api.RouteComparison.SourceHash);
        await Assert.That(routeComparison.GetProperty("routeOracleHash").GetString()).IsEqualTo(api.RouteComparison.RouteOracleHash);
        await Assert.That(routeComparison.GetProperty("normalizedPageSet").GetProperty("status").GetString())
            .IsEqualTo(api.RouteComparison.NormalizedPageSet.Status.ToString());
        await Assert.That(routeComparison.GetProperty("normalizedPageSet").GetProperty("missing").GetArrayLength())
            .IsEqualTo(api.RouteComparison.NormalizedPageSet.MissingRoutes.Count);
        await Assert.That(api.RouteComparison.RawStatus).IsEqualTo(MigrationRouteComparisonStatus.Differences);
        await Assert.That(api.RouteComparison.NormalizedPageSet.Status).IsEqualTo(MigrationPageSetComparisonStatus.Match);
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
