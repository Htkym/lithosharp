using System.Text.Json;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;

namespace LithoSharp.Tests;

public sealed class SiteQualityReportTests
{
    [Test]
    public async Task Diagnostics_AreDeterministicAndDeduplicated()
    {
        var duplicate = new SiteDiagnostic("LS002", SiteDiagnosticSeverity.Warning, "second", new SiteSourceLocation("b.md", 2, 1));
        var report = new SiteQualityReport([duplicate, new SiteDiagnostic("LS001", SiteDiagnosticSeverity.Error, "first"), duplicate]);

        await Assert.That(report.Diagnostics.Count).IsEqualTo(2);
        await Assert.That(report.Diagnostics[0].Id).IsEqualTo("LS001");
        await Assert.That(report.Format(SiteDiagnosticFormat.Text)).IsEqualTo("ERROR LS001: first\nWARNING LS002: second (b.md:2:1)");
    }

    [Test]
    public async Task Json_ContainsLocations()
    {
        using var json = JsonDocument.Parse(new SiteQualityReport([
            new SiteDiagnostic("LS001", SiteDiagnosticSeverity.Error, "bad", new SiteSourceLocation("a.md", 3, 4)),
        ]).Format(SiteDiagnosticFormat.Json));

        await Assert.That(json.RootElement.GetProperty("diagnostics")[0].GetProperty("location").GetProperty("line").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task Sarif_UsesObjectsLevelsAndEncodedUris()
    {
        var report = new SiteQualityReport([
            new SiteDiagnostic("LS001", SiteDiagnosticSeverity.Error, "bad", new SiteSourceLocation("docs/a space/#/日本.md", 3, 4)),
            new SiteDiagnostic("LS002", SiteDiagnosticSeverity.Warning, "warn"),
            new SiteDiagnostic("LS003", SiteDiagnosticSeverity.Info, "note"),
        ]);
        using var json = JsonDocument.Parse(report.Format(SiteDiagnosticFormat.Sarif));
        var results = json.RootElement.GetProperty("runs")[0].GetProperty("results");

        await Assert.That(results[0].GetProperty("message").GetProperty("text").GetString()).IsEqualTo("bad");
        await Assert.That(results[0].GetProperty("level").GetString()).IsEqualTo("error");
        await Assert.That(results[1].GetProperty("level").GetString()).IsEqualTo("warning");
        await Assert.That(results[2].GetProperty("level").GetString()).IsEqualTo("note");
        await Assert.That(results[0].GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString()).IsEqualTo("docs/a%20space/%23/%E6%97%A5%E6%9C%AC.md");
    }

    [Test]
    public async Task Sarif_AbsoluteWindowsPathUsesFileUri()
    {
        if (!OperatingSystem.IsWindows()) return;
        var report = new SiteQualityReport([new SiteDiagnostic("LS001", SiteDiagnosticSeverity.Error, "bad", new SiteSourceLocation(@"C:\docs\a b.md"))]);
        using var json = JsonDocument.Parse(report.Format(SiteDiagnosticFormat.Sarif));
        var uri = json.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString();
        await Assert.That(uri).IsEqualTo("file:///C:/docs/a%20b.md");
    }

    [Test]
    public async Task InvalidInputsAreRejected()
    {
        await Assert.That(() => new SiteQualityReport([null!])).Throws<ArgumentException>();
        await Assert.That(() => new SiteQualityReport().Format((SiteDiagnosticFormat)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SiteQualityOptions((SiteDiagnosticSeverity)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ExternalLinkCheckOptions("cache", TimeSpan.Zero, TimeSpan.FromMilliseconds(uint.MaxValue), TimeSpan.FromSeconds(1))).Throws<ArgumentOutOfRangeException>();
    }
}
