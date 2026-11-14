using System.Security.Cryptography;
using LithoSharp.Build;

namespace LithoSharp.Tests;

public sealed class SiteBuildCacheTests
{
    [Test]
    public async Task BodyReadPreservesLogicalLengthEmptyAndStrictUtf8()
    {
        using var workspace = new TemporaryWorkspace();
        var root = Path.Combine(workspace.Root, "cache");
        var identity = new string('a', 64);
        var cache = new SiteBuildCache(root, identity);
        var body = new string('x', 1_048_575) + "\u00e9";
        var hash = await cache.StoreBodyAsync(body, CancellationToken.None);
        await Assert.That(await cache.ReadBodyAsync(hash, CancellationToken.None)).IsEqualTo(body);
        var bodyPath = Path.Combine(root, identity, "blobs", hash + ".utf8");
        using (var heldBody = new FileStream(bodyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var duplicates = await Task.WhenAll(cache.StoreBodyAsync(body, CancellationToken.None),
                cache.StoreBodyAsync(body, CancellationToken.None));
            await Assert.That(duplicates).IsEquivalentTo(new[] { hash, hash });
        }
        await File.WriteAllTextAsync(bodyPath, "corrupt");
        await Assert.That(await cache.ReadBodyAsync(hash, CancellationToken.None)).IsNull();
        if (OperatingSystem.IsWindows())
        {
            using var heldCorruptBody = new FileStream(bodyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var writeFailed = false;
            try
            {
                await cache.StoreBodyAsync(body, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                writeFailed = true;
            }
            await Assert.That(writeFailed).IsTrue();
            await Assert.That(await cache.ReadBodyAsync(hash, CancellationToken.None)).IsNull();
        }
        await Assert.That(await cache.StoreBodyAsync(body, CancellationToken.None)).IsEqualTo(hash);
        await Assert.That(await cache.ReadBodyAsync(hash, CancellationToken.None)).IsEqualTo(body);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.StoreBodyAsync(body, canceled.Token));

        var emptyHash = await cache.StoreBodyAsync(string.Empty, CancellationToken.None);
        await Assert.That(await cache.ReadBodyAsync(emptyHash, CancellationToken.None)).IsEqualTo(string.Empty);

        byte[] malformed = [0xc3, 0x28];
        var malformedHash = Convert.ToHexStringLower(SHA256.HashData(malformed));
        await File.WriteAllBytesAsync(Path.Combine(root, identity, "blobs", malformedHash + ".utf8"), malformed);
        await Assert.That(await cache.ReadBodyAsync(malformedHash, CancellationToken.None)).IsNull();
    }
}
