using LithoSharp;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;

namespace LithoSharp.Tests;

public sealed class LegacyTemplateBuildPlanTests
{
    private static readonly DateTimeOffset BuildTimestamp =
        new(2026, 8, 30, 10, 11, 12, TimeSpan.Zero);

    [Test]
    public async Task GenerateAsync_DocsAndBlogDeclareOneOwnerPerGeneratedArtifact()
    {
        foreach (var templateCase in new[]
                 {
                     new TemplateCase(new DocsSiteTemplate(), new[] { "index.html", "posts/post.html" }),
                     new TemplateCase(new BlogSiteTemplate(), new[] { "archives.html", "feed.xml", "posts/post.html" }),
                 })
        {
            using var workspace = new TemporaryWorkspace();
            var result = await GenerateAsync(
                workspace,
                templateCase.Template,
                [Post("post", "Published")]);

            await Assert.That(templateCase.ExpectedPaths.All(path =>
                result.BuildPlan.Artifacts.Any(artifact => artifact.RelativeOutputPath == path))).IsTrue();
            await Assert.That(result.BuildPlan.Nodes.Any(node =>
                node.Id.Value == "template:legacy-opaque")).IsFalse();
            await Assert.That(result.BuildPlan.Artifacts.All(artifact =>
                result.BuildPlan.GetArtifactOwner(artifact.Id).Id.Equals(artifact.OwnerNodeId))).IsTrue();
        }
    }

    [Test]
    public async Task GenerateAsync_CustomTemplateIsOneOpaqueNodeAndUsesOnlyPublishedPages()
    {
        using var workspace = new TemporaryWorkspace();
        var result = await GenerateAsync(
            workspace,
            new EchoTemplate(),
            [
                Post("published", "Published title"),
                Post("draft", "Hidden title", draft: true),
            ],
            extraPages:
            [
                new SiteExtraPage
                {
                    RelativePath = "about.html",
                    Title = "About",
                    BodyHtml = "<p>About body</p>",
                },
            ],
            text: SiteText.Japanese,
            theme: new SiteThemeOptions { ThemeColor = "#123456" },
            environmentName: "Staging");

        var node = result.BuildPlan.Nodes.Single();
        var inputText = string.Join('\n', node.Inputs.Select(input => $"{input.Key}:{input.Value}"));
        await Assert.That(node.Id.Value).IsEqualTo("template:legacy-opaque");
        await Assert.That(node.Artifacts.Select(artifact => artifact.RelativeOutputPath))
            .IsEquivalentTo(["custom.html"]);
        await Assert.That(inputText).Contains("opaque-whole-site");
        await Assert.That(inputText).Contains("always-rebuild");
        await Assert.That(inputText).Contains(typeof(EchoTemplate).FullName!);
        await Assert.That(inputText).Contains("Published title");
        await Assert.That(inputText).Contains("posts/published.html");
        await Assert.That(inputText).DoesNotContain("Hidden title");
        await Assert.That(inputText).Contains("About body");
        await Assert.That(inputText).Contains("about.html");
        await Assert.That(inputText).Contains("Test Site");
        await Assert.That(node.Inputs.Any(input =>
            input.Key == "customization.text"
            && input.Value == System.Text.Json.JsonSerializer.Serialize(SiteText.Japanese))).IsTrue();
        await Assert.That(inputText).Contains("#123456");
        await Assert.That(inputText).Contains(BuildTimestamp.ToString("O"));
        await Assert.That(inputText).Contains("Staging");
    }

    [Test]
    public async Task GenerateAsync_BuiltInCommonArtifactsHavePreciseOwnersAndAssetInputs()
    {
        using var workspace = new TemporaryWorkspace();
        var faviconSource = Path.Combine(workspace.Root, "favicon");
        await WriteFaviconAssetsAsync(faviconSource);
        var output = Path.Combine(workspace.Root, "output");
        var result = await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            [Post("post", "Post")],
            output,
            clean: true,
            new SiteCustomization
            {
                Template = new DocsSiteTemplate(),
                FaviconSourceDirectory = faviconSource,
                GenerateLlmsTxt = true,
            },
            new SiteGenerationOptions { BuildTimestamp = BuildTimestamp },
            CancellationToken.None);

        await Assert.That(result.BuildPlan.Nodes.Count(node =>
            node.Id.Value.StartsWith("asset:favicon:", StringComparison.Ordinal))).IsEqualTo(14);
        await Assert.That(result.BuildPlan.Nodes
            .Where(node => node.Id.Value.StartsWith("asset:favicon:", StringComparison.Ordinal))
            .All(node => node.Inputs.Single().Kind == BuildInputKind.File
                         && node.Inputs.Single().Value is not null)).IsTrue();

