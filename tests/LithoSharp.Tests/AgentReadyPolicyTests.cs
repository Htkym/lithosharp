using LithoSharp.AgentReady;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class AgentReadyPolicyTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task DefaultsDoNotEnableTheFeature()
    {
        var decision = AgentReadyPolicy.Evaluate(new(), new(), false, true, Timestamp, "Production");
        await Assert.That(decision.ShouldProvideMarkdown).IsFalse();
        await Assert.That(decision.RequiresMarkdown).IsFalse();
        await Assert.That(decision.InitialStatus).IsEqualTo(PublicMarkdownStatus.NotRequested);
        await Assert.That(new SiteCustomization().AgentReady.Enabled).IsFalse();
    }

    [Test]
    [Arguments(false, false, PublicMarkdownMode.Auto, AgentIndexMode.Auto, true, true, true)]
    [Arguments(true, false, PublicMarkdownMode.Auto, AgentIndexMode.Auto, true, false, false)]
    [Arguments(false, true, PublicMarkdownMode.Auto, AgentIndexMode.Auto, true, false, false)]
    [Arguments(true, false, PublicMarkdownMode.Required, AgentIndexMode.Auto, true, true, false)]
    [Arguments(false, true, PublicMarkdownMode.Required, AgentIndexMode.Include, true, true, true)]
    [Arguments(false, false, PublicMarkdownMode.Disabled, AgentIndexMode.Auto, true, false, true)]
    [Arguments(false, false, PublicMarkdownMode.Required, AgentIndexMode.Include, false, true, false)]
    [Arguments(false, false, PublicMarkdownMode.Auto, AgentIndexMode.Exclude, true, true, false)]
    public async Task ProvisionAndIndexSelectionAreIndependent(bool noIndex, bool unlisted, PublicMarkdownMode mode,
        AgentIndexMode indexing, bool derivedLlms, bool provides, bool listed)
    {
        var metadata = new PageMetadata { NoIndex = noIndex, AgentReady = new() { MarkdownMode = mode, Indexing = indexing } };
        var decision = AgentReadyPolicy.Evaluate(new() { Enabled = true }, metadata, unlisted, derivedLlms, Timestamp, "Production");
        await Assert.That(decision.ShouldProvideMarkdown).IsEqualTo(provides);
        await Assert.That(decision.ShouldIndex).IsEqualTo(listed);
    }

    [Test]
    public async Task PublicationAndDisabledOverrideRequiredOrReplacement()
    {
        var draft = new PageMetadata(draft: true) { AgentReady = new() { MarkdownMode = PublicMarkdownMode.Required } };
        var excluded = AgentReadyPolicy.Evaluate(new() { Enabled = true }, draft, false, true, Timestamp, "Production");
        await Assert.That(excluded.RequiresMarkdown).IsFalse();
        await Assert.That(excluded.ShouldProvideMarkdown).IsFalse();
        var disabled = new PageMetadata { AgentReady = new() { MarkdownMode = PublicMarkdownMode.Disabled, MarkdownReplacement = "Body" } };
        await Assert.That(AgentReadyPolicy.Evaluate(new() { Enabled = true }, disabled, false, true, Timestamp, "Production").ShouldProvideMarkdown).IsFalse();
        var explicitBody = new PageMetadata { NoIndex = true, AgentReady = new() { MarkdownReplacement = string.Empty } };
        var replacement = AgentReadyPolicy.Evaluate(new() { Enabled = true }, explicitBody, true, true, Timestamp, "Production");
        await Assert.That(replacement.ShouldProvideMarkdown).IsTrue();
        await Assert.That(replacement.ShouldIndex).IsFalse();
        await Assert.That(replacement.InitialStatus).IsEqualTo(PublicMarkdownStatus.ExplicitReplacement);
    }

    [Test]
    [Arguments("index.html", "index.md", "/product/index.md")]
    [Arguments("guide/start/index.html", "guide/start/index.md", "/product/guide/start/index.md")]
    [Arguments("v2/ja/100%25.html", "v2/ja/100%.md", "/product/v2/ja/100%25.md")]
    [Arguments("ja/概要.html", "ja/概要.md", "/product/ja/%E6%A6%82%E8%A6%81.md")]
    public async Task RouteUsesFinalHtmlOutputWithoutLosingEscapes(string input, string output, string url)
    {
        const string baseUrl = "https://example.test/product/";
        var route = AgentReadyPolicy.MarkdownRoute(SiteRoute.ForFile(input, baseUrl), new(), baseUrl)!;
        await Assert.That(route.RelativeOutputPath).IsEqualTo(output);
        await Assert.That(route.PublicPath).IsEqualTo(url);
    }

    [Test]
    public async Task DirectoryIndexAndExplicitNonHtmlRouteAreDistinct()
    {
        const string baseUrl = "https://example.test/product/";
        var html = SiteRoute.ForDirectoryIndex("guide/start", baseUrl);
        await Assert.That(AgentReadyPolicy.MarkdownRoute(html, new(), baseUrl)!.RelativeOutputPath).IsEqualTo("guide/start/index.md");
        var other = SiteRoute.ForFile("feed.xml", baseUrl);
        await Assert.That(AgentReadyPolicy.MarkdownRoute(other, new(), baseUrl)).IsNull();
        await Assert.That(AgentReadyPolicy.MarkdownRoute(other, new() { MarkdownRoute = SiteRoute.ForFile("feed.md") }, baseUrl)!.PublicPath)
            .IsEqualTo("/product/feed.md");
    }
}
