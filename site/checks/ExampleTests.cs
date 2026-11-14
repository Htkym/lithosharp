using LithoSharp.Website.Examples;
using LithoSharp.Content;
using LithoSharp.Documentation;
using LithoSharp.Inspection;
using LithoSharp.Routing;

public sealed class ExampleTests
{
    [Test]
    public async Task PublishedDocumentsRequireAnExplicitVersion()
    {
        await Assert.That(WebsiteExamples.HasPublishedVersion(false, null)).IsFalse();
        await Assert.That(WebsiteExamples.HasPublishedVersion(false, " ")).IsFalse();
        await Assert.That(WebsiteExamples.HasPublishedVersion(false, "3.0")).IsTrue();
        await Assert.That(WebsiteExamples.HasPublishedVersion(true, null)).IsTrue();
    }

    [Test]
    public async Task PublishedExamplesExposeGuideAndSnippetImpactsForAnExplicitApiRemovalPreview()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LithoSharp.Website.csproj")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("The website source root is required for its example checks.");
        const string id = "M:LithoSharp.Website.Examples.WebsiteExamples.HasPublishedVersion(System.Boolean,System.String)";
        foreach (var locale in new[] { "en", "ja" })
        {
            var path = $"examples/{locale}/typed-content.md";
            var key = new DocumentKey("examples", "3.0", locale, id);
            var page = new DocumentPage(key, "examples/source/WebsiteExamples.cs", new() { Title = "HasPublishedVersion" },
                ApiReferenceSite.RouteFor("api/" + locale, id));
            var project = ProjectInspectionSnapshot.Create("website", 1, "website-examples", "markdown", "document", "3.0", locale,
                [new(path, $"/examples/{locale}/typed-content/", "website", "website-examples", "3.0", locale, DocumentPublicationState.Published)])
                .WithMarkdownProfile(MarkdownOptions.DocumentationProfile)
                .WithSemantics("sha256:" + new string('a', 64), new(new([page]), [], true), new Dictionary<string, DocumentSemanticRuleSet>());
            var document = new DocumentReferenceDocument(Path.Combine(root, path), await File.ReadAllTextAsync(Path.Combine(root, path)), 1, path);
            var references = new DocumentReferenceSnapshot(project, [document], catalogComplete: true);
            var snippet = new DocumentSnippet("published-version", path, "checks/LithoSharp.Website.Checks.csproj")
                { ApiReferences = [key] };
            var changes = ApiReferenceSite.Compare([new(id, "HasPublishedVersion", "", "")], []);
            var report = references.FindApiImpacts(new Dictionary<DocumentKey, IReadOnlyList<ApiChange>> { [key] = changes }, [snippet]);
            await Assert.That(report.Impacts.Count).IsEqualTo(2);
            await Assert.That(report.Impacts.Count(impact => impact.SnippetId == "published-version")).IsEqualTo(1);
            await Assert.That(report.Impacts.All(impact => impact.Api == key && impact.Changes.Single().Kind == "removed"
                && impact.Document.TextHash == document.TextHash)).IsTrue();
            await Assert.That(report.IsComplete).IsFalse();
        }
    }
}
