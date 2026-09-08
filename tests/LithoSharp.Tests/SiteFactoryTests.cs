using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

[NotInParallel]
public sealed class SiteFactoryTests
{
    [Test]
    public async Task ResultRoutes_PreserveFileAndDirectoryIndexesUnderBasePath()
    {
        using var workspace = new TemporaryWorkspace();
        var collection = new ContentCollection<string, string>(new ContentCollectionId("routes"), workspace.Root,
            [new(new ContentEntryId("file"), "file.txt", "v1", "File", "file"),
             new(new ContentEntryId("directory"), "directory.txt", "v1", "Directory", "directory")],
            entry => entry.Id.Value == "file" ? SiteRoute.ForFile("file/index.html") : SiteRoute.ForDirectoryIndex("directory"),
            entry => new PageMetadata(entry.FrontMatter));
        var result = await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.com/sub/" }, [], Path.Combine(workspace.Root, "dist"), false, null,
            new SiteGenerationOptions
            {
                ContentCollections = [new SiteContentCollection<string, string>(collection,
                    (entry, context) => context.RenderDocument(new HtmlText(entry.Body).ToHtmlString()))]
            }, CancellationToken.None);
        await Assert.That(result.Routes.Single(route => route.RelativeOutputPath == "file/index.html").PublicPath)
            .IsEqualTo("/sub/file/index.html");
        await Assert.That(result.Routes.Single(route => route.RelativeOutputPath == "directory/index.html").PublicPath)
            .IsEqualTo("/sub/directory/");
        await Assert.That(result.Routes.Count).IsEqualTo(result.BuildPlan.Artifacts.Count);
    }

    [Test]
    public async Task Definition_ValidatesInputsAndSnapshotsPosts()
    {
        await Assert.That(() => new SiteFactoryContext(" ")).Throws<ArgumentException>();
        await Assert.That(() => new SiteDefinition(null!, [])).Throws<ArgumentNullException>();
        await Assert.That(() => new SiteDefinition(new SiteSettings(), null!)).Throws<ArgumentNullException>();
        await Assert.That(() => new SiteDefinition(new SiteSettings(), [null!])).Throws<ArgumentException>();
        var posts = new List<MarkdownPost> { new("one.md", "one", new PostFrontMatter(), "body", "one.html") };
        var definition = new SiteDefinition(new SiteSettings(), posts);
        posts.Clear();
        await Assert.That(definition.Posts.Count).IsEqualTo(1);
        await Assert.That(Path.IsPathFullyQualified(new SiteFactoryContext(".").ProjectDirectory)).IsTrue();
    }

    [Test]
    public async Task Clean_RemovesOnlyUnchangedOwnedFiles_AndCanGenerateAgain()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "dist");
        var generator = new SiteGenerator();
        var definition = await new Factory().CreateAsync(new SiteFactoryContext(workspace.Root));
        var first = await generator.GenerateWithOptionsAsync(definition.Site, definition.Posts,
            output, false, definition.Customization, definition.Options, CancellationToken.None);
        var modified = first.GeneratedFiles.First(path => path.EndsWith("index.html", StringComparison.Ordinal));
        await File.WriteAllTextAsync(modified, "user edit");
        var unowned = Path.Combine(output, "my-file.txt");
        await File.WriteAllTextAsync(unowned, "user file");

        var removed = await generator.CleanAsync(output);
        await Assert.That(removed.Count > 0).IsTrue();
        await Assert.That(removed.Contains(".lithosharp-output-manifest.json")).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(modified)).IsEqualTo("user edit");
        await Assert.That(await File.ReadAllTextAsync(unowned)).IsEqualTo("user file");
        await Assert.That(removed.All(path => !File.Exists(Path.Combine(output, path)))).IsTrue();
        await Assert.That((await generator.CleanAsync(output)).Count).IsEqualTo(0);

        var regenerated = await generator.GenerateWithOptionsAsync(definition.Site, definition.Posts,
            output, false, definition.Customization, definition.Options, CancellationToken.None);
        await Assert.That(regenerated.BuildReport.CacheHitCount).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(unowned)).IsEqualTo("user file");
    }

    [Test]
    public async Task Clean_UntrustedManifestCannotAuthorizeDeletion_AndCancellationPreservesOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "dist");
        Directory.CreateDirectory(output);
        var file = Path.Combine(output, "mine.txt");
        await File.WriteAllTextAsync(file, "mine");
        var manifest = Path.Combine(output, ".lithosharp-output-manifest.json");
        await File.WriteAllTextAsync(manifest, "{\"version\":1,\"files\":[\"mine.txt\"]}");
        var generator = new SiteGenerator();
        await Assert.That((await generator.CleanAsync(output)).Count).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("mine");
        await Assert.That(File.Exists(manifest)).IsTrue();
        await Assert.That(async () => await generator.CleanAsync(output, new CancellationToken(true)))
            .Throws<OperationCanceledException>();
        await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("mine");
        await Assert.That(async () => await generator.CleanAsync(Path.GetPathRoot(output)!))
            .Throws<InvalidOperationException>();
    }

    private sealed class Factory : ISiteFactory
    {
        public Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SiteDefinition(new SiteSettings
            {
                Title = "Factory test", BaseUrl = "https://example.com/", TimeZone = "UTC"
            }, []) { Options = new SiteGenerationOptions { BuildTimestamp = DateTimeOffset.UnixEpoch } });
        }
    }
}
