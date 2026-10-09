using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Quality;

namespace LithoSharp.Build;

// One output transaction owns this cache. Its lock also excludes ClearCache and other writers.
// Only complete local facts enter it; site-wide validity is never cached. Pending records are
// discarded on failure/cancellation and become persistent only after successful publication.
internal sealed class SiteHtmlFactsCache : IDisposable
{
    internal const int MaxRecordBytes = 1024 * 1024;
    internal const int MaxRecords = 128;
    internal const int MaxBytes = 32 * 1024 * 1024;
    internal static string Schema => "quality-facts/1;tree/1;document;scripting=false;projection/1;"
        + typeof(SiteHtmlFactsCache).Module.ModuleVersionId;
    private readonly string directory;
    private readonly Dictionary<string, byte[]> pending = new(StringComparer.Ordinal);
    private int pendingBytes;
    private bool disposed;
    internal int ParseCount { get; private set; }
    internal int HitCount { get; private set; }
    internal int PendingBytes => pendingBytes;

    internal SiteHtmlFactsCache(string partition) => directory = Path.Combine(partition, "html-facts");

    internal async Task<QualityHtmlFacts> GetAsync(string content, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var contentHash = Hash(Encoding.UTF8.GetBytes(content));
        var key = Hash(Encoding.UTF8.GetBytes(Schema + "\n" + contentHash));
        var path = Path.Combine(directory, key + ".json");
        try
        {
            EnsureSafe(path);
            await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(directory, path, asynchronous: true);
            // Limit both allocation and deserialization, including a concurrently growing record.
            if (stream.Length <= MaxRecordBytes)
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + count > MaxRecordBytes) break;
                    buffer.Write(chunk, 0, count);
                }
                if (count == 0)
                {
                    var record = JsonSerializer.Deserialize<Record>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
                    if (record is not null && record.Schema == Schema && record.ContentHash == contentHash
                        && record.Facts is not null && record.Facts.IsValid()
                        && record.Integrity == Integrity(record))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        HitCount++;
                        return record.Facts;
                    }
                }
            }
        }
        catch (Exception exception) when (OptionalFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        ParseCount++;
        var facts = QualityHtmlFacts.Parse(content, cancellationToken);
        if (facts.MaximumSerializedBytes > MaxRecordBytes || pending.Count >= MaxRecords)
            return facts;
        var value = new Record(Schema, contentHash, facts, null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value with { Integrity = Integrity(value) });
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length <= MaxRecordBytes && !pending.ContainsKey(key)
            && pending.Count < MaxRecords && pendingBytes + bytes.Length <= MaxBytes)
        {
            pending.Add(key, bytes);
            pendingBytes += bytes.Length;
        }
        return facts;
    }

    // Publication has passed its cancellation boundary. Optional cache I/O cannot fail it.
    internal async Task PublishAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            EnsureSafe(directory);
            Directory.CreateDirectory(directory);
            foreach (var (key, bytes) in pending)
            {
                var path = Path.Combine(directory, key + ".json");
                var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    EnsureSafe(temporary);
                    await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
                    EnsureSafe(temporary);
                    EnsureSafe(path);
                    File.Move(temporary, path, overwrite: true);
                }
                finally
                {
                    EnsureSafe(temporary);
                    File.Delete(temporary);
                }
            }
            // FIFO eviction within this partition; no recursive traversal or other cache folders.
            EnsureSafe(directory);
            var retainedFiles = new List<FileInfo>(MaxRecords + 1);
            long bytesRetained = 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json"))
            {
                EnsureSafe(file.FullName);
                if (file.Length > MaxRecordBytes) { File.Delete(file.FullName); continue; }
                retainedFiles.Add(file);
                bytesRetained += file.Length;
                while (retainedFiles.Count > MaxRecords || bytesRetained > MaxBytes)
                {
                    var oldest = retainedFiles.MinBy(item => (item.LastWriteTimeUtc, item.Name),
                        Comparer<(DateTime, string)>.Create((left, right) =>
                        {
                            var time = left.Item1.CompareTo(right.Item1);
                            return time != 0 ? time : StringComparer.Ordinal.Compare(left.Item2, right.Item2);
                        }))!;
                    EnsureSafe(oldest.FullName);
                    File.Delete(oldest.FullName);
                    bytesRetained -= oldest.Length;
                    retainedFiles.Remove(oldest);
                }
            }
        }
        catch (Exception exception) when (OptionalFailure(exception)) { }
        finally { pending.Clear(); pendingBytes = 0; }
    }

    public void Dispose() { pending.Clear(); pendingBytes = 0; disposed = true; }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Integrity(Record record) => Hash(JsonSerializer.SerializeToUtf8Bytes(record with { Integrity = null }));
    private static void EnsureSafe(string path) => SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(path)!, path);
    private static bool OptionalFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or ArgumentException or InvalidOperationException or JsonException or NotSupportedException or System.ComponentModel.Win32Exception;
    private sealed record Record(string Schema, string ContentHash, QualityHtmlFacts Facts, string? Integrity);
}
