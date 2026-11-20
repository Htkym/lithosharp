using LithoSharp.OpenKnowledge;

namespace LithoSharp.Tests;

public sealed class OkfWriterTests
{
    [Test]
    public async Task NewOutputUsesExplicitConversionAndKeepsComponentSourcesWhenAnAlternativeIsMissing()
    {
        using var workspace = new TemporaryWorkspace();
        var original = "import Chart from './chart';\n\n<Chart value={42} />\n";
        var unavailable = OkfWriter.Prepare([new("metric.md", "type: Metric", original, OkfSourceBodyKind.Mdx)]);
        await Assert.That(unavailable.CanExport).IsFalse();
        await Assert.That(unavailable.Documents.Single().OriginalMarkdown).IsEqualTo(original);
        await Assert.That(unavailable.Diagnostics.Single().Category).IsEqualTo("Unsupported");
        await Assert.That(() => unavailable.ExportAsync(Path.Combine(workspace.Root, "failed"))).Throws<InvalidOperationException>();
        await Assert.That(Directory.Exists(Path.Combine(workspace.Root, "failed"))).IsFalse();
        var alternative = "# Metric\n\nThe declared value is 42.\n";
        var prepared = OkfWriter.Prepare([new("metric.md", "type: Metric\nunknown: {nested: true}", original, OkfSourceBodyKind.Mdx, alternative)],
            [new("query.sql", new byte[] { 83, 69, 76, 69, 67, 84, 32, 49 })]);
        await Assert.That(prepared.CanExport).IsTrue();
        var output = Path.Combine(workspace.Root, "out");
        await prepared.ExportAsync(output);
        var consumed = await OkfBundle.ReadAsync(output);
        await Assert.That(consumed.IsConformant).IsTrue();
        await Assert.That(consumed.DeclaredVersion).IsEqualTo("0.2");
        var document = consumed.Documents.Single(document => document.Path == "metric.md");
        await Assert.That(document.Body).IsEqualTo(alternative);
        await Assert.That(document.Metadata!.Generated).IsNull();
        await Assert.That(document.Metadata.GetField("status")).IsNull();
        await Assert.That(document.Metadata.Sources.Count).IsEqualTo(0);
        await Assert.That(document.Metadata.GetField("unknown")!.Entries.Single().Key.Value).IsEqualTo("nested");
    }

    [Test]
    public async Task NewWriterDeclaresTheSelectedVersionAndUsesItsTimestampRules()
    {
        using var workspace = new TemporaryWorkspace();
        var prepared = OkfWriter.PrepareForVersion("0.3", [
            new("metric.md", "type: Metric\nstale_after: 2026-11-13", "Body"),
        ]);
        await Assert.That(prepared.CanExport).IsTrue();
        var output = Path.Combine(workspace.Root, "out");
        await prepared.ExportAsync(output);
        var bundle = await OkfBundle.ReadAsync(output);
        await Assert.That(bundle.SpecificationVersion).IsEqualTo("0.3");
        await Assert.That(bundle.IsSuccess).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(output, "index.md"))).Contains("okf_version: \"0.3\"");
    }

    [Test]
    public async Task NewWriterRejectsAmbiguousPathsAndTimestampCoercionBeforeCreatingOutput()
    {
        var prepared = OkfWriter.Prepare([
            new("Name.md", "type: Reference\ngenerated: {by: process:producer, at: 2026-11-13}", "Body"),
            new("name.md", "type: Reference", "Other"),
        ]);
        await Assert.That(prepared.CanExport).IsFalse();
        await Assert.That(prepared.Diagnostics.Any(diagnostic => diagnostic.Id == "OKF118")).IsTrue();
        await Assert.That(prepared.Diagnostics.Any(diagnostic => diagnostic.Id == "OKFW006")).IsTrue();
        var numericType = OkfWriter.Prepare([new("number.md", "type: 42", "Body")]);
        await Assert.That(numericType.CanExport).IsFalse();
        await Assert.That(numericType.Diagnostics.Single().Id).IsEqualTo("OKFW003");
        var splitMetadata = OkfWriter.Prepare([new("split.md", "type: Reference\n---\nignored: metadata", "Body")]);
        await Assert.That(splitMetadata.CanExport).IsFalse();
        await Assert.That(splitMetadata.Diagnostics.Single().Id).IsEqualTo("OKFW007");
    }
}
