using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Mdx;

namespace LithoSharp.Tests;

/// <summary>
/// C06: document-level incremental compilation. Expected invalidation sets at
/// execution level, no re-parse on no-op, parse reuse on layout-only changes,
/// and safe recovery from cache damage.
/// </summary>
public sealed class DocumentIncrementalTests
{
    private static readonly DateTimeOffset FixedBuildTimestamp =
        new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static SiteSettings TestSite() => new()
    {
        Title = "Incremental Site",
        Description = "Document incremental tests.",
        BaseUrl = "https://example.test/",
        Language = "en",
        TimeZone = "UTC",
    };

    private static MarkdownPost Post(string slug, string title, string? body = null) =>
        new(
            $"content/{slug}.md",
            slug,
            new PostFrontMatter
            {
                Title = title,
                Date = FixedBuildTimestamp.AddDays(-1),
                Summary = $"{title} summary",
                Tags = ["test"],
            },
            body ?? $"# {title}\n\nBody for {slug}.",
            $"posts/{slug}.html");

    private static SiteCustomization Blog(SiteThemeOptions? theme = null) => new()
    {
        Template = new BlogSiteTemplate(),
        Theme = theme ?? new SiteThemeOptions(),
    };

    private static async Task<SiteGenerationResult> GenerateAsync(
        SiteGenerator generator,
        IReadOnlyList<MarkdownPost> posts,
        string output,
        bool clean,
        SiteBuildPlan? previousPlan,
        SiteCustomization? customization = null)
    {
        return await generator.GenerateWithOptionsAsync(
            TestSite(),
            posts,
            output,
            clean,
            customization ?? Blog(),
            new SiteGenerationOptions
            {
                BuildTimestamp = FixedBuildTimestamp,
                PreviousBuildPlan = previousPlan,
            },
            CancellationToken.None);
    }

    private static string[] MissedNodeIds(SiteGenerationResult result) =>
        result.BuildReport.Nodes.Where(node => !node.CacheHit).Select(node => node.NodeId).ToArray();

    private static string[] HitNodeIds(SiteGenerationResult result) =>
        result.BuildReport.Nodes.Where(node => node.CacheHit).Select(node => node.NodeId).ToArray();

    [Test]
    public async Task NoOpBuild_ReusesCacheWithoutReparsing()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var posts = new[] { Post("alpha", "Alpha"), Post("beta", "Beta") };
        var generator = new SiteGenerator();

        var first = await GenerateAsync(generator, posts, output, clean: true, previousPlan: null);
        var parsesAfterFirst = generator.MarkdownCompiler.ParseCount;
        await Assert.That(parsesAfterFirst > 0).IsTrue();
        var before = await SnapshotAsync(output);

        var second = await GenerateAsync(generator, posts, output, clean: false, first.BuildPlan);

