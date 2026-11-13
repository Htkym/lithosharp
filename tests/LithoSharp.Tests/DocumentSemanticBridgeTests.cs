using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;

namespace LithoSharp.Tests;

public sealed class DocumentSemanticBridgeTests
{
    [Test]
    public async Task DocumentationPreparationRetainsScopedMetadataAndDefersSearchUntilRendering()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "docs");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "intro.md"), "---\ntitle: Intro\n---\n# Intro\n");
        var topic = Path.Combine(input, "topic.md");
        await File.WriteAllTextAsync(topic, "---\ntitle: Topic\ndraft: true\n---\n# Topic\n");
        await using var docs = new DocumentationSite(new(workspace.Root))
        {
            Browser = new() { Search = true },
        };
        docs.AddCollection(new("guide", [new("v1", "en", input, "docs")]));
        var context = new SiteBuildContext(new SiteSettings(), new(), Path.Combine(workspace.Root, "out"),
            Path.Combine(workspace.Root, "cache"), new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero));
        var first = await docs.PrepareAsync(context);
        var snapshot = first.DocumentSemantics.Single();

        await Assert.That(snapshot.IsComplete).IsTrue();
        await Assert.That(snapshot.CompleteScopes!).IsEquivalentTo([new DocumentSemanticScope("guide", "v1", "en")]);
        await Assert.That(snapshot.Resolve(new("guide", "v1", "en", "topic")).State)
            .IsEqualTo(DocumentSemanticReferenceState.Unpublished);
        await Assert.That(snapshot.Resolve(new("unregistered", "v1", "en", "topic")).State)
            .IsEqualTo(DocumentSemanticReferenceState.Unknown);
        await Assert.That(docs.SemanticResults.Count).IsEqualTo(0);
        await Assert.That(first.ScopedSearch.Single().RelativeOutputPath).IsEqualTo("docs/_search.json");
        await Assert.That(first.Assets.Any(asset => asset.RelativeOutputPath == "docs/_search.json")).IsFalse();

        await File.WriteAllTextAsync(topic, "---\ntitle: Updated\n---\n# Topic\n");
        var second = await docs.PrepareAsync(context);
        await Assert.That(snapshot.Documents.Single(document => document.Key.Id == "topic").Values["title"])
            .IsEqualTo("Topic");
        await Assert.That(second.DocumentSemantics.Single().Documents.Single(document => document.Key.Id == "topic").Values["title"])
            .IsEqualTo("Updated");
        await Assert.That(second.DocumentSemantics.Single().Resolve(new("guide", "v1", "en", "topic")).State)
            .IsEqualTo(DocumentSemanticReferenceState.Resolved);
    }
}
