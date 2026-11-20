using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.OpenKnowledge;

namespace LithoSharp.Tests;

public sealed class OkfVersionSelectionTests
{
    [Test]
    public async Task BundleDeclarationRequiresTheExactReservedRootName()
    {
        using var workspace = new TemporaryWorkspace();
        await Write(workspace.Root, "INDEX.md", "---\nokf_version: \"0.3\"\n---\n");
        var bundle = await OkfBundle.ReadAsync(workspace.Root);
        await Assert.That(bundle.SpecificationVersion).IsNull();
        await Assert.That(bundle.Diagnostics.Any(item => item.Id == "OKF010")).IsTrue();
    }

    [Test]
    public async Task MissingMalformedAndUnknownDeclarationsCannotPublishButRawExportIsPreserved()
    {
        foreach (var header in new string?[] { null, "# Index\n", "---\n---\n", "---\ntitle: Bundle\n---\n",
            "---\nokf_version: 0.3\n---\n", "---\nokf_version: \"banana\"\n---\n", "---\nokf_version: \"0.4\"\n---\n",
            "---\nokf_version: \"0.3\"\nx-extra: &anchor value\n---\n" })
        {
            using var workspace = new TemporaryWorkspace();
            var input = Path.Combine(workspace.Root, "input");
            await Write(input, "concept.md", "---\ntype: Reference\n---\nBody\n");
            if (header is not null)
                await Write(input, "index.md", header);
            var bundle = await OkfBundle.ReadAsync(input);
            await Assert.That(bundle.IsSuccess).IsFalse();
            await Assert.That(bundle.SpecificationVersion).IsNull();
            await Assert.That(() => bundle.CreateSiteOptions(new("knowledge"))).Throws<InvalidOperationException>();
            await Assert.That(() => bundle.CreateAuxiliaryAssets("knowledge")).Throws<InvalidOperationException>();
            await bundle.ExportPreservedAsync(Path.Combine(workspace.Root, "output"));
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(workspace.Root, "output", "concept.md"))).Contains("type: Reference");
            if (header is null || header.StartsWith('#') || !header.Contains("okf_version"))
            {
                var diagnostic = bundle.Diagnostics.Single(item => item.Id == "OKF010");
                await Assert.That(diagnostic.Message).Contains("index.md");
                await Assert.That(diagnostic.Message).Contains("0.2");
                await Assert.That(diagnostic.Message).Contains("0.3");
            }
        }
    }

    [Test]
    public async Task Version03SkipsUntypedMarkdownDotPathsAndPreservesReservedFilesAndAssets()
    {
        using var workspace = new TemporaryWorkspace();
        await Write(workspace.Root, "index.md", "---\nokf_version: \"0.3\"\ntitle: Bundle\nx-extra: retained\n---\n# Bundle\n");
        await Write(workspace.Root, "README.md", "# Ordinary text\n");
        await Write(workspace.Root, "README.html", "asset");
        await Write(workspace.Root, "UPPER.MD", "asset");
        await Write(workspace.Root, "typed.md", "---\ntype: Unfamiliar\n---\n# Typed\n");
        await Write(workspace.Root, ".hidden/secret.md", "not parsed\n");
        await Write(workspace.Root, "sub/index.md", "# Reserved\n");
        await Write(workspace.Root, "log.md", "## 2026-11-20\nCreated.\n");
        var bundle = await OkfBundle.ReadAsync(workspace.Root);
        await Assert.That(bundle.IsSuccess).IsTrue();
        await Assert.That(bundle.Files.Any(file => file.Path.StartsWith('.'))).IsFalse();
        await Assert.That(bundle.Files.Single(file => file.Path == "UPPER.MD").Kind).IsEqualTo(ContentInputKind.Auxiliary);
        await Assert.That(bundle.Documents.Single(document => document.Path == "README.md").IsSkipped).IsTrue();
        await Assert.That(bundle.Documents.Where(document => document.IsConcept).Single().Path).IsEqualTo("typed.md");
        await Assert.That(bundle.Catalog!.Entries.Any(entry => entry.InputPath == "README.md")).IsFalse();
        await Assert.That(bundle.CreateContentCollection(new("knowledge")).Entries.Any(entry => entry.SourcePath == "README.md")).IsFalse();
        var limited = await OkfBundle.ReadAsync(workspace.Root, new() { MaxFiles = 1 });
        await Assert.That(limited.Files.Count).IsEqualTo(1);
        await Assert.That(limited.Files.Single().Path).IsEqualTo("index.md");
        await Assert.That(limited.SnapshotComplete).IsFalse();
        await Assert.That(limited.Diagnostics.Any(item => item.Id == "OKF010")).IsFalse();
        await Write(workspace.Root, "log.md", "---\ntype: Reference\n---\n## 2026-11-20\n");
        var log = await OkfBundle.ReadAsync(workspace.Root);
        await Assert.That(log.Diagnostics.Any(item => item.Id == "OKF009")).IsTrue();
        await Write(workspace.Root, "index.md", "---\nokf_version: \"0.2\"\n---\n");
        var legacy = await OkfBundle.ReadAsync(workspace.Root);
        await Assert.That(legacy.Files.Any(file => file.Path == ".hidden/secret.md")).IsTrue();
        await Assert.That(legacy.Files.Single(file => file.Path == "UPPER.MD").Kind).IsEqualTo(ContentInputKind.Concept);
        await Assert.That(legacy.Diagnostics.Any(item => item.Id == "OKF009")).IsFalse();
    }

    [Test]
    public async Task ChangingOnlyRootVersionAndPathFieldInvalidatesPublishedMetadataAndBacklinks()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "input");
        var output = Path.Combine(workspace.Root, "output");
        await Write(input, "index.md", "---\nokf_version: \"0.2\"\n---\n# Bundle\n");
        await Write(input, "source.md", "---\ntype: Reference\nresource: target.md\ngenerated: {by: process:new, at: 2026-11-20T00:00:00Z}\nverified: {by: human:old, at: 2026-11-19T00:00:00Z}\n---\n# Source\n");
        await Write(input, "target.md", "---\ntype: Reference\n---\n# Target\n");
        await Write(input, "other.md", "---\ntype: Reference\n---\n# Other\n");
        var asOf = new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero);
        var settings = new SiteSettings { BaseUrl = "https://example.test/", Language = "en", TimeZone = "UTC" };
        var customization = new SiteCustomization { Template = new DocsSiteTemplate(), FaviconSourceDirectory = Path.Combine(workspace.Root, "none") };
        var legacy = await OkfBundle.ReadAsync(input, new() { AsOf = asOf });
        var first = await new SiteGenerator().GenerateWithOptionsAsync(settings, [], output, false, customization,
            legacy.CreateSiteOptions(new("knowledge")) with { BuildTimestamp = asOf }, CancellationToken.None);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "knowledge", "source.html"))).Contains("HumanReviewed");
        await Write(input, "index.md", "---\nokf_version: \"0.3\"\n---\n# Bundle\n");
        var current = await OkfBundle.ReadAsync(input, new() { AsOf = asOf });
        var second = await new SiteGenerator().GenerateWithOptionsAsync(settings, [], output, false, customization,
            current.CreateSiteOptions(new("knowledge")) with { BuildTimestamp = asOf, PreviousBuildPlan = first.BuildPlan }, CancellationToken.None);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "knowledge", "source.html"))).Contains("Unverified");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "knowledge", "target.html"))).Contains("Referenced by");
        await Write(input, "source.md", "---\ntype: Reference\nresource: other.md\n---\n# Source\n");
        var retargeted = await OkfBundle.ReadAsync(input, new() { AsOf = asOf });
        await new SiteGenerator().GenerateWithOptionsAsync(settings, [], output, false, customization,
            retargeted.CreateSiteOptions(new("knowledge")) with { BuildTimestamp = asOf, PreviousBuildPlan = second.BuildPlan }, CancellationToken.None);
        await Assert.That((await File.ReadAllTextAsync(Path.Combine(output, "knowledge", "target.html"))).Contains("Referenced by")).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "knowledge", "other.html"))).Contains("Referenced by");
    }

    private static async Task Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
    }
}
