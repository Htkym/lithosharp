using LithoSharp.AgentReady;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

[NotInParallel]
public sealed class AgentReadyValidationReuseTests
{
    private const string BaseUrl = "https://example.test/project/";
    private static readonly SiteRoute HtmlRoute = SiteRoute.ForFile("guide.html", BaseUrl);
    private static readonly SiteRoute MarkdownRoute = SiteRoute.ForFile("guide.md", BaseUrl);
    private static readonly SiteRoute LlmsRoute = SiteRoute.ForFile("llms.txt", BaseUrl);
    private static readonly AgentReadyArtifactExpectation Expected = new(HtmlRoute, new()
    {
        SiteUrl = BaseUrl,
        CanonicalUrl = BaseUrl + "guide.html",
        Language = "ja",
        MarkdownUrl = BaseUrl + "guide.md",
        LlmsUrl = BaseUrl + "llms.txt",
    }, new() { Mode = AgentReadyStructuredDataMode.UserManaged }, MarkdownRoute, LlmsRoute);
    private const string Html = """
        <!doctype html>
        <html lang="ja"><head>
        <base href="https://example.test/project/">
        <link rel="canonical" href="https://example.test/project/guide.html">
        <link rel="alternate" type="text/markdown" href="guide.md">
        <link rel="describedby" href="llms.txt">
        <script type="application/ld+json">not json</script>
        </head><body>本文</body></html>
        """;
    private static Dictionary<string, SiteRoute> Artifacts() => new(StringComparer.Ordinal)
    {
        [HtmlRoute.RelativeOutputPath] = HtmlRoute,
        [MarkdownRoute.RelativeOutputPath] = MarkdownRoute,
        [LlmsRoute.RelativeOutputPath] = LlmsRoute,
    };

    [Test]
    public async Task PipelineWithoutQualityReusesFactsAndHonorsCacheDisable()
    {
        using var workspace = new ValidationWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var timestamp = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        var site = new SiteSettings { Title = "Public site", BaseUrl = BaseUrl, Language = "ja", TimeZone = "UTC" };
        var customization = new SiteCustomization
        {
            GenerateLlmsTxt = true,
            AgentReady = new() { Enabled = true, StructuredData = new() { Mode = AgentReadyStructuredDataMode.Automatic } },
        };
        var options = new SiteGenerationOptions { BuildTimestamp = timestamp, MaxDegreeOfParallelism = 1 };
        var post = new MarkdownPost(Path.Combine(workspace.Root, "guide.md"), "guide",
            new() { Title = "Guide", Date = timestamp }, "Public body", "guide.html");
        var generator = new SiteGenerator();
        var cold = await generator.GenerateWithOptionsAsync(site, [post], output, true, customization, options, default);
        var original = await File.ReadAllBytesAsync(Path.Combine(output, "guide.html"));
        var warm = await generator.GenerateWithOptionsAsync(site, [post], output, false, customization, options, default);
        await Assert.That(cold.AgentReadyValidationFacts.CacheParseCount > 0).IsTrue();
        await Assert.That(warm.AgentReadyValidationFacts.DirectParseCount).IsEqualTo(0);
        await Assert.That(warm.AgentReadyValidationFacts.CacheParseCount).IsEqualTo(0);
        await Assert.That(warm.AgentReadyValidationFacts.CacheHitCount).IsEqualTo(cold.AgentReadyValidationFacts.CacheParseCount);
        var disabled = await generator.GenerateWithOptionsAsync(site, [post], output, false, customization,
            options with { HtmlFactsCacheEnabled = false }, default);
        await Assert.That(disabled.AgentReadyValidationFacts.DirectParseCount).IsEqualTo(cold.AgentReadyValidationFacts.CacheParseCount);
        await Assert.That(disabled.AgentReadyValidationFacts.CacheParseCount).IsEqualTo(0);
        await Assert.That(disabled.AgentReadyValidationFacts.CacheHitCount).IsEqualTo(0);
        await Assert.That((await File.ReadAllBytesAsync(Path.Combine(output, "guide.html"))).SequenceEqual(original)).IsTrue();
        await Assert.That(disabled.QualityReport.Diagnostics.SequenceEqual(warm.QualityReport.Diagnostics)).IsTrue();
    }

