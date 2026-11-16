using System.Text.Json;
using LithoSharp.AgentReady;
using LithoSharp.HtmlParsing;
using LithoSharp.Quality;
using LithoSharp.Routing;
using LithoSharp.Content;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class AgentReadyMetadataTests
{
    private static readonly AgentReadyStructuredDataOptions Automatic = new() { Mode = AgentReadyStructuredDataMode.Automatic };
    private static AgentReadyHeadData Data => new()
    {
        SiteTitle = "Example",
        SiteUrl = "https://example.test/project/",
        SiteLanguage = "en",
        CanonicalUrl = "https://example.test/project/guide.html",
        Title = "Guide",
        Description = "Published description",
        Language = "ja",
    };

    [Test]
    public async Task Defaults_DoNotGenerateStructuredDataOrRobots()
    {
        await Assert.That(AgentReadyMetadata.RenderHead(Data, new()).ToHtmlString()).IsEqualTo(string.Empty);
        await Assert.That(AgentReadyRobots.Build(Data.SiteUrl, null, new())).IsNull();
        await Assert.That(AgentReadyRobots.Build(Data.SiteUrl, null, new() { Mode = AgentReadyRobotsMode.External })).IsNull();
    }

    [Test]
    public async Task Automatic_UsesOnlyExplicitPublicFactsAndSafeScriptEncoding()
    {
        var data = Data with { Title = "</script><script>alert('x')</script> & \"title\"", Description = "Line\nnext" };
        var html = AgentReadyMetadata.RenderHead(data, Automatic).ToHtmlString();
        var facts = HtmlFacts.Parse(html);
        await Assert.That(facts.Elements.Count(element => element.Name == "script")).IsEqualTo(1);
        using var json = JsonDocument.Parse(facts.TextContent(facts.Elements.Single(element => element.Name == "script").Id));
        var page = json.RootElement.GetProperty("@graph")[1];
        await Assert.That(page.GetProperty("@type").GetString()).IsEqualTo("WebPage");
        await Assert.That(page.GetProperty("name").GetString()).IsEqualTo(data.Title);
        await Assert.That(page.GetProperty("url").GetString()).IsEqualTo(data.CanonicalUrl);
        await Assert.That(page.GetProperty("inLanguage").GetString()).IsEqualTo("ja");
        await Assert.That(json.RootElement.GetProperty("@graph")[0].GetProperty("inLanguage").GetString()).IsEqualTo("en");
        await Assert.That(page.TryGetProperty("author", out _)).IsFalse();
        await Assert.That(page.TryGetProperty("datePublished", out _)).IsFalse();
        await Assert.That(page.TryGetProperty("dateModified", out _)).IsFalse();
        await Assert.That(page.TryGetProperty("image", out _)).IsFalse();
        await Assert.That(html.Contains("<script>alert", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task ExplicitArticleFacts_AreSerializedWithoutBuildTimeOrInventedIdentity()
    {
        var published = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(9));
        var data = Data with
        {
            Page = new()
            {
                Kind = AgentReadyPageKind.BlogPosting,
                Published = published,
                Modified = published.AddDays(1),
                ImageUrl = "https://example.test/image.png",
                Author = new() { Name = "Author" },
                Publisher = new() { Kind = AgentReadyIdentityKind.Organization, Name = "Publisher", Url = "https://example.test/" },
            },
        };
        using var json = JsonDocument.Parse(AgentReadyMetadata.BuildJson(data, Automatic));
        var page = json.RootElement.GetProperty("@graph")[1];
        await Assert.That(page.GetProperty("@type").GetString()).IsEqualTo("BlogPosting");
        await Assert.That(page.GetProperty("datePublished").GetString()).IsEqualTo(published.ToString("O"));
        await Assert.That(page.GetProperty("author").GetProperty("@type").GetString()).IsEqualTo("Person");
        await Assert.That(page.GetProperty("author").TryGetProperty("sameAs", out _)).IsFalse();
        await Assert.That(page.GetProperty("publisher").GetProperty("@type").GetString()).IsEqualTo("Organization");
    }

    [Test]
    public async Task Discovery_UsesRegisteredUrlsAndEscapesHtmlAttributes()
    {
        var data = Data with { MarkdownUrl = "https://example.test/project/guide.md?a=1&b=2", LlmsUrl = "https://example.test/project/llms.txt" };
        var html = AgentReadyMetadata.RenderHead(data, new()).ToHtmlString();
        await Assert.That(html.Contains("&amp;b=2", StringComparison.Ordinal)).IsTrue();
        var facts = HtmlFacts.Parse(html);
        await Assert.That(HtmlFacts.Attribute(facts.Elements.First(element => HtmlFacts.Attribute(element, "rel") == "alternate"), "href")).IsEqualTo(data.MarkdownUrl);
        await Assert.That(HtmlFacts.Attribute(facts.Elements.First(element => HtmlFacts.Attribute(element, "rel") == "describedby"), "href")).IsEqualTo(data.LlmsUrl);
    }

    [Test]
    public async Task UserManagedHead_DoesNotReceiveAnAutomaticDuplicateAndReportsOwnershipConflict()
    {
        var custom = Html.UnsafeRaw("<script type='application/ld+json'>{\"@type\":\"WebPage\",\"url\":\"https://example.test/project/guide.html\"}</script>");
        var data = Data with { ExistingHead = custom };
        var additions = AgentReadyMetadata.RenderHead(data, Automatic).ToHtmlString();
        await Assert.That(additions.Contains("application/ld+json", StringComparison.Ordinal)).IsFalse();
        var diagnostics = await Validate(data, Automatic, custom.ToHtmlString() + additions);
        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "LSQ013")).IsTrue();
        var managed = new AgentReadyStructuredDataOptions { Mode = AgentReadyStructuredDataMode.UserManaged };
        diagnostics = await Validate(data, managed, custom.ToHtmlString());
        await Assert.That(diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("{\"@type\":\"WebPage\",\"url\":\"https://example.test/wrong.html\",\"url\":\"https://example.test/project/guide.html\"}")]
    [Arguments("{\"@type\":\"WebPage\",\"url\":\"https://example.test/wrong.html\"}")]
    [Arguments("[{\"@type\":\"WebPage\",\"url\":\"https://example.test/project/guide.html\"},{\"@type\":\"TechArticle\",\"url\":\"https://example.test/project/guide.html\"}]")]
    public async Task UserManagedJson_IsCheckedFromActualOutput(string value)
    {
        var options = new AgentReadyStructuredDataOptions { Mode = AgentReadyStructuredDataMode.UserManaged };
        var diagnostics = await Validate(Data, options, "<script type='application/ld+json'>" + value + "</script>");
        await Assert.That(diagnostics.All(diagnostic => diagnostic.Id == "LSQ013")).IsTrue();
        await Assert.That(diagnostics.Count > 0).IsTrue();
        await Assert.That(diagnostics.All(diagnostic => diagnostic.Location?.Line == 2)).IsTrue();
    }

    [Test]
    public async Task UserManagedGraph_CanDescribeRelatedPagesWithoutDuplicatingThisSubject()
    {
        var custom = "<script type='application/ld+json'>[{\"@type\":\"WebPage\",\"url\":\"https://example.test/project/guide.html\"},{\"@type\":\"WebPage\",\"url\":\"https://example.test/related.html\"}]</script>";
        var diagnostics = await Validate(Data, new() { Mode = AgentReadyStructuredDataMode.UserManaged }, custom);
        await Assert.That(diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AutomaticJson_RejectsAlteredFactsWithoutCrashingOnUnexpectedValueKinds()
    {
        var html = AgentReadyMetadata.RenderHead(Data, Automatic).ToHtmlString()
            .Replace("\"inLanguage\":\"ja\"", "\"inLanguage\":123", StringComparison.Ordinal);
        var diagnostics = await Validate(Data, Automatic, html);
        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "LSQ013")).IsTrue();
    }

    [Test]
    public async Task ActualAlternateAndIndex_MustMatchThePlanAndArtifacts()
    {
        var data = Data with { MarkdownUrl = "https://example.test/project/guide.md", LlmsUrl = "https://example.test/project/llms.txt" };
        var html = AgentReadyMetadata.RenderHead(data, Automatic).ToHtmlString();
        var diagnostics = await Validate(data, Automatic, html, markdown: true, index: true);
        await Assert.That(diagnostics.Count).IsEqualTo(0);
        diagnostics = await Validate(data, Automatic, html, markdown: true, index: true, omitMarkdown: true);
        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "LSQ014")).IsTrue();
        diagnostics = await Validate(Data, new(), html);
        await Assert.That(diagnostics.Count(diagnostic => diagnostic.Id == "LSQ014")).IsEqualTo(2);
        diagnostics = await Validate(data, Automatic, string.Empty, markdown: true, index: true);
        await Assert.That(diagnostics.Count(diagnostic => diagnostic.Id == "LSQ014")).IsEqualTo(2);
    }

    [Test]
    public async Task LlmsEntry_PrefersOnlyAvailableMarkdownAndKeepsMetadataOnOneEscapedLine()
    {
        var entry = AgentReadyLlmsFormatting.Entry(new("https://example.test/a.html", "https://example.test/a.md?q=(a)&u=%2F", "A ] [ title\n# heading", "Description\n- [fake](https://bad.test/)"));
        await Assert.That(entry.Count(character => character == '\n')).IsEqualTo(1);
        await Assert.That(entry.StartsWith("- [A \\] \\[ title \\# heading](<https://example.test/a.md?q=(a)&#38;u=%2F>)", StringComparison.Ordinal)).IsTrue();
        await Assert.That(entry.Contains("Description \\- \\[fake\\]", StringComparison.Ordinal)).IsTrue();
        var fallback = AgentReadyLlmsFormatting.Entry(new("https://example.test/a.html", null, "A", null));
        await Assert.That(fallback).IsEqualTo("- [A](<https://example.test/a.html>)\n");
    }

    [Test]
    [Arguments(MarkdownOptions.CommonMarkProfile)]
    [Arguments(MarkdownOptions.DocumentationProfile)]
    public async Task LlmsEntry_PreservesEntityLikeUrlsAndPublicTitlesAfterSyntamarkParse(string profile)
    {
        const string url = "https://example.test/a.md?q=1&copy;=2&x=&amp;&escaped=%26copy%3B";
        const string title = "Guide &copy; &#x41; [public]";
        var entry = AgentReadyLlmsFormatting.Entry(new("https://example.test/a.html", url, title, "Use &copy; literally"));
        var analysis = MdParser.AnalyzeWithBlocks(entry, MdAnalysisParts.Links,
            "llms", "llms.txt", null, new MdOptions(MdInputKind.Body, profileId: profile), CancellationToken.None);
        var link = analysis.Links!.Single();
        await Assert.That(link.Target).IsEqualTo(url);
        await Assert.That(link.Label.Text).IsEqualTo(title);
        await Assert.That(entry.Contains("Use \\&copy; literally", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    [Arguments("")]
    [Arguments("{}")]
    [Arguments("[]")]
    [Arguments("123")]
    [Arguments("{\"@type\":\"UnknownType\",\"url\":\"https://example.test/project/guide.html\"}")]
    public async Task UserManagedJson_ReportsUnavailablePageEvidence(string value)
    {
        var head = value.Length == 0 ? string.Empty : "<script type='application/ld+json'>" + value + "</script>";
        var diagnostics = await Validate(Data, new() { Mode = AgentReadyStructuredDataMode.UserManaged }, head);
        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "LSQ013")).IsTrue();
    }

    [Test]
    public async Task Robots_RequiresOriginRootAndExplicitRulesAndExistingAbsoluteSitemap()
    {
        var options = new AgentReadyRobotsOptions
        {
            Mode = AgentReadyRobotsMode.Generated,
            Groups = [new() { UserAgents = ["*"], Disallow = ["/private/"] }],
        };
        await Assert.That(() => AgentReadyRobots.Build(Data.SiteUrl, null, options)).Throws<InvalidOperationException>();
        var robots = AgentReadyRobots.Build("https://example.test/", "https://example.test/sitemap.xml", options)!;
        await Assert.That(robots.Contains("Disallow: /private/\n", StringComparison.Ordinal)).IsTrue();
        await Assert.That(robots.Contains("Allow:", StringComparison.Ordinal)).IsFalse();
        var artifacts = new Dictionary<string, SiteRoute> { ["sitemap.xml"] = SiteRoute.ForFile("sitemap.xml", "https://example.test/") };
        await Assert.That(AgentReadyArtifactValidator.ValidateRobots("https://example.test/", robots, artifacts).Count).IsEqualTo(0);
        artifacts.Clear();
        await Assert.That(AgentReadyArtifactValidator.ValidateRobots("https://example.test/", robots, artifacts).Single().Id).IsEqualTo("LSQ015");
    }

    [Test]
    [Arguments("/private/\nAllow: /")]
    [Arguments("/private/ # comment")]
    [Arguments("relative/path")]
    public async Task Robots_RejectsDirectiveInjection(string path)
    {
        var options = new AgentReadyRobotsOptions { Mode = AgentReadyRobotsMode.Generated, Groups = [new() { Disallow = [path] }] };
        await Assert.That(() => AgentReadyRobots.Build("https://example.test/", null, options)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("javascript:alert(1)")]
    [Arguments("https://user:password@example.test/")]
    [Arguments("https://example.test/\r\nX: x")]
    public async Task PublicUrls_RejectCredentialsControlsAndNonHttpSchemes(string url)
    {
        await Assert.That(() => AgentReadyMetadata.RenderHead(Data with { MarkdownUrl = url }, new())).Throws<ArgumentException>();
    }

    private static Task<IReadOnlyList<LithoSharp.Diagnostics.SiteDiagnostic>> Validate(
        AgentReadyHeadData data, AgentReadyStructuredDataOptions options, string head,
        bool markdown = false, bool index = false, bool omitMarkdown = false)
    {
        var htmlRoute = SiteRoute.ForFile("guide.html", Data.SiteUrl);
        var markdownRoute = markdown ? SiteRoute.ForFile("guide.md", Data.SiteUrl) : null;
        var indexRoute = index ? SiteRoute.ForFile("llms.txt", Data.SiteUrl) : null;
        var artifacts = new Dictionary<string, SiteRoute> { ["guide.html"] = htmlRoute };
        if (markdownRoute is not null && !omitMarkdown)
            artifacts["guide.md"] = markdownRoute;
        if (indexRoute is not null)
            artifacts["llms.txt"] = indexRoute;
        return AgentReadyArtifactValidator.ValidateAsync([new(htmlRoute, data, options, markdownRoute, indexRoute)], artifacts,
            (_, _) => Task.FromResult("<!doctype html>\n<html lang='ja'><head><link rel=canonical href='https://example.test/project/guide.html'>" + head + "</head><body></body></html>"), default);
    }
}
