using System.Reflection;
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content.Compilation;

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
    public async Task SiteGenerator_HoldsNoExternalCompilerTypes()
    {
        var leaked = typeof(SiteGenerator)
            .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => field.FieldType.Namespace is "Markdig" or "Markdig.Renderers" or "Markdig.Syntax" or "Markdig.Syntax.Inlines")
            .Select(field => field.Name)
            .ToArray();

        await Assert.That(string.Join(",", leaked)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Fingerprint_ReflectsFrontendAndSettings()
    {
        var @default = new LithoMarkdownCompiler();
        var repeated = new LithoMarkdownCompiler(MarkdownCompilerOptions.Default);
        var changed = new LithoMarkdownCompiler(
            MarkdownCompilerOptions.Default with { SyntaxProfile = "commonmark" });

        await Assert.That(@default.Frontend).IsEqualTo("lithosharp");
        await Assert.That(@default.Fingerprint.StartsWith("lithosharp/", StringComparison.Ordinal)).IsTrue();
        await Assert.That(repeated.Fingerprint).IsEqualTo(@default.Fingerprint);
        await Assert.That(changed.Fingerprint == @default.Fingerprint).IsFalse();
    }

    [Test]
    public async Task Fingerprint_PinsImplementationAndSpecVersions()
    {
        // Changing output or semantics behavior requires bumping
        // LithoMarkdownCompiler.ImplementationVersion; this pin forces that
        // decision to be explicit instead of reusing stale parse records.
        await Assert.That(new LithoMarkdownCompiler().Fingerprint).IsEqualTo(
            $"lithosharp/{LithoMarkdownCompiler.ImplementationVersion}/spec={LithoMarkdownCompiler.SpecVersion}/syntax=advanced/output=disable-html");
        await Assert.That(LithoMarkdownCompiler.SpecVersion).IsEqualTo("commonmark-0.31.2+gfm-0.29");
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
