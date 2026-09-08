using System.Security.Cryptography;
using System.Text;
using LithoSharp.Build;
using LithoSharp.Configuration;

namespace LithoSharp.Tests;

public sealed class AssetTransformTests
{
    [Test]
    public async Task TransformCacheInvalidatesForSourceAndImplementationAndRecoversFromCorruption()
    {
        using var workspace = new TemporaryWorkspace();
        var sourcePath = Path.Combine(workspace.Root, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "one");
        var source = new SiteAsset("source", workspace.Root, "source.txt", "assets/source.txt");
        var output = new SiteAssetOutput("upper", "assets/upper.txt");
        var invocations = 0;
        SiteAssetTransform Transform(string fingerprint) => new(
            "upper", fingerprint, [source], [output], async (context, cancellationToken) =>
            {
                invocations++;
                using var reader = new StreamReader(context.OpenRead(source));
                await context.WriteAsync(output,
                    Encoding.UTF8.GetBytes((await reader.ReadToEndAsync(cancellationToken)).ToUpperInvariant()),
                    cancellationToken);
            });

        var transform = Transform("upper/v1");
        await Generate(workspace, [source], [transform]);
        await Generate(workspace, [source], [transform], clean: false);
        await Assert.That(invocations).IsEqualTo(1);

        await File.WriteAllTextAsync(sourcePath, "two");
        await Generate(workspace, [source], [transform], clean: false);
        await Assert.That(invocations).IsEqualTo(2);

        transform = Transform("upper/v2");
        await Generate(workspace, [source], [transform], clean: false);
        await Assert.That(invocations).IsEqualTo(3);

        foreach (var cacheFile in Directory.EnumerateFiles(Cache(workspace), "*.json"))
            await File.WriteAllTextAsync(cacheFile, "{ corrupt");
        await Generate(workspace, [source], [transform], clean: false);
        await Assert.That(invocations).IsEqualTo(4);
    }

