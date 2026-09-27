using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Documentation;
using LithoSharp.Inspection;

namespace LithoSharp.Tests;

/// <summary>V110-10: project-aware inspection resolves routes and validates front
/// matter from an explicitly acquired context. Without a context it stays
/// syntax-only and never guesses project rules.</summary>
public sealed class ProjectAwareInspectionTests
{
    private static ProjectInspectionSnapshot DocsSnapshot(
        string projectId = "docs",
        long generation = 1,
        string version = "v1",
        string locale = "en",
        IEnumerable<ProjectRouteCandidate>? routes = null) =>
        ProjectInspectionSnapshot.Create(
            projectId, generation, "docs", ToolingCapabilities.LanguageMarkdown, "document",
            version, locale,
            routes ?? [new ProjectRouteCandidate("intro.md", "/docs/intro/", projectId, "docs", version, locale, DocumentPublicationState.Published)]);

    private static DocumentInfo Inspect(
        string sourcePath, string text, ProjectInspectionSnapshot? project,
        string? version = null, string? locale = null, string? route = null) =>
        DocumentInspection.Inspect(sourcePath, text, new DocumentInspectionOptions
        {
            Project = project,
            Version = version,
            Locale = locale,
            Route = route,
        });

    private const string Body = "---\ntitle: Hello\n---\n# Hello\n";

    [Test]
    public async Task NoContext_KeepsLegacyEchoBehavior()
    {
        var info = Inspect("docs/a.md", Body, project: null, route: "/docs/a/", version: "v1", locale: "en");

        await Assert.That(info.ProjectStatus).IsEqualTo(DocumentProjectStatus.NoContext);
        await Assert.That(info.RouteCandidates.Count).IsEqualTo(0);
        await Assert.That(info.SchemaName).IsNull();
        await Assert.That(info.Route).IsEqualTo("/docs/a/");
        await Assert.That(info.Diagnostics.Any(item => item.Id.StartsWith("LSC"))).IsFalse();
    }

    [Test]
    public async Task DocsContext_ResolvesSingleRoute()
    {
        var info = Inspect("intro.md", Body, DocsSnapshot(), version: "v1", locale: "en");

        await Assert.That(info.ProjectStatus).IsEqualTo(DocumentProjectStatus.Resolved);
        await Assert.That(info.Route).IsEqualTo("/docs/intro/");
        await Assert.That(info.SchemaName).IsEqualTo("document");
        var candidate = info.RouteCandidates.Single();
        await Assert.That(candidate.PublicPath).IsEqualTo("/docs/intro/");
        await Assert.That(candidate.ProjectId).IsEqualTo("docs");
        await Assert.That(candidate.Collection).IsEqualTo("docs");
        await Assert.That(candidate.Version).IsEqualTo("v1");
        await Assert.That(candidate.Locale).IsEqualTo("en");
    }

    [Test]
    public async Task SameSourceWithTwoRoutes_StaysAmbiguous()
    {
        var project = DocsSnapshot(routes:
        [
            new ProjectRouteCandidate("guide.md", "/docs/guide/", "docs", "docs", "v1", "en", DocumentPublicationState.Published),
            new ProjectRouteCandidate("guide.md", "/docs/guide-2/", "docs", "docs", "v1", "en", DocumentPublicationState.Published),
        ]);

        var info = Inspect("guide.md", Body, project, version: "v1", locale: "en");

        await Assert.That(info.ProjectStatus).IsEqualTo(DocumentProjectStatus.Ambiguous);
        await Assert.That(info.Route).IsNull();
        await Assert.That(info.RouteCandidates.Count).IsEqualTo(2);
    }

