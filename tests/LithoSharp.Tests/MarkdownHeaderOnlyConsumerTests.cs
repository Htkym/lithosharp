using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Documentation;

namespace LithoSharp.Tests;

public sealed class MarkdownHeaderOnlyConsumerTests
{
    [Test]
    [Arguments("\n", "")]
    [Arguments("\r\n", "\uFEFF")]
    [Arguments("\r", "\uFEFF")]
    public async Task HeaderSplitNeedsNoProjectContextOrBodyRecognition(string newline, string bom)
    {
        var yaml = "title: Header 😀" + newline;
        var prefix = bom + "---" + newline + yaml + "---" + newline;
        foreach (var body in new[] { new string('x', 1024 * 1024 + 1), "# Body" + newline + "\uD800" })
        {
            var raw = prefix + body;
            var split = MarkdownDocumentParser.SplitFrontMatter(raw, "outside-project.md");
            var document = MarkdownContentCollectionLoader<object>.MarkdownSourceDocument.Parse(raw, "outside-project.md", default);
            var located = FrontMatterSplitter.TrySplit(raw);

            await Assert.That(split.Yaml).IsEqualTo(yaml);
            await Assert.That(split.Body).IsEqualTo(body);
            await Assert.That(document.IsSuccess).IsTrue();
            await Assert.That(document.Value!.Yaml).IsEqualTo(yaml);
            await Assert.That(document.Value.Body).IsEqualTo(body);
            await Assert.That(document.Value.YamlStartLine).IsEqualTo(2);
            await Assert.That(located.BodyStartOffset).IsEqualTo(prefix.Length);
        }
    }

    [Test]
    public async Task ClosedHeaderLeavesYamlValidationWithTheExistingConsumer()
    {
        const string raw = "---\ntitle: [broken\n---\nBody\n";
        var split = MarkdownDocumentParser.SplitFrontMatter(raw, "invalid-yaml.md");
        var document = MarkdownContentCollectionLoader<object>.MarkdownSourceDocument.Parse(raw, "invalid-yaml.md", default);
        var yaml = MarkdownContentCollectionLoader<object>.ParseYaml(split.Yaml, "invalid-yaml.md", 2, default);

        await Assert.That(document.IsSuccess).IsTrue();
        await Assert.That(split.Yaml).IsEqualTo("title: [broken\n");
        await Assert.That(split.Body).IsEqualTo("Body\n");
        await Assert.That(yaml.IsSuccess).IsFalse();
        await Assert.That(yaml.Diagnostics.Single().Id).IsEqualTo(MarkdownContentDiagnosticIds.InvalidYaml);
    }

    [Test]
    public async Task OversizedHeaderIsIncompleteWithoutInventingAbsenceOrEof()
    {
        var header = "---\ntitle: " + new string('x', 1024 * 1024);
        foreach (var raw in new[] { header, header + "\n---\nBody" })
        {
            var split = FrontMatterSplitter.TrySplit(raw);
            var document = MarkdownContentCollectionLoader<object>.MarkdownSourceDocument.Parse(raw, "large-header.md", default);

            await Assert.That(split.Status).IsEqualTo(FrontMatterSplitStatus.Incomplete);
            await Assert.That(document.IsSuccess).IsFalse();
            var diagnostic = document.Diagnostics.Single();
            await Assert.That(diagnostic.Id).IsEqualTo(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId);
            await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("large-header.md");
            await Assert.That(diagnostic.Location.Line).IsNull();
            await Assert.That(diagnostic.Location.Column).IsNull();
            await Assert.That(diagnostic.Location.EndLine).IsNull();
            await Assert.That(diagnostic.Location.EndColumn).IsNull();
            await Assert.That(() => MarkdownDocumentParser.SplitFrontMatter(raw, "large-header.md"))
                .Throws<InvalidOperationException>().WithMessageContaining("LSM013");
        }
    }

    [Test]
    [Arguments("\n", "")]
    [Arguments("\r\n", "\uFEFF")]
    public async Task InvalidUnicodeHeaderRetainsItsKnownRawPosition(string newline, string bom)
    {
        var raw = bom + "---" + newline + "title: \uD800" + newline + "---" + newline + "Body";
        var document = MarkdownContentCollectionLoader<object>.MarkdownSourceDocument.Parse(raw, "invalid-header.md", default);

        await Assert.That(document.IsSuccess).IsFalse();
        var diagnostic = document.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId);
        await Assert.That((diagnostic.Location!.Line, diagnostic.Location.Column, diagnostic.Location.EndLine, diagnostic.Location.EndColumn))
            .IsEqualTo(((int?)2, (int?)8, (int?)2, (int?)9));
        await Assert.That(() => MarkdownDocumentParser.SplitFrontMatter(raw, "invalid-header.md"))
            .Throws<InvalidOperationException>().WithMessageContaining("LSM013");
    }

    [Test]
    public async Task ConfirmedUnterminatedHeaderKeepsTheExistingFailureContract()
    {
        const string raw = "---\r\ntitle: Known\r\nBody";
        var document = MarkdownContentCollectionLoader<object>.MarkdownSourceDocument.Parse(raw, "unclosed.md", default);

        await Assert.That(document.IsSuccess).IsFalse();
        var diagnostic = document.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo(MarkdownContentDiagnosticIds.UnterminatedFrontMatter);
        await Assert.That(diagnostic.Location!.Line).IsEqualTo(4);
        await Assert.That(diagnostic.Location.Column).IsEqualTo(1);
        await Assert.That(() => MarkdownDocumentParser.SplitFrontMatter(raw, "unclosed.md"))
            .Throws<InvalidOperationException>().WithMessageContaining("Markdown document 'unclosed.md' has no closing front matter marker.");
    }

    [Test]
    public async Task EmptyHeaderKeepsThePublicTupleAndCollectionRejection()
    {
        const string raw = "\uFEFF---\r\n---\r\nBody\r\n";
        var split = MarkdownDocumentParser.SplitFrontMatter(raw, "empty.md");
        var document = MarkdownContentCollectionLoader<object>.MarkdownSourceDocument.Parse(raw, "empty.md", default);

        await Assert.That(split.Yaml).IsEqualTo(string.Empty);
        await Assert.That(split.Body).IsEqualTo("Body\r\n");
        await Assert.That(document.IsSuccess).IsFalse();
        await Assert.That(document.Diagnostics.Single().Id).IsEqualTo(MarkdownContentDiagnosticIds.EmptyFrontMatter);
    }

    [Test]
    public async Task MigrationReportsUnavailableHeaderWithoutAnInventedTitleOrPosition()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "docs"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "docs", "large.md"),
            "---\ntitle: " + new string('x', 1024 * 1024) + "\n---\nBody");

        var report = DocusaurusMigrationReport.Analyze(workspace.Root);
        var file = report.Files.Single(item => item.SourcePath == "docs/large.md");
        var diagnostic = file.Issues.Single();

        await Assert.That(file.Verdict).IsEqualTo(MigrationVerdict.Unsupported);
        await Assert.That(file.SuggestedActions).IsEmpty();
        await Assert.That(diagnostic.Id).IsEqualTo(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId);
        await Assert.That(diagnostic.Location.FilePath).IsEqualTo("docs/large.md");
        await Assert.That(diagnostic.Location.Line).IsNull();
        await Assert.That(diagnostic.Location.Column).IsNull();
        await Assert.That(diagnostic.Replacement).IsNull();
    }
}