    [Test]
    public async Task GenerationPublishesPublicFilesAndResolvedTransformUrlsWithIntegrityAndOwnership()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "app.js"), "console.log('ok');");
        var publicDirectory = Public(workspace);
        Directory.CreateDirectory(Path.Combine(publicDirectory, "manual"));
        await File.WriteAllTextAsync(Path.Combine(publicDirectory, "manual", "robots-extra.txt"), "public");
        var source = new SiteAsset("app", workspace.Root, "app.js", "assets/app.js");
        var css = new SiteAssetOutput("css", "assets/site.css");
        var transform = new SiteAssetTransform("css", "css/v1", [source], [css],
            (context, cancellationToken) => context.WriteAsync(css,
                Encoding.UTF8.GetBytes($"/* {context.GetUrl(source).Value} */"), cancellationToken));
        AssetUrl? sourceUrl = null;
        AssetUrl? cssUrl = null;

        var result = await Generate(workspace, [source], [transform], template: new CallbackTemplate(context =>
        {
            sourceUrl = context.Assets.GetUrl(source);
            cssUrl = context.Assets.GetUrl(css);
            return $"<a href=\"{sourceUrl.ToAttributeValue().ToHtmlString()}\"></a>";
        }));

        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "manual", "robots-extra.txt")))
            .IsEqualTo("public");
        await Assert.That(sourceUrl!.Value).StartsWith("/sub/assets/app.");
        await Assert.That(cssUrl!.Value).StartsWith("/sub/assets/site.");
        var sourceBytes = Encoding.UTF8.GetBytes("console.log('ok');");
        await Assert.That(sourceUrl.Integrity)
            .IsEqualTo("sha256-" + Convert.ToBase64String(SHA256.HashData(sourceBytes)));
        await Assert.That(cssUrl.Integrity).StartsWith("sha256-");

        var nodeIds = result.BuildPlan.Nodes.Select(node => node.Id.Value).ToArray();
        await Assert.That(nodeIds).Contains("asset:app");
        await Assert.That(nodeIds).Contains("asset-transform:css");
        var cssArtifact = result.BuildPlan.Nodes.Single(node => node.Id.Value == "asset-transform:css").Artifacts.Single();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), cssArtifact.RelativeOutputPath)))
            .Contains(sourceUrl.Value);
    }

    [Test]
    public async Task InvalidTransformDeclarationsPreserveCommittedOutput()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "source.txt"), "source");
        var source = new SiteAsset("source", workspace.Root, "source.txt", "assets/source.txt");
        var undeclared = new SiteAsset("other", workspace.Root, "source.txt", "assets/other.txt");
        var output = new SiteAssetOutput("result", "assets/result.txt");
        Directory.CreateDirectory(Output(workspace));
        var sentinel = Path.Combine(Output(workspace), "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");

        var missing = new SiteAssetTransform("missing", "v1", [source], [output], (_, _) => Task.CompletedTask);
        await Assert.That(async () => await Generate(workspace, [source], [missing])).Throws<InvalidOperationException>();
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");

        var duplicate = new SiteAssetTransform("duplicate", "v1", [source], [output], async (context, cancellationToken) =>
        {
            await context.WriteAsync(output, "first"u8.ToArray(), cancellationToken);
            await context.WriteAsync(output, "second"u8.ToArray(), cancellationToken);
        });
        await Assert.That(async () => await Generate(workspace, [source], [duplicate])).Throws<InvalidOperationException>();
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");

        var badInput = new SiteAssetTransform("input", "v1", [source], [output], (context, _) =>
        {
            using var ignored = context.OpenRead(undeclared);
            return Task.CompletedTask;
        });
        await Assert.That(async () => await Generate(workspace, [source], [badInput])).Throws<ArgumentException>();
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");

        await Assert.That(() => new SiteAssetTransform("outputs", "v1", [source], [output, output], (_, _) => Task.CompletedTask)).Throws<ArgumentException>();
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");
    }

    [Test]
    public async Task PreflightRunsOnCacheHitAndOutputReferencesRequireDeclarationIdentity()
    {
        using var workspace = new TemporaryWorkspace();
        var output = new SiteAssetOutput("value", "value.txt");
        var validates = 0;
        var executions = 0;
        var transform = new SiteAssetTransform("value", "v1", [], [output], async (context, token) =>
        {
            executions++;
            await context.WriteAsync(output, "value"u8.ToArray(), token);
        }, _ => { validates++; return Task.CompletedTask; });
        await Generate(workspace, [], [transform]);
        await Generate(workspace, [], [transform], clean: false, template: new CallbackTemplate(context =>
        {
            var alias = new SiteAssetOutput(output.Id, output.RelativeOutputPath);
            try { context.Assets.GetUrl(alias); throw new Exception("An undeclared alias was accepted."); }
            catch (AssetRegistryException) { }
            try { context.Assets.OpenRead(alias); throw new Exception("An undeclared alias was read."); }
            catch (AssetRegistryException) { }
            return "ok";
        }));
        await Assert.That(validates).IsEqualTo(2);
        await Assert.That(executions).IsEqualTo(1);
        foreach (var cacheFile in Directory.EnumerateFiles(Cache(workspace), "*.json"))
            await File.WriteAllTextAsync(cacheFile, "{\"Version\":1,\"Outputs\":null}");
        await Generate(workspace, [], [transform]);
        await Assert.That(executions).IsEqualTo(2);
    }

    [Test]
    public async Task PublicAndCacheDirectoryLinksAreRejectedWithoutChangingOutsideFiles()
    {
        using var workspace = new TemporaryWorkspace();
        var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        Directory.CreateDirectory(Public(workspace));
        var link = Path.Combine(Public(workspace), "linked");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
        try
        {
            await Assert.That(async () => await Generate(workspace, [], [])).Throws<IOException>();
            await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");
        }
        finally { Directory.Delete(link); }
        Directory.CreateSymbolicLink(Cache(workspace), outside);
        var output = new SiteAssetOutput("value", "value.txt");
        var transform = new SiteAssetTransform("value", "v1", [], [output],
            (context, token) => context.WriteAsync(output, "value"u8.ToArray(), token));
        try
        {
            await Assert.That(async () => await Generate(workspace, [], [transform])).Throws<InvalidOperationException>();
            await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");
            await Assert.That(Directory.GetFiles(outside).Length).IsEqualTo(1);
        }
        finally { Directory.Delete(Cache(workspace)); }
    }

    [Test]
    public async Task OverlappingPublicOutputAndCacheDirectoriesAreRejectedBeforeReadingInputs()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(Output(workspace));
        var sentinel = Path.Combine(Output(workspace), "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        foreach (var publicDirectory in new[] { Output(workspace), Path.Combine(Output(workspace), "public"), workspace.Root })
        {
            await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(
                new SiteSettings(), [], Output(workspace), true, null,
                new SiteGenerationOptions { PublicDirectory = publicDirectory }, default)).Throws<ArgumentException>();
            await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");
        }
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings(), [], Output(workspace), true, null,
            new SiteGenerationOptions { Assets = [new SiteAsset("input", Output(workspace), "keep.txt", "copied.txt")] }, default))
            .Throws<ArgumentException>();
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings(), [], Output(workspace), true, null,
            new SiteGenerationOptions { PublicDirectory = Public(workspace), AssetCacheDirectory = Path.Combine(Public(workspace), "cache") }, default))
            .Throws<ArgumentException>();
    }

    private static Task<SiteGenerationResult> Generate(
        TemporaryWorkspace workspace,
        IReadOnlyList<SiteAsset> assets,
        IReadOnlyList<SiteAssetTransform> transforms,
        bool clean = true,
        ISiteTemplate? template = null)
    {
        Directory.CreateDirectory(Public(workspace));
        return new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/sub/" }, [], Output(workspace), clean,
            new SiteCustomization { GenerateLlmsTxt = false, Template = template ?? new CallbackTemplate(_ => "ok") },
            new SiteGenerationOptions
            {
                Assets = assets,
                AssetTransforms = transforms,
                PublicDirectory = Public(workspace),
                AssetCacheDirectory = Cache(workspace)
            }, default);
    }

    private static string Output(TemporaryWorkspace workspace) => Path.Combine(workspace.Root, "output");
    private static string Public(TemporaryWorkspace workspace) => Path.Combine(workspace.Root, "public");
    private static string Cache(TemporaryWorkspace workspace) => Path.Combine(workspace.Root, "cache");

    private sealed class CallbackTemplate(Func<SiteTemplateContext, string> render) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult([new SiteTemplateFile { RelativePath = "index.html", Content = render(context) }]));
    }
}
