using System.Security.Cryptography;
using System.Text;
using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;

namespace LithoSharp.Tests;

public sealed class DocumentTranslationReviewTests
{
    [Test]
    public async Task ReviewUsesExactDocumentIdentitiesAndCannotInferMissingInputsFromPartialSnapshots()
    {
        var source = Revision(new("guide", "v1", "en", "intro"), "en/intro.md", "Original");
        var translation = Revision(source.Key with { Locale = "ja" }, "ja/intro.md", "Translation");
        var review = new DocumentTranslationReview(source, translation);
        var sources = new Dictionary<DocumentKey, DocumentKey> { [translation.Key] = source.Key };
        var partial = DocumentTranslationReviewResult.Inspect([translation], sources, [review], false).Single();
        await Assert.That(partial.State).IsEqualTo(DocumentTranslationReviewState.Unavailable);
        var missing = DocumentTranslationReviewResult.Inspect([translation], sources, [review], true).Single();
        await Assert.That(missing.State).IsEqualTo(DocumentTranslationReviewState.SourceMissing);
        var otherVersion = Revision(source.Key with { Version = "v2" }, source.SourcePath, "Original");
        var reassigned = DocumentTranslationReviewResult.Inspect([source, translation, otherVersion],
            new Dictionary<DocumentKey, DocumentKey> { [translation.Key] = otherVersion.Key }, [review], true).Single();
        await Assert.That(reassigned.State).IsEqualTo(DocumentTranslationReviewState.SourceReassigned);
        var edited = Revision(translation.Key, translation.SourcePath, "Edited translation");
        var changed = DocumentTranslationReviewResult.Inspect([source, edited], sources, [review], true).Single();
        await Assert.That(changed.State).IsEqualTo(DocumentTranslationReviewState.TranslationChanged);
    }

    [Test]
    public async Task ActualDocumentationBuildKeepsReviewStaleUntilExplicitlyConfirmed()
    {
        using var workspace = new TemporaryWorkspace();
        var en = Path.Combine(workspace.Root, "en");
        var ja = Path.Combine(workspace.Root, "ja");
        Directory.CreateDirectory(en);
        Directory.CreateDirectory(ja);
        var sourceText = "---\nid: intro\ntitle: Introduction\n---\n# Original\n";
        var translationText = "---\nid: intro\ntitle: Introduction\n---\n# Translation\n";
        var sourcePath = Path.Combine(en, "cafe\u0301.md");
        var translationPath = Path.Combine(ja, "cafe\u0301.md");
        await File.WriteAllTextAsync(sourcePath, sourceText);
        await File.WriteAllTextAsync(translationPath, translationText);
        var source = Revision(new("guide", "v1", "en", "intro"), sourcePath, sourceText);
        var translation = Revision(source.Key with { Locale = "ja" }, translationPath, translationText);
        var review = new DocumentTranslationReview(source, translation);
        var collection = new DocumentationCollection("guide", [new("v1", "en", en, "en"), new("v1", "ja", ja, "ja")])
        {
            SourceLocale = "en",
            TrackTranslationReviews = true,
            TranslationReviews = [review]
        };
        var output = Path.Combine(workspace.Root, "out");
        await using var docs = new DocumentationSite(new(workspace.Root));
        docs.AddCollection(collection);
        async Task Build() => await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], output,
            false, new() { Template = new DocsSiteTemplate() }, new() { Extensions = [docs] }, default);
        await Build();
        await Assert.That(docs.TranslationReviewResults.Single().State).IsEqualTo(DocumentTranslationReviewState.Current);
        await Assert.That(docs.TranslationReviewResults.Single().SourceRevision!.SourcePath).IsEqualTo(sourcePath);
        await File.WriteAllTextAsync(sourcePath, sourceText + "\nA source correction.\n");
        await Build();
        await Assert.That(docs.TranslationReviewResults.Single().State).IsEqualTo(DocumentTranslationReviewState.SourceChanged);
        await Build();
        await Assert.That(docs.TranslationReviewResults.Single().State).IsEqualTo(DocumentTranslationReviewState.SourceChanged);
        await Assert.That(review.Source.ContentHash).IsEqualTo(source.ContentHash);
        await File.WriteAllTextAsync(sourcePath, "---\ntitle: [\n---\n# Invalid source\n");
        await Assert.That(Build).Throws<LithoSharp.Build.SiteBuildExtensionException>();
        await Assert.That(docs.TranslationReviewResults.Single().State).IsEqualTo(DocumentTranslationReviewState.Unavailable);
        await File.WriteAllTextAsync(sourcePath, sourceText + "\nA source correction.\n");
        await Build();
        await Assert.That(docs.TranslationReviewResults.Single().State).IsEqualTo(DocumentTranslationReviewState.SourceChanged);
        var updatedReview = new DocumentTranslationReview(docs.TranslationReviewResults.Single().SourceRevision!, translation);
        await using var confirmed = new DocumentationSite(new(workspace.Root));
        confirmed.AddCollection(collection with { TranslationReviews = [updatedReview] });
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], output, false,
            new() { Template = new DocsSiteTemplate() }, new() { Extensions = [confirmed] }, default);
        await Assert.That(confirmed.TranslationReviewResults.Single().State).IsEqualTo(DocumentTranslationReviewState.Current);
    }

    private static DocumentRevision Revision(DocumentKey key, string path, string text) =>
        new(key, path, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
}
