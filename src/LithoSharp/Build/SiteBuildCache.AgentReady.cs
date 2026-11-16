using System.Security.Cryptography;
using System.Text.Json;
using LithoSharp.AgentReady;

namespace LithoSharp.Build;

internal sealed partial class SiteBuildCache
{
    /// <summary>Replays a complete outcome, or misses if any persisted part is invalid.</summary>
    internal async Task<PublicMarkdownBodyResult?> ReadPublicMarkdownAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDigest(key))
            return null;
        try
        {
            var path = Path.Combine(_directory, "public-markdown", key + ".json");
            EnsureSafePath(path);
            await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(_directory, path, asynchronous: true);
            var entry = await JsonSerializer.DeserializeAsync<PublicMarkdownCacheEntry>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null || entry.Version != 1 || entry.Key != key
                || (entry.BodyHash is not null && !IsDigest(entry.BodyHash))
                || !ValidPublicMarkdownOutcome(entry.Status, entry.BodyHash is not null, entry.Reason)
                || !IsDigest(entry.Integrity) || entry.Integrity != PublicMarkdownIntegrity(entry))
                return null;
            string? body = null;
            if (entry.BodyHash is { } hash)
            {
                body = await ReadBodyAsync(hash, cancellationToken).ConfigureAwait(false);
                if (body is null)
                    return null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(entry.Status, body, entry.Reason);
        }
        catch (Exception exception) when (IsCacheReadFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    /// <summary>
    /// Stores only the public body and generic outcome reason, never the source model.
    /// Like other cache writes, optional I/O failures are handled by the caller.
    /// </summary>
    internal async Task StorePublicMarkdownAsync(string key, PublicMarkdownBodyResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(result);
        if (!IsDigest(key))
            throw new ArgumentException("The cache key must be a lowercase SHA-256 digest.", nameof(key));
        if (!ValidPublicMarkdownOutcome(result.Status, result.Body is not null, result.Reason))
            throw new ArgumentException("The public Markdown outcome is inconsistent or was not requested.", nameof(result));
        var bodyHash = result.Body is null ? null : await StoreBodyAsync(result.Body, cancellationToken).ConfigureAwait(false);
        var entry = new PublicMarkdownCacheEntry(1, key, result.Status, bodyHash, result.Reason, null);
        entry = entry with { Integrity = PublicMarkdownIntegrity(entry) };
        var directory = Path.Combine(_directory, "public-markdown");
        var path = Path.Combine(directory, key + ".json");
        EnsureSafePath(path);
        Directory.CreateDirectory(directory);
        EnsureSafePath(path);
        var temporary = Path.Combine(directory, $".{key}-{Guid.NewGuid():N}.tmp");
        try
        {
            EnsureSafePath(temporary);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                EnsureSafePath(temporary);
                await JsonSerializer.SerializeAsync(stream, entry, cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePath(temporary);
            EnsureSafePath(path);
            // A key represents all conversion inputs; concurrent identical outcomes are harmless.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                EnsureSafePath(temporary);
                File.Delete(temporary);
            }
            catch (Exception exception) when (IsCacheReadFailure(exception)) { }
        }
    }

    private static bool ValidPublicMarkdownOutcome(PublicMarkdownStatus status, bool hasBody, string? reason) => status switch
    {
        PublicMarkdownStatus.Automatic or PublicMarkdownStatus.ExplicitReplacement => hasBody && reason is null,
        PublicMarkdownStatus.IntentionallyExcluded => !hasBody && (reason is null || !string.IsNullOrWhiteSpace(reason)),
        PublicMarkdownStatus.Unsupported or PublicMarkdownStatus.Unavailable or PublicMarkdownStatus.Failed =>
            !hasBody && !string.IsNullOrWhiteSpace(reason),
        _ => false,
    };

    private static string PublicMarkdownIntegrity(PublicMarkdownCacheEntry entry) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry with { Integrity = null })));

    private sealed record PublicMarkdownCacheEntry(int Version, string Key, PublicMarkdownStatus Status,
        string? BodyHash, string? Reason, string? Integrity);
}
