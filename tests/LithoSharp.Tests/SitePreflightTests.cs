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
public sealed class SitePreflightTests
{
    [Test]
    public async Task SourceErrorStopsBeforeFactoryConstructionAndRetainsPublishedBytes()
    {
        using var workspace = new TemporaryWorkspace();
        var sentinel = Path.Combine(workspace.Root, "published.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var counters = new Counters();
        var source = SitePreflight.InspectMarkdown("broken.md", "---\n---\n# broken");
        await Assert.That(source.Succeeded).IsFalse();
        await Assert.That(async () => await new SiteGenerator().PreflightFactoryAsync(
            token => new CountingFactory(counters, Definition(workspace.Root)).CreateAsync(new(workspace.Root), token), source))
            .Throws<SiteQualityValidationException>();
        await Assert.That(counters.Constructors + counters.Factories + counters.Renders + counters.Routes).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");
        await Assert.That(Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories).Length).IsEqualTo(1);
    }

    [Test]
    public async Task ActualCatalogCollisionCallsFactoryOnceAndEachRouteOnceWithoutRenderOrPrepare()
    {
        using var workspace = new TemporaryWorkspace();
        var counters = new Counters();
        var extension = new UninspectedExtension(counters);
        var definition = Definition(workspace.Root, counters, collision: true, extensions: [extension]);
        var warning = new SiteDiagnostic("LSA1201", SiteDiagnosticSeverity.Warning, "Explicitly downgraded compiler diagnostic");
        var report = await new SiteGenerator().PreflightFactoryAsync(
            token => new CountingFactory(counters, definition).CreateAsync(new(workspace.Root), token), SitePreflight.FromDiagnostics([warning]));
        await Assert.That(report.Mode).IsEqualTo("trusted-catalog");
        await Assert.That(report.Stage).IsEqualTo("catalog");
        await Assert.That(report.Succeeded).IsFalse();
        await Assert.That(report.Diagnostics.Any(d => d.Id == "LSA1201" && d.Severity == SiteDiagnosticSeverity.Warning)).IsTrue();
        await Assert.That(counters.Constructors).IsEqualTo(1);
        await Assert.That(counters.Factories).IsEqualTo(1);
        await Assert.That(counters.Routes).IsEqualTo(2);
        await Assert.That(counters.Renders + counters.Prepares).IsEqualTo(0);
        await Assert.That(Directory.GetFileSystemEntries(workspace.Root).Length).IsEqualTo(0);
    }

    [Test]
    public async Task UnknownExtensionStaysDeferredAndCancellationFailureNeverCallsRenderer()
    {
        using var workspace = new TemporaryWorkspace();
        var counters = new Counters();
        var definition = Definition(workspace.Root, counters, extensions: [new UninspectedExtension(counters)]);
        var report = await new SiteGenerator().PreflightCatalogAsync(definition);
        await Assert.That(report.Succeeded).IsTrue();
        await Assert.That(report.DeferredReasons.Any(r => r.Contains("no nonrendering catalog inspector"))).IsTrue();
        await Assert.That(counters.Prepares + counters.Renders).IsEqualTo(0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await new SiteGenerator().PreflightFactoryAsync(
            token => new CountingFactory(counters, definition).CreateAsync(new(workspace.Root), token), SitePreflight.FromDiagnostics([]), cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(counters.Factories).IsEqualTo(0);
        counters.Routes = 0;
        using var afterFactory = new CancellationTokenSource();
        await Assert.That(async () => await new SiteGenerator().PreflightFactoryAsync(token =>
        {
            counters.Factories++; afterFactory.Cancel(); return Task.FromResult(definition);
        }, SitePreflight.FromDiagnostics([]), afterFactory.Token)).Throws<OperationCanceledException>();
        await Assert.That(counters.Routes + counters.Renders).IsEqualTo(0);
        await Assert.That(async () => await new SiteGenerator().PreflightFactoryAsync(_ => throw new IOException("factory failed"),
            SitePreflight.FromDiagnostics([]))).Throws<IOException>();
        await Assert.That(counters.Routes + counters.Renders).IsEqualTo(0);
    }

    [Test]
    public async Task ExtensionErrorInNormalGenerationStopsBeforeRendererAndPublication()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "published.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var counters = new Counters();
        var definition = Definition(workspace.Root, counters, extensions: [new ErrorExtension(counters)]);
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(definition.Site, definition.Posts,
            output, true, definition.Customization, definition.Options, CancellationToken.None)).Throws<SiteQualityValidationException>();
        await Assert.That(counters.Prepares).IsEqualTo(1);
        await Assert.That(counters.Routes + counters.Renders).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");
        await Assert.That(Directory.GetFiles(output).Length).IsEqualTo(1);
    }