        await Assert.That(second.BuildReport.CacheMissCount).IsEqualTo(0);
        await Assert.That(second.BuildReport.Invalidations.Count).IsEqualTo(0);
        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(parsesAfterFirst);
        await Assert.That(await SnapshotAsync(output)).IsEquivalentTo(before);
    }

    [Test]
    public async Task BodyOnlyEdit_InvalidatesPageAndSearchButReusesOtherParses()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();
        var first = await GenerateAsync(
            generator, [Post("alpha", "Alpha"), Post("beta", "Beta")], output, clean: true, previousPlan: null);
        var alphaBefore = await File.ReadAllBytesAsync(Path.Combine(output, "posts", "alpha.html"));

        var changed = new[]
        {
            Post("alpha", "Alpha"),
            Post("beta", "Beta", "# Beta\n\nChanged body text."),
        };
        var parsesBefore = generator.MarkdownCompiler.ParseCount;
        var second = await GenerateAsync(generator, changed, output, clean: false, first.BuildPlan);

        // Only the changed page, the search index, and its search page re-execute.
        await Assert.That(MissedNodeIds(second)).IsEquivalentTo(
        [
            "index:search",
            "page:markdown:posts/beta.html",
            "page:search",
        ]);
        await Assert.That(HitNodeIds(second)).Contains("page:markdown:posts/alpha.html");
        await Assert.That(HitNodeIds(second)).Contains("navigation:blog");
        await Assert.That(HitNodeIds(second)).Contains("page:archives");
        await Assert.That(HitNodeIds(second)).Contains("page:tags");
        await Assert.That(HitNodeIds(second)).Contains("page:index");
        // Only the edited post is re-analyzed; the search render reuses both parses.
        await Assert.That(generator.MarkdownCompiler.ParseCount - parsesBefore).IsEqualTo(1);
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(output, "posts", "alpha.html")))
            .IsEquivalentTo(alphaBefore);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "search-index.json")))
            .Contains("Changed body text.");
    }

    [Test]
    public async Task LayoutChange_ReRendersWithoutReparsing()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var posts = new[] { Post("alpha", "Alpha"), Post("beta", "Beta") };
        var generator = new SiteGenerator();
        var first = await GenerateAsync(generator, posts, output, clean: true, previousPlan: null);
        var searchBefore = await File.ReadAllBytesAsync(Path.Combine(output, "search-index.json"));

        var parsesBefore = generator.MarkdownCompiler.ParseCount;
        var second = await GenerateAsync(
            generator, posts, output, clean: false, first.BuildPlan,
            Blog(new SiteThemeOptions { ThemeColor = "#123456" }));

        await Assert.That(second.BuildReport.Nodes.Single(node => node.NodeId == "index:search").CacheHit).IsTrue();
        await Assert.That(second.BuildReport.Nodes.Single(node => node.NodeId == "page:markdown:posts/alpha.html").CacheHit).IsFalse();
        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(parsesBefore);
        await Assert.That(await File.ReadAllBytesAsync(Path.Combine(output, "search-index.json")))
            .IsEquivalentTo(searchBefore);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "alpha.html")))
            .Contains("#123456");
    }

    [Test]
    public async Task TitleOnlyEdit_ReusesParse()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();
        var first = await GenerateAsync(
            generator, [Post("alpha", "Alpha"), Post("beta", "Beta")], output, clean: true, previousPlan: null);

        var renamed = new[]
        {
            Post("alpha", "Alpha"),
            // Front matter only: the body (and its source hash) is unchanged.
            Post("beta", "Renamed Beta", "# Beta\n\nBody for beta."),
        };
        var parsesBefore = generator.MarkdownCompiler.ParseCount;
        var second = await GenerateAsync(generator, renamed, output, clean: false, first.BuildPlan);

        // Listings follow the title, but the unchanged body is not re-analyzed.
        await Assert.That(MissedNodeIds(second)).Contains("page:markdown:posts/beta.html");
        await Assert.That(HitNodeIds(second)).Contains("page:markdown:posts/alpha.html");
        await Assert.That(generator.MarkdownCompiler.ParseCount).IsEqualTo(parsesBefore);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "beta.html")))
            .Contains("Renamed Beta");
    }

    [Test]
    public async Task AddDeleteRename_UpdatesArtifacts()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();
        var first = await GenerateAsync(
            generator, [Post("alpha", "Alpha")], output, clean: true, previousPlan: null);
        await Assert.That(File.Exists(Path.Combine(output, "posts", "alpha.html"))).IsTrue();

        var added = await GenerateAsync(
            generator, [Post("alpha", "Alpha"), Post("beta", "Beta")], output, clean: false, first.BuildPlan);
        await Assert.That(File.Exists(Path.Combine(output, "posts", "beta.html"))).IsTrue();
        await Assert.That(MissedNodeIds(added)).Contains("page:markdown:posts/beta.html");
        await Assert.That(HitNodeIds(added)).Contains("page:markdown:posts/alpha.html");

        // Rename via slug change: the old artifact leaves, the new one arrives.
        var renamed = new[] { Post("renamed", "Alpha") };
        var renamedResult = await GenerateAsync(generator, renamed, output, clean: false, added.BuildPlan);
        await Assert.That(File.Exists(Path.Combine(output, "posts", "alpha.html"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(output, "posts", "renamed.html"))).IsTrue();
        await Assert.That(renamedResult.BuildReport.StaleRemovedArtifacts).Contains("posts/alpha.html");

        var removed = await GenerateAsync(generator, [], output, clean: false, renamedResult.BuildPlan);
        await Assert.That(File.Exists(Path.Combine(output, "posts", "renamed.html"))).IsFalse();
        await Assert.That(removed.BuildReport.StaleRemovedArtifacts).Contains("posts/renamed.html");
    }

    [Test]
    public async Task SharedAssetChange_KeepsPages()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var favicon = Path.Combine(workspace.Root, "favicon");
        Directory.CreateDirectory(favicon);
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        foreach (var fileName in SiteGenerator.BundledFaviconAssetNames)
        {
            await File.WriteAllBytesAsync(Path.Combine(favicon, fileName), png);
        }

        var generator = new SiteGenerator();
        var customization = Blog() with { FaviconSourceDirectory = favicon };
        var posts = new[] { Post("alpha", "Alpha") };
        var first = await generator.GenerateWithOptionsAsync(
            TestSite(), posts, output, clean: true, customization,
            new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp }, CancellationToken.None);

        await File.WriteAllBytesAsync(
            Path.Combine(favicon, "favicon.ico"),
            [.. png, (byte)0x00]);
        var second = await generator.GenerateWithOptionsAsync(
            TestSite(), posts, output, clean: false, customization,
            new SiteGenerationOptions
            {
                BuildTimestamp = FixedBuildTimestamp,
                PreviousBuildPlan = first.BuildPlan,
            },
            CancellationToken.None);

        await Assert.That(MissedNodeIds(second)).Contains("asset:favicon:favicon.ico");
        await Assert.That(HitNodeIds(second)).Contains("page:markdown:posts/alpha.html");
        await Assert.That(HitNodeIds(second)).Contains("page:index");
    }

    [Test]
    public async Task CompilerChange_InvalidatesMarkdownNodes()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var posts = new[] { Post("alpha", "Alpha"), Post("beta", "Beta") };
        var first = await GenerateAsync(
            new SiteGenerator(), posts, output, clean: true, previousPlan: null);

        var changed = new SiteGenerator(new LithoMarkdownCompiler(
            MarkdownCompilerOptions.Default with { SyntaxProfile = "commonmark" }));
        var second = await GenerateAsync(changed, posts, output, clean: false, first.BuildPlan);

        await Assert.That(MissedNodeIds(second)).Contains("page:markdown:posts/alpha.html");
        await Assert.That(MissedNodeIds(second)).Contains("page:markdown:posts/beta.html");
        await Assert.That(MissedNodeIds(second)).Contains("index:search");
        await Assert.That(changed.MarkdownCompiler.ParseCount > 0).IsTrue();
    }

    [Test]
    public async Task IncludeTargetChange_InvalidatesDependentPage()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(Path.Combine(content, "snippets"));
        await File.WriteAllTextAsync(Path.Combine(content, "post.md"), """
            ---
            title: "Include Post"
            date: "2026-01-02T03:04:05Z"
            summary: "inclusion post"
            tags:
              - test
            ---

            ```csharp source="./snippets/demo.cs" region="main"
            ```
            """);
        await File.WriteAllTextAsync(
            Path.Combine(content, "snippets", "demo.cs"), "#region main\nvar answer = 42;\n#endregion\n");

        var generator = new SiteGenerator();
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        var first = await GenerateAsync(generator, posts, output, clean: true, previousPlan: null);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "post.html")))
            .Contains("var answer = 42;");

        // Only the include target changes: the post source is untouched, yet the
        // resolved body (and its source fingerprint) changes, so the page misses.
        await File.WriteAllTextAsync(
            Path.Combine(content, "snippets", "demo.cs"), "#region main\nvar answer = 43;\n#endregion\n");
        var changed = await new MarkdownPostReader().ReadAllAsync(content);
        var second = await GenerateAsync(generator, changed, output, clean: false, first.BuildPlan);

        await Assert.That(MissedNodeIds(second)).Contains("page:markdown:posts/post.html");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "post.html")))
            .Contains("var answer = 43;");
    }

    [Test]
    [Arguments("invalid-json")]
    [Arguments("negative-span")]
    [Arguments("overflow-span")]
    [Arguments("null-heading")]
    [Arguments("wrong-html")]
    public async Task CorruptParseCache_ReparsesWithIdenticalOutput(string corruption)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();
        var posts = new[] { Post("alpha", "Alpha"), Post("beta", "Beta") };
        var first = await GenerateAsync(generator, posts, output, clean: true, previousPlan: null);

        // Corrupt every parse record but keep the node manifest: the next build
        // re-analyzes rendered posts instead of failing.
        var cacheRoot = Path.Combine(Path.GetDirectoryName(output)!, ".lithosharp");
        var corrupted = 0;
        foreach (var directory in Directory.EnumerateDirectories(cacheRoot, "*", SearchOption.AllDirectories))
        {
            if (!string.Equals(Path.GetFileName(directory), "parses", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
                switch (corruption)
                {
                    case "negative-span": json["Headings"]![0]!["SpanStart"] = -1; break;
                    case "overflow-span": json["Headings"]![0]!["SpanLength"] = int.MaxValue; break;
                    case "null-heading": json["Headings"]![0] = null; break;
                    case "wrong-html": json["RawHtml"] = "<p>corrupt cached HTML</p>"; break;
                }
                await File.WriteAllTextAsync(file, corruption == "invalid-json" ? "corrupt" : json.ToJsonString());
                corrupted++;
            }
        }

        await Assert.That(corrupted).IsGreaterThan(0);

        // A body edit forces both posts through rendering: the edited post misses
        // by source change, the other by cache damage.
        var renamed = new[] { Post("alpha", "Alpha"), Post("beta", "Beta changed") };
        var parsesBefore = generator.MarkdownCompiler.ParseCount;
        var second = await GenerateAsync(generator, renamed, output, clean: false, first.BuildPlan);

        await Assert.That(generator.MarkdownCompiler.ParseCount - parsesBefore).IsEqualTo(2);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "beta.html")))
            .Contains("Beta changed");
        await Assert.That(MissedNodeIds(second)).Contains("page:markdown:posts/beta.html");

        // A following no-op build is fully cached again.
        var third = await GenerateAsync(generator, renamed, output, clean: false, second.BuildPlan);
        await Assert.That(third.BuildReport.CacheMissCount).IsEqualTo(0);
        await Assert.That(generator.MarkdownCompiler.ParseCount - parsesBefore).IsEqualTo(2);
    }

    [Test]
    public async Task DocsVersionAdded_UpdatesVersionSwitcherKeepingContent()
    {
        using var workspace = new TemporaryWorkspace();
        var v1 = Path.Combine(workspace.Root, "input", "v1", "en");
        Directory.CreateDirectory(v1);
        await File.WriteAllTextAsync(Path.Combine(v1, "01-intro.md"), "---\ntitle: Intro\n---\n# Intro\n");

        await using var firstDocs = new DocumentationSite(new(workspace.Root) { NodeExecutable = "must-not-start-node" });
        firstDocs.AddCollection(new("guide", [new("v1", "en", v1, "guide/v1/en")]));
        var output = Path.Combine(workspace.Root, "out");
        var generator = new SiteGenerator();
        var first = await generator.GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/" }, [], output, clean: true,
            new() { Template = new DocsSiteTemplate() },
            new() { Extensions = [firstDocs], BuildTimestamp = FixedBuildTimestamp }, CancellationToken.None);
        var introPath = Directory.EnumerateFiles(
            Path.Combine(output, "guide", "v1", "en"), "index.html", SearchOption.AllDirectories).Single();
        _ = await File.ReadAllBytesAsync(introPath);

        var v2 = Path.Combine(workspace.Root, "input", "v2", "en");
        Directory.CreateDirectory(v2);
        await File.WriteAllTextAsync(Path.Combine(v2, "01-intro.md"), "---\ntitle: Intro v2\n---\n# Intro v2\n");
        await using var secondDocs = new DocumentationSite(new(workspace.Root) { NodeExecutable = "must-not-start-node" });
        secondDocs.AddCollection(new("guide", [new("v1", "en", v1, "guide/v1/en"), new("v2", "en", v2, "guide/v2/en")]));
        var second = await generator.GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/" }, [], output, clean: false,
            new() { Template = new DocsSiteTemplate() },
            new()
            {
                Extensions = [secondDocs],
                BuildTimestamp = FixedBuildTimestamp,
                PreviousBuildPlan = first.BuildPlan,
            },
            CancellationToken.None);

        await Assert.That(Directory.EnumerateFiles(
            Path.Combine(output, "guide", "v2", "en"), "index.html", SearchOption.AllDirectories).Count()).IsEqualTo(1);
        // Adding a version updates the version switcher embedded in existing pages
        // (an intended dependent), while their own content stays intact.
        var introAfter = await File.ReadAllTextAsync(introPath);
        await Assert.That(introAfter).Contains("Intro");
        await Assert.That(introAfter).Contains("guide/v2/en");
        await Assert.That(second.BuildReport.Nodes.Any(node => !node.CacheHit)).IsTrue();
    }

    private static async Task<IReadOnlyList<(string Path, string Bytes)>> SnapshotAsync(string root) =>
        (await Task.WhenAll(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(async path => (Path.GetRelativePath(root, path).Replace('\\', '/'),
                Convert.ToBase64String(await File.ReadAllBytesAsync(path)))))).ToArray();
}
