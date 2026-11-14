using System.Text.Json;
using System.Text.Json.Nodes;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;

namespace LithoSharp.Tests;

public sealed class SitePublicationCompatibilityTests
{
    private const string BaseUrl = "https://example.test/docs/";
    private static readonly IReadOnlyDictionary<string, string> NoAllowances = new Dictionary<string, string>();

    [Test]
    public async Task DetectsAnchorLossAfterAllInternalLinksAreFixedAndAcceptsLegacyAliasOrExplicitAllowance()
    {
        using var workspace = new TemporaryWorkspace();
        var output = CreateOutput(workspace.Root);
        await Write(output, "index.html", "<a href='guide.html#old'>Guide</a>");
        await Write(output, "guide.html", "<h2 id='old'>Old heading</h2>");
        var baseline = await Baseline(output, workspace.Root);
        var baselineBytes = await File.ReadAllTextAsync(baseline);
        await Write(output, "index.html", "<a href='guide.html#new'>Guide</a>");
        await Write(output, "guide.html", "<h2 id='new'>New heading</h2>");
        var inspection = await Capture(output);
        await Assert.That(inspection.Diagnostics.Any(d => d.Severity >= SiteDiagnosticSeverity.Error)).IsFalse();
        var report = await Compare(inspection, baseline);
        await Assert.That(report.PublicationCompatibility).IsEqualTo(SitePublicationCompatibilityState.Incompatible);
        await Assert.That(report.Diagnostics.Single().Id).IsEqualTo("LSPUB003");
        await Assert.That(report.Diagnostics.Single().Message.Contains(BaseUrl + "guide.html#old", StringComparison.Ordinal)).IsTrue();
        report = await SitePublicationCompatibility.CompareAsync(inspection.PublicationManifest!, baseline,
            new Dictionary<string, string> { [BaseUrl + "guide.html#old"] = "Retired obsolete setup instructions." }, true);
        await Assert.That(report.PublicationCompatibility).IsEqualTo(SitePublicationCompatibilityState.ExplicitlyAllowed);
        await Assert.That(report.Diagnostics.All(d => d.Severity == SiteDiagnosticSeverity.Info)).IsTrue();
        report = await SitePublicationCompatibility.CompareAsync(inspection.PublicationManifest!, baseline,
            new Dictionary<string, string> { [BaseUrl + "guide.html#typo"] = "Incorrect allowance." }, true);
        await Assert.That(report.PublicationCompatibility).IsEqualTo(SitePublicationCompatibilityState.Incompatible);
        await Assert.That(report.Diagnostics.Any(d => d.Id == "LSPUB004" && d.Severity == SiteDiagnosticSeverity.Error)).IsTrue();
        await Write(output, "guide.html", "<a name='old'></a><h2 id='new'>New heading</h2>");
        report = await Compare(await Capture(output), baseline);
        await Assert.That(report.PublicationCompatibility).IsEqualTo(SitePublicationCompatibilityState.Compatible);
        await Assert.That(await File.ReadAllTextAsync(baseline)).IsEqualTo(baselineBytes);
    }

    [Test]
    public async Task NativeRefreshPreservesTheUrlButCannotClaimToPreserveTheOldAnchor()
    {
        using var workspace = new TemporaryWorkspace();
        var output = CreateOutput(workspace.Root);
        await Write(output, "index.html", "Home");
        await Write(output, "old.html", "<h2 id='legacy'>Legacy</h2>");
        var baseline = await Baseline(output, workspace.Root);
        await File.WriteAllTextAsync(Path.Combine(output, "old.html"),
            $"<html><head><link rel='canonical' href='{BaseUrl}index.html'><meta http-equiv='refresh' content='0;url={BaseUrl}index.html'></head></html>");
        var report = await Compare(await Capture(output), baseline);
        await Assert.That(report.Diagnostics.Any(d => d.Id == "LSPUB002")).IsFalse();
        await Assert.That(report.Diagnostics.Single().Id).IsEqualTo("LSPUB003");
        await Write(output, "index.html", "<span id='legacy'></span>Home");
        report = await Compare(await Capture(output), baseline);
        await Assert.That(report.PublicationCompatibility).IsEqualTo(SitePublicationCompatibilityState.Incompatible);
        await Assert.That(report.Diagnostics.Single().Id).IsEqualTo("LSPUB003");
        report = await SitePublicationCompatibility.CompareAsync((await Capture(output)).PublicationManifest!, baseline,
            new Dictionary<string, string> { [BaseUrl + "old.html#legacy"] = "Explicitly retired fragment after relocating this page." }, true);
        await Assert.That(report.PublicationCompatibility).IsEqualTo(SitePublicationCompatibilityState.ExplicitlyAllowed);
        await Write(output, "old.html", "<a name='legacy'></a><a href='index.html#legacy'>Moved section</a>");
        report = await Compare(await Capture(output), baseline);
        await Assert.That(report.PublicationCompatibility).IsEqualTo(SitePublicationCompatibilityState.Compatible);
        await File.WriteAllTextAsync(Path.Combine(output, "old.html"),
            $"<html><head><link rel='canonical' href='{BaseUrl}old.html'><meta http-equiv='refresh' content='0;url={BaseUrl}old.html'></head></html>");
        var invalid = await Capture(output);
        await Assert.That(invalid.PublicationManifest).IsNull();
        await Assert.That(invalid.Diagnostics.Any(d => d.Id == "LSQ004")).IsTrue();
    }

