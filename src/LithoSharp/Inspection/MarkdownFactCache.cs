using Syntamark.Compilation;
using LithoSharp.Content.Compilation;

namespace LithoSharp.Inspection;

// This cache is owned by one workspace. Legacy site inspection never reads or writes it.
internal sealed class MarkdownFactCache(int maxEntries = 64, long maxBytes = 16 * 1024 * 1024)
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Scope, string Source), LinkedListNode<Entry>> _bySource = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly int _maxEntries = maxEntries > 0 ? maxEntries : throw new ArgumentOutOfRangeException(nameof(maxEntries));
    private readonly long _maxBytes = maxBytes > 0 ? maxBytes : throw new ArgumentOutOfRangeException(nameof(maxBytes));
    private long _retainedBytes;

    private sealed record Key(string ScopeId, string SourceId, string? SourceVersion, string TextHash,
        string ParserVersion, string ContractVersion, string ProfileId, string OptionsHash);
    private sealed record Entry(Key Key, MdDocumentFacts Facts, long Bytes);
    internal int Count { get { lock (_gate) return _bySource.Count; } }
    internal long RetainedBytes { get { lock (_gate) return _retainedBytes; } }

    internal bool TryGet(string scopeId, string sourceId, string? sourceVersion, string raw,
        MdOptions options, CancellationToken cancellationToken, out MdDocumentFacts facts, out long bytes)
    {
        facts = null!; bytes = 0;
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<Entry>? candidate;
        lock (_gate) _bySource.TryGetValue((scopeId, sourceId), out candidate);
        if (candidate is null || raw.Length > options.MaxInputUtf16) return false;
        var key = candidate.Value.Key;
        // Exact raw equality proves the retained TextHash without hashing a cache hit again.
        var matches = key.ScopeId == scopeId && key.SourceId == sourceId
            && key.SourceVersion == sourceVersion && key.ParserVersion == MdParserVersion.Value
            && key.ContractVersion == "1.0" && key.ProfileId == options.ProfileId
            && key.OptionsHash == options.Hash
            && string.Equals(candidate.Value.Facts.RawText, raw, StringComparison.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        if (!matches) return false;
        lock (_gate)
        {
            if (!_bySource.TryGetValue((scopeId, sourceId), out var current) || !ReferenceEquals(current, candidate)) return false;
            _lru.Remove(candidate); _lru.AddFirst(candidate);
            facts = candidate.Value.Facts; bytes = candidate.Value.Bytes;
            return true;
        }
    }

    // Logical size is measured outside the workspace's lifetime lock.
    // The caller publishes only after checking its reservation and epoch.
    internal void Publish(MdDocumentFacts facts, long bytes)
    {
        lock (_gate)
        {
            RemoveCore((facts.ScopeId, facts.SourceId));
            if (facts.Status != MdParseStatus.Complete || facts.TextHash is null || bytes > _maxBytes) return;
            if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            while (_bySource.Count >= _maxEntries || _retainedBytes + bytes > _maxBytes)
                RemoveCore((_lru.Last!.Value.Key.ScopeId, _lru.Last.Value.Key.SourceId));
            var key = new Key(facts.ScopeId, facts.SourceId, facts.SourceVersion, facts.TextHash,
                facts.ParserVersion, "1.0", "lithosharp-markdown/1", facts.OptionsHash);
            var node = _lru.AddFirst(new Entry(key, facts, bytes));
            _bySource.Add((facts.ScopeId, facts.SourceId), node);
            _retainedBytes += bytes;
        }
    }

    internal void Remove(string scopeId, string sourceId) { lock (_gate) RemoveCore((scopeId, sourceId)); }
    internal void Clear() { lock (_gate) { _bySource.Clear(); _lru.Clear(); _retainedBytes = 0; } }
    private void RemoveCore((string Scope, string Source) source)
    {
        if (!_bySource.Remove(source, out var node)) return;
        _lru.Remove(node); _retainedBytes -= node.Value.Bytes;
    }
}