        var artifactPaths = result.BuildPlan.Artifacts
            .Select(artifact => artifact.RelativeOutputPath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var generatedPaths = result.GeneratedFiles
            .Select(path => Path.GetRelativePath(output, path).Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        await Assert.That(artifactPaths.SequenceEqual(generatedPaths)).IsTrue();
        await Assert.That(result.BuildPlan.Artifacts.Select(artifact => artifact.RelativeOutputPath)
            .Distinct(StringComparer.Ordinal).Count()).IsEqualTo(result.BuildPlan.Artifacts.Count);
        await Assert.That(result.BuildPlan.GetArtifactOwner(
                result.BuildPlan.Artifacts.Single(artifact => artifact.RelativeOutputPath == "llms.txt").Id)
            .Id.Value).IsEqualTo("text:llms");
        await Assert.That(result.BuildPlan.GetArtifactOwner(
                result.BuildPlan.Artifacts.Single(artifact => artifact.RelativeOutputPath == "index.html").Id)
            .Id.Value).IsEqualTo("page:index");
    }

    [Test]
    public async Task GenerateAsync_EquivalentInputsProduceDeterministicallyOrderedPlans()
    {
        using var firstWorkspace = new TemporaryWorkspace();
        using var secondWorkspace = new TemporaryWorkspace();
        var first = await GenerateAsync(
            firstWorkspace,
            new BlogSiteTemplate(),
            [Post("b", "B"), Post("a", "A")]);
        var second = await GenerateAsync(
            secondWorkspace,
            new BlogSiteTemplate(),
            [Post("b", "B"), Post("a", "A")]);

        await Assert.That(Describe(first.BuildPlan).SequenceEqual(Describe(second.BuildPlan))).IsTrue();
        await Assert.That(first.BuildPlan.Nodes.Select(node => node.Id.Value)
            .SequenceEqual(first.BuildPlan.Nodes.Select(node => node.Id.Value)
                .Order(StringComparer.Ordinal))).IsTrue();
        await Assert.That(first.BuildPlan.Artifacts.Select(artifact => artifact.Id.Value)
            .SequenceEqual(first.BuildPlan.Artifacts.Select(artifact => artifact.Id.Value)
                .Order(StringComparer.Ordinal))).IsTrue();
    }

    private static async Task<SiteGenerationResult> GenerateAsync(
        TemporaryWorkspace workspace,
        ISiteTemplate template,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<SiteExtraPage>? extraPages = null,
        SiteText? text = null,
        SiteThemeOptions? theme = null,
        string environmentName = "Production")
    {
        return await new SiteGenerator().GenerateWithOptionsAsync(
            TestSite(),
            posts,
            Path.Combine(workspace.Root, "output"),
            clean: true,
            new SiteCustomization
            {
                Template = template,
                ExtraPages = extraPages ?? [],
                Text = text ?? SiteText.English,
                Theme = theme ?? new SiteThemeOptions(),
            },
            new SiteGenerationOptions
            {
                BuildTimestamp = BuildTimestamp,
                EnvironmentName = environmentName,
            },
            CancellationToken.None);
    }

    private static SiteSettings TestSite() => new()
    {
        Title = "Test Site",
        Description = "Build plan test.",
        BaseUrl = "https://example.test/",
        Language = "en",
        TimeZone = "UTC",
    };

    private static MarkdownPost Post(string slug, string title, bool draft = false) =>
        new(
            $"content/{slug}.md",
            slug,
            new PostFrontMatter
            {
                Title = title,
                Date = BuildTimestamp.AddDays(-1),
                Summary = $"{title} summary",
                Draft = draft,
            },
            $"# {title}\n\nBody for {slug}.",
            $"posts/{slug}.html");

    private static IEnumerable<string> Describe(SiteBuildPlan plan) =>
        plan.Nodes.Select(node =>
            $"{node.Id}|{string.Join(',', node.Inputs.Select(input => $"{input.Kind}:{input.Key}:{input.Value}"))}|{string.Join(',', node.Dependencies)}|{string.Join(',', node.Artifacts.Select(artifact => $"{artifact.Id}:{artifact.RelativeOutputPath}"))}");

    private static async Task WriteFaviconAssetsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var bytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        foreach (var fileName in new[]
                 {
                     "favicon.ico",
                     "apple-touch-icon.png",
                     "android-chrome-192x192.png",
                     "android-chrome-512x512.png",
                     "favicon-16x16.png",
                     "favicon-32x32.png",
                     "favicon-48x48.png",
                     "favicon-64x64.png",
                     "favicon-96x96.png",
                     "favicon-128x128.png",
                     "favicon-180x180.png",
                     "favicon-192x192.png",
                     "favicon-256x256.png",
                     "favicon-512x512.png",
                 })
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, fileName), bytes);
        }
    }

    private sealed record TemplateCase(
        ISiteTemplate Template,
        IReadOnlyList<string> ExpectedPaths);

    private sealed class EchoTemplate : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(
            SiteTemplateContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult(
            [
                new SiteTemplateFile
                {
                    RelativePath = "custom.html",
                    Content = string.Join('|',
                        context.Pages.Select(page => page.Post.FrontMatter.Title)
                            .Concat(context.ExtraPages.Select(page => page.Title))),
                },
            ]));
    }
}
