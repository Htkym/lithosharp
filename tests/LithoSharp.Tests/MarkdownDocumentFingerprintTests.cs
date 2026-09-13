using LithoSharp.Content.Compilation;

namespace LithoSharp.Tests;

/// <summary>C06: source and semantic fingerprints are stable, separated, and meaningful.</summary>
public sealed class MarkdownDocumentFingerprintTests
{
    [Test]
    public async Task SourceHash_IsStableSha256()
    {
        var first = MarkdownDocumentFingerprints.SourceHash("## Hi\n\nBody.\n");
        var second = MarkdownDocumentFingerprints.SourceHash("## Hi\n\nBody.\n");
        var other = MarkdownDocumentFingerprints.SourceHash("## Hi\n\nBody changed.\n");

        await Assert.That(first).IsEqualTo(second);
        await Assert.That(first.Length).IsEqualTo(64);
        await Assert.That(other == first).IsFalse();
    }

    [Test]
    public async Task SemanticHash_IgnoresMarkupSpelling()
    {
        var compiler = new LithoMarkdownCompiler();
        var star = compiler.Analyze("*a* and [t](https://example.org/u)\n").Semantics!;
        var underline = compiler.Analyze("_a_ and [t](https://example.org/u)\n").Semantics!;

        await Assert.That(MarkdownDocumentFingerprints.SemanticHash(star))
            .IsEqualTo(MarkdownDocumentFingerprints.SemanticHash(underline));
    }

    [Test]
    public async Task SemanticHash_ChangesWithMeaning()
    {
        var compiler = new LithoMarkdownCompiler();
        var first = compiler.Analyze("See [t](https://example.org/a).\n").Semantics!;
        var changedUrl = compiler.Analyze("See [t](https://example.org/b).\n").Semantics!;
        var changedText = compiler.Analyze("See altered [t](https://example.org/a).\n").Semantics!;
        var changedHeading = compiler.Analyze("# Title\n").Semantics!;
        var noHeading = compiler.Analyze("Title\n").Semantics!;

        await Assert.That(MarkdownDocumentFingerprints.SemanticHash(changedUrl) == MarkdownDocumentFingerprints.SemanticHash(first)).IsFalse();
        await Assert.That(MarkdownDocumentFingerprints.SemanticHash(changedText) == MarkdownDocumentFingerprints.SemanticHash(first)).IsFalse();
        await Assert.That(MarkdownDocumentFingerprints.SemanticHash(changedHeading) == MarkdownDocumentFingerprints.SemanticHash(noHeading)).IsFalse();
    }

    [Test]
    public async Task SemanticHash_IgnoresPositions()
    {
        var compiler = new LithoMarkdownCompiler();
        var plain = compiler.Analyze("## Hi\n", new DocumentSource(null, 0)).Semantics!;
        var shifted = compiler.Analyze("## Hi\n", new DocumentSource("doc.md", 120)).Semantics!;

        await Assert.That(shifted.Headings[0].Span.Start).IsNotEqualTo(plain.Headings[0].Span.Start);
        await Assert.That(MarkdownDocumentFingerprints.SemanticHash(shifted))
            .IsEqualTo(MarkdownDocumentFingerprints.SemanticHash(plain));
    }
}
