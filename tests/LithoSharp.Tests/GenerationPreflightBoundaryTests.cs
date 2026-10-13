using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Inspection;
using LithoSharp.Mdx;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

[NotInParallel]
public sealed class GenerationPreflightBoundaryTests
{
    [Test]
    public async Task KnownInspectorErrorStopsEveryPrepareEvenWhenUnknownExtensionComesFirst()
    {
        using var workspace = new TemporaryWorkspace();
        var output = await SeedOutput(workspace.Root);
        var before = Snapshot(output);
        var counters = new Counters();
        var unknown = new UnknownPrepare(counters);
        var inspector = new ErrorInspector(counters);
        var failure = await Failure<SiteQualityValidationException>(() => new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings(), [], output, true, new() { Template = new CountingTemplate(counters) },
            new() { Extensions = [unknown, inspector], BuildTimestamp = DateTimeOffset.UnixEpoch }, default));
        await Assert.That(failure.Diagnostics.Any(d => d.Id == "LSC7001" && d.Severity == SiteDiagnosticSeverity.Error)).IsTrue();
        await Assert.That(counters.Inspections).IsEqualTo(1);
        await Assert.That(counters.Prepares + counters.Renders).IsEqualTo(0);
        await Assert.That(Snapshot(output)).IsEqualTo(before);
    }

    [Test]
    public async Task RealMdxAndCoreRouteCollisionStopsBeforeModulesPluginsSsrAndRendering()
    {
        using var workspace = new TemporaryWorkspace();
        var source = await CanarySource(workspace.Root, "content", "---\ntitle: Collision\n---\n");
        var output = await SeedOutput(workspace.Root);
        var before = Snapshot(output);
        var counters = new Counters();
        await using var mdx = new MdxSite(CanaryOptions(workspace.Root));
        AddMdx(mdx, source, "collision", counters);
        var failure = await Failure<SiteRouteValidationException>(() => new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings(), [], output, true, null,
            new() { ContentCollections = [CoreCollection(workspace.Root, "collision", counters)],
                Extensions = [new UnknownPrepare(counters), mdx], BuildTimestamp = DateTimeOffset.UnixEpoch }, default));
        await Assert.That(failure.Diagnostics.Any(d => d.Id == SiteRouteDiagnosticIds.DuplicatePublicPath)).IsTrue();
        await Assert.That(counters.Prepares + counters.Renders).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(workspace.Root, "executed.txt"))).IsFalse();
        await AssertNoMdxWork(mdx.Metrics);
        await Assert.That(Snapshot(output)).IsEqualTo(before);
    }

    [Test]
    public async Task PublicInspectorEditingRawMdxAfterInspectionInvalidatesPreparationBeforeWorkerStart()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        var file = Path.Combine(source, "hello.mdx");
        await File.WriteAllTextAsync(file, "---\ntitle: Hello\n---\n# Original\n");
        var output = await SeedOutput(workspace.Root);
        var before = Snapshot(output);
        var counters = new Counters();
        await using var mdx = new MdxSite(new(workspace.Root, WorkerDirectory()));
        AddMdx(mdx, source, "hello", counters);
        var mutation = new MutatingInspector(file, counters);
        var failure = await Failure<SiteBuildExtensionException>(() => new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings(), [], output, true, null,
            new() { Extensions = [new UnknownPrepare(counters), mdx, mutation], BuildTimestamp = DateTimeOffset.UnixEpoch }, default));
        await Assert.That(failure.Diagnostics.Any(d => d.Id == "LSMDX005" && d.Severity == SiteDiagnosticSeverity.Error)).IsTrue();
        await Assert.That(counters.Inspections).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(file)).Contains("Changed raw bytes");
        await Assert.That(counters.Prepares).IsEqualTo(0);
        await Assert.That(counters.Renders).IsEqualTo(0);
        await AssertNoMdxWork(mdx.Metrics);
        await Assert.That(Snapshot(output)).IsEqualTo(before);
    }

    [Test]
    public async Task RealMdxSuccessfulGenerationLoadsAndMapsOnceAndRetainsDeferredCatalogReport()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "hello.mdx"), "---\ntitle: Hello\n---\n# Real successful MDX\n");
        var counters = new Counters();
        await using var mdx = new MdxSite(new(workspace.Root, WorkerDirectory()));
        AddMdx(mdx, source, "hello", counters);
        var legacy = new LegacyInspector(workspace.Root);
        var output = Path.Combine(workspace.Root, "output");
        var result = await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], output, true, null,
            new() { Extensions = [new UnknownPrepare(counters), mdx, legacy], BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
        await Assert.That(counters.Loads).IsEqualTo(1);
        await Assert.That(counters.Routes).IsEqualTo(1);
        await Assert.That(counters.Publications).IsEqualTo(1);
        await Assert.That(counters.Prepares).IsEqualTo(1);
        await Assert.That(counters.Renders).IsEqualTo(1);
        await Assert.That(mdx.Metrics.WorkerStarts).IsEqualTo(1);
        await Assert.That(mdx.Metrics.RenderedPages).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "hello", "index.html"))).Contains("Real successful MDX");
        await Assert.That(legacy.Inspections).IsEqualTo(1);
        await Assert.That(legacy.Prepares).IsEqualTo(1);
        await Assert.That(legacy.Renders).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "legacy", "index.html"))).Contains("Legacy compatible inspector");
        await Assert.That(result.GeneratedFiles.Any(path => path.Replace('\\', '/').EndsWith("hello/index.html", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Preflight!.Mode).IsEqualTo("trusted-catalog");
        await Assert.That(result.Preflight.Stage).IsEqualTo("catalog");
        await Assert.That(result.Preflight.Succeeded).IsTrue();
        await Assert.That(result.Preflight.DeferredReasons.Any(reason => reason.Contains(nameof(UnknownPrepare), StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.Preflight.DeferredReasons.Any(reason => reason.Contains("final HTML", StringComparison.OrdinalIgnoreCase))).IsTrue();
        await Assert.That(result.Preflight.DeferredReasons.Any(reason => reason.Contains(nameof(LegacyInspector), StringComparison.Ordinal)
            && reason.Contains("snapshot handoff", StringComparison.Ordinal))).IsTrue();
        await Assert.That(result.BuildReport.Diagnostics.Any(diagnostic => diagnostic.Message.Contains(nameof(LegacyInspector), StringComparison.Ordinal)
            && diagnostic.Message.Contains("snapshot handoff", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task RealBlogAuthorValidationStopsEveryPrepareBeforeImportedModuleAndSsr()
    {
        using var workspace = new TemporaryWorkspace();
        var source = await CanarySource(workspace.Root, "blog", "---\ntitle: Blog\ndate: 2020-01-01\nsummary: Valid summary\nauthors: [unknown]\n---\n");
        var output = await SeedOutput(workspace.Root);
        var before = Snapshot(output);
        var counters = new Counters();
        await using var blog = new MdxBlogSite(CanaryOptions(workspace.Root));
        blog.AddCollection(new("news", source, "news"));
        var failure = await Failure<ArgumentException>(() => new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings(), [], output, true, null,
            new() { ContentCollections = [CoreCollection(workspace.Root, "core", counters)],
                Extensions = [new UnknownPrepare(counters), blog], BuildTimestamp = DateTimeOffset.Parse("2021-01-01T00:00:00Z") }, default));
        await Assert.That(failure.Message).Contains("known author IDs");
        await Assert.That(counters.Prepares + counters.Renders).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(workspace.Root, "executed.txt"))).IsFalse();
        await Assert.That(Snapshot(output)).IsEqualTo(before);
    }

    [Test]
    public async Task RealDocumentationMissingTranslationStopsEveryPrepareBeforeImportedModuleAndSsr()
    {
        using var workspace = new TemporaryWorkspace();
        var english = await CanarySource(workspace.Root, "en", "---\ntitle: Introduction\n---\n");
        var japanese = Path.Combine(workspace.Root, "ja");
        Directory.CreateDirectory(japanese);
        var output = await SeedOutput(workspace.Root);
        var before = Snapshot(output);
        var counters = new Counters();
        await using var docs = new DocumentationSite(CanaryOptions(workspace.Root));
        docs.AddCollection(new("guide", [new("current", "en", english, "guide/en"), new("current", "ja", japanese, "guide/ja")])
            { UseMdx = true, SourceLocale = "en", MissingDocuments = MissingDocumentPolicy.Error });
        var failure = await Failure<SiteBuildExtensionException>(() => new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings(), [], output, true, null,
            new() { ContentCollections = [CoreCollection(workspace.Root, "core", counters)],
                Extensions = [new UnknownPrepare(counters), docs], BuildTimestamp = DateTimeOffset.UnixEpoch }, default));
        await Assert.That(failure.Diagnostics.Any(d => d.Id == "LSDOC001" && d.Severity == SiteDiagnosticSeverity.Error)).IsTrue();
        await Assert.That(counters.Prepares + counters.Renders).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(workspace.Root, "executed.txt"))).IsFalse();
        await AssertNoMdxWork(docs.MdxMetrics);
        await Assert.That(Snapshot(output)).IsEqualTo(before);
    }

    public sealed class Front { public string Title { get; set; } = ""; }
    private sealed class Counters { public int Loads; public int Routes; public int Publications; public int Renders; public int Prepares; public int Inspections; }

    private static void AddMdx(MdxSite mdx, string source, string route, Counters counters)
    {
        var real = new MdxContentCollectionLoader<Front>(new("mdx"), source,
            _ => { counters.Routes++; return SiteRoute.ForDirectoryIndex(route); },
            entry => { counters.Publications++; return new(entry.FrontMatter.Title); });
        mdx.AddCollection(new CountingLoader(real, counters), renderer: (entry, rendering) => {
            counters.Renders++;
            return rendering.RenderDocument(entry.Body.ToHtmlString());
        });
    }

    private sealed class CountingLoader(IContentCollectionLoader<Front, MdxDocument> real, Counters counters) : IContentCollectionLoader<Front, MdxDocument>
    {
        public async ValueTask<ContentLoadResult<Front, MdxDocument>> LoadAsync(CancellationToken cancellationToken = default)
        { counters.Loads++; return await real.LoadAsync(cancellationToken); }
    }

    private static SiteContentCollection CoreCollection(string root, string route, Counters counters)
    {
        var collection = new ContentCollection<Front, string>(new("core"), root,
            [new ContentEntry<Front, string>(new("one"), "one.md", "core-one", new() { Title = "Core" }, "Core")],
            _ => SiteRoute.ForDirectoryIndex(route), entry => new PageMetadata(entry.FrontMatter.Title));
        return new SiteContentCollection<Front, string>(collection, (_, rendering) => {
            counters.Renders++;
            return rendering.RenderDocument("Core");
        });
    }

    private sealed class UnknownPrepare(Counters counters) : ISiteBuildExtension
    {
        public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
        { counters.Prepares++; return Task.FromResult(new SiteBuildContribution()); }
    }

    private sealed class LegacyInspector(string root) : ISiteCatalogPreflightExtension
    {
        public int Inspections { get; private set; }
        public int Prepares { get; private set; }
        public int Renders { get; private set; }
        public Task<SiteCatalogPreflightContribution> InspectCatalogAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
        {
            Inspections++;
            return Task.FromResult(new SiteCatalogPreflightContribution(
                [new(SiteRoute.ForDirectoryIndex("legacy", context.Site.BaseUrl), "catalog-one")], []));
        }
        public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
        {
            Prepares++;
            var collection = new ContentCollection<Front, string>(new("legacy"), root,
                [new ContentEntry<Front, string>(new("one"), "legacy.md", "legacy-one", new() { Title = "Legacy" }, "Legacy compatible inspector")],
                _ => SiteRoute.ForDirectoryIndex("legacy"), entry => new PageMetadata(entry.FrontMatter.Title));
            return Task.FromResult(new SiteBuildContribution { ContentCollections = [new SiteContentCollection<Front, string>(collection,
                (entry, rendering) => { Renders++; return rendering.RenderDocument(entry.Body); })] });
        }
    }

    private sealed class ErrorInspector(Counters counters) : ISiteCatalogPreflightExtension
    {
        public Task<SiteCatalogPreflightContribution> InspectCatalogAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
        {
            counters.Inspections++;
            return Task.FromResult(new SiteCatalogPreflightContribution([], [new("LSC7001", SiteDiagnosticSeverity.Error, "Known catalog error")]));
        }
        public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
        { counters.Prepares++; return Task.FromResult(new SiteBuildContribution()); }
    }

    private sealed class MutatingInspector(string file, Counters counters) : ISiteCatalogPreflightExtension
    {
        public async Task<SiteCatalogPreflightContribution> InspectCatalogAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
        {
            counters.Inspections++;
            var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
            await File.WriteAllBytesAsync(file, bytes.Concat(Encoding.UTF8.GetBytes("\nChanged raw bytes\n")).ToArray(), cancellationToken);
            return new([], []);
        }
        public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
        { counters.Prepares++; return Task.FromResult(new SiteBuildContribution()); }
    }

    private sealed class CountingTemplate(Counters counters) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default)
        { counters.Renders++; return Task.FromResult(new SiteTemplateResult([])); }
    }

    private static async Task<string> CanarySource(string root, string name, string frontMatter)
    {
        var source = Path.Combine(root, name);
        Directory.CreateDirectory(source);
        var marker = JsonSerializer.Serialize(Path.Combine(root, "executed.txt"));
        await File.WriteAllTextAsync(Path.Combine(source, "evil.mjs"),
            $"import fs from 'node:fs'; fs.writeFileSync({marker}, 'module'); export default function Evil() {{ throw new Error('SSR executed'); }}");
        await File.WriteAllTextAsync(Path.Combine(root, "plugin.mjs"),
            $"import fs from 'node:fs'; fs.writeFileSync({marker}, 'plugin'); export default function plugin() {{ throw new Error('Plugin executed'); }}");
        await File.WriteAllTextAsync(Path.Combine(source, "hello.mdx"), frontMatter + "import Evil from './evil.mjs'\n\n# Hello\n\n<Evil />\n");
        return source;
    }

    private static MdxOptions CanaryOptions(string root) => new(root, WorkerDirectory())
    { Plugins = [new("remark", "./plugin.mjs", JsonSerializer.SerializeToElement(new { }))] };

    private static async Task<string> SeedOutput(string root)
    {
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Combine(output, "sentinel.bin"), [0, 255, 42, 13, 10]);
        return output;
    }
    private sealed record OutputSnapshot(string SentinelHash, int FileCount);
    private static OutputSnapshot Snapshot(string output) => new(
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, "sentinel.bin")))),
        Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Count());

    private static async Task<T> Failure<T>(Func<Task<SiteGenerationResult>> generate) where T : Exception
    {
        try { await generate(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Generation succeeded despite an expected " + typeof(T).Name);
    }
    private static async Task AssertNoMdxWork(MdxBuildMetrics metrics) =>
        await Assert.That(metrics.WorkerStarts + metrics.CompiledModules + metrics.RenderedPages + metrics.BundledPages).IsEqualTo(0);

    private static string WorkerDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LithoSharp.slnx"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository worker was not found."), "src", "LithoSharp.Mdx", "worker");
    }
}
