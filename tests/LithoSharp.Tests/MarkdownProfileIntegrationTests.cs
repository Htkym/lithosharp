using System.Text.RegularExpressions;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Inspection;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class MarkdownProfileIntegrationTests
{
    [Test]
    public async Task DocumentAnalysisFootnoteLinksRetainFullOriginalOffsets()
    {
        const string raw = "\uFEFF---\r\ntitle: 😀\r\n---\r\n# Guide\r\n\r\nUse[^n].\r\n\r\n[^n]: Note.\r\n";
        var analysis = MdParser.AnalyzeWithBlocks(raw, MdAnalysisParts.Headings | MdAnalysisParts.Links,
            "inspection", "guide.md", null,
            new MdOptions(profileId: MarkdownOptions.DocumentationProfile), CancellationToken.None);
        var links = MarkdownAnalysisAdapter.InspectionLinks(analysis, CancellationToken.None);
        var reference = links.Single(static link => link.RawText == "[^n]");
        await Assert.That(reference.Url).IsEqualTo("#fn-1");
        await Assert.That(reference.Span.Start).IsEqualTo(raw.IndexOf("[^n]", StringComparison.Ordinal));
        await Assert.That(reference.Span.Length).IsEqualTo(4);
    }

    [Test]
    public async Task OmittedFootnoteDefinitionsDoNotConsumeVisibleHeadingIds()
    {
        const string body = "[^unused]: # Repeat\n\n# Repeat\n\nUse[^n].\n\n"
            + "[^n]: # Repeat\n\n[^n]: # Hidden\n\n# Hidden\n";
        var result = Compiler(MarkdownOptions.DocumentationProfile).Analyze(body);
        await Assert.That(result.Semantics!.Headings.Select(static heading => (heading.Text, heading.Id)))
            .IsEquivalentTo(new[] { ("Repeat", (string?)"repeat"), ("Repeat", "repeat-1"), ("Hidden", "hidden") });
        var renderedIds = Regex.Matches(result.Html, "<h[1-6] id=\"([^\"]+)\"")
            .Select(static match => match.Groups[1].Value).ToArray();
        await Assert.That(renderedIds).IsEquivalentTo(new[] { "repeat", "repeat-1", "hidden" });
        await Assert.That(result.Html).DoesNotContain("id=\"hidden-2\"");
    }

    [Test]
    public async Task FootnotesInLinkLabelsRenderAfterTheLinkWithoutNestedAnchors()
    {
        const string body = "[Link *label[^n]*](guide.html).\n\n[^n]: Note.\n";
        var result = Compiler(MarkdownOptions.DocumentationProfile).Analyze(body);
        await Assert.That(result.Html).Contains("<a href=\"guide.html\">Link <em>label</em></a><sup class=\"footnote-ref\"");
        await Assert.That(result.Html).Contains("href=\"#fn-1\" role=\"doc-noteref\">1</a>");
        await Assert.That(result.Semantics!.Links.Select(static link => link.Url))
            .IsEquivalentTo(new[] { "guide.html", "#fn-1" });
    }

    [Test]
    public async Task DocumentationFootnotesPreserveRepeatedUsesAndAvoidHeadingIdCollisions()
    {
        const string body = "# fn-1\n\nSee[^n] twice[^n] and missing[^missing].\n\n"
            + "[^n]: Note *body* with [guide](guide.html).\n\n[^n]: Duplicate text.\n";
        var compiler = Compiler(MarkdownOptions.DocumentationProfile);
        var result = compiler.Analyze(body, new DocumentSource("guide.md", 17) { BodyStartLine = 4 });
        await Assert.That(result.Html).Contains("id=\"fn-1\"");
        await Assert.That(result.Html).Contains("href=\"#fn-1-2\" role=\"doc-noteref\">1</a>");
        await Assert.That(result.Html).Contains("href=\"#fnref-1-2\" role=\"doc-backlink\"");
        await Assert.That(result.Html).Contains("missing[^missing]");
        await Assert.That(result.Html).Contains("Note <em>body</em> with <a href=\"guide.html\">guide</a>");
        await Assert.That(result.Html).DoesNotContain("Duplicate text.");
        await Assert.That(result.Semantics!.Diagnostics.Select(static item => item.Id))
            .IsEquivalentTo(new[] { "LMD030", "LMD031" });
        var unresolved = result.Semantics.Diagnostics.Single(static item => item.Id == "LMD031");
        await Assert.That(unresolved.Location!.Line).IsEqualTo(6);
        var references = result.Semantics.Links.Where(static item => item.RawText == "[^n]").ToArray();
        await Assert.That(references.Length).IsEqualTo(2);
        await Assert.That(references.All(static item => item.Url == "#fn-1-2")).IsTrue();
        await Assert.That(references[0].Span.Start).IsEqualTo(17 + body.IndexOf("[^n]", StringComparison.Ordinal));
        var ids = Regex.Matches(result.Html, " id=\"([^\"]+)\"").Select(static match => match.Groups[1].Value).ToArray();
        await Assert.That(ids.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(ids.Length);
        foreach (Match link in Regex.Matches(result.Html, " href=\"#([^\"]+)\""))
            await Assert.That(ids.Contains(link.Groups[1].Value, StringComparer.Ordinal)).IsTrue();
        await Assert.That(compiler.ParseCount).IsEqualTo(1);
    }

    [Test]
    public async Task ExplicitProfilesKeepLegacyOutputAndUseNativeCommonMarkInterpretation()
    {
        const string body = "---\n\n# Guide $x$\n\n~~strike~~ and [^n].\n\n[^n]: Note\n\n<div>\n# Inside HTML\n</div>\n";
        var legacy = new LithoMarkdownCompiler().Analyze(body);
        var explicitLegacy = Compiler(MarkdownOptions.LegacyProfile).Analyze(body);
        var commonMark = Compiler(MarkdownOptions.CommonMarkProfile).Analyze(body);
        await Assert.That(explicitLegacy.Html).IsEqualTo(legacy.Html);
        await Assert.That(legacy.Html).Contains("<del>strike</del>");
        await Assert.That(legacy.Html).Contains("[^n]");
        await Assert.That(commonMark.Html).Contains("~~strike~~");
        await Assert.That(commonMark.Html).Contains("&lt;div&gt;");
        await Assert.That(commonMark.Html).DoesNotContain("<div>");
        await Assert.That(commonMark.Html).DoesNotContain("id=\"inside-html\"");
        await Assert.That(commonMark.Html).Contains("<hr />");
        var native = MdParser.AnalyzeWithBlocks(body, MdAnalysisParts.Headings, "test", "body", null,
            new MdOptions(MdInputKind.Body, profileId: MarkdownOptions.CommonMarkProfile), CancellationToken.None);
        await Assert.That(commonMark.Semantics!.Headings.Select(static heading => (heading.Text, heading.Id)))
            .IsEquivalentTo(native.Headings!.Select(static heading => (heading.Text.Text!, (string?)heading.Anchor)));
        await Assert.That(Compiler(MarkdownOptions.DocumentationProfile).Fingerprint == Compiler(MarkdownOptions.CommonMarkProfile).Fingerprint).IsFalse();
        await Assert.That(() => Compiler("unknown/1")).Throws<ArgumentException>();
    }

    [Test]
    public async Task EditorProfileChangesInvalidateSameRevisionAndKeepOriginalLocations()
    {
        const string body = "---\r\n\r\n# Guide\r\n\r\nText[^n].\r\n\r\n[^n]: Note\r\n";
        const string header = "\uFEFF---\r\ntitle: Test\r\n---\r\n";
        var raw = header + body;
        await using var workspace = new DocumentWorkspace();
        var legacy = await workspace.InspectVersionedAsync("guide.md", raw, 1, 1);
        var docs = await workspace.InspectVersionedAsync("guide.md", raw, 1, 1,
            new DocumentInspectionOptions { Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.DocumentationProfile } });
        var commonMark = DocumentInspection.Inspect("guide.md", raw,
            new DocumentInspectionOptions { Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.CommonMarkProfile } });
        await Assert.That(legacy.Diagnostics.Any(static item => item.Id == "LIT001")).IsTrue();
        await Assert.That(docs.Diagnostics.Any(static item => item.Id == "LIT001")).IsFalse();
        await Assert.That(docs.Links.Single(static item => item.RawText == "[^n]").Url).IsEqualTo("#fn-1");
        await Assert.That(commonMark.FrontMatter["title"]).IsEqualTo("Test");
        await Assert.That(commonMark.Headings.Single().Text).IsEqualTo("Guide");
        await Assert.That(commonMark.Headings.Single().Location!.Line).IsEqualTo(6);
        await Assert.That(commonMark.Diagnostics.Any(static item => item.Id is "LSM001" or "LSM002")).IsFalse();
        await Assert.That(ReferenceEquals(legacy, docs)).IsFalse();
    }

    [Test]
    public async Task ChangingSiteProfileRegeneratesCachedArticleWithFootnotes()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var timestamp = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        var post = new MarkdownPost("guide.md", "guide",
            new PostFrontMatter { Title = "Guide", Date = timestamp, Summary = "Guide." },
            "# Guide\n\nText[^n].\n\n[^n]: Note.\n", "posts/guide.html");
        var settings = new SiteSettings { Title = "Profiles", BaseUrl = "https://example.test/docs/" };
        var customization = new SiteCustomization { Template = new BlogSiteTemplate(), FaviconSourceDirectory = Path.Combine(workspace.Root, "no-favicons") };
        var options = new SiteGenerationOptions { BuildTimestamp = timestamp };
        var generator = new SiteGenerator();
        var first = await generator.GenerateWithOptionsAsync(settings, [post], output, true,
            customization, options, CancellationToken.None);
        var path = Path.Combine(output, "posts", "guide.html");
        await Assert.That(await File.ReadAllTextAsync(path)).Contains("Text[^n]");
        var docs = options with
        {
            PreviousBuildPlan = first.BuildPlan,
            Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.DocumentationProfile },
        };
        var second = await generator.GenerateWithOptionsAsync(settings, [post], output, false,
            customization, docs, CancellationToken.None);
        await Assert.That(await File.ReadAllTextAsync(path)).Contains("role=\"doc-noteref\"");
        await generator.GenerateWithOptionsAsync(settings, [post], output, false,
            customization, options with { PreviousBuildPlan = second.BuildPlan }, CancellationToken.None);
        await Assert.That(await File.ReadAllTextAsync(path)).Contains("Text[^n]");
        await Assert.That(await File.ReadAllTextAsync(path)).DoesNotContain("role=\"doc-noteref\"");
    }

    private static LithoMarkdownCompiler Compiler(string profile) => new(
        MarkdownCompilerOptions.Default with { Markdown = new MarkdownOptions { ProfileId = profile } });
}
