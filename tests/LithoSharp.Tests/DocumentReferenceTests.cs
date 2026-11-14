using LithoSharp.Content;
using LithoSharp.Documentation;
using LithoSharp.Inspection;
using LithoSharp.OpenKnowledge;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class DocumentReferenceTests
{
    private static ProjectRouteCandidate Route(string path, string url, string version = "v1", string locale = "en") =>
        new(path, url, "project", "docs", version, locale, DocumentPublicationState.Published);
    private static ProjectInspectionSnapshot Project(params ProjectRouteCandidate[] routes) =>
        ProjectInspectionSnapshot.Create("project", 7, "docs", "markdown", "document", "v1", "en", routes)
            .WithMarkdownProfile(MarkdownOptions.DocumentationProfile);
    private static DocumentReferenceDocument Document(string name, string body, long version = 1) =>
        new("C:/site/content/" + name, body, version, "content/" + name);

    [Test]
    public async Task ApiChangesFindExactGuideAndDeclaredSnippetImpactsAgainstCapturedBuffers()
    {
        const string id = "M:Box.Read(System.Int32)";
        var key = new DocumentKey("api", "v1", "en", id);
        var variants = new[] { key, key with { Version = "v2" }, key with { Locale = "ja" } };
        var pages = variants.Select((api, index) => new DocumentPage(api, "api.xml", new() { Title = "Read" },
            SiteRoute.ForDirectoryIndex("api/" + index))).Append(new(key with { Id = "M:Box.Read(System.String)" },
                "api.xml", new() { Title = "Read overload" }, SiteRoute.ForDirectoryIndex("api/string")));
        var project = Project(Route("content/source.md", "/source/")).WithSemantics("sha256:" + new string('a', 64),
            new(new(pages), [], true), new Dictionary<string, DocumentSemanticRuleSet>());
        var source = Document("source.md", """
            ---
            title: Source
            ---
            [Read][api] [Other overload](xref:M:Box.Read(System.String))
            [Public](/api/0/) [Relative](../api/0/)
            [Shared API source](../api.xml)
            `[Inline](xref:M:Box.Read(System.Int32))`

            [api]: xref:M:Box.Read(System.Int32)
            [unused]: xref:M:Box.Read(System.Int32)

            ```csharp snippet="read"
            // [Code](xref:M:Box.Read(System.Int32))
            ```
            """, version: 7);
        var declared = new[] { key };
        var snippet = new DocumentSnippet("read", "content/source.md", "checks.csproj") { ApiReferences = declared };
        declared[0] = variants[1];
        var changes = ApiReferenceSite.Compare([new(id, "Read", "", "")], []).ToArray();
        var selected = variants.ToDictionary(api => api, _ => (IReadOnlyList<ApiChange>)changes);
        var snapshot = new DocumentReferenceSnapshot(project, [source], catalogComplete: true);
        var report = snapshot.FindApiImpacts(selected, [snippet]);
        changes[0] = new(id, "documentation");
        selected.Clear();

        await Assert.That(report.Impacts.Count).IsEqualTo(4);
        await Assert.That(report.Impacts.All(impact => impact.Api == key && impact.Changes.Single().Kind == "removed")).IsTrue();
        var guide = report.Impacts.Single(impact => impact.Location.Start == source.Text.IndexOf("[Read]", StringComparison.Ordinal));
        await Assert.That(guide.Location.Start).IsEqualTo(source.Text.IndexOf("[Read]", StringComparison.Ordinal));
        await Assert.That(source.Text.Substring(guide.Location.Start, guide.Location.Length)).IsEqualTo("[Read][api]");
        await Assert.That(snapshot.ResolveAt(source.SourcePath, source.Text.IndexOf("[Public]", StringComparison.Ordinal)).Candidates.Single().Route.Collection).IsEqualTo("api");
        await Assert.That(report.Impacts.Count(impact => impact.SnippetId is null)).IsEqualTo(3);
        await Assert.That(snapshot.ResolveAt(source.SourcePath, source.Text.IndexOf("[Shared API source]", StringComparison.Ordinal)).State)
            .IsEqualTo(DocumentReferenceState.Ambiguous);
        var example = report.Impacts.Single(impact => impact.SnippetId == "read");
        await Assert.That(example.Location.Start).IsEqualTo(source.Text.IndexOf("```", StringComparison.Ordinal));
        await Assert.That(example.Document.Version).IsEqualTo(7);
        await Assert.That(example.Document.TextHash).IsEqualTo(source.TextHash);
        await Assert.That(Document("source.md", source.Text + "Changed", 8).TextHash).IsNotEqualTo(example.Document.TextHash);
        await Assert.That(report.IsComplete).IsFalse();
        await Assert.That(report.ManualScopes.Any(scope => scope.Contains("C# usage is not extracted", StringComparison.Ordinal))).IsTrue();

        var collisionProject = Project(Route("content/source.md", "/source/"), Route("content/shadow.md", "/api/0/"))
            .WithSemantics("sha256:" + new string('a', 64), project.Semantics, new Dictionary<string, DocumentSemanticRuleSet>());
        var collision = new DocumentReferenceSnapshot(collisionProject, [source], catalogComplete: true);
        await Assert.That(collision.ResolveAt(source.SourcePath, source.Text.IndexOf("[Public]", StringComparison.Ordinal)).State)
            .IsEqualTo(DocumentReferenceState.Ambiguous);
        var collisionReport = collision.FindApiImpacts(new Dictionary<DocumentKey, IReadOnlyList<ApiChange>> { [key] = [new(id, "removed")] }, [snippet]);
        await Assert.That(collisionReport.Impacts.Count).IsEqualTo(2);

        var duplicateProject = Project(Route("content/source.md", "/source/"),
            new("api.xml", "/api/0/", "project", "api", "v1", "en", DocumentPublicationState.Published),
            new("other-api.xml", "/api/0/", "project", "api", "v1", "en", DocumentPublicationState.Published))
            .WithSemantics("sha256:" + new string('a', 64), project.Semantics, new Dictionary<string, DocumentSemanticRuleSet>());
        var duplicate = new DocumentReferenceSnapshot(duplicateProject, [source], catalogComplete: true);
        await Assert.That(duplicate.ResolveAt(source.SourcePath, source.Text.IndexOf("[Read]", StringComparison.Ordinal)).State)
            .IsEqualTo(DocumentReferenceState.Ambiguous);
        var duplicateReport = duplicate.FindApiImpacts(new Dictionary<DocumentKey, IReadOnlyList<ApiChange>> { [key] = [new(id, "removed")] }, [snippet]);
        await Assert.That(duplicateReport.Impacts.Single().SnippetId).IsEqualTo("read");
    }

    [Test]
    public async Task AmbiguousOrRemovedCatalogTargetsAndUndeclaredExamplesRemainManual()
    {
        const string id = "T:Box";
        var key = new DocumentKey("api", "v1", "en", id);
        var pages = new[] { key, key with { Collection = "other-api" } }.Select((api, index) =>
            new DocumentPage(api, "api.xml", new() { Title = "Box" }, SiteRoute.ForDirectoryIndex("api/" + index)));
        var source = Document("source.md", "[Box](xref:T:Box)\n\n```csharp snippet=\"box\"\nnew Box();\n```\n");
        var dynamic = new DocumentReferenceDocument("C:/site/content/dynamic.mdx", "[Box](xref:T:Box)", 1,
            "content/dynamic.mdx", isDynamic: true);
        var project = Project(Route("content/source.md", "/source/"), Route("content/dynamic.mdx", "/dynamic/"));
        var selected = new Dictionary<DocumentKey, IReadOnlyList<ApiChange>> { [key] = [new(id, "removed")] };
        var snippet = new DocumentSnippet("box", "content/source.md", "checks.csproj");
        var ambiguous = new DocumentReferenceSnapshot(project.WithSemantics("sha256:" + new string('a', 64),
            new(new(pages), [], true), new Dictionary<string, DocumentSemanticRuleSet>()), [source, dynamic], true);
        var report = ambiguous.FindApiImpacts(selected, [snippet]);
        await Assert.That(report.Impacts.Count).IsEqualTo(0);
        await Assert.That(report.ManualScopes.Any(scope => scope.Contains("could not be assessed", StringComparison.Ordinal))).IsTrue();
        await Assert.That(report.ManualScopes.Any(scope => scope.Contains("No API references", StringComparison.Ordinal))).IsTrue();
        await Assert.That(report.ManualScopes.Any(scope => scope.Contains("Dynamic MDX", StringComparison.Ordinal))).IsTrue();

        var afterRemoval = new DocumentReferenceSnapshot(project.WithSemantics("sha256:" + new string('b', 64),
            new(new([]), [], true), new Dictionary<string, DocumentSemanticRuleSet>()), [source], true);
        var missing = afterRemoval.FindApiImpacts(selected, []);
        await Assert.That(missing.Impacts.Count).IsEqualTo(0);
        await Assert.That(missing.ManualScopes.Any(scope => scope.Contains("before removal", StringComparison.Ordinal))).IsTrue();
        await Assert.That(() => ambiguous.FindApiImpacts(selected, [snippet, snippet])).Throws<ArgumentException>();
        await Assert.That(() => ambiguous.FindApiImpacts(new Dictionary<DocumentKey, IReadOnlyList<ApiChange>>
        { [key] = [new("T:Different", "removed")] }, [])).Throws<ArgumentException>();
        await Assert.That(() => new DocumentSnippet("box", "content/source.md", "checks.csproj")
        { ApiReferences = [key, key] }).Throws<ArgumentException>();
        await Assert.That(() => ambiguous.FindApiImpacts(selected, [], new CancellationToken(true))).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ExactLinksUseTheSelectedContextAndUnknownCatalogsDoNotProveAbsence()
    {
        var source = Document("source.md", "---\ntitle: Source\n---\n[Guide](target.md#details) [Missing](not-created.md)\n");
        var target = Document("target.md", "---\ntitle: Target\n---\n# Target\n\n## Details\n");
        var project = Project(Route("content/source.md", "/docs/v1/en/source/"), Route("content/target.md", "/docs/v1/en/target/"),
            Route("content/other-version.md", "/docs/v2/en/target/", "v2"));
        var snapshot = new DocumentReferenceSnapshot(project, [source, target]);
        var resolved = snapshot.ResolveAt(source.SourcePath, source.Text.IndexOf("target.md", StringComparison.Ordinal));
        await Assert.That(resolved.State).IsEqualTo(DocumentReferenceState.Resolved);
        await Assert.That(resolved.Candidates.Single().Location!.Start).IsEqualTo(target.Text.IndexOf("Details", StringComparison.Ordinal));
        await Assert.That(resolved.Candidates.Single().Route.Version).IsEqualTo("v1");
        await Assert.That(snapshot.ResolveAt(source.SourcePath, source.Text.IndexOf("not-created", StringComparison.Ordinal)).State).IsEqualTo(DocumentReferenceState.Unavailable);
        await Assert.That(snapshot.Complete(source.SourcePath, "/docs/").All(completion => completion.Target.Route.Version == "v1")).IsTrue();
        await Assert.That(snapshot.Complete(source.SourcePath, "./").Any(completion => completion.InsertText == "./target.md")).IsTrue();
        var closed = new DocumentReferenceSnapshot(project, [source, target], catalogComplete: true);
        await Assert.That(closed.ResolveAt(source.SourcePath, source.Text.IndexOf("not-created", StringComparison.Ordinal)).State).IsEqualTo(DocumentReferenceState.Unresolved);
    }

    [Test]
    public async Task SharedReferenceDefinitionsAreEditedOnceAndEveryOriginalBufferIsGuarded()
    {
        var source = Document("source.md", "---\r\ntitle: Source\r\n---\r\n[One][guide] and [Two][guide]\r\n\r\n[guide]: <target.md#details> \"Existing title\"\r\n");
        var target = Document("target.md", "---\ntitle: Target\n---\n## Details\n");
        var other = Document("other.md", "---\ntitle: Other\n---\n## Next\n");
        var project = Project(Route("content/source.md", "/source/"), Route("content/target.md", "/target/"), Route("content/other.md", "/other/"));
        var snapshot = new DocumentReferenceSnapshot(project, [source, target, other], catalogComplete: true);
        var plan = snapshot.PlanTargetChange(source.SourcePath, source.Text.IndexOf("[One]", StringComparison.Ordinal), "other.md#next");
        await Assert.That(plan.CanApply).IsTrue();
        await Assert.That(plan.Edits.Count).IsEqualTo(1);
        await Assert.That(plan.IsComplete).IsFalse();
        await Assert.That(plan.TryApply([source, target, other], out var changed)).IsTrue();
        await Assert.That(changed[source.SourcePath]).IsEqualTo(source.Text.Replace("target.md#details", "other.md#next", StringComparison.Ordinal));
        await Assert.That(plan.TryApply([Document("source.md", source.Text, 2), target, other], out _)).IsFalse();
        await Assert.That(plan.TryApply([Document("source.md", source.Text + "changed", 1), target, other], out _)).IsFalse();
        var references = snapshot.FindReferences(target.SourcePath, target.Text.IndexOf("Details", StringComparison.Ordinal));
        await Assert.That(references.Locations.Count).IsEqualTo(3);
        await Assert.That(references.IsComplete).IsTrue();
    }

    [Test]
    public async Task HeadingRenameUsesNativeAnchorsAndUpdatesDuplicateAnchorOrdinals()
    {
        var source = Document("source.md", "---\ntitle: Source\n---\n[First](target.md#same) [Second](target.md#same-1)\n");
        var target = Document("target.md", "---\r\ntitle: Target\r\n---\r\n# Title\r\n\r\n## Same\r\n\r\n## Same\r\n");
        var snapshot = new DocumentReferenceSnapshot(Project(Route("content/source.md", "/source/"), Route("content/target.md", "/target/")),
            [source, target], catalogComplete: true);
        var plan = snapshot.PlanHeadingRename(target.SourcePath, target.Text.IndexOf("## Same", StringComparison.Ordinal) + 3, "Changed");
        await Assert.That(plan.CanApply).IsTrue();
        await Assert.That(plan.Edits.Count).IsEqualTo(3);
        await Assert.That(plan.TryApply([source, target], out var changed)).IsTrue();
        await Assert.That(changed[source.SourcePath]).Contains("[First](target.md#changed) [Second](target.md#same)");
        await Assert.That(changed[target.SourcePath]).Contains("## Changed\r\n\r\n## Same\r\n");
        var fragmentPlan = snapshot.PlanHeadingRename(source.SourcePath, source.Text.IndexOf("same-1", StringComparison.Ordinal), "Second renamed");
        await Assert.That(fragmentPlan.CanApply).IsTrue();
        await Assert.That(fragmentPlan.Edits.Any(edit => edit.SourcePath == target.SourcePath)).IsTrue();
    }

    [Test]
    public async Task MovesPreserveLabelsEscapingNewlinesAndRebaseOutgoingRelativePaths()
    {
        var source = Document("source.md", "---\r\ntitle: Source\r\n---\r\n[Label](./target.md#details \"Title\")\r\n[Public](/target/#details)\r\n");
        var target = Document("target.md", "---\ntitle: Target\n---\n## Details\n\n[Back](source.md)\n");
        var snapshot = new DocumentReferenceSnapshot(Project(Route("content/source.md", "/source/"), Route("content/target.md", "/target/")),
            [source, target], catalogComplete: true);
        var plan = snapshot.PlanMove(target.SourcePath, "C:/site/content/nested/renamed.md", "/renamed/", "content/nested/renamed.md");
        await Assert.That(plan.CanApply).IsTrue();
        await Assert.That(plan.TryApply([source, target], out var changed)).IsTrue();
        await Assert.That(changed[source.SourcePath]).Contains("[Label](./nested/renamed.md#details \"Title\")\r\n");
        await Assert.That(changed[source.SourcePath]).Contains("[Public](/renamed/#details)\r\n");
        await Assert.That(changed["C:/site/content/nested/renamed.md"]).Contains("[Back](../source.md)");
        await Assert.That(changed.ContainsKey(target.SourcePath)).IsFalse();
        await Assert.That(snapshot.PlanMove(target.SourcePath, "C:/outside.md").CanApply).IsFalse();
    }

    [Test]
    public async Task CommonMarkHeadersCodeAndDynamicInputsNeverBecomeGuessedReferences()
    {
        var source = Document("source.md", "---\nlink: \"[Metadata](target.md)\"\n---\n# Source\n\n[Body](target.md)\n\n`[Code](target.md)`\n");
        var target = Document("target.md", "---\ntitle: Target\n---\n# Target\n");
        var project = Project(Route("content/source.md", "/source/"), Route("content/target.md", "/target/")).WithMarkdownProfile(MarkdownOptions.CommonMarkProfile);
        var snapshot = new DocumentReferenceSnapshot(project, [source, target], catalogComplete: true);
        await Assert.That(snapshot.ResolveAt(source.SourcePath, source.Text.IndexOf("Metadata", StringComparison.Ordinal)).Reference).IsNull();
        await Assert.That(snapshot.ResolveAt(source.SourcePath, source.Text.IndexOf("Code", StringComparison.Ordinal)).Reference).IsNull();
        var body = snapshot.ResolveAt(source.SourcePath, source.Text.IndexOf("Body", StringComparison.Ordinal));
        await Assert.That(body.State).IsEqualTo(DocumentReferenceState.Resolved);
        await Assert.That(body.Reference!.Range.Start).IsEqualTo(source.Text.IndexOf("[Body]", StringComparison.Ordinal));
        var dynamic = new DocumentReferenceDocument(source.SourcePath, source.Text, source.Version, source.ProjectPath, isDynamic: true);
        var limited = new DocumentReferenceSnapshot(project, [dynamic, target], catalogComplete: true);
        await Assert.That(limited.ResolveAt(source.SourcePath, 0).State).IsEqualTo(DocumentReferenceState.Unavailable);
        await Assert.That(limited.ManualScopes.Any(scope => scope.Contains("Dynamic MDX", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task MovesKeepPublicRelativeFormsAndMakeUnknownNewRoutesManual()
    {
        var source = Document("source.md", "---\ntitle: Source\n---\n# Source\n\n[Other](../other/?mode=one&amp;view=two#details)\n");
        var other = Document("other.md", "---\ntitle: Other\n---\n## Details\n\n[Source](../old/#source)\n");
        var snapshot = new DocumentReferenceSnapshot(Project(Route("content/source.md", "/v1/old/"), Route("content/other.md", "/v1/other/")),
            [source, other], catalogComplete: true);
        var plan = snapshot.PlanMove(source.SourcePath, "C:/site/content/deep/new.md", "/v1/deep/new/", "content/deep/new.md");
        await Assert.That(plan.TryApply([source, other], out var changed)).IsTrue();
        await Assert.That(changed["C:/site/content/deep/new.md"]).Contains("[Other](../../other/?mode=one&amp;view=two#details)");
        await Assert.That(changed[other.SourcePath]).Contains("[Source](../deep/new/#source)");
        var unknownRoute = snapshot.PlanMove(source.SourcePath, "C:/site/content/deep/new.md", newProjectPath: "content/deep/new.md");
        await Assert.That(unknownRoute.ManualScopes.Any(scope => scope.Contains("supply the new route explicitly", StringComparison.Ordinal))).IsTrue();
        var stable = snapshot.PlanMove(source.SourcePath, "C:/site/content/deep/new.md", "/v1/old/", "content/deep/new.md");
        await Assert.That(stable.CanApply).IsTrue();
        await Assert.That(stable.Edits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AmbiguityIncompleteTargetsAndCancellationRemainExplicit()
    {
        var source = Document("source.md", "---\ntitle: Source\n---\n[Guide](target.md#details) [Api](xref:T:Missing)\n");
        var target = Document("target.md", "---\ntitle: Target\n---\n## Details\n");
        var project = Project(Route("content/source.md", "/source/"), Route("content/target.md", "/one/"), Route("content/target.md", "/two/"));
        var snapshot = new DocumentReferenceSnapshot(project, [source, target], catalogComplete: true);
        await Assert.That(snapshot.ResolveAt(source.SourcePath, source.Text.IndexOf("target.md", StringComparison.Ordinal)).State).IsEqualTo(DocumentReferenceState.Ambiguous);
        await Assert.That(snapshot.Complete(source.SourcePath, "target").Select(static item => item.InsertText)).IsEquivalentTo(["/one/", "/two/"]);
        await Assert.That(snapshot.ResolveAt(source.SourcePath, source.Text.IndexOf("xref:", StringComparison.Ordinal)).State).IsEqualTo(DocumentReferenceState.Unavailable);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => snapshot.FindReferences(source.SourcePath, 0, cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        var closedTarget = new DocumentReferenceSnapshot(Project(Route("content/source.md", "/source/"), Route("content/target.md", "/target/")), [source], catalogComplete: true);
        await Assert.That(closedTarget.ResolveAt(source.SourcePath, source.Text.IndexOf("target.md", StringComparison.Ordinal)).State).IsEqualTo(DocumentReferenceState.Unavailable);
        await Assert.That(closedTarget.ManualScopes.Count > 0).IsTrue();
    }

    [Test]
    public async Task OkfRelationsPreserveInputKindsBundleRootsAndAuthoritativeUnresolvedReceipts()
    {
        var root = Path.Combine(Path.GetTempPath(), "lithosharp-reference-bundle").Replace('\\', '/');
        ProjectRouteCandidate Input(string path, ContentInputKind kind, params ProjectReferenceRelation[] relations) =>
            new(root + "/" + path, "/mounted/knowledge/" + path, "project", "docs", "v1", "en", DocumentPublicationState.Published)
            {
                InputProfile = ContentInputProfile.OpenKnowledgeFormat,
                InputKind = kind,
                InputRoot = root,
                InputPath = path,
                Relations = relations,
            };
        var source = new DocumentReferenceDocument(root + "/source.md",
            "---\nunknown: &metadata \"[Hidden](/future.md)\"\n---\n[Concept](/nested/target.md#details) ![Preview](/nested/target.md#details) [Index](/nested/index.md) [Log](/log.md) [Asset](/data.bin#binary) [Future](/future.md) [New](/new.md) [Outside](../outside.md) [External](https://example.invalid/) [Missing](/missing.md)\n", 1);
        var target = new DocumentReferenceDocument(root + "/nested/target.md", "---\nextra: *unknown\n---\n## Details\n", 1);
        var sourceRoute = Input("source.md", ContentInputKind.Concept,
            new("/nested/target.md#details", root + "/nested/target.md", OkfLinkKind.Concept, false),
            new("/nested/target.md#details", root + "/nested/target.md", OkfLinkKind.Concept, true),
            new("/nested/index.md", root + "/nested/index.md", OkfLinkKind.ReservedDocument, false),
            new("/log.md", root + "/log.md", OkfLinkKind.ReservedDocument, false),
            new("/data.bin#binary", root + "/data.bin", OkfLinkKind.Auxiliary, false),
            new("/future.md", root + "/future.md", OkfLinkKind.Unresolved, false),
            new("../outside.md", null, OkfLinkKind.OutsideBundle, false),
            new("https://example.invalid/", null, OkfLinkKind.External, false));
        var project = Project(sourceRoute, Input("nested/target.md", ContentInputKind.Concept), Input("nested/index.md", ContentInputKind.Index),
            Input("log.md", ContentInputKind.Log), Input("data.bin", ContentInputKind.Auxiliary), Input("future.md", ContentInputKind.Concept), Input("new.md", ContentInputKind.Concept));
        var snapshot = new DocumentReferenceSnapshot(project, [source, target], catalogComplete: true);
        DocumentReferenceResolution At(string label) => snapshot.ResolveAt(source.SourcePath, source.Text.IndexOf("[" + label + "]", StringComparison.Ordinal));
        await Assert.That(At("Concept").State).IsEqualTo(DocumentReferenceState.Resolved);
        await Assert.That(At("Concept").RelationKind).IsEqualTo(OkfLinkKind.Concept);
        await Assert.That(At("Concept").Candidates.Single().InputKind).IsEqualTo(ContentInputKind.Concept);
        await Assert.That(At("Concept").Relation!.IsImage).IsFalse();
        await Assert.That(At("Preview").Relation!.IsImage).IsTrue();
        await Assert.That(At("Index").RelationKind).IsEqualTo(OkfLinkKind.ReservedDocument);
        await Assert.That(At("Index").Candidates.Single().InputKind).IsEqualTo(ContentInputKind.Index);
        await Assert.That(At("Log").Candidates.Single().InputKind).IsEqualTo(ContentInputKind.Log);
        await Assert.That(At("Asset").State).IsEqualTo(DocumentReferenceState.Resolved);
        await Assert.That(At("Asset").Candidates.Single().InputKind).IsEqualTo(ContentInputKind.Auxiliary);
        await Assert.That(At("Asset").Candidates.Single().Location).IsNull();
        await Assert.That(At("Future").State).IsEqualTo(DocumentReferenceState.Unavailable);
        await Assert.That(At("Future").RelationKind).IsEqualTo(OkfLinkKind.Unresolved);
        await Assert.That(At("New").State).IsEqualTo(DocumentReferenceState.Resolved);
        await Assert.That(At("Outside").State).IsEqualTo(DocumentReferenceState.Unavailable);
        await Assert.That(At("Outside").RelationKind).IsEqualTo(OkfLinkKind.OutsideBundle);
        await Assert.That(At("External").RelationKind).IsEqualTo(OkfLinkKind.External);
        await Assert.That(At("Missing").State).IsEqualTo(DocumentReferenceState.Unavailable);
        await Assert.That(At("Hidden").Reference).IsNull();
        await Assert.That(snapshot.Complete(source.SourcePath, "/").Any(completion => completion.InsertText == "/data.bin"
            && completion.Target.InputKind == ContentInputKind.Auxiliary)).IsTrue();
        await Assert.That(snapshot.Complete(source.SourcePath, "/mounted/").Count).IsEqualTo(0);
        await Assert.That(snapshot.ManualScopes.Any(scope => scope.Contains("No versioned editor snapshot", StringComparison.Ordinal)
            && scope.Contains("data.bin", StringComparison.Ordinal))).IsFalse();
        var plan = snapshot.PlanMove(target.SourcePath, root + "/moved/renamed.md", newProjectPath: root + "/moved/renamed.md");
        await Assert.That(plan.TryApply([source, target], out var changed)).IsTrue();
        await Assert.That(changed[source.SourcePath]).Contains("[Concept](/moved/renamed.md#details)");
        var outside = Path.GetFullPath(Path.Combine(root, "..", "outside.md")).Replace('\\', '/');
        await Assert.That(snapshot.PlanMove(target.SourcePath, outside, newProjectPath: outside).CanApply).IsFalse();
    }
}
