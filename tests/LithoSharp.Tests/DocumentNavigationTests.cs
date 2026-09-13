using LithoSharp.Documentation;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class DocumentNavigationTests
{
    private const string BaseUrl = "https://example.test/";

    private static DocumentPage Page(string collection, string version, string locale, string id, string source, string title, string slug, bool unlisted = false) =>
        new(new(collection, version, locale, id), source,
            new DocumentFrontMatter { Title = title, Unlisted = unlisted },
            SiteRoute.ForDirectoryIndex($"route/{collection}/{version}/{locale}/{slug}", BaseUrl));

    private static DocumentCatalog GuideCatalog() => new([
        Page("guide", "v1", "en", "intro", "01-intro.md", "Intro", "intro"),
        Page("guide", "v1", "en", "setup", "02-setup.md", "Setup", "setup"),
        Page("guide", "v1", "en", "secret", "secret.md", "Secret", "secret", unlisted: true),
        Page("guide", "v1", "ja", "intro", "01-intro.md", "Intro", "intro"),
        Page("guide", "v2", "en", "intro", "01-intro.md", "Intro", "intro"),
        Page("api", "v1", "en", "intro", "intro.md", "API Intro", "intro"),
    ]);

    [Test]
    public async Task CrossReferencesResolveWithoutRereadingSidebars()
    {
        var snapshot = DocumentNavigationSnapshot.Create(GuideCatalog(), "guide", "v1", "en");

        await Assert.That(snapshot.Collection).IsEqualTo("guide");
        await Assert.That(snapshot.Sidebars.ContainsKey("default")).IsEqualTo(true);

        var foundSource = snapshot.TryGetBySourcePath("02-setup.md", out var setup);
        await Assert.That(foundSource).IsEqualTo(true);
        await Assert.That(setup!.Key).IsEqualTo(new DocumentKey("guide", "v1", "en", "setup"));
        await Assert.That(setup.Label).IsEqualTo("Setup");
        await Assert.That(setup.Publication).IsEqualTo(DocumentPublicationState.Published);
        await Assert.That(setup.Sidebars).IsEquivalentTo(["default"]);

        var foundPublic = snapshot.TryGetByPublicPath(setup.Route.PublicPath, out var byPublic);
        await Assert.That(foundPublic).IsEqualTo(true);
        await Assert.That(byPublic!.Key).IsEqualTo(setup.Key);

        var foundOutput = snapshot.TryGetByOutputPath(setup.Route.RelativeOutputPath, out var byOutput);
        await Assert.That(foundOutput).IsEqualTo(true);
        await Assert.That(byOutput!.Key).IsEqualTo(setup.Key);

        await Assert.That(snapshot.TryGetByPublicPath("/guide/unknown/", out _)).IsEqualTo(false);
        await Assert.That(snapshot.TryGetByOutputPath("route/guide/v1/en/unknown/index.html", out _)).IsEqualTo(false);
        await Assert.That(snapshot.TryGetBySourcePath("missing.md", out _)).IsEqualTo(false);
        await Assert.That(snapshot.TryGet(new DocumentKey("guide", "v1", "en", "missing"), out _)).IsEqualTo(false);

        // Same id in another collection resolves only within its own scope.
        await Assert.That(snapshot.TryGet(new DocumentKey("api", "v1", "en", "intro"), out _)).IsEqualTo(false);
        var api = DocumentNavigationSnapshot.Create(GuideCatalog(), "api", "v1", "en");
        var foundApi = api.TryGetBySourcePath("intro.md", out var apiIntro);
        await Assert.That(foundApi).IsEqualTo(true);
        await Assert.That(apiIntro!.Route.PublicPath).IsNotEqualTo(setup.Route.PublicPath);
    }

    [Test]
    public async Task UnlistedIsRoutableButExcludedFromNavigation()
    {
        var snapshot = DocumentNavigationSnapshot.Create(GuideCatalog(), "guide", "v1", "en");

        var found = snapshot.TryGetBySourcePath("secret.md", out var secret);
        await Assert.That(found).IsEqualTo(true);
        await Assert.That(secret!.Publication).IsEqualTo(DocumentPublicationState.Unlisted);
        await Assert.That(secret.Sidebars.Count).IsEqualTo(0);
        await Assert.That(snapshot.Breadcrumbs(secret.Key).Count).IsEqualTo(0);

        var intro = new DocumentKey("guide", "v1", "en", "intro");
        var (previous, next) = snapshot.Adjacent(intro);
        await Assert.That(previous).IsNull();
        await Assert.That(next!.Key.Id).IsEqualTo("setup");
    }

    [Test]
    public async Task PrevNextHonorsSidebarSelection()
    {
        var catalog = new DocumentCatalog([
            new DocumentPage(new("guide", "v1", "en", "intro"), "01-intro.md",
                new DocumentFrontMatter { Title = "Intro", DisplayedSidebar = "other" },
                SiteRoute.ForDirectoryIndex("route/guide/v1/en/intro", BaseUrl)),
            Page("guide", "v1", "en", "setup", "02-setup.md", "Setup", "setup"),
        ]);
        var sidebars = new Dictionary<string, IReadOnlyList<SidebarItem>>(StringComparer.Ordinal)
        {
            ["main"] = [new SidebarItem("doc", Id: "intro"), new SidebarItem("doc", Id: "setup")],
            ["other"] = [new SidebarItem("doc", Id: "setup"), new SidebarItem("doc", Id: "intro")],
        };
        var snapshot = DocumentNavigationSnapshot.Create(catalog, "guide", "v1", "en", sidebars);

        var intro = new DocumentKey("guide", "v1", "en", "intro");
        var (displayedPrevious, displayedNext) = snapshot.Adjacent(intro);
        await Assert.That(displayedPrevious!.Key.Id).IsEqualTo("setup");
        await Assert.That(displayedNext).IsNull();

        var (mainPrevious, mainNext) = snapshot.Adjacent(intro, "main");
        await Assert.That(mainPrevious).IsNull();
        await Assert.That(mainNext!.Key.Id).IsEqualTo("setup");

        var crumbs = snapshot.Breadcrumbs(intro);
        await Assert.That(crumbs.Count).IsEqualTo(1);
        var unknownCrumbs = snapshot.Breadcrumbs(new DocumentKey("guide", "v1", "en", "missing"));
        await Assert.That(unknownCrumbs.Count).IsEqualTo(0);
        var (unknownPrevious, unknownNext) = snapshot.Adjacent(new DocumentKey("guide", "v1", "en", "missing"));
        await Assert.That(unknownPrevious).IsNull();
        await Assert.That(unknownNext).IsNull();
    }

    [Test]
    public async Task CategoryHierarchyIsExposed()
    {
        var catalog = new DocumentCatalog([
            Page("guide", "v1", "en", "tutorials/basics", "tutorials/01-basics.md", "Basics", "basics"),
            Page("guide", "v1", "en", "tutorials/advanced", "tutorials/02-advanced.md", "Advanced", "advanced"),
        ]);
        var categories = new Dictionary<string, DocumentCategory>(StringComparer.Ordinal)
        {
            ["tutorials"] = new DocumentCategory { Label = "Tutorials" },
        };
        var snapshot = DocumentNavigationSnapshot.Create(
            new DocumentCatalog(GuideCatalog().Pages.Concat(catalog.Pages)), "guide", "v1", "en",
            categories: categories);

        var advanced = new DocumentKey("guide", "v1", "en", "tutorials/advanced");
        var crumbs = snapshot.Breadcrumbs(advanced);
        await Assert.That(crumbs.Count).IsEqualTo(2);
        await Assert.That(crumbs[0].Label).IsEqualTo("Tutorials");
        await Assert.That(crumbs[0].Children.Count).IsEqualTo(2);
        await Assert.That(crumbs[1].Document).IsEqualTo(advanced);

        var (previous, next) = snapshot.Adjacent(advanced);
        await Assert.That(previous!.Key.Id).IsEqualTo("tutorials/basics");
        await Assert.That(next!.Key.Id).IsEqualTo("setup");
    }

    [Test]
    public async Task MissingVariantsAreExplicit()
    {
        var snapshot = DocumentNavigationSnapshot.Create(GuideCatalog(), "guide", "v1", "en");

        var setup = snapshot.Variants("setup");
        await Assert.That(setup.Count).IsEqualTo(3);
        await Assert.That(setup[0].Version).IsEqualTo("v1");
        await Assert.That(setup[0].Locale).IsEqualTo("en");
        await Assert.That(setup[0].Exists).IsEqualTo(true);
        await Assert.That(setup[0].Route).IsNotNull();
        await Assert.That(setup[1].Exists).IsEqualTo(false);
        await Assert.That(setup[1].Route).IsNull();
        await Assert.That(setup[2].Exists).IsEqualTo(false);

        var intro = snapshot.Variants("intro");
        await Assert.That(intro.All(link => link.Exists)).IsEqualTo(true);

        await Assert.That(snapshot.Variants("unknown-id").Count).IsEqualTo(0);
    }

    [Test]
    public async Task CollisionsAndUnknownsAreExplicit()
    {
        var duplicate = Page("guide", "v1", "en", "copy", "copy.md", "Copy", "intro");
        await Assert.That(() => new DocumentCatalog([
            Page("guide", "v1", "en", "intro", "01-intro.md", "Intro", "intro"), duplicate]))
            .Throws<SiteRouteValidationException>();

        var snapshot = DocumentNavigationSnapshot.Create(GuideCatalog(), "guide", "v1", "en");
        await Assert.That(() => snapshot.Adjacent(new DocumentKey("guide", "v1", "en", "intro"), "missing-sidebar"))
            .Throws<ArgumentException>();
        await Assert.That(() => DocumentNavigationSnapshot.Create(GuideCatalog(), "guide", "v1", "en", defaultSidebar: "missing-sidebar"))
            .Throws<ArgumentException>();
    }
}
