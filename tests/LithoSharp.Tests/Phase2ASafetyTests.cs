using LithoSharp.Diagnostics;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class Phase2ASafetyTests
{
    [Test]
    public async Task HtmlValuesEncodeAndRawRequiresExplicitFactory()
    {
        await Assert.That(new HtmlText("<b>&").ToHtmlString()).IsEqualTo("&lt;b&gt;&amp;");
        await Assert.That(new HtmlAttributeValue("\" onclick=\"x").ToHtmlString()).IsEqualTo("&quot; onclick=&quot;x");
        await Assert.That(Html.UnsafeRaw("<b>x</b>").ToHtmlString()).IsEqualTo("<b>x</b>");
        await Assert.That(() => new HtmlAttributeValue("a\0b")).Throws<ArgumentException>();
    }

    [Test]
    public async Task SiteUrlRejectsUnsafeAbsoluteUrlsAndPreservesBasePath()
    {
        await Assert.That(SiteUrl.ForFile("assets/app.js", "https://example.test/docs/").Value)
            .IsEqualTo("/docs/assets/app.js");
        await Assert.That(() => SiteUrl.FromAbsolute("javascript:alert(1)")).Throws<UriFormatException>();
        await Assert.That(() => SiteUrl.FromAbsolute("https://example.test/a b")).Throws<UriFormatException>();
        await Assert.That(() => SiteUrl.ForFile("../secret.js")).Throws<ArgumentException>();
        await Assert.That(() => SiteUrl.ForFile("app.js", "https://example.test/../docs/")).Throws<UriFormatException>();
    }

    [Test]
    public async Task AssetRegistrySnapshotsBytesAndReportsUnregisteredAsset()
    {
        using var workspace = new TemporaryWorkspace();
        var root = workspace.Root;
        var source = Path.Combine(root, "app#1.js");
        await File.WriteAllTextAsync(source, "first");
        var asset = new SiteAsset("app", root, "app#1.js", "assets/app#1.js");
        var registry = await AssetRegistry.CreateAsync([asset], "https://example.test/docs/", CancellationToken.None);
        await File.WriteAllTextAsync(source, "second");

        await Assert.That(registry.Files.Single().Bytes).IsEquivalentTo(System.Text.Encoding.UTF8.GetBytes("first"));
        await Assert.That(registry.GetUrl(asset).Value).Contains("/docs/assets/app%231.");
        await Assert.That(registry.GetUrl(asset).Value).EndsWith(".js");
        var other = new SiteAsset("other", root, "app#1.js", "assets/other.js");
        var exception = await Assert.That(() => registry.GetUrl(other)).Throws<AssetRegistryException>();
        await Assert.That(exception!.Diagnostic.Id).IsEqualTo("LSA001");
        await Assert.That(exception.Diagnostic.Severity).IsEqualTo(SiteDiagnosticSeverity.Error);
    }

    [Test]
    [Arguments("100%.js", "100%25.")]
    [Arguments("%41.js", "%2541.")]
    [Arguments("a#b.js", "a%23b.")]
    public async Task AssetNamesPreserveLiteralFileCharacters(string name, string escapedStem)
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, name), "source");
        var asset = new SiteAsset("asset", workspace.Root, name, name);
        var registry = await AssetRegistry.CreateAsync([asset], null, CancellationToken.None);
        await Assert.That(registry.GetUrl(asset).Value).StartsWith("/" + escapedStem);
        await Assert.That(registry.Files.Single().RelativePath).StartsWith(Path.GetFileNameWithoutExtension(name) + ".");
    }

    [Test]
    public async Task GenerationSnapshotsAssetsAndInvalidatesBothRenderingContracts()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "app.js");
        await File.WriteAllTextAsync(source, "first");
        var asset = new SiteAsset("app", workspace.Root, "app.js", "assets/app.js");
        var collection = new ContentCollection<string, string>(
            new ContentCollectionId("typed"), workspace.Root,
            [new ContentEntry<string, string>(new ContentEntryId("one"), "page.md", "fixed", "Title", "Body")],
            _ => SiteRoute.ForFile("typed.html"), _ => new PageMetadata("Title"));
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = DateTimeOffset.UnixEpoch,
            Assets = [asset],
            ContentCollections = [new SiteContentCollection<string, string>(collection,
                (_, context) => context.Assets.GetUrl(asset).Value)]
        };
        var customization = new SiteCustomization
        {
            GenerateLlmsTxt = false,
            Template = new CallbackTemplate(context =>
            {
                File.WriteAllText(source, "second");
                return new SiteTemplateFile { RelativePath = "custom.html", Content = context.Assets.GetUrl(asset).Value };
            })
        };
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();
        var site = new SiteSettings { BaseUrl = "https://example.test/sub/" };
        var first = await generator.GenerateWithOptionsAsync(site, [], output, true, customization, options, default);
        var firstAsset = first.BuildPlan!.Nodes.Single(node => node.Id.Value == "asset:app");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, firstAsset.Artifacts.Single().RelativeOutputPath)))
            .IsEqualTo("first");
        foreach (var path in new[] { "custom.html", "typed.html" })
        {
            var owner = first.BuildPlan.Nodes.Single(node => node.Artifacts.Any(artifact => artifact.RelativeOutputPath == path));
            await Assert.That(owner.Dependencies.Contains(new BuildNodeId("asset:app"))).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, path))).StartsWith("/sub/assets/app.");
        }
        var second = await generator.GenerateWithOptionsAsync(site, [], output, false, customization,
            options with { PreviousBuildPlan = first.BuildPlan }, default);
        var invalidated = second.BuildPlan!.GetInvalidatedNodes(first.BuildPlan).Select(item => item.NodeId.Value).ToArray();
        foreach (var path in new[] { "custom.html", "typed.html" })
        {
            var owner = second.BuildPlan.Nodes.Single(node => node.Artifacts.Any(artifact => artifact.RelativeOutputPath == path));
            await Assert.That(invalidated.Contains(owner.Id.Value)).IsTrue();
        }
        await Assert.That(File.Exists(Path.Combine(output, firstAsset.Artifacts.Single().RelativeOutputPath))).IsFalse();
    }

    [Test]
    public async Task AssetCollisionAndCancellationPreserveExistingOutput()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "app.js"), "source");
        var asset = new SiteAsset("app", workspace.Root, "app.js", "assets/app.js");
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var options = new SiteGenerationOptions { Assets = [asset] };
        var customization = new SiteCustomization
        {
            GenerateLlmsTxt = false,
            Template = new CallbackTemplate(context => new SiteTemplateFile
            {
                RelativePath = context.Assets.GetUrl(asset).Route.RelativeOutputPath, Content = "conflict"
            })
        };
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings(), [], output, true, customization, options, default)).Throws<SiteRouteValidationException>();
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings(), [], output, true, customization, options, cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");
    }

    private sealed class CallbackTemplate(Func<SiteTemplateContext, SiteTemplateFile> render) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult([render(context)]));
    }
}
