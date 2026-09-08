using System.Text;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class BuildExtensionTests
{
    [Test]
    public async Task PreparedChunksUseTheCommonGraphCacheAndAtomicOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "site");
        var extension = new FixtureExtension(workspace.Root);
        var options = new SiteGenerationOptions { Extensions = [extension], BuildTimestamp = DateTimeOffset.UnixEpoch };
        var generator = new SiteGenerator();
        var first = await generator.GenerateWithOptionsAsync(new SiteSettings(), [], output, true, null, options, default);
        var second = await generator.GenerateWithOptionsAsync(new SiteSettings(), [], output, false, null,
            options with { PreviousBuildPlan = first.BuildPlan }, default);
        await Assert.That(extension.Timestamp).IsEqualTo(DateTimeOffset.UnixEpoch);
        await Assert.That(second.BuildReport.CacheMissCount).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "chunks/entry.js"))).IsEqualTo("import './shared.js';");
        var entryNode = second.BuildPlan!.Nodes.Single(node => node.Id.Value == "asset:entry");
        await Assert.That(entryNode.Dependencies.Select(id => id.Value)).Contains("asset:shared");
        var pageNode = second.BuildPlan.Nodes.Single(node => node.Id.Value.StartsWith("page:collection:", StringComparison.Ordinal));
        await Assert.That(pageNode.Dependencies.Select(id => id.Value)).Contains("asset:entry");
        var previous = await File.ReadAllBytesAsync(Path.Combine(output, "index.html"));
        extension.Conflict = true;
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(new SiteSettings(), [], output, false, null, options, default))
            .ThrowsException();
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(output, "index.html"))).IsEquivalentTo(previous);
    }

    [Test]
    public async Task PreparedAssetsSnapshotBytesAndRejectUnknownReferences()
    {
        using var workspace = new TemporaryWorkspace();
        var bytes = Encoding.UTF8.GetBytes("original");
        var asset = new SiteGeneratedAsset("entry", "bundle.js", bytes, referencedAssetIds: ["missing"]);
        bytes[0] = 0;
        using var reader = new StreamReader(asset.OpenRead());
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("original");
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [],
            Path.Combine(workspace.Root, "out"), true, null, new() { GeneratedAssets = [asset] }, default))
            .Throws<ArgumentException>();
    }

    private sealed class FixtureExtension(string root) : ISiteBuildExtension
    {
        public DateTimeOffset Timestamp { get; private set; }
        public bool Conflict { get; set; }
        public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Timestamp = context.BuildTimestamp;
            var collection = new ContentCollection<string, string>(new("extension"), root,
                [new(new("one"), "one.mdx", "source-v1", "One", "<h1>One</h1>")
                { DeclaredDependencies = [ContentDependency.FromAsset("entry")] }],
                _ => SiteRoute.ForDirectoryIndex("one"), entry => new PageMetadata(entry.FrontMatter),
                transformationId: new("fixture-v1"), isCacheable: true);
            return Task.FromResult(new SiteBuildContribution
            {
                ContentCollections = [new SiteContentCollection<string, string>(collection, (entry, rendering) => rendering.RenderDocument(entry.Body))
                { RendererFingerprint = "fixture-v1", IsThreadSafe = true }],
                Assets = [
                    new("shared", "chunks/shared.js", Encoding.UTF8.GetBytes("export const value=1;")),
                    new("entry", Conflict ? "index.html" : "chunks/entry.js", Encoding.UTF8.GetBytes("import './shared.js';"), referencedAssetIds: ["shared"])
                ]
            });
        }
    }
}