    [Test]
    public async Task UnknownSource_IsUnresolvedWithNullRoute()
    {
        var info = Inspect("missing.md", Body, DocsSnapshot(), version: "v1", locale: "en");

        await Assert.That(info.ProjectStatus).IsEqualTo(DocumentProjectStatus.Unresolved);
        await Assert.That(info.Route).IsNull();
        await Assert.That(info.RouteCandidates.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DraftSource_IsDraftWithNullRoute()
    {
        const string draft = "---\ntitle: Wip\ndraft: true\n---\n# Wip\n";
        var info = Inspect("wip.md", draft, DocsSnapshot(), version: "v1", locale: "en");

        await Assert.That(info.ProjectStatus).IsEqualTo(DocumentProjectStatus.Draft);
        await Assert.That(info.Route).IsNull();
    }

    [Test]
    public async Task UnlistedCandidate_StaysResolved()
    {
        var project = DocsSnapshot(routes:
        [
            new ProjectRouteCandidate("secret.md", "/docs/secret/", "docs", "docs", "v1", "en", DocumentPublicationState.Unlisted),
        ]);

        var info = Inspect("secret.md", Body, project, version: "v1", locale: "en");

        await Assert.That(info.ProjectStatus).IsEqualTo(DocumentProjectStatus.Resolved);
        await Assert.That(info.Route).IsEqualTo("/docs/secret/");
        await Assert.That(info.RouteCandidates.Single().Publication).IsEqualTo(DocumentPublicationState.Unlisted);
    }

    [Test]
    public async Task DocsUnknownField_MatchesBuildDiagnostic()
    {
        const string text = "---\ntitle: Hello\nunknown_field_xyz: 1\n---\n# Hello\n";
        var info = Inspect("intro.md", text, DocsSnapshot(), version: "v1", locale: "en");

        var diagnostic = info.Diagnostics.Single(item => item.Id == ContentFrontMatterDiagnosticIds.UnknownField);
        await Assert.That(diagnostic.Severity).IsEqualTo(LithoSharp.Diagnostics.SiteDiagnosticSeverity.Error);

        // Same input through the real binder: identical ID, severity, and position.
        var split = FrontMatterSplitter.TrySplit(text);
        var parsed = MarkdownContentCollectionLoader<DocumentFrontMatter>.ParseYaml(split.Yaml, "intro.md", 2, default);
        var expected = new ReflectionContentFrontMatterBinder<DocumentFrontMatter>()
            .Bind(parsed.Value!, new LithoSharp.Diagnostics.SiteSourceLocation("intro.md", 2, 1)).Diagnostics.Single();
        await Assert.That(expected.Id).IsEqualTo(diagnostic.Id);
        await Assert.That(expected.Severity).IsEqualTo(diagnostic.Severity);
        await Assert.That(expected.Location?.Line).IsEqualTo(diagnostic.Location?.Line);
        await Assert.That(expected.Location?.Column).IsEqualTo(diagnostic.Location?.Column);
    }

    [Test]
    public async Task BlogSchema_UsesPostBinder()
    {
        var project = ProjectInspectionSnapshot.Create(
            "blog", 1, "blog", ToolingCapabilities.LanguageMarkdown, "post", "v1", "en",
            [new ProjectRouteCandidate("first.md", "/blog/first/", "blog", "blog", "v1", "en", DocumentPublicationState.Published)]);
        const string text = "---\ntitle: First\nunknown_field_xyz: 1\n---\n# First\n";
        var info = Inspect("first.md", text, project, version: "v1", locale: "en");

        await Assert.That(info.SchemaName).IsEqualTo("post");
        await Assert.That(info.Diagnostics.Any(item => item.Id == ContentFrontMatterDiagnosticIds.UnknownField)).IsTrue();
        await Assert.That(info.ProjectStatus).IsEqualTo(DocumentProjectStatus.Resolved);
    }

    [Test]
    public async Task CustomSchema_DoesNotForceDocsRulesOrExecute()
    {
        var project = ProjectInspectionSnapshot.Create(
            "custom", 1, "custom", ToolingCapabilities.LanguageMarkdown, "my-custom-type", "v1", "en",
            [new ProjectRouteCandidate("page.md", "/custom/page/", "custom", "custom", "v1", "en", DocumentPublicationState.Published)]);
        // Would fail a Docs bind (unknown field, wrong type) but custom schemas are never bound here.
        const string text = "---\ntitle: 123\nunknown_field_xyz: [unclosed\n---\n# Page\n";
        var yaml = FrontMatterSplitter.TrySplit(text);
        await Assert.That(yaml.Status).IsEqualTo(FrontMatterSplitStatus.Ok);

        var info = Inspect("page.md", "---\ntitle: 123\nunknown_field_xyz: 1\n---\n# Page\n", project);

        await Assert.That(info.SchemaName).IsEqualTo("my-custom-type");
        await Assert.That(info.Diagnostics.Any(item => item.Id.StartsWith("LSC"))).IsFalse();
        await Assert.That(info.ProjectStatus).IsEqualTo(DocumentProjectStatus.Resolved);
        await Assert.That(info.Route).IsEqualTo("/custom/page/");
    }

    [Test]
    [Arguments("docs/a.md", "# Hi\n", "LSM001")]
    [Arguments("docs/a.md", "---\ntitle: x\n", "LSM002")]
    [Arguments("docs/a.md", "---\n---\n# Hi\n", "LSM003")]
    [Arguments("docs/a.md", "---\ntitle: [unclosed\n---\n# Hi\n", "LSM004")]
    public async Task BrokenFrontMatter_KeepsSplitDiagnosticsWithContext(string source, string text, string id)
    {
        var info = Inspect(source, text, DocsSnapshot());

        await Assert.That(info.Diagnostics.Any(item => item.Id == id)).IsTrue();
        await Assert.That(info.ProjectStatus).IsEqualTo(DocumentProjectStatus.Unresolved);
        await Assert.That(info.Route).IsNull();
    }

    [Test]
    public async Task SameNameAcrossVariants_ResolvesPerContext()
    {
        var v1 = DocsSnapshot(version: "v1", locale: "en", routes:
            [new ProjectRouteCandidate("intro.md", "/v1/intro/", "docs", "docs", "v1", "en", DocumentPublicationState.Published)]);
        var v2 = DocsSnapshot(version: "v2", locale: "ja", routes:
            [new ProjectRouteCandidate("intro.md", "/v2/intro/", "docs", "docs", "v2", "ja", DocumentPublicationState.Published)]);

        var first = Inspect("intro.md", Body, v1, version: "v1", locale: "en");
        var second = Inspect("intro.md", Body, v2, version: "v2", locale: "ja");

        await Assert.That(first.Route).IsEqualTo("/v1/intro/");
        await Assert.That(second.Route).IsEqualTo("/v2/intro/");
    }

    [Test]
    public async Task SameSourceAcrossProjects_ResolvesPerProject()
    {
        var docs = DocsSnapshot(projectId: "docs", routes:
            [new ProjectRouteCandidate("shared.md", "/docs/shared/", "docs", "docs", "v1", "en", DocumentPublicationState.Published)]);
        var blog = ProjectInspectionSnapshot.Create(
            "blog", 1, "blog", ToolingCapabilities.LanguageMarkdown, "post", "v1", "en",
            [new ProjectRouteCandidate("shared.md", "/blog/shared/", "blog", "blog", "v1", "en", DocumentPublicationState.Published)]);

        var first = Inspect("shared.md", Body, docs);
        var second = Inspect("shared.md", Body, blog);

        await Assert.That(first.Route).IsEqualTo("/docs/shared/");
        await Assert.That(second.Route).IsEqualTo("/blog/shared/");
    }

    [Test]
    public async Task GenerationChange_ResolvesFromSuppliedSnapshot()
    {
        var old = DocsSnapshot(generation: 1, routes:
            [new ProjectRouteCandidate("intro.md", "/old/intro/", "docs", "docs", "v1", "en", DocumentPublicationState.Published)]);
        var current = DocsSnapshot(generation: 2, routes:
            [new ProjectRouteCandidate("intro.md", "/new/intro/", "docs", "docs", "v1", "en", DocumentPublicationState.Published)]);

        await Assert.That(Inspect("intro.md", Body, old).Route).IsEqualTo("/old/intro/");
        await Assert.That(Inspect("intro.md", Body, current).Route).IsEqualTo("/new/intro/");
        await Assert.That(old.ProjectGeneration).IsNotEqualTo(current.ProjectGeneration);
    }

    [Test]
    public async Task Snapshot_RoundTripsThroughJson()
    {
        var snapshot = DocsSnapshot();
        var restored = ProjectInspectionSnapshot.ParseJson(snapshot.ToJson());

        await Assert.That(restored.ProjectId).IsEqualTo(snapshot.ProjectId);
        await Assert.That(restored.ProjectGeneration).IsEqualTo(snapshot.ProjectGeneration);
        await Assert.That(restored.CoreVersion).IsEqualTo(snapshot.CoreVersion);
        await Assert.That(restored.Collection).IsEqualTo(snapshot.Collection);
        await Assert.That(restored.Language).IsEqualTo(snapshot.Language);
        await Assert.That(restored.SchemaName).IsEqualTo(snapshot.SchemaName);
        await Assert.That(restored.Version).IsEqualTo(snapshot.Version);
        await Assert.That(restored.Locale).IsEqualTo(snapshot.Locale);
        await Assert.That(restored.Routes.Count).IsEqualTo(1);
        await Assert.That(restored.Routes[0].PublicPath).IsEqualTo("/docs/intro/");
    }

    [Test]
    public async Task Snapshot_IsDeeplyImmutable()
    {
        var routes = new List<ProjectRouteCandidate>
        {
            new("intro.md", "/docs/intro/", "docs", "docs", "v1", "en", DocumentPublicationState.Published),
        };
        var snapshot = DocsSnapshot(routes: routes);
        routes.Clear();
        routes.Add(new ProjectRouteCandidate("other.md", "/other/", "docs", "docs", "v1", "en", DocumentPublicationState.Published));

        await Assert.That(snapshot.Routes.Count).IsEqualTo(1);
        await Assert.That(snapshot.Routes[0].SourcePath).IsEqualTo("intro.md");
        await Assert.That(() => ((IList<ProjectRouteCandidate>)snapshot.Routes)[0] =
            new ProjectRouteCandidate("x.md", "/x/", "docs", "docs", "v1", "en", DocumentPublicationState.Published))
            .Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("{not json")]
    [Arguments("{\"schemaVersion\":\"1.0\"}")]
    [Arguments("{\"schemaVersion\":\"2.0\",\"projectId\":\"x\",\"projectGeneration\":1,\"collection\":\"c\",\"version\":\"v\",\"locale\":\"l\"}")]
    public async Task Snapshot_RejectsUnknownShapes(string? json)
    {
        await Assert.That(() => ProjectInspectionSnapshot.ParseJson(json!)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Inspection_DoesNotWriteOutputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        DocumentInspection.Inspect(
            Path.Combine(directory, "a.md"), Body,
            new DocumentInspectionOptions { Project = DocsSnapshot() });

        await Assert.That(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length).IsEqualTo(0);
    }
}
