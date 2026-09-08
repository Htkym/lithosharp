using LithoSharp.Diagnostics;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class SiteRouteTableTests
{
    [Test]
    public async Task Validate_CollectsMultipleCollisionDiagnostics()
    {
        var table = new SiteRouteTable();
        table.Register(SiteRoute.ForFile("guide.html"), "page:a");
        table.Register(SiteRoute.ForFile("guide.html"), "page:b");
        table.Register(SiteRoute.ForFile("Guide.html"), "page:c");

        var result = table.Validate();

        await Assert.That(result.IsValid).IsFalse();
        await Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Id))
            .IsEquivalentTo(
            [
                SiteRouteDiagnosticIds.DuplicatePublicPath,
                SiteRouteDiagnosticIds.DuplicateOutputPath,
                SiteRouteDiagnosticIds.OutputPathCaseCollision,
                SiteRouteDiagnosticIds.OutputPathCaseCollision,
            ]);
        var exception = await Assert.That(() => table.ValidateOrThrow())
            .Throws<SiteRouteValidationException>();
        await Assert.That(exception!.Diagnostics.Count).IsEqualTo(result.Diagnostics.Count);
    }

    [Test]
    public async Task Validate_DetectsPublicAndOutputDuplicatesIndependently()
    {
        var table = new SiteRouteTable();
        table.Register(SiteRoute.ForFile("asset.html", "https://example.test/docs/"), "public:a");
        table.Register(SiteRoute.ForFile("docs/asset.html"), "public:b");
        table.Register(SiteRoute.ForFile("shared.html"), "output:a");
        table.Register(SiteRoute.ForFile("shared.html", "https://example.test/product/"), "output:b");

        var result = table.Validate();

        await Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Id))
            .IsEquivalentTo(
            [
                SiteRouteDiagnosticIds.DuplicatePublicPath,
                SiteRouteDiagnosticIds.DuplicateOutputPath,
            ]);
    }

    [Test]
    public async Task Validate_DetectsCaseInsensitiveOutputCollisionOnEveryHost()
    {
        var table = new SiteRouteTable();
        table.Register(SiteRoute.ForFile("assets/Search.json"), "search:upper");
        table.Register(SiteRoute.ForFile("assets/search.json"), "search:lower");

        var result = table.Validate();

        await Assert.That(result.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(result.Diagnostics[0].Id)
            .IsEqualTo(SiteRouteDiagnosticIds.OutputPathCaseCollision);
    }

    [Test]
    public async Task Validate_TreatsIdenticalClaimsFromSameOwnerAsIdempotent()
    {
        var table = new SiteRouteTable();
        var route = SiteRoute.ForFile("sitemap.xml");
        table.Register(route, "artifact:sitemap");
        table.Register(route, "artifact:sitemap");
        table.ReserveOutputPath("sitemap.xml", "artifact:sitemap");
        table.ReserveOutputPath("SITEMAP.XML", "artifact:sitemap");

        var result = table.ValidateOrThrow();

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Validate_AcceptsLiteralPercentInPhysicalOutputPaths()
    {
        var table = new SiteRouteTable();
        table.Register(SiteRoute.ForFile("100%25.html"), "page:percent");
        table.ReserveOutputPath("100%.html", "page:percent");

        var result = table.ValidateOrThrow();

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Validate_DeduplicatesRepeatedClaimsBeforeCollisionAnalysis()
    {
        var table = new SiteRouteTable();
        var route = SiteRoute.ForFile("duplicate.html");
        table.Register(route, "page:a", new SiteSourceLocation("z.md", 3, 1));
        table.Register(route, "page:a", new SiteSourceLocation("a.md", 1, 1));
        table.Register(route, "page:a", new SiteSourceLocation("m.md", 2, 1));
        table.Register(route, "page:b");
        table.ReserveOutputPath("feed.xml", "artifact:a", new SiteSourceLocation("z.yml", 3, 1));
        table.ReserveOutputPath("feed.xml", "artifact:a", new SiteSourceLocation("a.yml", 1, 1));
        table.ReserveOutputPath("feed.xml", "artifact:a", new SiteSourceLocation("m.yml", 2, 1));
        table.ReserveOutputPath("feed.xml", "artifact:b");

        var result = table.Validate();

        await Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Id))
            .IsEquivalentTo(
            [
                SiteRouteDiagnosticIds.DuplicatePublicPath,
                SiteRouteDiagnosticIds.DuplicateOutputPath,
                SiteRouteDiagnosticIds.ReservedOutputPathCollision,
            ]);
        await Assert.That(DescribeLocation(result.Diagnostics.Single(
                diagnostic => diagnostic.Id == SiteRouteDiagnosticIds.DuplicatePublicPath).Location))
            .IsEqualTo("a.md|1|1");
        await Assert.That(DescribeLocation(result.Diagnostics.Single(
                diagnostic => diagnostic.Id == SiteRouteDiagnosticIds.ReservedOutputPathCollision).Location))
            .IsEqualTo("a.yml|1|1");
    }

    [Test]
    public async Task Validate_DetectsRouteAncestorConflictIgnoringCase()
    {
        var table = new SiteRouteTable();
        table.Register(SiteRoute.ForFile("Routes/Foo"), "page:file");
        table.Register(SiteRoute.ForFile("routes/foo/bar.html"), "page:child");

        var result = table.Validate();

        await Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Id))
            .IsEquivalentTo([SiteRouteDiagnosticIds.OutputPathAncestorConflict]);
    }

    [Test]
    public async Task Validate_DetectsRouteReservationAncestorConflictIgnoringCase()
    {
        var table = new SiteRouteTable();
        table.Register(SiteRoute.ForFile("Assets/SEARCH/index.html"), "page:search");
        table.ReserveOutputPath("assets/search", "artifact:search");

        var result = table.Validate();

        await Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Id))
            .IsEquivalentTo([SiteRouteDiagnosticIds.OutputPathAncestorConflict]);
    }

    [Test]
    public async Task Validate_DetectsReservationAncestorConflictIgnoringCase()
    {
        var table = new SiteRouteTable();
        table.ReserveOutputPath("Feeds/Main", "artifact:file");
        table.ReserveOutputPath("feeds/main/archive.xml", "artifact:archive");

        var result = table.Validate();

        await Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Id))
            .IsEquivalentTo([SiteRouteDiagnosticIds.OutputPathAncestorConflict]);
    }

    [Test]
    public async Task Validate_ReportsReservationsAndUnsafeOutputPaths()
    {
        var table = new SiteRouteTable();
        var routeLocation = new SiteSourceLocation("content/page.md", 12, 4);
        var invalidLocation = new SiteSourceLocation("template.yml", 3, 1);
        table.ReserveOutputPath("search-index.json", "artifact:search");
        table.Register(SiteRoute.ForFile("SEARCH-INDEX.JSON"), "page:search", routeLocation);
        table.ReserveOutputPath("../outside.html", "artifact:unsafe", invalidLocation);

        var result = table.Validate();

        await Assert.That(result.Diagnostics.Select(diagnostic => diagnostic.Id))
            .IsEquivalentTo(
            [
                SiteRouteDiagnosticIds.ReservedOutputPathCollision,
                SiteRouteDiagnosticIds.InvalidRoute,
            ]);
        var reservationCollision = result.Diagnostics.Single(
            diagnostic => diagnostic.Id == SiteRouteDiagnosticIds.ReservedOutputPathCollision);
        await Assert.That(reservationCollision.Location).IsEqualTo(routeLocation);
        var invalidRoute = result.Diagnostics.Single(
            diagnostic => diagnostic.Id == SiteRouteDiagnosticIds.InvalidRoute);
        await Assert.That(invalidRoute.Location).IsEqualTo(invalidLocation);
    }

    [Test]
    public async Task Validate_DetectsReservationsOwnedByDifferentGenerators()
    {
        var table = new SiteRouteTable();
        table.ReserveOutputPath("feed.xml", "artifact:feed");
        table.ReserveOutputPath("FEED.XML", "template:feed");

        var result = table.Validate();

        await Assert.That(result.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(result.Diagnostics[0].Id)
            .IsEqualTo(SiteRouteDiagnosticIds.ReservedOutputPathCollision);
    }

    [Test]
    public async Task Validate_ReturnsDiagnosticsInDeterministicOrder()
    {
        var forward = CreateCollidingTable(reverse: false);
        var reverse = CreateCollidingTable(reverse: true);

        var forwardDiagnostics = forward.Validate().Diagnostics.Select(Describe).ToArray();
        var reverseDiagnostics = reverse.Validate().Diagnostics.Select(Describe).ToArray();

        await Assert.That(reverseDiagnostics.SequenceEqual(forwardDiagnostics)).IsTrue();
    }

    [Test]
    public async Task Validate_ReturnsAncestorDiagnosticsInDeterministicOrder()
    {
        var forward = CreateAncestorConflictTable(reverse: false);
        var reverse = CreateAncestorConflictTable(reverse: true);

        var forwardDiagnostics = forward.Validate().Diagnostics.Select(Describe).ToArray();
        var reverseDiagnostics = reverse.Validate().Diagnostics.Select(Describe).ToArray();

        await Assert.That(forwardDiagnostics.Length).IsEqualTo(3);
        await Assert.That(forwardDiagnostics.All(
            diagnostic => diagnostic.StartsWith(
                SiteRouteDiagnosticIds.OutputPathAncestorConflict,
                StringComparison.Ordinal))).IsTrue();
        await Assert.That(reverseDiagnostics.SequenceEqual(forwardDiagnostics)).IsTrue();
    }

    [Test]
    public async Task SourceLocation_RequiresOneBasedCoordinates()
    {
        await Assert.That(() => new SiteSourceLocation("content.md", 0, 1))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SiteSourceLocation("content.md", 1, 0))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SiteSourceLocation("content.md", column: 1))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Validate_ValidTableHasNoDiagnostics()
    {
        var table = new SiteRouteTable();
        table.Register(SiteRoute.ForDirectoryIndex(""), "page:home");
        table.Register(SiteRoute.ForFile("posts/guide.html"), "page:guide");
        table.ReserveOutputPath("feed.xml", "artifact:feed");

        var result = table.ValidateOrThrow();

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task Validate_ScalesToTenThousandUniqueRoutes()
    {
        var table = new SiteRouteTable();
        for (var index = 0; index < 10_000; index++)
        {
            table.Register(
                SiteRoute.ForFile($"posts/section-{index / 100:D3}/page-{index:D5}.html"),
                $"page:{index:D5}");
        }

        var result = table.ValidateOrThrow();

        await Assert.That(result.IsValid).IsTrue();
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    private static SiteRouteTable CreateCollidingTable(bool reverse)
    {
        var registrations = new[]
        {
            (SiteRoute.ForFile("same.html"), "owner:b", new SiteSourceLocation("b.md", 2, 3)),
            (SiteRoute.ForFile("same.html"), "owner:a", new SiteSourceLocation("a.md", 1, 2)),
            (SiteRoute.ForFile("Same.html"), "owner:c", new SiteSourceLocation("c.md", 3, 4)),
        };
        var table = new SiteRouteTable();
        foreach (var registration in reverse ? registrations.Reverse() : registrations)
        {
            table.Register(registration.Item1, registration.Item2, registration.Item3);
        }

        return table;
    }

    private static SiteRouteTable CreateAncestorConflictTable(bool reverse)
    {
        var claims = new Action<SiteRouteTable>[]
        {
            table => table.Register(
                SiteRoute.ForFile("routes/Foo"),
                "page:route-file",
                new SiteSourceLocation("routes-file.md", 1, 1)),
            table => table.Register(
                SiteRoute.ForFile("routes/foo/child.html"),
                "page:route-child",
                new SiteSourceLocation("routes-child.md", 2, 1)),
            table => table.ReserveOutputPath(
                "routes/Foo",
                "page:route-file",
                new SiteSourceLocation("routes-reservation.yml", 2, 2)),
            table => table.Register(
                SiteRoute.ForFile("mixed/ROOT/child.html"),
                "page:mixed-child",
                new SiteSourceLocation("mixed-child.md", 3, 1)),
            table => table.ReserveOutputPath(
                "mixed/root",
                "artifact:mixed-file",
                new SiteSourceLocation("mixed-file.yml", 4, 1)),
            table => table.ReserveOutputPath(
                "reserved/Data",
                "artifact:reserved-file",
                new SiteSourceLocation("reserved-file.yml", 5, 1)),
            table => table.ReserveOutputPath(
                "reserved/data/archive.json",
                "artifact:reserved-child",
                new SiteSourceLocation("reserved-child.yml", 6, 1)),
        };
        var table = new SiteRouteTable();
        foreach (var claim in reverse ? claims.Reverse() : claims)
        {
            claim(table);
        }

        return table;
    }

    private static string Describe(SiteDiagnostic diagnostic) =>
        $"{diagnostic.Id}|{diagnostic.Message}|{diagnostic.Location?.FilePath}|{diagnostic.Location?.Line}|{diagnostic.Location?.Column}";

    private static string DescribeLocation(SiteSourceLocation? location) =>
        $"{location?.FilePath}|{location?.Line}|{location?.Column}";
}
