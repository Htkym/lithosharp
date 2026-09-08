using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LithoSharp.Build;

internal sealed class SiteBuildCache
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _directory;

    internal SiteBuildCache(string root, string outputIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!IsDigest(outputIdentity))
            throw new ArgumentException("The output identity must be a lowercase SHA-256 digest.", nameof(outputIdentity));
        _directory = Path.Combine(Path.GetFullPath(root), outputIdentity);
    }

    internal async Task<IReadOnlyDictionary<string, CachedBuildNode>> LoadAsync(
        string? digest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDigest(digest)) return new Dictionary<string, CachedBuildNode>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(_directory, "builds", digest + ".json");
            EnsureSafePath(path);
            await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(_directory, path, asynchronous: true);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) != digest)
                return new Dictionary<string, CachedBuildNode>(StringComparer.Ordinal);
            stream.Position = 0;
            var manifest = await JsonSerializer.DeserializeAsync<CacheManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (manifest is null || manifest.Version != 1 || manifest.Nodes is null)
                return new Dictionary<string, CachedBuildNode>(StringComparer.Ordinal);
            var nodes = new Dictionary<string, CachedBuildNode>(StringComparer.Ordinal);
            foreach (var node in manifest.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsValid(node) || !nodes.TryAdd(node.NodeId, node))
                    return new Dictionary<string, CachedBuildNode>(StringComparer.Ordinal);
            }
            return nodes;
        }
        catch (Exception exception) when (IsCacheReadFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new Dictionary<string, CachedBuildNode>(StringComparer.Ordinal);
        }
    }

    internal async Task<string> SaveAsync(
        IEnumerable<CachedBuildNode> nodes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = nodes.Select(node => node with
        {
            Artifacts = node.Artifacts.OrderBy(artifact => artifact.ArtifactId, StringComparer.Ordinal)
                .ThenBy(artifact => artifact.RelativePath, StringComparer.Ordinal).ToArray(),
            Inputs = node.Inputs.OrderBy(input => input.Kind).ThenBy(input => input.Key, StringComparer.Ordinal)
                .ThenBy(input => input.Value, StringComparer.Ordinal).ToArray(),
            Dependencies = node.Dependencies.Order(StringComparer.Ordinal).ToArray(),
        }).OrderBy(node => node.NodeId, StringComparer.Ordinal).ToArray();
        if (snapshot.Any(node => !IsValid(node))
            || snapshot.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Cache nodes must have valid, unique identities and artifact fingerprints.", nameof(nodes));
        var directory = Path.Combine(_directory, "builds");
        EnsureSafePath(directory);
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            EnsureSafePath(temporary);
            string digest;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                EnsureSafePath(temporary);
                await JsonSerializer.SerializeAsync(stream, new CacheManifest(1, snapshot), cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                stream.Position = 0;
                digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, digest + ".json");
            EnsureSafePath(temporary);
            EnsureSafePath(path);
            File.Move(temporary, path, overwrite: true);
            return digest;
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

    internal Task<string> StoreBodyAsync(string body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StoreAsync("blobs", ".utf8", StrictUtf8.GetBytes(body), cancellationToken);
    }

    internal async Task<string?> ReadBodyAsync(string hash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDigest(hash)) return null;
        try
        {
            var bytes = await ReadVerifiedAsync("blobs", hash, ".utf8", cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : StrictUtf8.GetString(bytes);
        }
        catch (Exception exception) when (IsCacheReadFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private async Task<byte[]?> ReadVerifiedAsync(
        string folder, string digest, string extension, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, folder, digest + extension);
        EnsureSafePath(path);
        await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(_directory, path, asynchronous: true);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var bytes = buffer.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(SHA256.HashData(bytes)) == digest ? bytes : null;
    }

    private async Task<string> StoreAsync(
        string folder, string extension, byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var directory = Path.Combine(_directory, folder);
        var path = Path.Combine(directory, digest + extension);
        EnsureSafePath(path);
        Directory.CreateDirectory(directory);
        EnsureSafePath(path);
        var temporary = Path.Combine(directory, $".{digest}-{Guid.NewGuid():N}.tmp");
        try
        {
            EnsureSafePath(temporary);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                EnsureSafePath(temporary);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafePath(temporary);
            EnsureSafePath(path);
            // Repair corrupted bytes at the same content address; valid records retain identical bytes.
            File.Move(temporary, path, overwrite: true);
            return digest;
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

    private static void EnsureSafePath(string path) =>
        SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(path)!, path);

    private static bool IsDigest(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsValid(CachedBuildNode? node) => node is not null
        && !string.IsNullOrWhiteSpace(node.NodeId) && IsDigest(node.NodeKey)
        && (node.DerivedBodyHash is null || IsDigest(node.DerivedBodyHash))
        && node.Artifacts is not null && node.Artifacts.All(artifact => artifact is not null
            && !string.IsNullOrWhiteSpace(artifact.ArtifactId) && !string.IsNullOrWhiteSpace(artifact.RelativePath)
            && artifact.Length >= 0 && IsDigest(artifact.Sha256))
        && node.Artifacts.Select(artifact => artifact.ArtifactId).Distinct(StringComparer.Ordinal).Count() == node.Artifacts.Count
        && node.Artifacts.Select(artifact => artifact.RelativePath).Distinct(StringComparer.Ordinal).Count() == node.Artifacts.Count
        && node.Inputs is not null && node.Inputs.All(input => input is not null
            && Enum.IsDefined(input.Kind) && !string.IsNullOrWhiteSpace(input.Key))
        && node.Dependencies is not null && node.Dependencies.All(dependency => !string.IsNullOrWhiteSpace(dependency));

    private static bool IsCacheReadFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException
        or System.ComponentModel.Win32Exception or NotSupportedException;

    private sealed record CacheManifest(int Version, IReadOnlyList<CachedBuildNode> Nodes);
}

internal sealed record CachedBuildNode(
    string NodeId,
    string NodeKey,
    IReadOnlyList<CachedBuildArtifact> Artifacts,
    string? DerivedBodyHash = null)
{
    public IReadOnlyList<CachedBuildInput> Inputs { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}

internal sealed record CachedBuildArtifact(string ArtifactId, string RelativePath, long Length, string Sha256);

internal sealed record CachedBuildInput(BuildInputKind Kind, string Key, string? Value);
