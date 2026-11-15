using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LithoSharp.AgentReady;
using LithoSharp.Build;

namespace LithoSharp.Tests;

public sealed class AgentReadyOutcomeCacheTests
{
    private static readonly string Identity = new('a', 64);
    private static readonly string Key = new('b', 64);

    [Test]
    public async Task RoundTripsCompleteEmptyAndUnavailableOutcomesAcrossInstances()
    {
        using var workspace = new TemporaryWorkspace();
        var cache = new SiteBuildCache(workspace.Root, Identity);
        PublicMarkdownBodyResult[] outcomes =
        [
            new(PublicMarkdownStatus.Automatic, "\uFEFF# 日本語 🐈\r\n\n  exact \t\0", null),
            new(PublicMarkdownStatus.Automatic, "", null),
            new(PublicMarkdownStatus.ExplicitReplacement, "", null),
            new(PublicMarkdownStatus.ExplicitReplacement, "# Replacement\n", null),
            new(PublicMarkdownStatus.Unsupported, null, "Unsupported syntax."),
            new(PublicMarkdownStatus.Unavailable, null, "Analysis unavailable."),
            new(PublicMarkdownStatus.Failed, null, "Conversion failed."),
            new(PublicMarkdownStatus.IntentionallyExcluded, null, null),
            new(PublicMarkdownStatus.IntentionallyExcluded, null, "Excluded by policy."),
        ];
        foreach (var outcome in outcomes)
        {
            await cache.StorePublicMarkdownAsync(Key, outcome, CancellationToken.None);
            var fresh = new SiteBuildCache(workspace.Root, Identity);
            await Assert.That(await fresh.ReadPublicMarkdownAsync(Key, CancellationToken.None)).IsEqualTo(outcome);
        }
        await Assert.That(Directory.GetFiles(workspace.Root, "*.tmp", SearchOption.AllDirectories)).IsEmpty();
    }

    [Test]
    public async Task CorruptMissingOrMalformedBodyIsAlwaysAMissAndCanBeRepaired()
    {
        using var workspace = new TemporaryWorkspace();
        var cache = new SiteBuildCache(workspace.Root, Identity);
        var result = new PublicMarkdownBodyResult(PublicMarkdownStatus.Automatic, "exact", null);
        await cache.StorePublicMarkdownAsync(Key, result, CancellationToken.None);
        var blob = Directory.GetFiles(Path.Combine(workspace.Root, Identity, "blobs"), "*.utf8").Single();
        await File.WriteAllTextAsync(blob, "corrupt");
        await Assert.That(await cache.ReadPublicMarkdownAsync(Key, CancellationToken.None)).IsNull();
        await cache.StorePublicMarkdownAsync(Key, result, CancellationToken.None);
        await Assert.That(await cache.ReadPublicMarkdownAsync(Key, CancellationToken.None)).IsEqualTo(result);
        File.Delete(blob);
        await Assert.That(await cache.ReadPublicMarkdownAsync(Key, CancellationToken.None)).IsNull();

        byte[] malformed = [0xc3, 0x28];
        var hash = Convert.ToHexStringLower(SHA256.HashData(malformed));
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, Identity, "blobs", hash + ".utf8"), malformed);
        await Mutate(workspace.Root, entry => entry["BodyHash"] = hash);
        await Assert.That(await cache.ReadPublicMarkdownAsync(Key, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task RejectsCorruptVersionKeyIntegrityStatusAndBodyCombinations()
    {
        using var workspace = new TemporaryWorkspace();
        var cache = new SiteBuildCache(workspace.Root, Identity);
        var success = new PublicMarkdownBodyResult(PublicMarkdownStatus.Automatic, "body", null);
        Action<JsonNode>[] changes =
        [
            entry => entry["Version"] = 2,
            entry => entry["Key"] = new string('c', 64),
            entry => entry["Status"] = 999,
            entry => entry["Status"] = (int)PublicMarkdownStatus.NotRequested,
            entry => entry["Status"] = (int)PublicMarkdownStatus.Failed,
            entry => entry["BodyHash"] = null,
            entry => entry["BodyHash"] = "../escape",
            entry => entry["Reason"] = "Partial result is not complete.",
        ];
        foreach (var change in changes)
        {
            await cache.StorePublicMarkdownAsync(Key, success, CancellationToken.None);
            await Mutate(workspace.Root, change);
            await Assert.That(await cache.ReadPublicMarkdownAsync(Key, CancellationToken.None)).IsNull();
        }
        await cache.StorePublicMarkdownAsync(Key, new(PublicMarkdownStatus.Failed, null, "Generic failure."), CancellationToken.None);
        await Mutate(workspace.Root, entry => entry["Reason"] = " ");
        await Assert.That(await cache.ReadPublicMarkdownAsync(Key, CancellationToken.None)).IsNull();
        await cache.StorePublicMarkdownAsync(Key, success, CancellationToken.None);
        await Mutate(workspace.Root, entry => entry["Integrity"] = new string('0', 64), recompute: false);
        await Assert.That(await cache.ReadPublicMarkdownAsync(Key, CancellationToken.None)).IsNull();
        await File.WriteAllBytesAsync(EntryPath(workspace.Root), [0xff, 0xfe, 0x00]);
        await Assert.That(await cache.ReadPublicMarkdownAsync(Key, CancellationToken.None)).IsNull();
        await Assert.That(await cache.ReadPublicMarkdownAsync("../escape", CancellationToken.None)).IsNull();
        await Assert.That(await cache.ReadPublicMarkdownAsync(new string('c', 64), CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task InvalidStoresAndCancellationDoNotReplaceAnOutcome()
    {
        using var workspace = new TemporaryWorkspace();
        var cache = new SiteBuildCache(workspace.Root, Identity);
        var success = new PublicMarkdownBodyResult(PublicMarkdownStatus.Automatic, "body", null);
        await cache.StorePublicMarkdownAsync(Key, success, CancellationToken.None);
        PublicMarkdownBodyResult[] invalid =
        [
            new(PublicMarkdownStatus.NotRequested, null, null),
            new(PublicMarkdownStatus.Automatic, null, null),
            new(PublicMarkdownStatus.ExplicitReplacement, "partial", "failed"),
            new(PublicMarkdownStatus.Failed, "partial", "failed"),
            new(PublicMarkdownStatus.Failed, null, null),
            new(PublicMarkdownStatus.Unsupported, null, " "),
            new((PublicMarkdownStatus)999, null, "unknown"),
        ];
        foreach (var outcome in invalid)
            await Assert.ThrowsAsync<ArgumentException>(() => cache.StorePublicMarkdownAsync(Key, outcome, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.StorePublicMarkdownAsync("../escape", success, CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.StorePublicMarkdownAsync(Key, success, canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.ReadPublicMarkdownAsync(Key, canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.ReadPublicMarkdownAsync("invalid", canceled.Token));
        await Assert.That(await cache.ReadPublicMarkdownAsync(Key, CancellationToken.None)).IsEqualTo(success);
        await Assert.That(Directory.GetFiles(workspace.Root, "*.tmp", SearchOption.AllDirectories)).IsEmpty();
    }

    private static string EntryPath(string root) => Path.Combine(root, Identity, "public-markdown", Key + ".json");

    private static async Task Mutate(string root, Action<JsonNode> change, bool recompute = true)
    {
        var path = EntryPath(root);
        var entry = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        change(entry);
        if (recompute)
        {
            entry["Integrity"] = null;
            entry["Integrity"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry.ToJsonString())));
        }
        await File.WriteAllTextAsync(path, entry.ToJsonString());
    }
}
