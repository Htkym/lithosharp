using Syntamark.Compilation;
using System.Collections.Concurrent;
using LithoSharp.Content.Compilation;
using Syntamark.Hosting;

namespace LithoSharp.Inspection;

public sealed partial class DocumentWorkspace
{
    private readonly MarkdownFactCache _markdownFacts = new();
    private readonly ConcurrentDictionary<string, DocumentEntry> _markdownEntries = new(StringComparer.Ordinal);

    // Internal generic consumer adapter. It does not publish DocumentInfo or resolve site context,
    // and legacy InspectAsync/InspectVersionedAsync never pass through this strict cache.
    internal async Task<MdDocumentFacts> ParseMarkdownAsync(string sourceId, string raw, string? sourceVersion,
        long documentVersion, long projectGeneration, MdOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId); ArgumentNullException.ThrowIfNull(raw);
        cancellationToken.ThrowIfCancellationRequested();
        if (MdHash.InvalidUnicode(sourceId, cancellationToken) >= 0
            || sourceVersion is not null && MdHash.InvalidUnicode(sourceVersion, cancellationToken) >= 0)
            throw new ArgumentException("Markdown identity must be well-formed Unicode.");
        try { MarkdownYamlIdentity.RequirePinned(); }
        catch (InvalidOperationException) { ClearMarkdownFacts(); throw; }
        var effective = options ?? new MdOptions();
        DocumentEntry entry; CancellationTokenSource supersede; long epoch;
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            entry = _markdownEntries.GetOrAdd(sourceId, static _ => new DocumentEntry());
            if (IsStale(entry, documentVersion, projectGeneration) || IsOlderReservation(entry, documentVersion, projectGeneration))
                throw new OperationCanceledException(cancellationToken);
            entry.ReservedVersion = documentVersion; entry.ReservedGeneration = projectGeneration;
            entry.Pending?.Cancel(); supersede = new CancellationTokenSource(); entry.Pending = supersede; epoch = entry.Epoch;
        }
        var acquiredSlot = false;
        Task<(MdDocumentFacts Facts, long Bytes)>? analysis = null;
        try
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, supersede.Token))
            {
                await _analysisSlots.WaitAsync(linked.Token).ConfigureAwait(false); acquiredSlot = true;
                lock (_lifetimeLock)
                {
                    if (_disposed || entry.Epoch != epoch || supersede.IsCancellationRequested)
                        throw new OperationCanceledException(linked.Token);
                    analysis = Task.Run(() =>
                    {
                        if (_markdownFacts.TryGet(Id, sourceId, sourceVersion, raw, effective, linked.Token, out var cached, out var bytes))
                            return (cached, bytes);
                        var facts = MdParser.Parse(raw, Id, sourceId, sourceVersion, effective, linked.Token);
                        var measured = facts.Status == MdParseStatus.Complete && facts.TextHash is not null
                            ? MarkdownLogicalSize.Measure(facts, linked.Token) : 0;
                        return (facts, measured);
                    }, linked.Token);
                    _activeAnalyses.Add(analysis);
                }
                (MdDocumentFacts Facts, long Bytes) result;
                try { result = await analysis.ConfigureAwait(false); }
                finally { lock (_lifetimeLock) _activeAnalyses.Remove(analysis); }
                lock (_lifetimeLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed || entry.Epoch != epoch || supersede.IsCancellationRequested
                        || IsStale(entry, documentVersion, projectGeneration)
                        || IsOlderReservation(entry, documentVersion, projectGeneration))
                        throw new OperationCanceledException(cancellationToken);
                    _markdownFacts.Publish(result.Facts, result.Bytes);
                    entry.Version = documentVersion; entry.Generation = projectGeneration;
                    return result.Facts;
                }
            }
        }
        finally
        {
            lock (_lifetimeLock) if (ReferenceEquals(entry.Pending, supersede)) entry.Pending = null;
            if (acquiredSlot) _analysisSlots.Release();
            supersede.Dispose();
        }
    }

    internal void ClearMarkdownFacts()
    {
        lock (_lifetimeLock)
        {
            foreach (var entry in _markdownEntries.Values) { entry.Epoch++; entry.Pending?.Cancel(); entry.Pending = null; }
            _markdownEntries.Clear(); _markdownFacts.Clear();
        }
    }

    private void RemoveMarkdownFacts(string sourceId)
    {
        // Called while holding the lifetime lock; pending work still owns its old entry locally.
        if (_markdownEntries.TryRemove(sourceId, out var entry)) { entry.Epoch++; entry.Pending?.Cancel(); entry.Pending = null; }
        _markdownFacts.Remove(Id, sourceId);
    }
}
