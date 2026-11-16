using System.Text;
using LithoSharp.Content;
using LithoSharp.OpenKnowledge;
using LithoSharp.Configuration;
using LithoSharp.Pages;
using LithoSharp.Build;

namespace LithoSharp.Tests;

public sealed class OkfBundleTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task InputRootMustBeADirectoryAndMayBeEmpty()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "bundle");
        var missing = await OkfBundle.ReadAsync(input, new() { AsOf = AsOf });
        await Assert.That(missing.SnapshotComplete).IsFalse();
        await Assert.That(missing.IsComplete).IsFalse();
        await Assert.That(missing.Conformance).IsEqualTo(OkfConformance.NotAssessed);
        await Assert.That(missing.IsSuccess).IsFalse();
        var diagnostic = missing.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("OKF901");
        await Assert.That(diagnostic.Category).IsEqualTo("Io");
        var location = diagnostic.Location!;
        await Assert.That(location.FilePath).IsEqualTo(input);
        await Assert.That(location.Line).IsNull();
        await Assert.That(location.Column).IsNull();
        await Assert.That(location.EndLine).IsNull();
        await Assert.That(location.EndColumn).IsNull();

        await File.WriteAllTextAsync(input, "Not a bundle directory.");
        var file = await OkfBundle.ReadAsync(input, new() { AsOf = AsOf });
        await Assert.That(file.SnapshotComplete).IsFalse();
        var fileDiagnostic = file.Diagnostics.Single();
        await Assert.That(fileDiagnostic.Id).IsEqualTo("OKF901");
        await Assert.That(fileDiagnostic.Category).IsEqualTo("Io");
        await Assert.That(fileDiagnostic.Location!.FilePath).IsEqualTo(input);
        var output = Path.Combine(workspace.Root, "export");
        await Assert.That(() => file.ExportPreservedAsync(output)).Throws<InvalidOperationException>();
        await Assert.That(Directory.Exists(output)).IsFalse();

        File.Delete(input);
        Directory.CreateDirectory(input);
        var empty = await OkfBundle.ReadAsync(input, new() { AsOf = AsOf });
        await Assert.That(empty.SnapshotComplete).IsTrue();
        await Assert.That(empty.IsComplete).IsTrue();
        await Assert.That(empty.Conformance).IsEqualTo(OkfConformance.Conformant);
        await Assert.That(empty.IsSuccess).IsTrue();
        await Assert.That(empty.Files.Count).IsEqualTo(0);
        await Assert.That(empty.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MetadataPreservesUnknownStructureAndDerivesTrustWithoutInventingRecords()
    {
        using var workspace = new TemporaryWorkspace();
        await Write(workspace.Root, "index.md", "---\nokf_version: \"0.2\"\n---\n# Bundle\n");
        await Write(workspace.Root, "minimal.md", "---\ntype: Novel type\nextension: {nested: [true, null, \"null\", 999999999999999999999999]}\n---\n# Minimal\n");
        await Write(workspace.Root, "reviewed.md", """
            ---
            type: Reference
            sources:
              - id: scope
                resource: all queries in BigQuery project X
            generated: {by: unfamiliar-producer/build-7}
            verified:
              - {by: human:reviewer, at: 2026-11-13T09:00:00+09:00}
              - {by: process:bad-date, at: 2026-11-13}
            stale_after: 2026-11-13T01:00:00+01:00
            status: draft
            ---
            Content.
            """);
        await Write(workspace.Root, "machine.md", "---\ntype: Reference\nverified: {by: unfamiliar/build-7, at: 2026-11-12T23:00:00Z}\n---\n");
        await Write(workspace.Root, "empty.md", "---\ntype: Reference\nverified: []\n---\n");
        var bundle = await OkfBundle.ReadAsync(workspace.Root, new() { AsOf = AsOf });
        await Assert.That(bundle.IsConformant).IsTrue();
        await Assert.That(bundle.IsSuccess).IsTrue();
        var minimal = bundle.Documents.Single(document => document.Path == "minimal.md");
        await Assert.That(minimal.Metadata!.Type).IsEqualTo("Novel type");
        await Assert.That(minimal.Metadata.TrustTier).IsEqualTo(OkfTrustTier.Unverified);
        await Assert.That(minimal.Metadata.GetField("extension")!.Entries[0].Value.Children.Count).IsEqualTo(4);
        var reviewed = bundle.Documents.Single(document => document.Path == "reviewed.md").Metadata!;
        await Assert.That(reviewed.TrustTier).IsEqualTo(OkfTrustTier.HumanReviewed);
        await Assert.That(reviewed.VerificationIsComplete).IsFalse();
        await Assert.That(reviewed.Freshness).IsEqualTo(OkfFreshness.Stale);
        await Assert.That(reviewed.GeneratedAt).IsNull();
        await Assert.That(reviewed.Status).IsEqualTo("draft");
        await Assert.That(reviewed.Sources.Single().Resource).IsEqualTo("all queries in BigQuery project X");
        await Assert.That(bundle.Documents.Single(document => document.Path == "machine.md").Metadata!.TrustTier).IsEqualTo(OkfTrustTier.MachineConfirmed);
        await Assert.That(bundle.Documents.Single(document => document.Path == "empty.md").Metadata!.TrustTier).IsEqualTo(OkfTrustTier.NoRecords);
        var strict = await OkfBundle.ReadAsync(workspace.Root, new() { AsOf = AsOf, StrictLint = true });
        await Assert.That(strict.IsConformant).IsTrue();
        await Assert.That(strict.IsSuccess).IsFalse();
    }

    [Test]
    public async Task InvalidStructureAndUnsupportedYamlRemainDistinctAndOtherDocumentsAreInspected()
    {
        using var workspace = new TemporaryWorkspace();
        await Write(workspace.Root, "index.md", "---\nokf_version: &version \"0.2\"\n---\n# Bundle\n");
        await Write(workspace.Root, "numeric.md", "---\ntype: 42\n---\n");
        await Write(workspace.Root, "empty.md", "---\ntype: \"\"\n---\n");
        await Write(workspace.Root, "null.md", "---\ntype: null\n---\n");
        await Write(workspace.Root, "array.md", "---\ntype: []\n---\n");
        await Write(workspace.Root, "root.md", "---\n- type: Reference\n---\n");
        await Write(workspace.Root, "nested/index.md", "---\ntype: Reference\n---\n");
        await Write(workspace.Root, "log.md", "---\nproducer: retained\n---\n# Updates\n## 2026-02-30\nInvalid date\n");
        await Write(workspace.Root, "README.md", "---\ntype: Reference\n---\n");
        var bundle = await OkfBundle.ReadAsync(workspace.Root, new() { AsOf = AsOf });
        await Assert.That(bundle.Documents.Count).IsEqualTo(9);
        await Assert.That(bundle.SnapshotComplete).IsTrue();
        await Assert.That(bundle.Conformance).IsEqualTo(OkfConformance.NonConformant);
        await Assert.That(bundle.Diagnostics.Any(diagnostic => diagnostic.Category == "Unsupported" && diagnostic.Location!.FilePath == "index.md")).IsTrue();
        await Assert.That(bundle.Diagnostics.Any(diagnostic => diagnostic.Category == "Structure" && diagnostic.Location!.FilePath == "index.md")).IsFalse();
        await Assert.That(bundle.Diagnostics.Count(diagnostic => diagnostic.Id is "OKF004" or "OKF005" or "OKF006" or "OKF008")).IsEqualTo(7);
        await Assert.That(bundle.Documents.Single(document => document.Path == "README.md").Kind).IsEqualTo(ContentInputKind.Concept);
    }

    [Test]
    public async Task LinksUseParsedFactsAndBundleRoutesWhileMissingConceptsRemainValueRelations()
    {
        using var workspace = new TemporaryWorkspace();
        await Write(workspace.Root, "index.md", "# Bundle\n");
        await Write(workspace.Root, "tables/index.md", "# Tables\n");
        await Write(workspace.Root, "tables/target.md", "---\ntype: Reference\n---\n# Target\n");
        await Write(workspace.Root, "cafe\u0301.md", "---\ntype: Reference\n---\n# Title\n");
        await Write(workspace.Root, "tables/source.md", """
            ---
            type: Reference
            ---
            [Root](/tables/target.md#target) [Relative](target.md) [Directory](/tables/) [Index](/index.md)
            [Future](future.md) [Script](../query.sql) [Outside](../../outside.md)
            [NFD](/caf%C3%A9.md#title)
            `[Code](code.md)`
            \[Escaped](escape.md)
            ```md
            [Fence](fence.md)
            ```
            """);
        await Write(workspace.Root, "query.sql", "SELECT 1;\n");
        var bundle = await OkfBundle.ReadAsync(workspace.Root, new() { AsOf = AsOf, BaseUrl = "https://example.test/docs/" });
        var links = bundle.Links.Where(link => link.SourcePath == "tables/source.md").ToArray();
        await Assert.That(links.Length).IsEqualTo(8);
        await Assert.That(links.Single(link => link.OriginalTarget == "/tables/target.md#target").PublicUrl).IsEqualTo("/docs/tables/target.html#target");
        await Assert.That(links.Single(link => link.OriginalTarget == "/tables/").PublicUrl).IsEqualTo("/docs/tables/index.html");
        await Assert.That(links.Single(link => link.OriginalTarget == "future.md").TargetConceptId).IsEqualTo("tables/future");
        await Assert.That(links.Single(link => link.OriginalTarget == "../query.sql").Kind).IsEqualTo(OkfLinkKind.Auxiliary);
        await Assert.That(links.Single(link => link.OriginalTarget == "../../outside.md").Kind).IsEqualTo(OkfLinkKind.OutsideBundle);
        await Assert.That(links.Single(link => link.OriginalTarget == "/caf%C3%A9.md#title").TargetConceptId).IsEqualTo("cafe\u0301");
        await Assert.That(bundle.IsSuccess).IsTrue();
    }

    [Test]
    public async Task PreservedExportRetainsBomNewlinesUnknownMetadataAndAuxiliaryBytes()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "in");
        var output = Path.Combine(workspace.Root, "out");
        Directory.CreateDirectory(input);
        var document = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("---\r\ntype: Reference\r\nunknown: {quoted: \"null\"}\r\n---\r\n# Text\r\n")).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(input, "cafe\u0301.md"), document);
        await File.WriteAllBytesAsync(Path.Combine(input, "image.png"), [0, 255, 17, 42]);
        var bundle = await OkfBundle.ReadAsync(input, new() { AsOf = AsOf });
        var externalCopy = bundle.Files.Single(file => file.Path.EndsWith(".md", StringComparison.Ordinal)).GetOriginalBytes();
        externalCopy[0] = 0;
        await bundle.ExportPreservedAsync(output);
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(output, "cafe\u0301.md")))).IsEqualTo(Convert.ToHexString(document));
        await Assert.That(Convert.ToHexString(await File.ReadAllBytesAsync(Path.Combine(output, "image.png")))).IsEqualTo("00FF112A");
        await Assert.That(() => bundle.ExportPreservedAsync(output)).Throws<IOException>();
        await Assert.That(() => bundle.ExportPreservedAsync(Path.Combine(input, "nested"))).Throws<ArgumentException>();
    }

    [Test]
    public async Task ResourceLimitsAndCancellationNeverClaimValidatedCompleteSnapshots()
    {
        using var workspace = new TemporaryWorkspace();
        await Write(workspace.Root, "first.md", "---\ntype: Reference\n---\n");
        await Write(workspace.Root, "second.md", "---\ntype: Reference\n---\n");
        var limited = await OkfBundle.ReadAsync(workspace.Root, new() { AsOf = AsOf, MaxFiles = 1 });
        await Assert.That(limited.SnapshotComplete).IsFalse();
        await Assert.That(limited.Conformance).IsEqualTo(OkfConformance.NotAssessed);
        await Assert.That(limited.Diagnostics.Single(diagnostic => diagnostic.Id == "OKF900").Category).IsEqualTo("ResourceLimit");
        await Assert.That(() => limited.ExportPreservedAsync(Path.Combine(workspace.Root, "export"))).Throws<InvalidOperationException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = await OkfBundle.ReadAsync(workspace.Root, new() { AsOf = AsOf }, cancellation.Token);
        await Assert.That(canceled.IsSuccess).IsFalse();
        await Assert.That(canceled.Diagnostics.Single().Category).IsEqualTo("Canceled");
    }

    [Test]
    public async Task EmptyDirectoriesAlsoConsumeTheExplicitDiscoveryLimit()
    {
        using var workspace = new TemporaryWorkspace();
        foreach (var name in new[] { "a", "b", "c" })
            Directory.CreateDirectory(Path.Combine(workspace.Root, name));
        var bundle = await OkfBundle.ReadAsync(workspace.Root, new() { AsOf = AsOf, MaxDiscoveryEntries = 2 });
        await Assert.That(bundle.SnapshotComplete).IsFalse();
        await Assert.That(bundle.Conformance).IsEqualTo(OkfConformance.NotAssessed);
        await Assert.That(bundle.Diagnostics.Single().Category).IsEqualTo("ResourceLimit");
        await Assert.That(bundle.Diagnostics.Single().Message).Contains("MaxDiscoveryEntries (2)");
    }

    [Test]
    public async Task TimestampViewsRejectTrailingNewlinesAndFreshnessKeepsSubTickPrecision()
    {
        using var workspace = new TemporaryWorkspace();
        await Write(workspace.Root, "newline.md", "---\ntype: Reference\nstale_after: |\n  2026-11-13T00:00:00Z\nverified: {by: human:reviewer, at: \"2026-11-13T00:00:00Z\\n\"}\n---\n");
        await Write(workspace.Root, "precise.md", "---\ntype: Reference\nstale_after: 2026-11-13T09:00:00.00000001+09:00\nverified: {by: human:reviewer, at: 2026-11-13T00:00:00.00000001Z}\n---\n");
        var bundle = await OkfBundle.ReadAsync(workspace.Root, new() { AsOf = AsOf });
        var newline = bundle.Documents.Single(document => document.Path == "newline.md").Metadata!;
        await Assert.That(newline.Freshness).IsEqualTo(OkfFreshness.Invalid);
        await Assert.That(newline.TrustTier).IsEqualTo(OkfTrustTier.Indeterminate);
        var precise = bundle.Documents.Single(document => document.Path == "precise.md").Metadata!;
        await Assert.That(precise.Freshness).IsEqualTo(OkfFreshness.Fresh);
        await Assert.That(precise.StaleAfter).IsNull();
        await Assert.That(precise.TrustTier).IsEqualTo(OkfTrustTier.HumanReviewed);
        await Assert.That(precise.VerificationEvents.Single().IsValid).IsTrue();
        await Assert.That(precise.VerificationEvents.Single().At).IsNull();
        await Assert.That(precise.GetField("stale_after")!.Value).IsEqualTo("2026-11-13T09:00:00.00000001+09:00");
        var later = await OkfBundle.ReadAsync(workspace.Root, new() { AsOf = AsOf.AddTicks(1) });
        await Assert.That(later.Documents.Single(document => document.Path == "precise.md").Metadata!.Freshness).IsEqualTo(OkfFreshness.Stale);
        await Assert.That(OkfMetadata.TryParseOffsetDateTime("2026-11-13T00:00:00Z\n", out _)).IsFalse();
        await Assert.That(OkfMetadata.TryParseOffsetDateTime("2026-11-13T00:00:00.000000100Z", out var exact)).IsTrue();
        await Assert.That(exact).IsEqualTo(AsOf.AddTicks(1));
    }

    [Test]
    public async Task CapturedDependenciesRejectConflictingHashesAndPreservePublicFileValues()
    {
        var first = new string('a', 64);
        var captured = ContentDependency.FromCapturedFile("cafe\u0301.md", first);
        var snapshot = ContentDependency.Snapshot([ContentDependency.FromFile("cafe\u0301.md"), captured]);
        await Assert.That(snapshot.Single().Value).IsNull();
        await Assert.That(snapshot.Single().CapturedHash).IsEqualTo(first);
        await Assert.That(snapshot.Single().PhysicalFilePath).IsEqualTo("cafe\u0301.md");
        await Assert.That(() => ContentDependency.Snapshot([captured, ContentDependency.FromCapturedFile("cafe\u0301.md", new string('b', 64))]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ImmutableBundlePublicationKeepsCapturedBytesAndFileHashesUntilTheNextRead()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "in");
        var output = Path.Combine(workspace.Root, "out");
        await Write(input, "guide.md", "---\ntype: Reference\n---\n# Old snapshot\n\n[Data](data.bin)\n");
        await Write(input, "data.bin", "old-data");
        var old = await OkfBundle.ReadAsync(input, new() { AsOf = AsOf });
        await Write(input, "guide.md", "---\ntype: Reference\n---\n# New snapshot\n\n[Data](data.bin)\n");
        await Write(input, "data.bin", "new-data");
        var settings = new SiteSettings { BaseUrl = "https://example.test/sub/", Language = "en", TimeZone = "UTC" };
        var customization = new SiteCustomization { Template = new DocsSiteTemplate(), FaviconSourceDirectory = Path.Combine(workspace.Root, "none") };
        var first = await new SiteGenerator().GenerateWithOptionsAsync(settings, [], output, false, customization,
            old.CreateSiteOptions(new("knowledge")) with { BuildTimestamp = AsOf }, CancellationToken.None);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "knowledge", "data.bin"))).IsEqualTo("old-data");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "knowledge", "guide.html"))).Contains("Old snapshot");
        var source = old.Files.Single(file => file.Path == "guide.md");
        await Assert.That(first.BuildPlan.Nodes.SelectMany(node => node.Inputs)
            .Any(value => value.Kind == BuildInputKind.File && value.Key == "guide.md" && value.Value == source.Sha256)).IsTrue();
        var current = await OkfBundle.ReadAsync(input, new() { AsOf = AsOf });
        await Assert.That(current.SnapshotFingerprint).IsNotEqualTo(old.SnapshotFingerprint);
        await new SiteGenerator().GenerateWithOptionsAsync(settings, [], output, false, customization,
            current.CreateSiteOptions(new("knowledge")) with { BuildTimestamp = AsOf, PreviousBuildPlan = first.BuildPlan }, CancellationToken.None);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "knowledge", "data.bin"))).IsEqualTo("new-data");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "knowledge", "guide.html"))).Contains("New snapshot");
    }

    private static async Task Write(string root, string path, string text)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, text, new UTF8Encoding(false));
    }

    [Test]
    public async Task ActualDocsBuildProjectsLinksAndFootnotesAndUsesAnExplicitPublicationPolicy()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "in");
        var output = Path.Combine(workspace.Root, "out");
        await Write(input, "index.md", "# Knowledge\n\n[Guide](guide.md)\n");
        await Write(input, "guide.md", """
            ---
            type: Reference
            sources:
              - {id: source-b, resource: an observed source population}
              - {id: source-a, resource: references/original.md}
            ---
            # Guide
            ## Details
            [Missing](not-written.md) [Draft](draft.md) [SQL](query.sql)
            A claim.[^SOURCE-A] The same claim.[^source-a]

            [^source-a]: Attribution prose.
            """);
        await Write(input, "draft.md", "---\ntype: Reference\nstatus: draft\nstale_after: 2020-01-01T00:00:00Z\n---\n# Draft\n");
        await Write(input, "query.sql", "SELECT 'inert';\n");
        var bundle = await OkfBundle.ReadAsync(input, new() { AsOf = AsOf, BaseUrl = "https://example.test/sub/" });
        var guide = bundle.Documents.Single(document => document.Path == "guide.md");
        await Assert.That(guide.Citations.Count).IsEqualTo(2);
        await Assert.That(guide.Citations.All(static citation => citation.Source?.Id == "source-a")).IsTrue();
        var options = bundle.CreateSiteOptions(new("knowledge")) with { BuildTimestamp = AsOf };
        var customization = new SiteCustomization { Template = new DocsSiteTemplate(), FaviconSourceDirectory = Path.Combine(workspace.Root, "no-favicons") };
        var settings = new SiteSettings { BaseUrl = "https://example.test/sub/", Language = "en", TimeZone = "UTC" };
        var built = await new SiteGenerator().GenerateWithOptionsAsync(settings, [], output, false, customization, options, CancellationToken.None);
        var receipt = built.BuildPlan.Nodes.Single(node => node.Inputs.Any(input => input.Key == "content.sourcePath" && input.Value == "guide.md"));
        await Assert.That(receipt.Inputs.Any(input => input.Key == "content.inputProfile" && input.Value == "OpenKnowledgeFormat")).IsTrue();
        await Assert.That(receipt.Inputs.Any(input => input.Key == "content.inputKind" && input.Value == "Concept")).IsTrue();
        await Assert.That(receipt.Inputs.Single(input => input.Key == "content.inputRelations").Value!).Contains("\"Kind\":\"Auxiliary\"");
        await Assert.That(built.BuildPlan.Nodes.Any(node => node.Inputs.Any(input => input.Key == "content.sourcePath" && input.Value == "query.sql")
            && node.Inputs.Any(input => input.Key == "content.inputKind" && input.Value == "Auxiliary"))).IsTrue();
        var html = await File.ReadAllTextAsync(Path.Combine(output, "knowledge", "guide.html"));
        await Assert.That(html).Contains("<span class=\"okf-unresolved\" data-okf-target=\"not-written.md\">Missing</span>");
        await Assert.That(html).Contains("href=\"/sub/knowledge/draft.html\"");
        await Assert.That(html).Contains("href=\"/sub/knowledge/query.sql\"");
        await Assert.That(html).Contains("href=\"#details\"");
        await Assert.That(html).Contains("footnote");
        await Assert.That(File.Exists(Path.Combine(output, "knowledge", "draft.html"))).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "knowledge", "query.sql"))).IsEqualTo("SELECT 'inert';\n");

        var policyOptions = bundle.CreateSiteOptions(new("knowledge"),
            document => new PageMetadata(document.Metadata?.Title ?? document.ConceptId ?? document.Path, draft: document.Metadata?.Status == "draft"), "hide-declared-drafts/1")
            with
        { BuildTimestamp = AsOf };
        await new SiteGenerator().GenerateWithOptionsAsync(settings, [], output, false, customization, policyOptions, CancellationToken.None);
        var policyHtml = await File.ReadAllTextAsync(Path.Combine(output, "knowledge", "guide.html"));
        await Assert.That(policyHtml).Contains("data-okf-target=\"draft.md\">Draft</span>");
        await Assert.That(File.Exists(Path.Combine(output, "knowledge", "draft.html"))).IsFalse();
    }
}