    [Test]
    public async Task MdxActualLoaderAndSyntaxWorkerDoNotExecuteImportedModulesPluginsOrRender()
    {
        using var workspace = new TemporaryWorkspace();
        var marker = Path.Combine(workspace.Root, "executed.txt");
        var escaped = JsonSerializer.Serialize(marker);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "evil.mjs"), $"import fs from 'node:fs'; fs.writeFileSync({escaped}, 'module'); export default function Evil() {{ throw new Error('SSR'); }}");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "plugin.mjs"), $"import fs from 'node:fs'; fs.writeFileSync({escaped}, 'plugin'); export default function() {{ throw new Error('plugin'); }}");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "lithosharp.config.mjs"), $"import fs from 'node:fs'; fs.writeFileSync({escaped}, 'config'); throw new Error('config');");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "one.mdx"), "---\ntitle: One\n---\nimport Evil from './evil.mjs'\n\n# Hello\n\n<Evil />");
        var counters = new Counters();
        var options = new MdxOptions(workspace.Root, WorkerDirectory())
        { Plugins = [new MdxPlugin("remark", "./plugin.mjs", JsonSerializer.SerializeToElement(new { }))] };
        await using var mdx = new MdxSite(options);
        mdx.AddCollection(new MdxContentCollectionLoader<Front>(new("mdx"), workspace.Root,
            _ => { counters.Routes++; return SiteRoute.ForDirectoryIndex("mdx/"); }, entry => new PageMetadata(entry.FrontMatter.Title)),
            renderer: (_, _) => { counters.Renders++; File.WriteAllText(marker, "renderer"); return "rendered"; });
        var report = await new SiteGenerator().PreflightCatalogAsync(Definition(workspace.Root, extensions: [mdx]));
        await Assert.That(report.Succeeded).IsTrue();
        await Assert.That(counters.Routes).IsEqualTo(1);
        await Assert.That(counters.Renders).IsEqualTo(0);
        await Assert.That(mdx.Metrics.RenderedPages + mdx.Metrics.BundledPages + mdx.Metrics.WorkerStarts).IsEqualTo(0);
        await Assert.That(File.Exists(marker)).IsFalse();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "one.mdx"), "---\ntitle: One\n---\n<Unclosed>");
        var broken = await new SiteGenerator().PreflightCatalogAsync(Definition(workspace.Root, extensions: [mdx]));
        await Assert.That(broken.Succeeded).IsFalse();
        await Assert.That(broken.Diagnostics.Any(d => d.Id == "LSMDX001" && d.Severity == SiteDiagnosticSeverity.Error)).IsTrue();
        await Assert.That(counters.Renders).IsEqualTo(0);
        await Assert.That(File.Exists(marker)).IsFalse();
    }
    private static SiteDefinition Definition(string root, Counters? counters = null, bool collision = false, IReadOnlyList<ISiteBuildExtension>? extensions = null)
    {
        var registrations = new List<SiteContentCollection>();
        if (counters is not null)
        {
            var entries = new[] { "one", "two" }.Select(id => new ContentEntry<Front, string>(new(id), id + ".md", "source:" + id, new() { Title = id }, "body")).ToArray();
            var collection = new ContentCollection<Front, string>(new("pages"), root, entries,
                entry => { counters.Routes++; return SiteRoute.ForDirectoryIndex(collision ? "same/" : entry.Id.Value + "/"); },
                entry => new PageMetadata(entry.FrontMatter.Title));
            registrations.Add(new SiteContentCollection<Front, string>(collection, (_, _) => { counters.Renders++; return "rendered"; }));
        }
        return new(new SiteSettings(), []) { OutputDirectory = Path.Combine(root, "output"),
            Options = new() { ContentCollections = registrations, Extensions = extensions ?? [] } };
    }
    private static string WorkerDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LithoSharp.slnx"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository worker was not found."), "src", "LithoSharp.Mdx", "worker");
    }
    public sealed class Front { public string Title { get; set; } = ""; }
    private sealed class Counters { public int Constructors; public int Factories; public int Routes; public int Renders; public int Prepares; }
    private sealed class CountingFactory : ISiteFactory
    {
        private readonly Counters counters; private readonly SiteDefinition definition;
        public CountingFactory(Counters counters, SiteDefinition definition) { this.counters = counters; this.definition = definition; counters.Constructors++; }
        public Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default)
        { counters.Factories++; return Task.FromResult(definition); }
    }
    private sealed class UninspectedExtension(Counters counters) : ISiteBuildExtension
    {
        public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
        { counters.Prepares++; throw new InvalidOperationException("Preflight must not call PrepareAsync."); }
    }
    private sealed class ErrorExtension(Counters counters) : ISiteBuildExtension
    {
        public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
        {
            counters.Prepares++;
            return Task.FromResult(new SiteBuildContribution { Diagnostics = [new("LSC7001", SiteDiagnosticSeverity.Error, "Extension reported a definite failure")] });
        }
    }
}