    [Test]
    public async Task WarmFactsStillReadOutputAndRevalidateCurrentArtifactGraphAndPlan()
    {
        using var workspace = new ValidationWorkspace();
        using (var cold = new SiteHtmlFactsCache(workspace.Root))
        {
            await Assert.That((await Validate(Html, Artifacts(), cold)).Count).IsEqualTo(0);
            await Assert.That(cold.ParseCount).IsEqualTo(1);
            await cold.PublishAsync();
        }
        using var warm = new SiteHtmlFactsCache(workspace.Root);
        var reads = 0;
        var directParses = 0;
        var artifacts = Artifacts();
        var valid = await AgentReadyArtifactValidator.ValidateAsync([Expected], artifacts,
            (_, _) =>
            {
                reads++;
                return Task.FromResult(Html);
            }, default,
            warm.GetAsync, validateJson: false, onDirectParse: () => directParses++);
        await Assert.That(valid.Count).IsEqualTo(0);
        await Assert.That(reads).IsEqualTo(1);
        await Assert.That(directParses).IsEqualTo(0);
        artifacts.Remove(MarkdownRoute.RelativeOutputPath);
        var missing = await Validate(Html, artifacts, warm);
        await Assert.That(missing.Single().Id).IsEqualTo("LSQ014");
        await Assert.That(missing.Single().Location?.Line).IsEqualTo(5);
        var changedPlan = Expected with { Head = Expected.Head with { Language = "en" } };
        var changed = await AgentReadyArtifactValidator.ValidateAsync([changedPlan], Artifacts(),
            (_, _) => Task.FromResult(Html), default, warm.GetAsync, validateJson: false);
        await Assert.That(changed.Single().Id).IsEqualTo("LSQ014");
        await Assert.That(warm.ParseCount).IsEqualTo(0);
        await Assert.That(warm.HitCount).IsEqualTo(3);
    }

    [Test]
    public async Task ChangedOutputMissesCacheAndStillChecksResolvedDiscoveryUris()
    {
        using var workspace = new ValidationWorkspace();
        using (var cold = new SiteHtmlFactsCache(workspace.Root))
        {
            await Validate(Html, Artifacts(), cold);
            await cold.PublishAsync();
        }
        using var warm = new SiteHtmlFactsCache(workspace.Root);
        var changed = Html.Replace("<base href=\"https://example.test/project/\">", "<base href=\"https://other.test/\">", StringComparison.Ordinal);
        var diagnostics = await Validate(changed, Artifacts(), warm);
        await Assert.That(diagnostics.Count).IsEqualTo(2);
        await Assert.That(diagnostics.All(diagnostic => diagnostic.Id == "LSQ014")).IsTrue();
        await Assert.That(warm.ParseCount).IsEqualTo(1);
        await Assert.That(warm.HitCount).IsEqualTo(0);
    }

    [Test]
    public async Task JsonChecksRemainDefaultAndMandatoryOnlyPathMatchesDiagnostics()
    {
        var changed = Html.Replace("lang=\"ja\"", "lang=\"en\"", StringComparison.Ordinal);
        var directParses = 0;
        var full = await AgentReadyArtifactValidator.ValidateAsync([Expected], Artifacts(),
            (_, _) => Task.FromResult(changed), default, onDirectParse: () => directParses++);
        var mandatory = await Validate(changed, Artifacts());
        await Assert.That(full.Any(diagnostic => diagnostic.Id == "LSQ013")).IsTrue();
        await Assert.That(System.Text.Json.JsonSerializer.Serialize(mandatory)).IsEqualTo(System.Text.Json.JsonSerializer.Serialize(full.Where(diagnostic => diagnostic.Id != "LSQ013").ToArray()));
        await Assert.That(directParses).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationAfterArtifactReadDoesNotConsultFactsProvider()
    {
        using var canceled = new CancellationTokenSource();
        var consulted = false;
        await Assert.ThrowsAsync<OperationCanceledException>(() => AgentReadyArtifactValidator.ValidateAsync(
            [Expected], Artifacts(), (_, _) =>
            {
                canceled.Cancel();
                return Task.FromResult(Html);
            }, canceled.Token,
            (_, _) =>
            {
                consulted = true;
                return Task.FromResult(QualityHtmlFacts.Parse(Html, default));
            }, validateJson: false));
        await Assert.That(consulted).IsFalse();
    }

    private static Task<IReadOnlyList<SiteDiagnostic>> Validate(string html, IReadOnlyDictionary<string, SiteRoute> artifacts,
        SiteHtmlFactsCache? cache = null) => AgentReadyArtifactValidator.ValidateAsync([Expected], artifacts,
            (_, _) => Task.FromResult(html), default, cache is not null ? cache.GetAsync : null, validateJson: false);
    private sealed class ValidationWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : AppContext.BaseDirectory, "lithosharp-agent-validation-" + Guid.NewGuid().ToString("N"));
        public ValidationWorkspace() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