    [Test]
    public async Task MissingUnreadableMalformedAndUnsupportedBaselinesStayNotComparedInEveryReportFormat()
    {
        using var workspace = new TemporaryWorkspace();
        var output = CreateOutput(workspace.Root);
        await Write(output, "index.html", "Home");
        var current = (await Capture(output)).PublicationManifest!;
        var bad = Path.Combine(workspace.Root, "bad.json");
        foreach (var text in new[] { "{}", "not json", current.ToJson().Replace("\"version\":1", "\"version\":99", StringComparison.Ordinal),
            current.ToJson().Replace("\"siteId\":\"docs\"", "\"siteId\":\"tampered\"", StringComparison.Ordinal) })
        {
            await File.WriteAllTextAsync(bad, text);
            await Check(bad);
        }
        await Check(null);
        await Check(Path.Combine(workspace.Root, "missing.json"));
        await Check(workspace.Root);
        await File.WriteAllBytesAsync(bad, [0xc3, 0x28]);
        await Check(bad);
        var optional = await SitePublicationCompatibility.CompareAsync(current, null, NoAllowances, false);
        await Assert.That(optional.PublicationCompatibility).IsEqualTo(SitePublicationCompatibilityState.NotCompared);
        await Assert.That(optional.Diagnostics.Any(d => d.Severity >= SiteDiagnosticSeverity.Error)).IsFalse();

        async Task Check(string? path)
        {
            var report = await SitePublicationCompatibility.CompareAsync(current, path, NoAllowances, true);
            await Assert.That(report.PublicationCompatibility).IsEqualTo(SitePublicationCompatibilityState.NotCompared);
            await Assert.That(report.Diagnostics.Single().Severity).IsEqualTo(SiteDiagnosticSeverity.Error);
            await Assert.That(report.Format(SiteDiagnosticFormat.Text).Contains("NotCompared", StringComparison.Ordinal)).IsTrue();
            using var json = JsonDocument.Parse(report.Format(SiteDiagnosticFormat.Json));
            await Assert.That(json.RootElement.GetProperty("publicationCompatibility").GetProperty("state").GetString()).IsEqualTo("NotCompared");
            await Assert.That(json.RootElement.GetProperty("publicationCompatibility").GetProperty("required").GetBoolean()).IsTrue();
            using var sarif = JsonDocument.Parse(report.Format(SiteDiagnosticFormat.Sarif));
            await Assert.That(sarif.RootElement.GetProperty("runs")[0].GetProperty("properties").GetProperty("publicationCompatibility")
                .GetProperty("state").GetString()).IsEqualTo("NotCompared");
        }
    }

    [Test]
    public async Task BaseUrlSiteAndExclusionChangesCannotHidePublishedRemovals()
    {
        using var workspace = new TemporaryWorkspace();
        var output = CreateOutput(workspace.Root);
        await Write(output, "index.html", "Home");
        await File.WriteAllBytesAsync(Path.Combine(output, "asset.bin"), [0, 255]);
        var baseline = await Baseline(output, workspace.Root);
        var reports = new List<SiteQualityReport>
        {
            await GeneratedSiteInspector.InspectPublicationAsync(output, BaseUrl, "another-site", []),
            await GeneratedSiteInspector.InspectPublicationAsync(output, BaseUrl, "docs", ["asset.bin"]),
        };
        const string moved = "https://example.test/new-docs/";
        await Write(output, "index.html", "Home", moved);
        reports.Add(await GeneratedSiteInspector.InspectPublicationAsync(output, moved, "docs", []));
        foreach (var inspection in reports)
        {
            var report = await Compare(inspection, baseline);
            await Assert.That(report.PublicationCompatibility).IsEqualTo(SitePublicationCompatibilityState.NotCompared);
            await Assert.That(report.Diagnostics.Single().Message.Contains("identity mismatch", StringComparison.Ordinal)).IsTrue();
        }
    }

