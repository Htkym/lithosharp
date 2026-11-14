using System.Text.Json.Nodes;
using LithoSharp.Content;
using LithoSharp.Documentation;
using LithoSharp.Inspection;
using LithoSharp.OpenKnowledge;

namespace LithoSharp.Tests;

public sealed class OkfInspectionTests
{
    private static string Root => Path.GetFullPath("inspection-bundle").Replace('\\', '/');
    private static string Source(string input) => Root + "/" + input;

    private static ProjectInspectionSnapshot Snapshot(long generation = 1, bool okf = true) => ProjectInspectionSnapshot.Create(
        "okf-inspection", generation, "knowledge", "markdown", "document", "current", "en",
        new[] { ("index.md", ContentInputKind.Index), ("log.md", ContentInputKind.Log), ("guide.md", ContentInputKind.Concept) }
            .Select(item => new ProjectRouteCandidate(Source(item.Item1), "/knowledge/" + item.Item1, "okf-inspection",
                "knowledge", "current", "en", DocumentPublicationState.Published)
            {
                InputProfile = okf ? ContentInputProfile.OpenKnowledgeFormat : ContentInputProfile.Markdown,
                InputKind = okf ? item.Item2 : ContentInputKind.Markdown,
                InputRoot = Root,
                InputPath = item.Item1,
                Relations = item.Item1 == "guide.md"
                    ? [new ProjectReferenceRelation("index.md", Source("index.md"), OkfLinkKind.ReservedDocument, false)] : [],
            }));

    [Test]
    public async Task ExplicitOkfInputsUseNativeMetadataAndReservedDocumentsDoNotRequireTypedFrontMatter()
    {
        var project = Snapshot();
        foreach (var (input, text) in new[] { ("index.md", "# Contents\n"), ("log.md", "# Updates\n\n## 2026-11-13\nChange.\n") })
        {
            var info = DocumentInspection.Inspect(Source(input), text, new() { Project = project });
            await Assert.That(info.Diagnostics.Any(diagnostic => diagnostic.Severity == Diagnostics.SiteDiagnosticSeverity.Error)).IsFalse();
            await Assert.That(info.SchemaInspectionScope).IsEqualTo("okf-native-metadata");
        }
        var guide = DocumentInspection.Inspect(Source("guide.md"),
            "---\ntype: Reference\nunknown: {nested: [one, {answer: 42}]}\n---\n# Guide\n", new() { Project = project });
        await Assert.That(guide.Diagnostics.Any(diagnostic => diagnostic.Severity == Diagnostics.SiteDiagnosticSeverity.Error)).IsFalse();
        await Assert.That(guide.FrontMatterSyntax!.Root!.Entries.Any(entry => entry.Key.Value == "unknown")).IsTrue();
        await Assert.That(guide.StaticSchema).IsNull();
        var missingType = DocumentInspection.Inspect(Source("guide.md"), "---\ntype: 42\n---\nBody", new() { Project = project });
        await Assert.That(missingType.Diagnostics.Select(diagnostic => diagnostic.Id)).Contains("OKF005");
        var absent = DocumentInspection.Inspect(Source("guide.md"), "# Guide\n", new() { Project = project });
        await Assert.That(absent.Diagnostics.Select(diagnostic => diagnostic.Id)).Contains("OKF004");
        await Assert.That(absent.Diagnostics.Any(diagnostic => diagnostic.Id == MarkdownContentDiagnosticIds.MissingFrontMatter)).IsFalse();
    }

    [Test]
    public async Task InputClassificationSurvivesKnownTextInvalidationWithoutRetainingRouteClosure()
    {
        var acquired = Snapshot();
        var json = JsonNode.Parse(acquired.ToJson())!.AsObject();
        json["routes"] = new JsonArray();
        json["semanticSnapshot"] = null;
        json["semanticRules"] = new JsonObject();
        var retained = ProjectInspectionSnapshot.ParseJson(json.ToJsonString()).WithMarkdownProfile(MarkdownOptions.DocumentationProfile);
        await Assert.That(retained.Routes.Count).IsEqualTo(0);
        await Assert.That(retained.InputMetadata.Count).IsEqualTo(3);
        foreach (var (input, text) in new[] { ("index.md", "# Changed index\n"), ("log.md", "## 2026-11-13\nChanged log\n"),
            ("guide.md", "---\ntype: Concept\nunknown: preserved\n---\nChanged concept\n") })
        {
            var info = DocumentInspection.Inspect(Source(input), text, new() { Project = retained });
            await Assert.That(info.SchemaInspectionScope).IsEqualTo("okf-native-metadata");
            await Assert.That(info.Diagnostics.Any(diagnostic => diagnostic.Id == MarkdownContentDiagnosticIds.MissingFrontMatter)).IsFalse();
            await Assert.That(info.ProjectStatus).IsEqualTo(DocumentProjectStatus.Unresolved);
        }
        var roundTrip = ProjectInspectionSnapshot.ParseJson(acquired.ToJson());
        var route = roundTrip.Routes.Single(route => route.InputKind == ContentInputKind.Concept);
        await Assert.That(route.Relations.Single().Kind).IsEqualTo(OkfLinkKind.ReservedDocument);
        await Assert.That(route.Relations.Single().TargetSourcePath).IsEqualTo(Source("index.md"));
        await Assert.That(route.InputRoot).IsEqualTo(Root);
    }

    [Test]
    public async Task InputPolicyChangeInvalidatesIncrementalReuseAndSchemaNameDoesNotSelectOkf()
    {
        const string text = "# Contents\n\nParagraph.\n";
        await using var workspace = new DocumentWorkspace();
        var first = await workspace.InspectVersionedAsync(Source("index.md"), text, 1, 1, new() { Project = Snapshot() });
        await Assert.That(first.Diagnostics.Any(diagnostic => diagnostic.Id == MarkdownContentDiagnosticIds.MissingFrontMatter)).IsFalse();
        var changed = await workspace.InspectVersionedAsync(Source("index.md"), text + "Changed.\n", 2, 2, new() { Project = Snapshot(2, okf: false) });
        await Assert.That(changed.IncrementalAnalysis!.Mode).IsEqualTo("Full");
        await Assert.That(changed.Diagnostics.Select(diagnostic => diagnostic.Id)).Contains(MarkdownContentDiagnosticIds.MissingFrontMatter);
        var nameOnly = ProjectInspectionSnapshot.Create("name-only", 1, "knowledge", "markdown", "okf", "current", "en");
        var syntax = DocumentInspection.Inspect(Source("index.md"), text, new() { Project = nameOnly });
        await Assert.That(syntax.Diagnostics.Select(diagnostic => diagnostic.Id)).Contains(MarkdownContentDiagnosticIds.MissingFrontMatter);
    }
}
