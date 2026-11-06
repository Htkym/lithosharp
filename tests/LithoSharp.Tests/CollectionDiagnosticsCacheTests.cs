using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LithoSharp.Build;
using LithoSharp.Diagnostics;

namespace LithoSharp.Tests;

public sealed class CollectionDiagnosticsCacheTests
{
    private static readonly string OutputIdentity = new('a', 64);

    [Test]
    public async Task KnownEmptyOutcomeRoundTripsSeparatelyFromLegacyUnknown()
    {
        using var workspace = new TemporaryWorkspace();
        var root = Path.Combine(workspace.Root, "cache");
        var cache = new SiteBuildCache(root, OutputIdentity);
        var digest = await cache.SaveAsync(
            [Node("known", CachedCollectionDiagnostics.Capture([])), Node("legacy", null)],
            CancellationToken.None);

        var loaded = await cache.LoadAsync(digest, CancellationToken.None);

        await Assert.That(loaded.Count).IsEqualTo(2);
        await Assert.That(loaded["known"].CollectionDiagnostics).IsNotNull();
        await Assert.That(loaded["known"].CollectionDiagnostics!.Diagnostics).IsEmpty();
        await Assert.That(loaded["known"].CollectionDiagnostics!.Restore()).IsEmpty();
        await Assert.That(loaded["legacy"].CollectionDiagnostics).IsNull();

        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(ManifestPath(root, digest)))!;
        var legacy = manifest["Nodes"]!.AsArray().Single(node => node!["NodeId"]!.GetValue<string>() == "legacy")!;
        legacy.AsObject().Remove("CollectionDiagnostics");
        var legacyDigest = await WriteManifestAsync(root, manifest);
        var legacyLoaded = await cache.LoadAsync(legacyDigest, CancellationToken.None);

