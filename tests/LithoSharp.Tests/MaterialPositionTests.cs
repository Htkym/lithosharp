using System.Security.Cryptography;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.HtmlParsing;
using LithoSharp.Quality;

namespace LithoSharp.Tests;

public sealed class MaterialPositionTests
{
    [Test]
    public async Task TextMaterialsPreserveValuesAndOptionalSearchCoverage()
    {
        const string html = "<!doctype html>\r\n<title> A &amp; B </title><meta name=description content=D>"
            + "<meta property='og:t&#105;tle' content=T><body><nav><h2 id=nav>Hidden</h2></nav>"
            + "<h2 id=heading>Hi &amp; 😀<em>X</em></h2><p>A<em>B</em>C</p><p>D</p>"
            + "<style>p{color:red}</style><script>ignored</script></body>";
        await using var owner = new BuildHtmlFacts(default, rawDemand: HtmlRawFactsDemand.Search | HtmlRawFactsDemand.Quality);
        var view = owner.Get(html, default);
        var search = view.SearchSource!;
        await Assert.That(search.Body.Value).IsEqualTo("Hi & 😀XABCD");
        await Assert.That(search.NormalizedIndex.Value).IsEqualTo("Hi & 😀 X A B C D");
        await Assert.That(search.Sections.Single().Body.Value).IsEqualTo("ABC D");
        await Assert.That(view.SearchSections().Single().Title).IsEqualTo("Hi & 😀X");
        await Assert.That(view.SearchSourceCoverage.Status).IsEqualTo(HtmlFactsCoverageStatus.Partial);
        await Assert.That(search.NormalizedIndex.Segments.Any(segment => segment.Source is null
            && segment.Reason == "normalized_separator_unmapped")).IsTrue();
        await using var plainOwner = new BuildHtmlFacts(default, rawDemand: HtmlRawFactsDemand.Search | HtmlRawFactsDemand.Quality);
        var plain = plainOwner.Get("<body>text</body>", default);
        await Assert.That(plain.SearchSource!.Body.Coverage.Status).IsEqualTo(HtmlFactsCoverageStatus.Complete);
        await Assert.That(plain.SearchSource.IndexText.Coverage.Status).IsEqualTo(HtmlFactsCoverageStatus.Partial);
        await Assert.That(plain.SearchSource.NormalizedIndex.Coverage.Status).IsEqualTo(HtmlFactsCoverageStatus.Complete);
        await Assert.That(plain.SearchSource.NormalizedIndex.Value).IsEqualTo("text");
        await Assert.That(plain.SearchSourceCoverage.Status).IsEqualTo(HtmlFactsCoverageStatus.Partial);
        await Assert.That(plain.RawAttributes.Coverage.Single(c => c.Projection == HtmlFactsProjection.Search).Status)
            .IsEqualTo(HtmlFactsCoverageStatus.Partial);
        await Assert.That(plain.SearchSourceCoverage.Reasons.Select(r => r.Code))
            .IsEquivalentTo(plain.RawAttributes.Coverage.Single(c => c.Projection == HtmlFactsProjection.Search).Reasons.Select(r => r.Code));
        await owner.BeginQualityAsync();
        var bound = await owner.GetQualitySnapshotAsync(html, new("index.html", "/index.html", "home", null),
            new("https://example.test/index.html"), default);
        bound.Snapshot.RequireComplete(); // Supplementary search Unknown cannot fail valid Quality.
        await Assert.That(bound.Snapshot.TitleMaterial!.Value).IsEqualTo("A & B");
        await Assert.That(bound.Snapshot.Headings.Count).IsEqualTo(2); // Quality material keeps nav headings.
        var heading = bound.Snapshot.Headings.Single(h => h.Anchor == "heading");
        await Assert.That(heading.Level).IsEqualTo(2);
        await Assert.That(heading.Text.Value).IsEqualTo("Hi & 😀X");
        await Assert.That(heading.Text.Segments.Any(segment => segment.Source is { } span
            && html.Substring(span.Start, span.Length) == "&amp;")).IsTrue();
        await Assert.That(html.Substring(heading.OpeningTag!.Value.Start, heading.OpeningTag.Value.Length)).IsEqualTo("<h2 id=heading>");
        await Assert.That(bound.Snapshot.Metadata.Single(meta => meta.Name == "description").NameValue!.Raw).IsEqualTo("description");
        await Assert.That(bound.Snapshot.Metadata.Single(meta => meta.Property == "og:title").PropertyValue!.Raw).IsEqualTo("og:t&#105;tle");
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(1);
        await Assert.That(bound.Snapshot.SourceCoverage.Single(c => c.Projection == HtmlFactsProjection.Search).Status)
            .IsEqualTo(HtmlFactsCoverageStatus.NotRequested);
        await owner.ReleaseAsync();
        await Assert.That(owner.Counters.EntryCount).IsEqualTo(0);
        await Assert.That(search.NormalizedIndex.Value).IsEqualTo("Hi & 😀 X A B C D");
    }

