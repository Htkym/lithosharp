using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Documentation;
using LithoSharp.Mdx;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class DocumentSemanticRuleTests
{
    private static readonly DocumentKey Intro = new("guide", "v1", "en", "intro");
    [Test]
    public async Task DeclaredConditionalRequirementIgnoresInitializersAndSharesGeneratedRuntimeSemantics()
    {
        var parsed = MarkdownContentCollectionLoader<ConditionalFrontMatter>.ParseYaml("status: stable\nsupported_version: '  '\n", "intro.md", 2, default).Value!;
        var reflection = new ReflectionContentFrontMatterBinder<ConditionalFrontMatter>().Bind(parsed, new("intro.md", 2, 1));
        var generated = new GeneratedContentBindingContext(parsed, new("intro.md", 2, 1));
        generated.ValidateRequiredWhen("supported_version", "status", "stable");
        var generatedResult = generated.Complete(new ConditionalFrontMatter());
        await Assert.That(reflection.Diagnostics.Single().Id).IsEqualTo(ContentFrontMatterDiagnosticIds.ConditionalRequiredField);
        await Assert.That(reflection.Diagnostics.Single().Location!.Line).IsEqualTo(3);
        await Assert.That(generatedResult.Diagnostics.Single().Message).IsEqualTo(reflection.Diagnostics.Single().Message);
        var absent = new ReflectionContentFrontMatterBinder<ConditionalFrontMatter>().Bind(new Dictionary<string, object?> { ["status"] = "stable" });
        await Assert.That(absent.Diagnostics.Single().Id).IsEqualTo(ContentFrontMatterDiagnosticIds.ConditionalRequiredField);
        var unrelated = new ReflectionContentFrontMatterBinder<ConditionalFrontMatter>().Bind(new Dictionary<string, object?> { ["status"] = "preview" });
        await Assert.That(unrelated.IsSuccess).IsTrue();
        var invalid = new Dictionary<string, object?>();
        invalid["recursive"] = invalid;
        await Assert.That(() => new DocumentSemanticDocument(Intro, "intro.md", invalid, DocumentSemanticPublication.Unknown, true)).Throws<ArgumentException>();
    }

    [Test]
    public async Task ConditionalRequiredUsesTheSameStaticRuleAndKeepsRuntimePublicationDeferred()
    {
        var rules = new DocumentSemanticRuleSet([DocumentSemanticRule.RequiredWhen("supported_version", "status", "stable"), DocumentSemanticRule.RequiredWhenPublished("supported_version")]);
        var current = new DocumentSemanticDocument(Intro, "intro.md", new Dictionary<string, object?> { ["status"] = "stable" }, DocumentSemanticPublication.Unknown, true, new Dictionary<string, SiteSourceLocation> { ["supported_version"] = new("intro.md", 4, 1) });
        var result = rules.Evaluate(current);
        await Assert.That(result[0].State).IsEqualTo(DocumentSemanticState.Failed);
        await Assert.That(result[0].Rule.Stage).IsEqualTo(DocumentSemanticStage.Static);
        await Assert.That(result[0].Diagnostics.Single().Location!.Line).IsEqualTo(4);
        await Assert.That(result[1].State).IsEqualTo(DocumentSemanticState.Deferred);
        await Assert.That(result[1].Diagnostics.Single().Severity).IsEqualTo(SiteDiagnosticSeverity.Info);
        var fixedDocument = new DocumentSemanticDocument(Intro, "intro.md", new Dictionary<string, object?> { ["status"] = "stable", ["supported_version"] = "3.0" }, DocumentSemanticPublication.Published, true);
        await Assert.That(rules.Evaluate(fixedDocument).All(item => item.State == DocumentSemanticState.Passed)).IsTrue();
        var incomplete = new DocumentSemanticDocument(Intro, "intro.md", new Dictionary<string, object?>(), DocumentSemanticPublication.Published, false);
        await Assert.That(rules.Evaluate(incomplete).All(item => item.State == DocumentSemanticState.Deferred)).IsTrue();
    }

    [Test]
    public async Task MissingAndUnpublishedTargetsRequireDifferentEvidenceAndRelatedLocations()
    {
        var draft = Intro with
        {
            Id = "draft"
        };
        var current = new DocumentSemanticDocument(Intro, "intro.md", new Dictionary<string, object?> { ["prerequisites"] = new object?[] { "draft", "missing" } }, DocumentSemanticPublication.Published, true);
        var unpublished = new DocumentSemanticDocument(draft, "draft.md", new Dictionary<string, object?>(), DocumentSemanticPublication.Unpublished, true);
        var rules = new DocumentSemanticRuleSet([DocumentSemanticRule.Reference("prerequisites")]);
        var catalog = new DocumentCatalog([Page(Intro)]);
        var complete = rules.Evaluate(current, new(catalog, [current, unpublished], true)).Single();
        await Assert.That(complete.State).IsEqualTo(DocumentSemanticState.Failed);
        await Assert.That(string.Join(",", complete.Diagnostics.Select(item => item.Id))).IsEqualTo("LSDOC212,LSDOC211");
        await Assert.That(complete.Diagnostics[0].RelatedLocations.Single().FilePath).IsEqualTo("draft.md");
        var partial = rules.Evaluate(current, new(catalog, [current, unpublished], false)).Single();
        await Assert.That(partial.State).IsEqualTo(DocumentSemanticState.Failed);
        await Assert.That(partial.Diagnostics.Any(item => item.Id == "LSDOC211")).IsFalse();
        await Assert.That(partial.Diagnostics.Any(item => item.Id == "LSDOC214")).IsTrue();
        var onlyMissing = new DocumentSemanticDocument(Intro, "intro.md", new Dictionary<string, object?> { ["prerequisites"] = "missing" }, DocumentSemanticPublication.Unknown, true);
        await Assert.That(rules.Evaluate(onlyMissing, new(catalog, [onlyMissing], false)).Single().State).IsEqualTo(DocumentSemanticState.Deferred);
        await Assert.That(rules.Evaluate(onlyMissing, new(catalog, [onlyMissing], true)).Single().State).IsEqualTo(DocumentSemanticState.Failed);
        var scoped = new DocumentSemanticSnapshot(catalog, [current, unpublished], true, [new("guide", "v1", "en")]);
        await Assert.That(scoped.Resolve(Intro with { Collection = "unregistered", Id = "topic" }).State).IsEqualTo(DocumentSemanticReferenceState.Unknown);
    }

    [Test]
    public async Task ReferenceScopeAndSerializedEvidenceStayExactAcrossVersionsLocalesAndBasePaths()
    {
        var target = Intro with
        {
            Version = "v2",
            Locale = "ja",
            Id = "topic"
        };
        var fields = new Dictionary<string, object?>
        {
            ["prerequisites"] = target
        };
        var source = new DocumentSemanticDocument(Intro, "intro.md", fields, DocumentSemanticPublication.Published, true);
        var catalog = new DocumentCatalog([Page(Intro), Page(target, SiteRoute.ForDirectoryIndex("v2/日本語", "https://example.test/docs/"))]);
        var snapshot = new DocumentSemanticSnapshot(catalog, [source], true);
        var rules = new DocumentSemanticRuleSet([DocumentSemanticRule.Reference("prerequisites")]);
        var result = rules.Evaluate(source, snapshot).Single();
        await Assert.That(result.Diagnostics.Count(item => item.Id == "LSDOC213")).IsEqualTo(2);
        var relaxed = new DocumentSemanticRuleSet([DocumentSemanticRule.Reference("prerequisites", sameVersion: false, sameLocale: false)]);
        await Assert.That(relaxed.Evaluate(source, snapshot).Single().State).IsEqualTo(DocumentSemanticState.Passed);
        var restoredRules = DocumentSemanticRuleSet.ParseJson(relaxed.ToJson());
        var restored = DocumentSemanticSnapshot.ParseJson(snapshot.ToJson());
        await Assert.That(restored.Resolve(target).Route).IsEqualTo(catalog.Resolve(target).Route);
        await Assert.That(restoredRules.Evaluate(restored.Documents.Single(), restored).Single().State).IsEqualTo(DocumentSemanticState.Passed);
        fields["prerequisites"] = "mutated";
        await Assert.That(snapshot.Resolve(target).State).IsEqualTo(DocumentSemanticReferenceState.Resolved);
        await Assert.That(relaxed.Evaluate(source, snapshot).Single().State).IsEqualTo(DocumentSemanticState.Passed);
        await Assert.That(() => DocumentSemanticRuleSet.ParseJson("{\"schemaVersion\":\"2.0\",\"rules\":[]}")).Throws<ArgumentException>();
        await Assert.That(() => DocumentSemanticRule.RequiredWhen("title", "status..state", "stable")).Throws<ArgumentException>();
    }

    [Test]
    public async Task ActualDocsBuildRejectsMissingVersionAndDraftPrerequisitesThenInvalidatesOldSuccess()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "docs");
        Directory.CreateDirectory(input);
        var intro = Path.Combine(input, "intro.md");
        var topic = Path.Combine(input, "topic.md");
        await File.WriteAllTextAsync(intro, "---\ntitle: Intro\nprerequisites: [topic]\n---\n# Intro\n");
        await File.WriteAllTextAsync(topic, "---\ntitle: Topic\nsupported_version: '3.0'\ndraft: true\n---\n# Topic\n");
        await using var docs = new DocumentationSite(new(workspace.Root));
        docs.AddCollection(new("guide", [new("v1", "en", input, "docs")]) { SemanticRules = DocumentSemanticRuleSet.Documentation });
        async Task Build() => await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], Path.Combine(workspace.Root, "out"), false, new() { Template = new DocsSiteTemplate() }, new() { Extensions = [docs] }, default);
        await Assert.That(Build).Throws<LithoSharp.Build.SiteBuildExtensionException>();
        await Assert.That(docs.SemanticResults.SelectMany(item => item.Diagnostics).Any(item => item.Id == "LSDOC210")).IsTrue();
        var unpublished = docs.SemanticResults.SelectMany(item => item.Diagnostics).Single(item => item.Id == "LSDOC212");
        await Assert.That(unpublished.Location!.Line).IsEqualTo(3);
        await Assert.That(unpublished.RelatedLocations.Single().FilePath).IsEqualTo(topic);
        await File.WriteAllTextAsync(intro, "---\ntitle: Intro\nsupported_version: '3.0'\nprerequisites: [topic]\n---\n# Intro\n");
        await File.WriteAllTextAsync(topic, "---\ntitle: Topic\nsupported_version: '3.0'\n---\n# Topic\n");
        await Build();
        await Assert.That(docs.SemanticResults.Any(item => item.State == DocumentSemanticState.Failed || item.State == DocumentSemanticState.Deferred)).IsFalse();
        await File.WriteAllTextAsync(topic, "---\ntitle: [\n---\n# Broken\n");
        await Assert.That(Build).Throws<LithoSharp.Build.SiteBuildExtensionException>();
        await Assert.That(docs.SemanticSnapshot).IsNull();
        await Assert.That(docs.SemanticResults.All(item => item.State == DocumentSemanticState.Deferred)).IsTrue();
    }

    private static DocumentPage Page(DocumentKey key, SiteRoute? route = null) => new(key, key.Id + ".md", new() { Title = key.Id }, route ?? SiteRoute.ForDirectoryIndex(key.Version + "/" + key.Locale + "/" + key.Id));
    private sealed class ConditionalFrontMatter
    {
        public string? Status { get; set; }

        [ContentRequiredWhen("status", "stable")]
        public string? SupportedVersion { get; set; } = "initializer does not establish input presence";
    }
}
