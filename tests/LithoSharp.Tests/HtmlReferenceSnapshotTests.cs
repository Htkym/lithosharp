using System.Security.Cryptography;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.HtmlParsing;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class HtmlReferenceSnapshotTests
{
    [Test]
    public async Task SharedReferencesBindVariantsAndBasesWithoutReparse()
    {
        const string html = "<base href='assets/'><a href='x&amp;y.png'>Link</a><img srcset='x&amp;y.png 1x, x&amp;y.png 2x'>";
        await using var owner = new BuildHtmlFacts(default, rawDemand: HtmlRawFactsDemand.Quality);
        owner.Get(html, default);
        await owner.BeginQualityAsync();
        var firstIdentity = new HtmlPageIdentity("v1/guide/index.html", "/v1/guide/", "guide",
            new DocumentKey("docs", "v1", "en", "guide"));
        var secondIdentity = new HtmlPageIdentity("v2/guide/index.html", "/v2/guide/", "guide",
            new DocumentKey("docs", "v2", "ja", "guide"));
        var first = await owner.GetQualityReferencesAsync(html, firstIdentity, new("https://example.test/v1/guide/"), default);
        var second = await owner.GetQualityReferencesAsync(html, secondIdentity, new("https://example.test/v2/guide/"), default);
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(1);
        await Assert.That(first.Snapshot.Identity.Document).IsEqualTo(firstIdentity.Document);
        await Assert.That(second.Snapshot.Identity.Document).IsEqualTo(secondIdentity.Document);
        await Assert.That(first.Snapshot.ResolutionBase.AbsoluteUri).IsEqualTo("https://example.test/v1/guide/assets/");
        await Assert.That(second.Snapshot.ResolutionBase.AbsoluteUri).IsEqualTo("https://example.test/v2/guide/assets/");
        await Assert.That(first.Snapshot.References.Single(r => r.Role == HtmlReferenceRole.Base).ResolvedUrl!.AbsoluteUri)
            .IsEqualTo("https://example.test/v1/guide/assets/");
        await Assert.That(second.Snapshot.References.Single(r => r.Role == HtmlReferenceRole.Base).ResolvedUrl!.AbsoluteUri)
            .IsEqualTo("https://example.test/v2/guide/assets/");
        var link = first.Snapshot.References.Single(r => r.Role == HtmlReferenceRole.Navigation);
        await Assert.That(link.Raw).IsEqualTo("x&amp;y.png");
        await Assert.That(link.Decoded).IsEqualTo("x&y.png");
        await Assert.That(link.UnresolvedValue).IsEqualTo("x&y.png");
        await Assert.That(link.ResolvedUrl!.AbsoluteUri).IsEqualTo("https://example.test/v1/guide/assets/x&y.png");
        await Assert.That(html.Substring(link.Source!.Value.Start, link.Source.Value.Length)).IsEqualTo(link.Raw);
        var candidates = first.Snapshot.References.Where(r => r.Role == HtmlReferenceRole.SrcsetCandidate).ToArray();
        await Assert.That(candidates.Select(r => r.UnresolvedValue).ToArray()).IsEquivalentTo(new[] { "x&y.png", "x&y.png" });
        await Assert.That(candidates.Select(r => r.CandidateOrdinal).ToArray()).IsEquivalentTo(new[] { 0, 1 });
        await Assert.That(candidates[0].Raw).IsEqualTo("x&amp;y.png 1x, x&amp;y.png 2x");
        await Assert.That(async () => await owner.GetQualityReferencesAsync(html, firstIdentity,
            new("https://example.test/wrong/"), default)).Throws<ArgumentException>();
        await owner.ReleaseAsync();
        await Assert.That(owner.Counters.EntryCount).IsEqualTo(0);
        await Assert.That(first.Snapshot.References.Count).IsEqualTo(4);
        await Assert.That(first.Snapshot.References is ICollection<HtmlReference> list && list.IsReadOnly).IsTrue();
    }

    [Test]
    public async Task ReferenceSnapshotsPreserveDiagnosticsOrderAndDuplicates()
    {
        const string html = "<title>T</title><base href='/root/'><link rel=canonical href='index.html'>"
            + "<a href='missing&amp;one'>A</a><a href='missing&amp;one'>B</a><img src='missing.png' poster='missing.png' "
            + "srcset='same.png 1x, same.png 2x'><object data='missing.bin'></object><form action='missing.html'></form>"
            + "<meta property='og:image'><div property='og:image' content='ignored.png'></div>"
            + "<svg><a xlink:href='ignored.svg'></a></svg><style>p{background:url(missing.css)}</style>";
        var route = SiteRoute.ForFile("index.html", "https://example.test/root/");
        var routes = new Dictionary<string, SiteRoute>(StringComparer.Ordinal) { ["index.html"] = route };
        var files = new Dictionary<string, string> { ["index.html"] = html };
        var options = new SiteQualityOptions(checkOrphans: false);
        var old = await SiteQualityValidator.ValidateAsync("https://example.test/root/", files, routes,
            new HashSet<string>(), new Dictionary<string, SiteRoute>(), options, default);
        await using var owner = new BuildHtmlFacts(default, rawDemand: HtmlRawFactsDemand.Quality);
        var identity = new HtmlPageIdentity("index.html", route.PublicPath, "home", new("docs", "v1", "en", "home"));
        var report = await SiteQualityValidator.ValidateAsync("https://example.test/root/", files.Keys.ToArray(),
            (path, _) => Task.FromResult(files[path]), routes, new HashSet<string>(), new Dictionary<string, SiteRoute>(),
            options, default, sharedFacts: owner, pageIdentities: new Dictionary<string, HtmlPageIdentity> { ["index.html"] = identity });
        await Assert.That(report.Format(SiteDiagnosticFormat.Json)).IsEqualTo(old.Format(SiteDiagnosticFormat.Json));
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(1);
        var snapshot = report.HtmlReferences.Single();
        await Assert.That(snapshot.Identity).IsEqualTo(identity);
        await Assert.That(string.Join(",", snapshot.References.Select(r => r.Role))).IsEqualTo(
            "Base,Canonical,Navigation,Navigation,Resource,Resource,SrcsetCandidate,SrcsetCandidate,Resource,FormAction,MetaImage");
        await Assert.That(snapshot.References.Count(r => r.UnresolvedValue == "missing&one")).IsEqualTo(2);
        var absent = snapshot.References.Single(r => r.Role == HtmlReferenceRole.MetaImage);
        await Assert.That(absent.Raw is null && absent.Decoded is null && absent.Source is null).IsTrue();
        await Assert.That(snapshot.References.Any(r => r.UnresolvedValue.StartsWith("ignored", StringComparison.Ordinal))).IsFalse();
        await Assert.That(old.Diagnostics.Any(d => d.Message.Contains("missing.css", StringComparison.Ordinal))).IsTrue();
        await Assert.That(async () => await SiteQualityValidator.ValidateAsync("https://example.test/root/", files.Keys.ToArray(),
            (path, _) => Task.FromResult(files[path]), routes, new HashSet<string>(), new Dictionary<string, SiteRoute>(),
            options, default, sharedFacts: owner, pageIdentities: new Dictionary<string, HtmlPageIdentity>
            { ["index.html"] = identity with { OutputPath = "different.html" } })).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RequiredReferencesRejectPartialBeforePublication()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var site = new SiteSettings { Title = "References", BaseUrl = "https://example.test/", TimeZone = "UTC" };
        var generator = new SiteGenerator();
        const string html = "<title>Home</title><link rel=canonical href='/'><a href='/'>Home</a>";
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = DateTimeOffset.UnixEpoch,
            Quality = new(checkOrphans: false),
            RawHtmlFactsDemand = HtmlRawFactsDemand.Quality
        };
        var first = await generator.GenerateWithOptionsAsync(site, [], output, true,
            new() { Template = new TextTemplate(html) }, options, default);
        var before = Snapshot(output);
        var bound = first.QualityReport.HtmlReferences.Single();
        await Assert.That(bound.Identity.OutputPath).IsEqualTo("index.html");
        await Assert.That(bound.Identity.PublicPath).IsEqualTo("/index.html");
        await Assert.That(first.HtmlFactsSharing.EntryCount).IsEqualTo(0);
        await Assert.That(bound.References.Single(r => r.Role == HtmlReferenceRole.Navigation).ResolvedUrl!.AbsoluteUri)
            .IsEqualTo("https://example.test/");
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(site, [], output, true,
            new() { Template = new TextTemplate(html + "<template shadowrootmode=open><p>X</p></template>") }, options, default))
            .Throws<HtmlFactsIncompleteException>();
        await Assert.That(Snapshot(output)).IsEquivalentTo(before);
    }

    private static string[] Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(path, file) + ":"
            + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();

    private sealed class TextTemplate(string html) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult([new SiteTemplateFile { RelativePath = "index.html", Content = html }]));
    }
}
