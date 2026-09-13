using LithoSharp.Inspection;

namespace LithoSharp.Tests;

public sealed class DocumentInspectionTests
{
    private const string Sample =
        "---\n" +
        "title: Hello\n" +
        "---\n" +
        "# Title\n" +
        "\n" +
        "## Sub\n" +
        "\n" +
        "See [docs](https://example.com/guide \"Guide\") and ![alt](img/photo.png).\n";

    [Test]
    public async Task Inspect_ReturnsDocumentInfoFromSingleCall()
    {
        var info = DocumentInspection.Inspect("docs/a.md", Sample);

        await Assert.That(info.DocumentId).IsEqualTo("docs/a.md");
        await Assert.That(info.Title).IsEqualTo("Title");
        await Assert.That(info.FrontMatter["title"]).IsEqualTo("Hello");
        await Assert.That(info.Headings.Count).IsEqualTo(2);
        await Assert.That(info.Headings[0].RawLevel).IsEqualTo(1);
        await Assert.That(info.Headings[0].OutputLevel).IsEqualTo(2);
        await Assert.That(info.Headings[1].Text).IsEqualTo("Sub");
        await Assert.That(info.Headings[1].Location?.Line).IsEqualTo(6);
        await Assert.That(info.Headings[1].Location?.EndLine).IsEqualTo(6);
        await Assert.That(info.Headings[1].Id).IsNotNull();
        await Assert.That(info.Links.Count).IsEqualTo(2);
        await Assert.That(info.Links[0].Url).IsEqualTo("https://example.com/guide");
        await Assert.That(info.Links[0].Title).IsEqualTo("Guide");
        await Assert.That(info.Links[0].IsImage).IsFalse();
        await Assert.That(info.Links[1].IsImage).IsTrue();
        await Assert.That(info.Assets.Count).IsEqualTo(1);
        await Assert.That(info.Assets[0].Url).IsEqualTo("img/photo.png");
        await Assert.That(info.Components.Count).IsEqualTo(0);
        await Assert.That(info.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Inspect_AcceptsUnsavedTextWithoutFrontMatter()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "draft.md");

        var info = DocumentInspection.Inspect(path, "# Draft\n");

        await Assert.That(info.Title).IsEqualTo("Draft");
        await Assert.That(info.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(info.Diagnostics[0].Id).IsEqualTo("LSM001");
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task Inspect_IncompleteSyntaxReturnsDiagnostics()
    {
        var info = DocumentInspection.Inspect("docs/b.md", "---\ntitle: x\n# Hi\n[bad](link");

        await Assert.That(info.Diagnostics.Count).IsEqualTo(1);
        await Assert.That(info.Diagnostics[0].Id).IsEqualTo("LSM002");
        await Assert.That(info.Headings).IsNotNull();
    }

    [Test]
    public async Task Inspect_AttachesSiteContext()
    {
        var info = DocumentInspection.Inspect("docs/a.md", Sample, new DocumentInspectionOptions
        {
            DocumentId = "doc-a",
            Route = "/docs/a/",
            Version = "1.0",
            Locale = "ja",
        });

        await Assert.That(info.DocumentId).IsEqualTo("doc-a");
        await Assert.That(info.Route).IsEqualTo("/docs/a/");
        await Assert.That(info.Version).IsEqualTo("1.0");
        await Assert.That(info.Locale).IsEqualTo("ja");
    }

    [Test]
    public async Task Inspect_DoesNotWriteOutputs()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        DocumentInspection.Inspect(Path.Combine(directory, "a.md"), Sample);

        await Assert.That(Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length).IsEqualTo(0);
    }
}