using System.Security.Cryptography;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Diagnostics;
using LithoSharp.HtmlParsing;
using LithoSharp.Quality;

namespace LithoSharp.Tests;

public sealed class RawSharedFactsTests
{
    [Test]
    public async Task DeclaredRawDemandSharesInputsAndSeparatesCancellationFaultsAndPhaseLifetime()
    {
        const string html = "<p data-x='A&amp;B'>Body</p>";
        await using var none = new BuildHtmlFacts(default);
        await Assert.That(async () => await none.GetRawAsync(html, default)).Throws<InvalidOperationException>();
        await Assert.That(none.Counters.ParseCount).IsEqualTo(0);

        await using var owner = new BuildHtmlFacts(default, rawDemand: HtmlRawFactsDemand.Search | HtmlRawFactsDemand.Quality);
        var first = await owner.GetAsync(html, default);
        var materials = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => owner.GetRawAsync(html, default)));
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(1);
        await Assert.That(materials.All(m => ReferenceEquals(m, first.Facts.RawAttributes))).IsTrue();
        await Assert.That(materials[0].Elements.Single(e => e.Name == "p").Attributes.Single().Value.Raw).IsEqualTo("A&amp;B");
        using var consumer = new CancellationTokenSource();
        var canceledView = await owner.GetAsync(html, consumer.Token);
        consumer.Cancel();
        await Assert.That(() => canceledView.Facts.RawAttributes).Throws<OperationCanceledException>();
        await Assert.That(first.Facts.RawAttributes.Elements.Count > 0).IsTrue();
        using var preCanceled = new CancellationTokenSource();
        preCanceled.Cancel();
        await Assert.That(async () => await owner.GetRawAsync(html, preCanceled.Token)).Throws<OperationCanceledException>();
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(1);

        await owner.GetRawAsync(html, default, new(Scripting: true));
        await owner.GetRawAsync(html, default, new(Fragment: new("div")));
        await owner.GetRawAsync(html, default, new(Fragment: new("div")));
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(3);
        var invalid = new HtmlTreeOptions(Fragment: new("div", "urn:invalid"));
        for (var i = 0; i < 2; i++)
            await Assert.That(async () => await owner.GetRawAsync(html, default, invalid)).Throws<ArgumentException>();
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(4);
        const string partial = "<template shadowrootmode=open><p>X</p></template>";
        for (var i = 0; i < 2; i++)
            await Assert.That(async () => await owner.GetRawAsync(partial, default)).Throws<HtmlFactsIncompleteException>();
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(5);
        await owner.BeginQualityAsync();
        await owner.GetQualityAsync(html, default);
        var qualityRaw = await owner.GetQualityRawAsync(html, default);
        await Assert.That(ReferenceEquals(qualityRaw, materials[0])).IsTrue();
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(5);
        await owner.ReleaseAsync();
        await Assert.That(owner.Counters.EntryCount).IsEqualTo(0);
        await Assert.That(owner.Counters.RetainedBytes).IsEqualTo(0L);
        await Assert.That(async () => await owner.GetQualityRawAsync(html, default)).Throws<InvalidOperationException>();
        await owner.DisposeAsync();
        await Assert.That(materials[0].Elements.Count > 0).IsTrue();
        await Assert.That(() => first.Facts.RawAttributes).Throws<OperationCanceledException>();

        await using var searchOnly = new BuildHtmlFacts(default, rawDemand: HtmlRawFactsDemand.Search);
        var searchRaw = await searchOnly.GetRawAsync(html, default);
        var retained = searchOnly.Counters.RetainedBytes;
        await searchOnly.BeginQualityAsync();
        var quality = await searchOnly.GetQualityAsync(html, default);
        await Assert.That(quality.Facts.Elements.Single(e => e.Name == "p").Attributes.Single().Value).IsEqualTo("A&B");
        await Assert.That(searchOnly.Counters.ParseCount).IsEqualTo(1);
        await Assert.That(retained - searchOnly.Counters.RetainedBytes >= searchRaw.RetentionCharge).IsTrue();
        await Assert.That(searchRaw.Elements.Count > 0).IsTrue();
        await Assert.That(async () => await searchOnly.GetQualityRawAsync(html, default)).Throws<InvalidOperationException>();

        using var buildCancellation = new CancellationTokenSource();
        await using var canceledOwner = new BuildHtmlFacts(buildCancellation.Token, rawDemand: HtmlRawFactsDemand.Search);
        await canceledOwner.GetRawAsync(html, default);
        buildCancellation.Cancel();
        await Assert.That(async () => await canceledOwner.GetRawAsync(html, default)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task RequiredRawBypassesDecodedCacheAndIncompleteInputKeepsPublishedBytes()
    {
        using var workspace = new TemporaryWorkspace();
        const string url = "https://example.test/sub/";
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator();
        var site = new SiteSettings { Title = "Home", Description = "Description", BaseUrl = url, TimeZone = "UTC" };
        var html = "<!doctype html><title>Home</title><link rel=canonical href='" + url + "'>"
            + "<meta name=description content=Description><meta property=og:title content=Home>"
            + "<meta property=og:description content=Description><meta property=og:url content='" + url + "'>"
            + "<h2 id=target>Body &amp; text</h2>";
        var template = new TextTemplate(html);
        var options = new SiteGenerationOptions { BuildTimestamp = DateTimeOffset.UnixEpoch, Quality = new(checkOrphans: false) };
        var first = await generator.GenerateWithOptionsAsync(site, [], output, true, new() { Template = template }, options, default);
        var original = Snapshot(output);
        var warm = await generator.GenerateWithOptionsAsync(site, [], output, false, new() { Template = template }, options, default);
        await Assert.That(first.QualityReport.HtmlParseCount > 0).IsTrue();
        await Assert.That(warm.QualityReport.HtmlParseCount).IsEqualTo(0);
        await Assert.That(warm.QualityReport.HtmlFactsCacheHitCount > 0).IsTrue();
        var rawOptions = options with { RawHtmlFactsDemand = HtmlRawFactsDemand.Quality };
        var raw = await generator.GenerateWithOptionsAsync(site, [], output, false, new() { Template = template }, rawOptions, default);
        await Assert.That(raw.QualityReport.HtmlFactsCacheHitCount).IsEqualTo(0);
        await Assert.That(raw.HtmlFactsSharing.ParseCount > 0).IsTrue();
        await Assert.That(raw.HtmlFactsSharing.EntryCount).IsEqualTo(0);
        await Assert.That(raw.HtmlFactsSharing.RetainedBytes).IsEqualTo(0L);
        await Assert.That(Snapshot(output)).IsEquivalentTo(original);
        await Assert.That(raw.QualityReport.Format(SiteDiagnosticFormat.Json)).IsEqualTo(warm.QualityReport.Format(SiteDiagnosticFormat.Json));
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(site, [], output, true,
            new() { Template = new TextTemplate(html + "<template shadowrootmode=open><p>X</p></template>") },
            rawOptions, default)).Throws<HtmlFactsIncompleteException>();
        await Assert.That(Snapshot(output)).IsEquivalentTo(original);
    }

    private static string[] Snapshot(string output) => Directory.GetFiles(output, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(output, path) + ":"
            + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class TextTemplate(string html) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult([new SiteTemplateFile { RelativePath = "index.html", Content = html }]));
    }
}
