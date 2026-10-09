using LithoSharp.Content;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class StaticManifestRuntimeTests
{
    [Test]
    public async Task ClosedLookupUsesImmutableCatalogSnapshotAndExactPublicPaths()
    {
        var entries = new[] { new StaticContentCatalogEntry(new("intro"), "intro.md", SiteRoute.ForDirectoryIndex("日本語/")) };
        var catalog = new StaticContentCatalog(new("guides"), entries, "docs", "ja");
        var manifest = StaticSiteManifest.Create(catalog, new string('a', 64));
        entries[0] = new(new("other"), "other.md", SiteRoute.ForDirectoryIndex("other/"));
        var path = catalog.Entries.Single().Route.PublicPath;
        await Assert.That(manifest.GetUrl(path).ToString()).IsEqualTo(SiteUrl.FromRoute(catalog.Entries.Single().Route).ToString());
        await Assert.That(manifest.RouteCoverage).IsEqualTo(StaticCoverageState.Closed);
        await Assert.That(manifest.AnchorCoverage).IsEqualTo(StaticCoverageState.Open);
        await Assert.That(manifest.AssetCoverage).IsEqualTo(StaticCoverageState.Open);
        await Assert.That(() => manifest.GetUrl("/missing/")).Throws<KeyNotFoundException>();
        await Assert.That(() => manifest.GetUrl(path + "#fragment")).Throws<KeyNotFoundException>();
        await Assert.That(() => StaticSiteManifest.Create(catalog, "invalid")).Throws<ArgumentException>();
    }

    [Test]
    public async Task OpenResolverCanSupplyTheSameMissingPath()
    {
        var catalog = new StaticContentCatalog(new("guides"), []);
        var calls = 0;
        var manifest = StaticSiteManifest.Open(catalog, path => { calls++; return SiteUrl.FromRoute(SiteRoute.ForDirectoryIndex(path.Trim('/'))); });
        await Assert.That(manifest.GetUrl("/missing/").ToString()).IsEqualTo("/missing/");
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(manifest.RouteCoverage).IsEqualTo(StaticCoverageState.Open);
    }
}
