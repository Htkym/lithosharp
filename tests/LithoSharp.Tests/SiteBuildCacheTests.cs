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
        await File.WriteAllTextAsync(Path.Combine(root, identity, "blobs", hash + ".utf8"), "corrupt");
        await Assert.That(await cache.ReadBodyAsync(hash, CancellationToken.None)).IsNull();

        var emptyHash = await cache.StoreBodyAsync(string.Empty, CancellationToken.None);
        await Assert.That(await cache.ReadBodyAsync(emptyHash, CancellationToken.None)).IsEqualTo(string.Empty);

        byte[] malformed = [0xc3, 0x28];
        var malformedHash = Convert.ToHexStringLower(SHA256.HashData(malformed));
        await File.WriteAllBytesAsync(Path.Combine(root, identity, "blobs", malformedHash + ".utf8"), malformed);
        await Assert.That(await cache.ReadBodyAsync(malformedHash, CancellationToken.None)).IsNull();
    }
}