    [Test]
    public async Task CandidateOffsetsComposeHtmlAndCssSourceMappings()
    {
        const string prefix = "<title>T</title><img srcset='";
        const string encoded = "a&amp;b.png";
        const string descriptor = " 1x, ";
        const string middle = " 2x'><div style='background:url(";
        const string between = ");mask:url(";
        var attributeHtml = prefix + encoded + descriptor + encoded + middle + encoded + between + encoded + ")'></div>";
        const string blockPrefix = "<style>p{background:url('";
        const string blockUrl = "block.png";
        var html = attributeHtml + blockPrefix + blockUrl + "')}</style>";
        await using var owner = new BuildHtmlFacts(default, rawDemand: HtmlRawFactsDemand.Quality);
        var bound = await owner.GetQualitySnapshotAsync(html, new("index.html", "/index.html", null, null),
            new("https://example.test/index.html"), default);
        bound.Snapshot.RequireComplete();
        var srcset = bound.Snapshot.References.Where(r => r.Role == HtmlReferenceRole.SrcsetCandidate).ToArray();
        var css = bound.Snapshot.CssReferences.Where(r => r.Attribute == "style").ToArray();
        int[] positions = [prefix.Length, prefix.Length + encoded.Length + descriptor.Length,
            prefix.Length + encoded.Length * 2 + descriptor.Length + middle.Length,
            prefix.Length + encoded.Length * 3 + descriptor.Length + middle.Length + between.Length];
        var candidates = srcset.Concat(css).ToArray();
        for (var i = 0; i < candidates.Length; i++)
        {
            await Assert.That(candidates[i].CandidateSource).IsEqualTo(new HtmlSpan(positions[i], encoded.Length));
            await Assert.That(candidates[i].Candidate!.Raw).IsEqualTo(encoded);
            await Assert.That(candidates[i].Candidate!.Value).IsEqualTo("a&b.png");
            await Assert.That(candidates[i].UnresolvedValue).IsEqualTo("a&b.png");
            await Assert.That(candidates[i].Candidate!.Coverage.Status).IsEqualTo(HtmlFactsCoverageStatus.Complete);
        }
        await Assert.That(candidates.Length).IsEqualTo(4);
        await Assert.That(css[0].Decoded).IsEqualTo("background:url(a&b.png);mask:url(a&b.png)");
        var block = bound.Snapshot.CssReferences.Single(r => r.UnresolvedValue == blockUrl);
        await Assert.That(block.CandidateSource).IsEqualTo(new HtmlSpan(attributeHtml.Length + blockPrefix.Length, blockUrl.Length));
        await Assert.That(block.Candidate!.Raw).IsEqualTo(blockUrl);
        await Assert.That(block.Candidate.Value).IsEqualTo(blockUrl);
        await Assert.That(html.Substring(block.CandidateSource!.Value.Start, block.CandidateSource.Value.Length)).IsEqualTo(blockUrl);
        await Assert.That(block.Candidate.Coverage.Status).IsEqualTo(HtmlFactsCoverageStatus.Complete);
        const string cssPrefix = "/*\r\n*/@import 'one.css';p{background:url('";
        const string escaped = "a\\26 b.png";
        const string cssBetween = "')}p{background:url('";
        var sheet = cssPrefix + escaped + cssBetween + escaped + "')}";
        var standalone = CssReferenceSnapshot.Create("site.css", new("https://example.test/site.css"), sheet, default);
        standalone.RequireComplete();
        var urls = standalone.References.Where(r => r.UnresolvedValue == "a&b.png").ToArray();
        await Assert.That(urls[0].CandidateSource).IsEqualTo(new HtmlSpan(cssPrefix.Length, escaped.Length));
        await Assert.That(urls[1].CandidateSource).IsEqualTo(new HtmlSpan(cssPrefix.Length + escaped.Length + cssBetween.Length, escaped.Length));
        await Assert.That(urls.All(r => r.Candidate!.Raw == escaped)).IsTrue();
        await Assert.That(urls[0].Candidate!.Segments.Any(segment => segment.ValueLength == 1 && segment.Source?.Length == 4)).IsTrue();
        await Assert.That(owner.Counters.ParseCount).IsEqualTo(1);

        // Share runtime observations with the packed compiler host, including active-tree/namespace decisions.
        using var corpus = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Environment.GetEnvironmentVariable("INLINE_CSS_CORPUS") ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "InlineCssUrlCases.json")));
        var observations = new List<object>();
        foreach (var row in corpus.RootElement.EnumerateArray())
        {
            var input = row.GetProperty("html").GetString()!;
            var snapshot = await owner.GetQualitySnapshotAsync(input, new("case.html", "/case.html", null, null),
                new("https://example.test/case.html"), default);
            var references = snapshot.Snapshot.CssReferences;
            var values = references.Select(reference => reference.UnresolvedValue).ToArray();
            await Assert.That(values).IsEquivalentTo(row.GetProperty("values").EnumerateArray().Select(value => value.GetString()).ToArray());
            observations.Add(new
            {
                name = row.GetProperty("name").GetString(),
                values,
                positions = references.Select(reference => new { reference.CandidateSource, raw = reference.Candidate?.Raw }).ToArray()
            });
        }
        if (Environment.GetEnvironmentVariable("RA04B_EVIDENCE_DIR") is { Length: > 0 } directory)
            File.WriteAllText(Path.Combine(directory, "inline-css-runtime.json"), System.Text.Json.JsonSerializer.Serialize(
                new { status = "PASS", cases = observations }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task RequiredCoverageRejectsOnlyRequiredUnknownAndPreservesOutput()
    {
        var unknown = HtmlSourceMap.Unknown("a.png", HtmlFactsProjection.References, "required_source_missing");
        await Assert.That(() => unknown.RequireComplete()).Throws<HtmlQualitySnapshotIncompleteException>();
        await Assert.That(unknown.Source is null && unknown.Raw is null).IsTrue();
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        const string html = "<title>Home</title><link rel=canonical href='/index.html'><link rel=stylesheet href='site.css'>"
            + "<h2 id=h>Heading</h2><p>A<em>B</em>C</p><p>D</p>";
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = DateTimeOffset.UnixEpoch,
            Quality = new(checkOrphans: false),
            RawHtmlFactsDemand = HtmlRawFactsDemand.Search | HtmlRawFactsDemand.Quality
        };
        var generator = new SiteGenerator();
        var site = new SiteSettings { Title = "Home", BaseUrl = "https://example.test/", TimeZone = "UTC" };
        var first = await generator.GenerateWithOptionsAsync(site, [], output, true,
            new() { Template = new FilesTemplate(html, "p{background:url('data:image/png;base64,AAAA')}") }, options, default);
        await Assert.That(first.QualityReport.HtmlReferences.Single().TitleMaterial!.Value).IsEqualTo("Home");
        var before = Snapshot(output);
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(site, [], output, true,
            new() { Template = new FilesTemplate(html, "p{background:url(var(--asset))}") }, options, default))
            .Throws<HtmlQualitySnapshotIncompleteException>();
        await Assert.That(Snapshot(output)).IsEquivalentTo(before);
    }

    private static string[] Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => Path.GetRelativePath(path, file) + ":"
            + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))).ToArray();

    private sealed class FilesTemplate(string html, string css) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SiteTemplateResult([
                new SiteTemplateFile { RelativePath = "index.html", Content = html },
                new SiteTemplateFile { RelativePath = "site.css", Content = css },
            ]));
    }
}
