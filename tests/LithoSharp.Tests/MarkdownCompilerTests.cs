using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

/// <summary>
/// C13: the internal Markdown compiler adapter preserves output and
/// invalidates caches when settings change. The Litho frontend is the only
/// compiler; no Markdig assembly may load in the normal suite.
/// </summary>
public sealed class MarkdownCompilerTests
{
    private static readonly DateTimeOffset FixedBuildTimestamp =
        new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task DefaultCompiler_PreservesBaselineOutput()
    {
        var html = new SiteGenerator().RenderMarkdown("## Shared\n\nBody text.");

        await Assert.That(html).Contains("<h2");
        await Assert.That(html).Contains("id=\"");
        await Assert.That(html).Contains("Body text.");

        var again = new SiteGenerator().RenderMarkdown("## Shared\n\nBody text.");
        await Assert.That(again).IsEqualTo(html);
    }

    [Test]
    public async Task ComponentContext_UsesSameCompilerOutput()
    {
        var site = new SiteSettings
        {
            Title = "Compiler Site",
            Description = "Compiler tests.",
            BaseUrl = "https://example.test/",
            Language = "en",
            TimeZone = "UTC",
        };
        const string markdown = "## Shared\n\nBody text.";
        var expected = new SiteGenerator().RenderMarkdown(markdown);
        var actual = PageRenderingContext.Create(site).RenderMarkdown(markdown).ToHtmlString();

        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task NoMarkdigAssemblyLoads()
    {
        _ = new SiteGenerator().RenderMarkdown("## Probe\n\nBody text.");
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => assembly.GetName().Name?.StartsWith("Markdig", StringComparison.Ordinal) == true)
            .Select(assembly => assembly.GetName().Name)
            .ToArray();

        await Assert.That(string.Join(",", loaded)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Fingerprint_ReflectsFrontendAndSettings()
    {
        var @default = new LithoMarkdownCompiler();
        var repeated = new LithoMarkdownCompiler(MarkdownCompilerOptions.Default);
        var changed = new LithoMarkdownCompiler(
            MarkdownCompilerOptions.Default with { SyntaxProfile = "commonmark" });

        await Assert.That(LithoMarkdownCompiler.SpecVersion).IsEqualTo("commonmark-0.31.2+gfm-0.29");
        await Assert.That(@default.Frontend).IsEqualTo("lithosharp");
        await Assert.That(@default.Fingerprint.StartsWith($"lithosharp/{LithoMarkdownCompiler.ImplementationVersion}/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(@default.Fingerprint).Contains($"/spec={LithoMarkdownCompiler.SpecVersion}/");
        await Assert.That(@default.Fingerprint).Contains($"/parser={MdParserVersion.Value}/profile=lithosharp-markdown/1/");
        await Assert.That(repeated.Fingerprint).IsEqualTo(@default.Fingerprint);
        await Assert.That(changed.Fingerprint == @default.Fingerprint).IsFalse();
    }

    [Test]
    [Arguments("unversioned")]
    [Arguments("previous-parser")]
    public async Task PersistentBodyCache_RejectsDifferentParserIdentity(string identity)
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var compiler = new LithoMarkdownCompiler();
        var previous = identity == "unversioned"
            ? $"lithosharp/{LithoMarkdownCompiler.ImplementationVersion}/spec={LithoMarkdownCompiler.SpecVersion}/syntax=advanced/output=disable-html"
            : compiler.Fingerprint.Replace(MdParserVersion.Value, "1/" + new string('0', 64), StringComparison.Ordinal);
        const string body = "## Current heading\n\nCurrent body.";
        var sourceHash = MarkdownDocumentFingerprints.SourceHash(body);
        var partition = SiteGenerator.MeasureCache(output).CacheDirectory;
        var cache = new SiteBuildCache(Path.GetDirectoryName(partition)!, Path.GetFileName(partition));
        await cache.StorePostParseAsync(new CachedPostParse(
            CachedPostParse.CurrentVersion, previous, sourceHash, body.Length,
            "<p>stale parser HTML</p>", "stale parser text", [], [], []), CancellationToken.None);
        var post = new MarkdownPost("content/post.md", "post",
            new PostFrontMatter { Title = "Post", Date = FixedBuildTimestamp, Summary = "Current summary." },
            body, "posts/post.html");
        var generator = new SiteGenerator(compiler);
        await generator.GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/" }, [post], output, clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() },
            new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp }, CancellationToken.None);

        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "post.html")))
            .Contains("Current body.");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "search-index.json")))
            .Contains("Current body.");
        await Assert.That(await cache.ReadPostParseAsync(
            compiler.Fingerprint, sourceHash, body.Length, CancellationToken.None)).IsNotNull();

        var next = new LithoMarkdownCompiler();
        await new SiteGenerator(next).GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/" },
            [post with { FrontMatter = post.FrontMatter with { Title = "Updated title" } }],
            output, clean: false, new SiteCustomization { Template = new BlogSiteTemplate() },
            new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp }, CancellationToken.None);
        await Assert.That(next.ParseCount).IsEqualTo(0);
    }

    [Test]
    public async Task CompilerChange_InvalidatesBuildCache()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var site = new SiteSettings
        {
            Title = "Compiler Site",
            Description = "Compiler tests.",
            BaseUrl = "https://example.test/",
            Language = "en",
            TimeZone = "UTC",
        };
        var customization = new SiteCustomization { Template = new BlogSiteTemplate() };
        var options = new SiteGenerationOptions { BuildTimestamp = FixedBuildTimestamp };

        var first = await new SiteGenerator().GenerateWithOptionsAsync(
            site, [], output, clean: true, customization, options, CancellationToken.None);
        var second = await new SiteGenerator().GenerateWithOptionsAsync(
            site, [], output, clean: false, customization,
            options with { PreviousBuildPlan = first.BuildPlan }, CancellationToken.None);

        await Assert.That(second.BuildReport.Invalidations.Count).IsEqualTo(0);
        await Assert.That(second.BuildReport.CacheMissCount).IsEqualTo(0);

        var changed = new LithoMarkdownCompiler(
            MarkdownCompilerOptions.Default with { SyntaxProfile = "commonmark" });
        var third = await new SiteGenerator(changed).GenerateWithOptionsAsync(
            site, [], output, clean: false, customization,
            options with { PreviousBuildPlan = second.BuildPlan }, CancellationToken.None);

        // The compiler fingerprint feeds execution cache keys, so a frontend or
        // settings change must not reuse previously rendered nodes.
        await Assert.That(third.BuildReport.CacheMissCount > 0).IsTrue();
    }
}
