using System.Text.Json;
using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;

namespace LithoSharp.Tests;

/// <summary>
/// C11: the committed Docusaurus corpus runs diagnose, convert, build, then
/// route and structural DOM comparison per fixture. MDX rendering itself stays
/// covered by mdx-baseline and mdx-browser; this corpus covers migration only.
/// </summary>
public sealed class DocusaurusCorpusTests
{
    private static readonly DocusaurusMigrationOptions Options = new("https://example.test/mig/", "en");
    private static readonly DateTimeOffset BuildTimestamp = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    ];

    private static string CorpusDirectory()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
            {
                return Path.Combine(path.FullName, "tests", "fixtures", "docusaurus");
            }
        }

        throw new DirectoryNotFoundException("The Docusaurus corpus requires the repository root.");
    }

    private static string WorkerDirectory()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
            {
                return Path.Combine(path.FullName, "src", "LithoSharp.Mdx", "worker");
            }
        }

        throw new DirectoryNotFoundException("The MDX integration fixture requires the repository's restored worker.");
    }

    [Test] public async Task Minimal() => await RunFixture("minimal");
    [Test] public async Task Docs() => await RunFixture("docs");
    [Test] public async Task DocsVersioned() => await RunFixture("docs-versioned");
    [Test] public async Task DocsI18n() => await RunFixture("docs-i18n");
    [Test] public async Task Blog() => await RunFixture("blog");
    [Test] public async Task MdxComponents() => await RunFixture("mdx-components");
    [Test] public async Task Admonitions() => await RunFixture("admonitions");
    [Test] public async Task Sidebars() => await RunFixture("sidebars");
    [Test] public async Task Assets() => await RunFixture("assets");
    [Test] public async Task FullSite() => await RunFixture("full-site");

    private static async Task RunFixture(string name)
    {
        using var workspace = new TemporaryWorkspace();
        var fixture = Path.Combine(CorpusDirectory(), name);
        var site = Path.Combine(workspace.Root, "site");
        CopyDirectory(Path.Combine(fixture, "source"), site);
        using var build = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "build.json")));
        using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "expected.json")));
        using var routes = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "expected-routes.json")));
        var expectedRoutes = routes.RootElement.EnumerateArray().Select(element => element.GetString()!).ToArray();
        if (build.RootElement.TryGetProperty("png", out var png))
        {
            var targets = png.ValueKind == JsonValueKind.Array
                ? png.EnumerateArray().Select(element => element.GetString()!).ToArray()
                : [png.GetString()!];
            foreach (var target in targets)
                await File.WriteAllBytesAsync(Path.Combine(site, target.Replace('/', Path.DirectorySeparatorChar)), PngBytes);
        }
        var before = Snapshot(site);

        var analyzed = DocusaurusMigration.Analyze(site, expectedRoutes, Options, CancellationToken.None);
        await Assert.That(Snapshot(site)).IsEquivalentTo(before);
        await Assert.That(analyzed.ExitCode).IsEqualTo(expected.RootElement.GetProperty("exitCode").GetInt32());
        await Assert.That(analyzed.Files.Select(file => file.SourcePath).Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(expected.RootElement.GetProperty("verdicts").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());
        foreach (var file in analyzed.Files)
            await Assert.That(file.Verdict.ToString()).IsEqualTo(expected.RootElement.GetProperty("verdicts").GetProperty(file.SourcePath).GetString());
        foreach (var property in expected.RootElement.GetProperty("issues").EnumerateObject())
        {
            var actual = analyzed.Files.Single(file => file.SourcePath == property.Name).Issues.Select(issue => issue.Id).ToArray();
            foreach (var id in property.Value.EnumerateArray().Select(element => element.GetString()!))
                await Assert.That(actual.Contains(id)).IsTrue();
        }

        await Assert.That(analyzed.MissingRoutes).IsEmpty();
        await Assert.That(analyzed.ExtraRoutes).IsEmpty();

        var destination = Path.Combine(workspace.Root, "converted");
        var converted = await DocusaurusMigration.ConvertAsync(site, destination, expectedRoutes, Options, CancellationToken.None);
        await Assert.That(converted.WroteOutput).IsTrue();
        await Assert.That(converted.ExitCode).IsEqualTo(analyzed.ExitCode);
        await Assert.That(converted.MissingRoutes).IsEmpty();
        await Assert.That(converted.ExtraRoutes).IsEmpty();

        var variants = expected.RootElement.GetProperty("variants").EnumerateArray()
            .Select(element => (element.GetProperty("version").GetString()!, element.GetProperty("locale").GetString()!,
                element.GetProperty("input").GetString()!, element.GetProperty("prefix").GetString()!)).ToArray();
        await Assert.That(converted.Manifest.Variants
            .Select(variant => (variant.Version, variant.Locale, variant.InputDirectory, variant.SuggestedRoutePrefix)).ToArray())
            .IsEquivalentTo(variants);
        if (expected.RootElement.TryGetProperty("manualStepContains", out var steps))
        {
            var joined = string.Join("\n", converted.Manifest.ManualSteps);
            foreach (var step in steps.EnumerateArray().Select(element => element.GetString()!))
                await Assert.That(joined.Contains(step)).IsTrue();
        }

        if (expected.RootElement.TryGetProperty("preservedBytes", out var preserved))
            foreach (var relative in preserved.EnumerateArray().Select(element => element.GetString()!))
                await Assert.That(await File.ReadAllBytesAsync(Path.Combine(destination, relative)))
                    .IsEquivalentTo(await File.ReadAllBytesAsync(Path.Combine(site, relative)));

        await using var docs = new DocumentationSite(new(workspace.Root, WorkerDirectory()) { Cacheable = true });
        if (converted.Manifest.Variants.Count > 0)
            docs.AddCollection(new("guide", converted.Manifest.Variants
                .Select(variant => new DocumentVariant(variant.Version, variant.Locale,
                    Path.Combine(destination, variant.InputDirectory), variant.SuggestedRoutePrefix)).ToArray())
            { UseMdx = build.RootElement.GetProperty("mdx").GetBoolean() });
        if (build.RootElement.GetProperty("blog").GetBoolean())
            docs.AddBlog(new MdxBlogCollection("news", Path.Combine(destination, "blog"), "blog")
            {
                Authors = converted.Manifest.BlogAuthors.ToDictionary(author => author.Id, author => new BlogAuthor(author.Name)),
            });
        var output = Path.Combine(workspace.Root, "out");
        var generation = await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/mig/" }, [], output, clean: true,
            new() { Template = new DocsSiteTemplate() },
            new() { Extensions = [docs], BuildTimestamp = BuildTimestamp }, CancellationToken.None);
        await Assert.That(generation.BuildReport.Diagnostics).IsEmpty();

        foreach (var artifact in expected.RootElement.GetProperty("artifacts").EnumerateArray().Select(element => element.GetString()!))
            await Assert.That(File.Exists(Path.Combine(output, artifact))).IsTrue();
        // Generated routes (not just migration-predicted routes) must cover
        // every expected route: the site actually publishes them.
        var publicPaths = generation.Routes.Select(route => route.PublicPath).ToHashSet(StringComparer.Ordinal);
        foreach (var expectedRoute in expectedRoutes)
            await Assert.That(publicPaths.Contains(expectedRoute)).IsTrue();
        foreach (var page in expected.RootElement.GetProperty("dom").EnumerateObject())
        {
            var html = await File.ReadAllTextAsync(Path.Combine(output, page.Name));
            foreach (var marker in page.Value.GetProperty("contains").EnumerateArray().Select(element => element.GetString()!))
                await Assert.That(html.Contains(marker)).IsTrue();
            foreach (var marker in page.Value.GetProperty("notContains").EnumerateArray().Select(element => element.GetString()!))
                await Assert.That(html.Contains(marker)).IsFalse();
        }

        if (expected.RootElement.TryGetProperty("assetGlob", out var glob))
        {
            var pattern = glob.GetString()!;
            var prefix = pattern[..pattern.IndexOf('*')];
            var suffix = pattern[(pattern.LastIndexOf('*') + 1)..];
            await Assert.That(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .Any(path => path.Replace('\\', '/').Contains(prefix) && path.EndsWith(suffix, StringComparison.Ordinal))).IsTrue();
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }

    private static IReadOnlyList<string> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path) + "=" + Convert.ToHexString(File.ReadAllBytes(path)))
            .Order(StringComparer.Ordinal).ToArray();
}