        await Assert.That(legacyLoaded.Count).IsEqualTo(2);
        await Assert.That(legacyLoaded["legacy"].CollectionDiagnostics).IsNull();
        await Assert.That(legacyLoaded["known"].CollectionDiagnostics!.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task CaptureAndSnapshotPreserveDiagnosticOrderAndOwnTheirArrays()
    {
        SiteSourceLocation[] related = [new("includes/note.md", 7, 2, 7, 8)];
        SiteDiagnostic[] source =
        [
            new("LSM001", SiteDiagnosticSeverity.Warning, "First warning.",
                new SiteSourceLocation("content/page.md", 3, 5, 3, 9), "Markdown", related)
            { SnapshotIdentity = new string('d', 64) },
            new("LSM001", SiteDiagnosticSeverity.Warning, "Second warning.")
            { SnapshotIdentity = new string('e', 64) },
        ];
        var captured = CachedCollectionDiagnostics.Capture(source);
        source[0] = new SiteDiagnostic("REPLACED", SiteDiagnosticSeverity.Warning, "Changed source.");
        related[0] = new SiteSourceLocation("changed.md");
        var snapshot = captured.Snapshot();

        var capturedFirst = captured.Diagnostics[0];
        ((CachedCollectionDiagnostic[])captured.Diagnostics)[0] = capturedFirst with { Message = "Changed captured warning." };
        ((CachedCollectionSourceLocation[])capturedFirst.RelatedLocations)[0] =
            new CachedCollectionSourceLocation("changed-again.md", null, null, null, null);
        using var workspace = new TemporaryWorkspace();
        var cache = new SiteBuildCache(Path.Combine(workspace.Root, "cache"), OutputIdentity);
        var digest = await cache.SaveAsync([Node("page", snapshot)], CancellationToken.None);
        var loaded = await cache.LoadAsync(digest, CancellationToken.None);
        var restored = loaded["page"].CollectionDiagnostics!.Restore();

        await Assert.That(restored.Select(diagnostic => diagnostic.Id)).IsEquivalentTo(["LSM001", "LSM001"]);
        await Assert.That(restored.Select(diagnostic => diagnostic.Message).SequenceEqual(
            ["First warning.", "Second warning."])).IsTrue();
        await Assert.That(restored[0].Category).IsEqualTo("Markdown");
        var location = restored[0].Location ?? throw new InvalidOperationException("Expected a restored source location.");
        await Assert.That(location.FilePath).IsEqualTo("content/page.md");
        await Assert.That((location.Line, location.Column, location.EndLine, location.EndColumn))
            .IsEqualTo(((int?)3, (int?)5, (int?)3, (int?)9));
        await Assert.That(restored[0].RelatedLocations.Single().FilePath).IsEqualTo("includes/note.md");
        await Assert.That((restored[0].RelatedLocations[0].Line, restored[0].RelatedLocations[0].Column,
            restored[0].RelatedLocations[0].EndLine, restored[0].RelatedLocations[0].EndColumn))
            .IsEqualTo(((int?)7, (int?)2, (int?)7, (int?)8));
        await Assert.That(restored[1].Location).IsNull();
        await Assert.That(restored[1].RelatedLocations).IsEmpty();
        await Assert.That(restored.Select(diagnostic => diagnostic.SnapshotIdentity)
            .SequenceEqual([new string('d', 64), new string('e', 64)])).IsTrue();
    }

    [Test]
    [Arguments("unknown-version")]
    [Arguments("missing-diagnostics")]
    [Arguments("null-diagnostics")]
    [Arguments("null-diagnostic")]
    [Arguments("error-diagnostic")]
    [Arguments("unknown-severity")]
    [Arguments("empty-message")]
    [Arguments("column-without-line")]
    [Arguments("reversed-source-span")]
    [Arguments("invalid-related-location")]
    [Arguments("null-related-locations")]
    [Arguments("null-related-location")]
    [Arguments("missing-snapshot-identity")]
    [Arguments("null-snapshot-identity")]
    [Arguments("invalid-snapshot-identity")]
    public async Task SelfHashedMalformedOutcomeIsRejectedInsteadOfReplayed(string corruption)
    {
        using var workspace = new TemporaryWorkspace();
        var root = Path.Combine(workspace.Root, "cache");
        var cache = new SiteBuildCache(root, OutputIdentity);
        var warning = new SiteDiagnostic("LSM001", SiteDiagnosticSeverity.Warning, "Original warning.",
            new SiteSourceLocation("content/page.md", 3, 5, 3, 9), "Markdown",
            [new SiteSourceLocation("includes/note.md", 7, 2)]) { SnapshotIdentity = new string('d', 64) };
        var digest = await cache.SaveAsync(
            [Node("page", CachedCollectionDiagnostics.Capture([warning]))], CancellationToken.None);
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(ManifestPath(root, digest)))!;
        var outcome = manifest["Nodes"]![0]!["CollectionDiagnostics"]!;
        var diagnostic = outcome["Diagnostics"]![0]!;

        switch (corruption)
        {
            case "unknown-version":
                outcome["Version"] = CachedCollectionDiagnostics.CurrentVersion + 1;
                break;
            case "missing-diagnostics":
                outcome.AsObject().Remove("Diagnostics");
                break;
            case "null-diagnostics":
                outcome["Diagnostics"] = null;
                break;
            case "null-diagnostic":
                outcome["Diagnostics"]![0] = null;
                break;
            case "error-diagnostic":
                diagnostic["Severity"] = (int)SiteDiagnosticSeverity.Error;
                break;
            case "unknown-severity":
                diagnostic["Severity"] = int.MaxValue;
                break;
            case "empty-message":
                diagnostic["Message"] = " ";
                break;
            case "column-without-line":
                diagnostic["Location"]!["Line"] = null;
                break;
            case "reversed-source-span":
                diagnostic["Location"]!["EndLine"] = 2;
                break;
            case "invalid-related-location":
                diagnostic["RelatedLocations"]![0]!["FilePath"] = " ";
                break;
            case "null-related-locations":
                diagnostic["RelatedLocations"] = null;
                break;
            case "null-related-location":
                diagnostic["RelatedLocations"]![0] = null;
                break;
            case "missing-snapshot-identity":
                diagnostic.AsObject().Remove("SnapshotIdentity");
                break;
            case "null-snapshot-identity":
                diagnostic["SnapshotIdentity"] = null;
                break;
            case "invalid-snapshot-identity":
                diagnostic["SnapshotIdentity"] = new string('g', 64);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        var corruptedDigest = await WriteManifestAsync(root, manifest);
        var loaded = await cache.LoadAsync(corruptedDigest, CancellationToken.None);

        await Assert.That(loaded).IsEmpty();
    }

    private static CachedBuildNode Node(string id, CachedCollectionDiagnostics? diagnostics) =>
        new(id, new string('b', 64), [new CachedBuildArtifact($"artifact:{id}", $"{id}.html", 0, new string('c', 64))])
        {
            CollectionDiagnostics = diagnostics,
        };

    private static string ManifestPath(string root, string digest) =>
        Path.Combine(root, OutputIdentity, "builds", digest + ".json");

    private static async Task<string> WriteManifestAsync(string root, JsonNode manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await File.WriteAllBytesAsync(ManifestPath(root, digest), bytes);
        return digest;
    }
}