    [Test]
    public async Task InventoryRetainsUnicodeIndexAliasesBinaryIdentityAndExplicitExactExclusions()
    {
        using var workspace = new TemporaryWorkspace();
        var output = CreateOutput(workspace.Root);
        Directory.CreateDirectory(Path.Combine(output, "話題"));
        await Write(output, "index.html", "<a href='話題/#節'>Topic</a>");
        await Write(output, "話題/index.html", "<h2 id='節'>Section</h2>");
        await File.WriteAllBytesAsync(Path.Combine(output, "asset.bin"), [0, 255]);
        await File.WriteAllTextAsync(Path.Combine(output, "ignored.txt"), "Excluded explicitly.");
        var first = await GeneratedSiteInspector.InspectPublicationAsync(output, BaseUrl, "docs", ["ignored.txt"]);
        await Assert.That(first.HtmlParseCount).IsEqualTo(2);
        var manifest = SitePublicationManifest.Parse(first.PublicationManifest!.ToJson());
        await Assert.That(manifest.ToJson()).IsEqualTo(first.PublicationManifest.ToJson());
        var json = JsonNode.Parse(manifest.ToJson())!;
        var topic = json["entries"]!.AsArray().Single(e => e!["outputPath"]!.GetValue<string>() == "話題/index.html")!;
        await Assert.That(topic["urls"]!.AsArray().Select(n => n!.GetValue<string>())).IsEquivalentTo(
            [BaseUrl + "%E8%A9%B1%E9%A1%8C/index.html", BaseUrl + "%E8%A9%B1%E9%A1%8C/"]);
        await Assert.That(topic["anchors"]![0]!.GetValue<string>()).IsEqualTo("節");
        var baseline = Path.Combine(workspace.Root, "published.json");
        await File.WriteAllTextAsync(baseline, manifest.ToJson());
        await File.WriteAllBytesAsync(Path.Combine(output, "asset.bin"), [1, 255]);
        var changed = await GeneratedSiteInspector.InspectPublicationAsync(output, BaseUrl, "docs", ["ignored.txt"]);
        await Assert.That(changed.PublicationManifest!.ContentIdentity).IsNotEqualTo(manifest.ContentIdentity);
        await Assert.That((await Compare(changed, baseline)).PublicationCompatibility).IsEqualTo(SitePublicationCompatibilityState.Compatible);
        File.Delete(Path.Combine(output, "asset.bin"));
        var removed = await GeneratedSiteInspector.InspectPublicationAsync(output, BaseUrl, "docs", ["ignored.txt"]);
        var report = await Compare(removed, baseline);
        await Assert.That(report.Diagnostics.Single().Id).IsEqualTo("LSPUB002");
        report = await SitePublicationCompatibility.CompareAsync(removed.PublicationManifest!, baseline,
            new Dictionary<string, string> { [BaseUrl + "asset.bin"] = "Retired download." }, true);
        await Assert.That(report.PublicationCompatibility).IsEqualTo(SitePublicationCompatibilityState.ExplicitlyAllowed);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(async () => await SitePublicationCompatibility.CompareAsync(manifest, baseline, NoAllowances, true, cancelled.Token))
            .Throws<OperationCanceledException>();
    }

    private static string CreateOutput(string root)
    {
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(output);
        return output;
    }

    private static Task<SiteQualityReport> Capture(string output) =>
        GeneratedSiteInspector.InspectPublicationAsync(output, BaseUrl, "docs", []);

    private static Task<SiteQualityReport> Compare(SiteQualityReport current, string baseline) =>
        SitePublicationCompatibility.CompareAsync(current.PublicationManifest!, baseline, NoAllowances, true);

    private static async Task<string> Baseline(string output, string root)
    {
        var path = Path.Combine(root, "published.json");
        await File.WriteAllTextAsync(path, (await Capture(output)).PublicationManifest!.ToJson());
        return path;
    }

    private static Task Write(string output, string file, string body, string baseUrl = BaseUrl) =>
        File.WriteAllTextAsync(Path.Combine(output, file.Replace('/', Path.DirectorySeparatorChar)),
            $"<!doctype html><html><head><title>{file}</title><link rel='canonical' href='{baseUrl}{string.Join('/', file.Split('/').Select(Uri.EscapeDataString))}'></head><body>{body}</body></html>");
}
