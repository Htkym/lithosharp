using ParsedHtmlText = global::LithoSharp.HtmlParsing.HtmlText;
using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.HtmlParsing;
using LithoSharp.Quality;
using LithoSharp.Routing;
using LithoSharp.Search;

namespace LithoSharp.Tests;

public sealed class BuildHtmlFactsTests
{
    [Test]
    public async Task SharedInputsKeepBindingsCancellationAndPhaseLifetimeSeparate()
    {
        const string html = "<!doctype html><title>Home</title><link rel=canonical href='./'>"
            + "<nav><a href='pixel.png'>Navigation</a></nav><h2 id=target>Text</h2>"
            + "<img src='pixel.png'><img src='/sub/pixel.png'>";
        await using var owner = new BuildHtmlFacts(default);
        var requests = Enumerable.Range(0, 32).Select(_ => owner.GetAsync(html, default)).ToArray();
        var consumers = await Task.WhenAll(requests);
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(1);
        await Assert.That(consumers.Count(result => result.NewParse)).IsEqualTo(1);
        await Assert.That(consumers[0].Facts.SearchText).DoesNotContain("Navigation");
        var acquiredSections = consumers[0].Facts.SearchSections();
        await Assert.That(() => ((IList<SearchSection>)acquiredSections)[0] = acquiredSections[0])
            .Throws<NotSupportedException>();
        var a = consumers[0].Facts.ToQuality();
        var b = consumers[1].Facts.ToQuality();
        a.Elements.Single(element => element.Name == "a").Attributes[0] = new("href", null, "poison.png");
        await Assert.That(QualityHtmlFacts.Attribute(b.Elements.Single(element => element.Name == "a"), "href")).IsEqualTo("pixel.png");

        const string baseUrl = "https://example.test/sub/";
        var files = new[] { "index.html", "guide/index.html" };
        var assets = new HashSet<string>(StringComparer.Ordinal) { "pixel.png" };
        Dictionary<string, SiteRoute> Routes(string url) => files.ToDictionary(path => path,
            path => SiteRoute.ForDirectoryIndex(path[..^10], url))
            .Concat(new Dictionary<string, SiteRoute> { ["pixel.png"] = SiteRoute.ForFile("pixel.png", url) })
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Task<SiteQualityReport> Validate(string url) => SiteQualityValidator.ValidateAsync(url, files,
            (_, _) => Task.FromResult(html), Routes(url), assets, new Dictionary<string, SiteRoute>(),
            new(checkOrphans: false), default, sharedFacts: owner);
        var sub = await Validate(baseUrl);
        await Assert.That(sub.Diagnostics.Where(d => d.Id == "LSQ001").All(d => d.Location?.FilePath == "guide/index.html")).IsTrue();
        await Assert.That(sub.Diagnostics.Count(d => d.Id == "LSQ001")).IsEqualTo(2);
        var alt = await Validate("https://example.test/alt/");
        await Assert.That(alt.Diagnostics.Any(d => d.Id == "LSQ001" && d.Location?.FilePath == "index.html")).IsTrue();
        await Assert.That(sub.HtmlParseCount + alt.HtmlParseCount).IsEqualTo(0);
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(1);

        using var viewCancellation = new CancellationTokenSource();
        var view = await owner.GetAsync(html, viewCancellation.Token);
        viewCancellation.Cancel();
        await Assert.That(() => view.Facts.SearchSections()).Throws<OperationCanceledException>();
        await Assert.That(consumers[1].Facts.SearchText).Contains("Text");

        var before = owner.Counters.ParseCount;
        await owner.GetAsync("<p>\uD800</p>", default);
        await owner.GetAsync("<p>\uD801</p>", default);
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(before + 2); // UTF-8 replacement would conflate these inputs.
        before = owner.Counters.ParseCount;
        await owner.GetAsync("<p>x", default);
        await owner.GetAsync("<p>x", default, new(Scripting: true));
        await owner.GetAsync("<p>x", default, new(Fragment: new("div")));
        await owner.GetAsync("<p>x", default, new(Fragment: new("span")));
        await owner.GetAsync("<p>x", default, new(Fragment: new("div")));
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(before + 4);
        before = owner.Counters.ParseCount;
        await owner.GetAsync("<p>x", default, new(Fragment: new("div", OwnerDocumentMode: HtmlDocumentMode.Quirks)));
        await owner.GetAsync("<p>x", default, new(Fragment: new("div", HasExternalFormAncestor: true)));
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(before + 2);
        static ParsedHtmlText Mapped(string value) => new(value, new(0, value.Length),
            Array.AsReadOnly(new[] { new HtmlTextSegment(0, value.Length, new(0, value.Length)) }));
        var attributes = new List<HtmlAttribute> { new(Mapped("encoding"), Mapped("text/html")) };
        var context = new HtmlTreeOptions(Fragment: new("annotation-xml", HtmlNamespaces.MathMl, attributes));
        const string contextualSource = "<g xlink:href='pixel.png'></g>";
        var original = owner.GetAsync(contextualSource, default, context);
        attributes[0] = attributes[0] with { Value = Mapped("application/xml") };
        var originalFacts = (await original).Facts;
        await Assert.That(originalFacts.ToQuality().Elements.Single(element => element.Name == "g").Attributes.Single().Namespace).IsNull();
        before = owner.Counters.ParseCount;
        var changedContext = await owner.GetAsync(contextualSource, default, context);
        await Assert.That(changedContext.Facts.ToQuality().Elements.Single(element => element.Name == "g").Attributes.Single().Namespace).IsEqualTo(HtmlNamespaces.XLink);
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(before + 1);

        // The second parse queues behind real parser work; canceling its waiter must not cancel its survivor.
        before = owner.Counters.ParseCount;
        var busy = owner.GetAsync(string.Concat(Enumerable.Repeat("<p>busy</p>", 10_000)), default);
        using var waiterCancellation = new CancellationTokenSource();
        var waiter = owner.GetAsync("<h2 id=queued>Queued</h2>", waiterCancellation.Token);
        waiterCancellation.Cancel();
        HtmlConsumerFacts? canceledView = null;
        try
        {
            canceledView = (await waiter).Facts;
        }
        catch (OperationCanceledException) { }
        // Completion may win the cancellation race; its caller view must still observe cancellation.
        if (canceledView is not null)
            await Assert.That(() => canceledView.SearchText).Throws<OperationCanceledException>();
        var survivor = await owner.GetAsync("<h2 id=queued>Queued</h2>", default);
        await busy;
        await Assert.That(survivor.Facts.SearchSections().Single().Anchor).IsEqualTo("queued");
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(before + 2);

        before = owner.Counters.ParseCount;
        const string partial = "<template shadowrootmode=open><p>Unknown</p></template>";
        await Assert.That(async () => await owner.GetAsync(partial, default)).Throws<HtmlFactsIncompleteException>();
        await Assert.That(async () => await owner.GetAsync(partial, default)).Throws<HtmlFactsIncompleteException>();
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(before + 1); // Terminal failure, not an empty Complete or a retry.
        before = owner.Counters.ParseCount;
        var invalidContext = new HtmlTreeOptions(Fragment: new("div", "urn:unsupported"));
        await Assert.That(async () => await owner.GetAsync("<p>x", default, invalidContext)).Throws<ArgumentException>();
        await Assert.That(async () => await owner.GetAsync("<p>x", default, invalidContext)).Throws<ArgumentException>();
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(before + 1);

        using var workspace = new TemporaryWorkspace();
        var partition = Path.Combine(workspace.Root, "facts-cache");
        using (var cache = new SiteHtmlFactsCache(partition, owner))
        {
            var first = await cache.GetAsync(html, default);
            first.Anchors[0] = "poison";
            var next = await cache.GetAsync(html, default);
            await Assert.That(next.Anchors.Single()).IsEqualTo("target");
            await Assert.That(cache.ParseCount).IsEqualTo(0);
            await Assert.That(cache.HitCount).IsEqualTo(0);
            await cache.PublishAsync();
        }
        await using (var fresh = new BuildHtmlFacts(default))
        using (var disk = new SiteHtmlFactsCache(partition, fresh))
        {
            await disk.GetAsync(html, default);
            await Assert.That(disk.HitCount).IsEqualTo(1);
            await Assert.That(disk.ParseCount + fresh.Counters.ParseCount).IsEqualTo(0);
            await Assert.That(fresh.Counters.EntryCount).IsEqualTo(0); // A projection hit does not invent a tree.
        }

        // Former cache thresholds must not become product capacity limits. These inputs fit the old parser profile.
        await using (var many = new BuildHtmlFacts(default))
        {
            for (var i = 0; i < 4097; i++)
                await many.GetAsync($"<p>{i}</p>", default);
            await many.GetAsync("<p>0</p>", default);
            await Assert.That(many.Counters.ParseCount).IsEqualTo(4097);
            var bytesBeforeQuality = many.Counters.RetainedBytes;
            await many.BeginQualityAsync();
            await Assert.That(many.Counters.RetainedBytes < bytesBeforeQuality).IsTrue();
            var reused = await many.GetQualityAsync("<p>0</p>", default);
            await Assert.That(reused.NewParse).IsFalse();
            await Assert.That(many.Counters.ParseCount).IsEqualTo(4097);
            await Assert.That(async () => await many.GetAsync("<p>0</p>", default)).Throws<InvalidOperationException>();
            await many.ReleaseAsync();
            await Assert.That(many.Counters.RetainedBytes + many.Counters.EntryCount).IsEqualTo(0);
            await Assert.That(async () => await many.GetQualityAsync("<p>0</p>", default)).Throws<InvalidOperationException>();
        }
        await using (var large = new BuildHtmlFacts(default))
        {
            var comment = "<!--" + new string('x', 256 * 1024) + "-->";
            var comments = string.Concat(Enumerable.Repeat(comment, 8));
            for (var i = 0; i < 9; i++)
            {
                var snapshot = await large.GetAsync(comments + $"<p>{i}</p>", default);
                await Assert.That(snapshot.Facts.SearchText.Trim()).IsEqualTo(i.ToString());
            }
            await Assert.That(large.Counters.RetainedBytes > 32L * 1024 * 1024).IsTrue();
            await Assert.That(large.Counters.ParseCount).IsEqualTo(9);
            await large.BeginQualityAsync();
            await large.GetQualityAsync(comments + "<p>0</p>", default);
            await Assert.That(large.Counters.ParseCount).IsEqualTo(9);
            await large.ReleaseAsync();
            await Assert.That(large.Counters.EntryCount).IsEqualTo(0);
        }
        using var buildCancellation = new CancellationTokenSource();
        await using (var canceled = new BuildHtmlFacts(buildCancellation.Token))
        {
            var pending = canceled.GetAsync(string.Concat(Enumerable.Repeat("<p>cancel</p>", 10_000)), default);
            buildCancellation.Cancel();
            HtmlConsumerFacts? completedView = null;
            try
            {
                completedView = (await pending).Facts;
            }
            catch (OperationCanceledException) { }
            if (completedView is not null)
                await Assert.That(() => completedView.SearchText).Throws<OperationCanceledException>();
            await Assert.That(async () => await canceled.GetAsync(html, default)).Throws<OperationCanceledException>();
        }
        await owner.DisposeAsync();
        await Assert.That(owner.Counters.RetainedBytes).IsEqualTo(0);
        await Assert.That(owner.Counters.EntryCount).IsEqualTo(0);
        await Assert.That(acquiredSections.Single().Anchor).IsEqualTo("target"); // Already acquired immutable values are not revoked.
        await Assert.That(() => consumers[0].Facts.SearchText).Throws<OperationCanceledException>();
        await Assert.That(async () => await owner.GetAsync(html, default)).Throws<ObjectDisposedException>();
        await using var nextBuild = new BuildHtmlFacts(default);
        await nextBuild.GetAsync(html, default);
        await Assert.That(nextBuild.Counters.ParseCount).IsEqualTo(1);
    }
}
