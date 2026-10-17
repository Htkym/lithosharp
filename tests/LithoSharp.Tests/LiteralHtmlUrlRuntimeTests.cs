using System.Text.Json;
using LithoSharp.HtmlParsing;
using LithoSharp.Internal;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class LiteralHtmlUrlRuntimeTests
{
    [Test]
    public async Task LiteralUrlCorpusRetainsRuntimeQualityDecisionsAndBridgeMetadata()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LiteralHtmlUrlCases.json")));
        const string origin = "https://example.test/";
        var proof = new List<object>();
        foreach (var row in corpus.RootElement.EnumerateArray())
        {
            var name = row.GetProperty("name").GetString()!;
            var html = row.GetProperty("html").GetString()!;
            var document = "<!doctype html><html><head><title>Test</title><link rel=canonical href='https://example.test/'></head><body>" + html + "</body></html>";
            var files = new Dictionary<string, string> { ["index.html"] = document };
            var routes = new Dictionary<string, SiteRoute> { ["index.html"] = SiteRoute.ForDirectoryIndex("", origin) };
            var report = await SiteQualityValidator.ValidateAsync(origin, files, routes, new HashSet<string>(), new Dictionary<string, SiteRoute>(), new(checkOrphans: false), default);
            var issues = report.Diagnostics.Where(d => d.Id == "LSQ001").ToArray();
            await Assert.That(issues.Length != 0).IsEqualTo(row.GetProperty("runtimeLsq001").GetBoolean());
            var facts = HtmlLiteralFacts.Parse(html);
            await Assert.That(facts.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
            if (name == "canonical-excluded") await Assert.That(facts.Urls.Single().Rel).IsEqualTo("alternate CANONICAL");
            proof.Add(new
            {
                name,
                html,
                analyzerError = row.GetProperty("analyzerError").GetBoolean(),
                runtimeLsq001 = issues.Length != 0,
                messages = issues.Select(d => d.Message).ToArray(),
                facts = JsonSerializer.SerializeToElement(facts)
            });
        }
        if (Environment.GetEnvironmentVariable("RA04B_EVIDENCE_DIR") is { Length: > 0 } directory)
            File.WriteAllText(Path.Combine(directory, "literal-runtime-parity.json"), JsonSerializer.Serialize(new { status = "PASS", cases = proof }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task IndependentIssuesAreRejectedForEveryRuntimeResolutionBase()
    {
        var bases = new[] { "https://example.test/sub/", "http://other.test/path/", "https://user:pass@base.test/", "file:///local/", "mailto:person@example.test" };
        var rejected = new List<object>();
        foreach (var value in new[] { "", " ", "javascript:alert(1)", "ftp://external.test/file", "http://[", "https://user:pass@external.test/", "file:///private", "https://[bad]", "http://", "http:\\bad" })
        {
            var independent = SiteQualityUrlRules.IndependentIssue(value, resource: true);
            if (independent == SiteQualityUrlIssue.None) continue;
            foreach (var baseUrl in bases)
            {
                var actual = SiteQualityUrlRules.Resolve(value, true, new Uri(baseUrl), out _);
                await Assert.That(actual != SiteQualityUrlIssue.None).IsTrue();
                rejected.Add(new { value, baseUrl, independent = independent.ToString(), runtime = actual.ToString() });
            }
        }
        if (Environment.GetEnvironmentVariable("RA04B_EVIDENCE_DIR") is { Length: > 0 } directory)
            File.WriteAllText(Path.Combine(directory, "literal-resolution-bases.json"), JsonSerializer.Serialize(new { status = "PASS", rejected }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
