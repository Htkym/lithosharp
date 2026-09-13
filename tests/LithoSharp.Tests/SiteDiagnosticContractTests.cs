using System.Text.Json;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;

namespace LithoSharp.Tests;

public sealed class SiteDiagnosticContractTests
{
    [Test]
    public async Task LegacyConstructorsKeepWorking()
    {
        var location = new SiteSourceLocation("a.md", 2, 3);
        var diagnostic = new SiteDiagnostic("LS001", SiteDiagnosticSeverity.Warning, "msg", location);

        await Assert.That(location.EndLine).IsNull();
        await Assert.That(location.EndColumn).IsNull();
        await Assert.That(diagnostic.Category).IsNull();
        await Assert.That(diagnostic.RelatedLocations.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EndPositionMustNotPrecedeStart()
    {
        await Assert.That(() => new SiteSourceLocation("a.md", 2, 3, 2, 2)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SiteSourceLocation("a.md", 2, 3, 1, 9)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SiteSourceLocation("a.md", null, null, 1, 1)).Throws<ArgumentException>();
        await Assert.That(() => new SiteDiagnostic("LS001", SiteDiagnosticSeverity.Error, "msg", null, "  ", null)).Throws<ArgumentException>();
    }

    [Test]
    public async Task SourceSpanMapsToStartAndEnd()
    {
        var source = new SourceText("ab\ncd");
        var location = new SourceSpan(0, 5).ToSourceLocation("doc.md", source);

        await Assert.That(location.Line).IsEqualTo(1);
        await Assert.That(location.Column).IsEqualTo(1);
        await Assert.That(location.EndLine).IsEqualTo(2);
        await Assert.That(location.EndColumn).IsEqualTo(3);
    }

    [Test]
    public async Task JsonCarriesCodeAndPositions()
    {
        var diagnostic = new SiteDiagnostic(
            "LSM011",
            SiteDiagnosticSeverity.Error,
            "missing",
            new SiteSourceLocation("a.md", 3, 4, 3, 9),
            "inclusion",
            [new SiteSourceLocation("b.md", 1, 1)]);
        using var json = JsonDocument.Parse(new SiteQualityReport([diagnostic]).Format(SiteDiagnosticFormat.Json));
        var item = json.RootElement.GetProperty("diagnostics")[0];

        await Assert.That(item.GetProperty("id").GetString()).IsEqualTo("LSM011");
        await Assert.That(item.GetProperty("category").GetString()).IsEqualTo("inclusion");
        await Assert.That(item.GetProperty("location").GetProperty("endLine").GetInt32()).IsEqualTo(3);
        await Assert.That(item.GetProperty("location").GetProperty("endColumn").GetInt32()).IsEqualTo(9);
        await Assert.That(item.GetProperty("relatedLocations")[0].GetProperty("filePath").GetString()).IsEqualTo("b.md");
    }

    [Test]
    public async Task SarifCarriesEndPositions()
    {
        using var json = JsonDocument.Parse(new SiteQualityReport([
            new SiteDiagnostic("LS001", SiteDiagnosticSeverity.Error, "bad", new SiteSourceLocation("a.md", 3, 4, 5, 6), "content", null),
        ]).Format(SiteDiagnosticFormat.Sarif));
        var result = json.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        var region = result.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("region");

        await Assert.That(region.GetProperty("endLine").GetInt32()).IsEqualTo(5);
        await Assert.That(region.GetProperty("endColumn").GetInt32()).IsEqualTo(6);
        await Assert.That(result.GetProperty("properties").GetProperty("category").GetString()).IsEqualTo("content");
    }

    [Test]
    public async Task BuildReportPreservesContract()
    {
        var report = new SiteBuildReport(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            "Production",
            "Template",
            diagnostics:
            [
                new SiteDiagnostic("LS001", SiteDiagnosticSeverity.Error, "bad", new SiteSourceLocation("a.md", 3, 4, 3, 9), "content", [new SiteSourceLocation("b.md", 1, 1)]),
                new SiteDiagnostic("LS002", SiteDiagnosticSeverity.Warning, "outside"),
            ]);

        await Assert.That(report.Diagnostics.Count).IsEqualTo(2);
        await Assert.That(report.Diagnostics[0].Location?.EndColumn).IsEqualTo(9);
        await Assert.That(report.Diagnostics[0].Category).IsEqualTo("content");
        await Assert.That(report.Diagnostics[0].RelatedLocations.Count).IsEqualTo(1);
        await Assert.That(report.Diagnostics[1].Location).IsNull();
    }
}