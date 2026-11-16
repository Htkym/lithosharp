using System.Reflection;
using LithoSharp.Configuration;
using LithoSharp.Content;

namespace LithoSharp.Tests;

public sealed class SocialImageTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExplicitImagesUseRegisteredUrlsAndMetadataForTheSelectedPage(bool docs)
    {
        using var workspace = new TemporaryWorkspace();
        // Deliberately undecodable bytes prove that Core only registers and copies these images.
        byte[] commonBytes = [0xFF, 0xD8, 0xFF, 0xD9];
        byte[] overrideBytes = [1, 2, 3, 4, 5];
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "preview.jpg"), commonBytes);
        var common = new SiteAsset("preview", workspace.Root, "preview.jpg", "assets/preview.jpg",
            new ImageMetadata(640, 320, "image/jpeg", ImageMetadataProvenance.Declared));
        var article = new SiteAssetOutput("article-preview", "assets/article.avif", "image/avif");
        var transform = new SiteAssetTransform("article-preview", "fixture/1", [], [article],
            (context, token) => context.WriteAsync(article, overrideBytes,
                new ImageMetadata(800, 400, "image/avif", ImageMetadataProvenance.Generated), token));
        var output = Path.Combine(workspace.Root, "output");
        var posts = new[]
        {
            Post("same") with { RelativeOutputPath = "posts/first.html" },
            Post("same") with { RelativeOutputPath = "posts/second.html" },
        };
        var result = await GenerateAsync(output, posts, new SiteGenerationOptions
        {
            Assets = [common],
            AssetTransforms = [transform],
            CommonSocialImage = new ImageSource(common),
            SocialImages = new Dictionary<string, ImageSource>
            {
                ["posts/first.html"] = new(article),
            },
        }, docs);

        var commonPath = result.BuildPlan.Nodes.Single(node => node.Id.Value == "asset:preview")
            .Artifacts.Single().RelativeOutputPath;
        var articlePath = result.BuildPlan.Nodes.Single(node => node.Id.Value == "asset-transform:article-preview")
            .Artifacts.Single().RelativeOutputPath;
        var home = await File.ReadAllTextAsync(Path.Combine(output, "index.html"));
        var first = await File.ReadAllTextAsync(Path.Combine(output, "posts", "first.html"));
        var second = await File.ReadAllTextAsync(Path.Combine(output, "posts", "second.html"));
        await Assert.That(home).Contains($"property=\"og:image\" content=\"https://example.test/sub/{commonPath}\"");
        await Assert.That(second).Contains($"property=\"og:image\" content=\"https://example.test/sub/{commonPath}\"");
        await Assert.That(first).Contains($"property=\"og:image\" content=\"https://example.test/sub/{articlePath}\"");
        await Assert.That(first).DoesNotContain($"content=\"https://example.test/sub/{commonPath}\"");
        await Assert.That(home).Contains("property=\"og:image:type\" content=\"image/jpeg\"");
        await Assert.That(home).Contains("property=\"og:image:width\" content=\"640\"");
        await Assert.That(first).Contains("property=\"og:image:type\" content=\"image/avif\"");
        await Assert.That(first).Contains("property=\"og:image:height\" content=\"400\"");
        await Assert.That((await File.ReadAllBytesAsync(Path.Combine(output, commonPath))).SequenceEqual(commonBytes)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(Path.Combine(output, articlePath))).SequenceEqual(overrideBytes)).IsTrue();
        await Assert.That(result.BuildPlan.Nodes.Any(node => node.Id.Value.StartsWith("social:", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ChangingAnExplicitImageInvalidatesOnlyItsConsumers()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var posts = new[] { Post("first"), Post("second") };
        SiteGenerationOptions Options(string name) => new()
        {
            CommonSocialImage = new ImageSource("/sub/shared.jpg"),
            SocialImages = new Dictionary<string, ImageSource>
            {
                ["posts/first.html"] = new("https://images.example/" + name),
            },
        };
        await GenerateAsync(output, posts, Options("first.webp"));
        var unchanged = await GenerateAsync(output, posts, Options("first.webp"));
        await Assert.That(unchanged.BuildReport.Nodes.Single(node => node.NodeId == "page:markdown:posts/first.html").CacheHit).IsTrue();

        var changed = await GenerateAsync(output, posts, Options("updated.avif"));
        await Assert.That(changed.BuildReport.Nodes.Single(node => node.NodeId == "page:markdown:posts/first.html").CacheHit).IsFalse();
        await Assert.That(changed.BuildReport.Nodes.Single(node => node.NodeId == "page:markdown:posts/second.html").CacheHit).IsTrue();
        await Assert.That(changed.BuildReport.Nodes.Single(node => node.NodeId == "page:index").CacheHit).IsTrue();
        var first = await File.ReadAllTextAsync(Path.Combine(output, "posts", "first.html"));
        await Assert.That(first).Contains("content=\"https://images.example/updated.avif\"");
        await Assert.That(first).DoesNotContain("property=\"og:image:width\"");
    }

    [Test]
    public async Task MissingRegisteredImageFailsBeforeReplacingOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "index.html");
        await File.WriteAllTextAsync(sentinel, "keep");
        var missing = new SiteAssetOutput("missing", "assets/missing.png");
        await Assert.That(async () => await GenerateAsync(output, [], new SiteGenerationOptions
        {
            CommonSocialImage = new ImageSource(missing),
        }))
            .Throws<AssetRegistryException>();
        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("keep");
    }

    [Test]
    public async Task LegacyTitleRenderingGivesAMigrationErrorToExistingCallers()
    {
        var property = typeof(SiteThemeOptions).GetProperty("DefaultSocialSubtitle")!;
        await Assert.That(property.GetCustomAttribute<ObsoleteAttribute>()!.IsError).IsTrue();
        try
        {
            property.SetValue(new SiteThemeOptions(), "Legacy subtitle");
            throw new InvalidOperationException("The removed title renderer was silently accepted.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is NotSupportedException failure)
        {
            await Assert.That(failure.Message).Contains("CommonSocialImage");
        }
    }

    private static Task<SiteGenerationResult> GenerateAsync(string output, IReadOnlyList<MarkdownPost> posts,
        SiteGenerationOptions options, bool docs = false) => new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings
            {
                Title = "Social image test",
                Description = "Explicit existing images",
                BaseUrl = "https://example.test/sub/",
                Language = "en",
                TimeZone = "UTC",
            }, posts, output, clean: false,
            new SiteCustomization { Template = docs ? new DocsSiteTemplate() : new BlogSiteTemplate() },
            options with { BuildTimestamp = Timestamp }, CancellationToken.None);

    private static MarkdownPost Post(string slug) => new($"content/{slug}.md", slug,
        new PostFrontMatter { Title = slug, Summary = slug, Date = Timestamp.AddDays(-1) },
        "# Heading\n\nBody.", $"posts/{slug}.html");
}
