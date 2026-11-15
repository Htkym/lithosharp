using LithoSharp.AgentReady;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Mdx;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

[NotInParallel("MdxToolchain")]
public sealed class AgentReadyMdxIntegrationTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments("auto", "Provided")]
    [Arguments("empty", "Provided")]
    [Arguments("unsupported", "Unsupported")]
    [Arguments("mismatch", "Unavailable")]
    [Arguments("custom", "Unsupported")]
    [Arguments("explicit-null", "Unsupported")]
    [Arguments("off", "Unsupported")]
    public async Task AutomaticBridgeUsesVerifiedOutcomeOnlyAndRespectsExplicitOwnership(string mode, string expected)
    {
        using var workspace = new Workspace();
        var worker = await MdxMarkdownProviderTests.WriteWorkerAsync(workspace.Root);
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), "---\ntitle: Title\n---\n" + mode + "\n");
        await using var site = new MdxSite(new(workspace.Root, worker) { Cacheable = true });
        var loader = Loader(source);
        if (mode == "explicit-null")
            site.AddCollectionWithMarkdown(loader, static (_, _) => null, "explicit:v1");
        else
            site.AddCollection(loader, renderer: mode == "custom" ? static (entry, context) => context.RenderDocument(entry.Body.ToHtmlString()) : null);
        var contribution = await site.PrepareAsync(Context(workspace.Root, mode != "off"));
        var registration = (SiteContentCollection<MdxIntegrationTests.FrontMatter, MdxDocument>)contribution.ContentCollections.Single();
        var entry = registration.Collection.Entries.Single();
        await Assert.That(entry.Body.HasOriginalBodySnapshot).IsTrue();
        await Assert.That(entry.Body.PublicMarkdown is null).IsEqualTo(mode == "off");
        var page = GeneratedMarkdownProviderTests.Page(registration);
        var result = page.GetPublicMarkdown(new SiteGenerator(), GeneratedMarkdownProviderTests.Configuration(page), "Production", AssetRegistry.Empty, default);
        await Assert.That(result.Status.ToString()).IsEqualTo(expected);
        if (mode == "empty")
            await Assert.That(result.Markdown).IsEqualTo("");
        if (mode == "auto")
        {
            await Assert.That(result.Markdown).IsEqualTo("auto\n");
            await Assert.That(page.RendererFingerprint).Contains(MdxPublicMarkdownCandidate.CurrentImplementationId);
        }
    }

    [Test]
    public async Task WorkerCacheSeparatesOffOnAndOffAgain()
    {
        using var workspace = new Workspace();
        var worker = await MdxMarkdownProviderTests.WriteWorkerAsync(workspace.Root);
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), "---\ntitle: Title\n---\nBody\n");
        await using var site = new MdxSite(new(workspace.Root, worker) { Cacheable = true });
        site.AddCollection(Loader(source));
        foreach (var enabled in new[] { false, true, false })
        {
            var contribution = await site.PrepareAsync(Context(workspace.Root, enabled));
            var registration = (SiteContentCollection<MdxIntegrationTests.FrontMatter, MdxDocument>)contribution.ContentCollections.Single();
            await Assert.That(registration.Collection.Entries.Single().Body.PublicMarkdown is not null).IsEqualTo(enabled);
            await Assert.That(registration.MarkdownOutcomeProvider is not null).IsEqualTo(enabled);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PreparedCandidatePublishesThroughTheSamePipelineAndCachesTheArtifact(bool requiredFailure)
    {
        using var workspace = new Workspace();
        var worker = await MdxMarkdownProviderTests.WriteWorkerAsync(workspace.Root);
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "page.mdx"), "---\ntitle: Title\n---\n" + (requiredFailure ? "mismatch\n" : "[Self](page.mdx#part)\n\n## Part\nBody\n"));
        await using var site = new MdxSite(new(workspace.Root, worker) { Cacheable = true });
        site.AddCollection(new MdxContentCollectionLoader<MdxIntegrationTests.FrontMatter>(new("mdx"), source,
            static _ => SiteRoute.ForFile("page.html"), entry => new PageMetadata(entry.FrontMatter.Title)
            { AgentReady = new() { MarkdownMode = requiredFailure ? PublicMarkdownMode.Required : PublicMarkdownMode.Auto } })
        { TransformationFingerprint = "loader:v1" });
        var generator = new SiteGenerator();
        var output = Path.Combine(workspace.Root, "out");
        var settings = new SiteSettings { BaseUrl = "https://example.test/product/", TimeZone = "UTC" };
        var custom = new SiteCustomization { AgentReady = new() { Enabled = true }, GenerateLlmsTxt = true };
        var options = new SiteGenerationOptions { Extensions = [site], BuildTimestamp = Timestamp, MaxDegreeOfParallelism = 1 };
        if (requiredFailure)
        {
            await Assert.That(async () => await generator.GenerateWithOptionsAsync(settings, [], output, true, custom, options, default))
                .Throws<SiteQualityValidationException>();
            await Assert.That(File.Exists(Path.Combine(output, "page.md"))).IsFalse();
            return;
        }
        var first = await generator.GenerateWithOptionsAsync(settings, [], output, true, custom, options, default);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(output, "page.md"));
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "page.md"))).Contains("https://example.test/product/page.html#part");
        await Assert.That(first.AgentReadyPages.Single().Status).IsEqualTo(PublicMarkdownStatus.Automatic);
        var second = await generator.GenerateWithOptionsAsync(settings, [], output, false, custom, options, default);
        await Assert.That((await File.ReadAllBytesAsync(Path.Combine(output, "page.md"))).SequenceEqual(bytes)).IsTrue();
        await Assert.That(second.BuildReport.Nodes.Single(node => node.NodeId == "agent-ready:page:page.html").CacheHit).IsTrue();
    }

    private static MdxContentCollectionLoader<MdxIntegrationTests.FrontMatter> Loader(string source) => new(new("mdx"), source,
        static _ => SiteRoute.ForFile("page.html"), static entry => new PageMetadata(entry.FrontMatter.Title))
    { TransformationFingerprint = "loader:v1" };
    private static SiteBuildContext Context(string root, bool enabled) => new(new SiteSettings(), new SiteGenerationOptions(),
        Path.Combine(root, "out"), Path.Combine(root, "cache"), Timestamp)
    { AgentReadyEnabled = enabled };
    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : AppContext.BaseDirectory, "lithosharp-agent-mdx-" + Guid.NewGuid().ToString("N"));
        public Workspace() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
