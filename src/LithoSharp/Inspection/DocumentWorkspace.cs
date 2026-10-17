using System.Collections.Concurrent;

namespace LithoSharp.Inspection;

/// <summary>Owns only its snapshot cache; returned immutable snapshots survive replacement or disposal.</summary>
/// <remarks>Versioned publication checks both document version and project generation so older requests cannot overwrite newer results.</remarks>
public sealed partial class DocumentWorkspace : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DocumentInfo> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DocumentEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _analysisSlots = new(2, 2);
    private readonly object _lifetimeLock = new();
    private readonly HashSet<Task> _activeAnalyses = new();
    private bool _disposed;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    private sealed class DocumentEntry
    {
        public long Version = -1;
        public long Generation = -1;
        public long Epoch;
        public long? ReservedVersion;
        public long? ReservedGeneration;
        public string? LastText;
        public string? LastSourcePath;
        public DocumentInspectionOptions? LastOptions;
        public CancellationTokenSource? Pending;
    }

    public Task<DocumentInfo> InspectAsync(string sourcePath, string text, DocumentInspectionOptions? options = null, CancellationToken cancellationToken = default) =>
        InspectCoreAsync(sourcePath, text, null, null, options, cancellationToken);

    /// <summary>Retains only the latest pending request per document and permits at most two concurrent analyses. Superseded requests are canceled.</summary>
    /// <remarks>Do not mix this API with unversioned inspection for the same key.</remarks>
    public Task<DocumentInfo> InspectVersionedAsync(
        string sourcePath,
        string text,
        long documentVersion,
        long projectGeneration,
        DocumentInspectionOptions? options = null,
        CancellationToken cancellationToken = default) =>
        InspectCoreAsync(sourcePath, text, documentVersion, projectGeneration, options, cancellationToken);

    private static string KeyOf(string documentId, DocumentInspectionOptions? options) =>
        options?.Project is null ? documentId : options.Project.ProjectId + "\0" + documentId;

    private async Task<DocumentInfo> InspectCoreAsync(
        string sourcePath,
        string text,
        long? documentVersion,
        long? projectGeneration,
        DocumentInspectionOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var key = KeyOf(options?.DocumentId ?? sourcePath, options);

        DocumentEntry entry;
        CancellationTokenSource supersede;
        long epoch;
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            entry = _entries.GetOrAdd(key, static _ => new DocumentEntry());
            if (IsStale(entry, documentVersion, projectGeneration)
                || IsOlderReservation(entry, documentVersion, projectGeneration))
            {
                throw new OperationCanceledException(cancellationToken);
            }
            if (IsSameRevision(entry, documentVersion, projectGeneration, sourcePath, text, options)
                && _snapshots.TryGetValue(key, out var current))
            {
                return current;
            }

            if (documentVersion is not null)
            {
                entry.ReservedVersion = documentVersion;
                entry.ReservedGeneration = projectGeneration ?? long.MaxValue;
            }

            entry.Pending?.Cancel();
            supersede = new CancellationTokenSource();
            entry.Pending = supersede;
            epoch = entry.Epoch;
        }

        var acquiredSlot = false;
        Task<DocumentInfo>? analysis = null;
        try
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, supersede.Token))
            {
                // Link supersession before waiting for a slot. Otherwise every stale keystroke
                // remains queued behind the two active parsers until it eventually gets a turn.
                await _analysisSlots.WaitAsync(linked.Token).ConfigureAwait(false);
                acquiredSlot = true;
                lock (_lifetimeLock)
                {
                    if (_disposed || entry.Epoch != epoch || supersede.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(linked.Token);
                    }

                    analysis = Task.Run(() => DocumentInspection.Inspect(sourcePath, text, options, linked.Token), linked.Token);
                    _activeAnalyses.Add(analysis);
                }

                DocumentInfo info;
                try
                {
                    info = await analysis.ConfigureAwait(false);
                }
                finally
                {
                    lock (_lifetimeLock)
                    {
                        _activeAnalyses.Remove(analysis);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                lock (_lifetimeLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_disposed)
                    {
                        throw new ObjectDisposedException(GetType().FullName);
                    }

                    if (entry.Epoch != epoch || supersede.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (IsStale(entry, documentVersion, projectGeneration))
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    _snapshots[key] = info;
                    entry.Version = documentVersion ?? long.MaxValue;
                    entry.Generation = projectGeneration ?? long.MaxValue;
                    entry.LastText = text;
                    entry.LastSourcePath = sourcePath;
                    entry.LastOptions = options;

                    return info;
                }
            }
        }
        finally
        {
            lock (_lifetimeLock)
            {
                if (ReferenceEquals(entry.Pending, supersede))
                {
                    entry.Pending = null;
                }
            }

            if (acquiredSlot)
            {
                _analysisSlots.Release();
            }

            // The linked token source above is disposed before its supersession source.
            supersede.Dispose();
        }
    }

    private static bool IsSameRevision(DocumentEntry entry, long? version, long? generation,
        string sourcePath, string text, DocumentInspectionOptions? options) =>
        entry.LastText is not null
        && string.Equals(entry.LastText, text, StringComparison.Ordinal)
        && string.Equals(entry.LastSourcePath, sourcePath, StringComparison.Ordinal)
        && SameOptions(entry.LastOptions, options)
        && (version is null || (entry.Version == version && entry.Generation == (generation ?? long.MaxValue)));

    // Project snapshots are immutable. A replacement object conservatively
    // invalidates reuse even when its project id and generation are unchanged.
    private static bool SameOptions(DocumentInspectionOptions? previous, DocumentInspectionOptions? current) =>
        string.Equals(previous?.DocumentId, current?.DocumentId, StringComparison.Ordinal)
        && string.Equals(previous?.Route, current?.Route, StringComparison.Ordinal)
        && string.Equals(previous?.Version, current?.Version, StringComparison.Ordinal)
        && string.Equals(previous?.Locale, current?.Locale, StringComparison.Ordinal)
        && (previous?.EnableCompatibilityAdvisory ?? false) == (current?.EnableCompatibilityAdvisory ?? false)
        && ReferenceEquals(previous?.Project, current?.Project);

    private static bool IsOlderReservation(DocumentEntry entry, long? version, long? generation)
    {
        if (version is null || entry.ReservedVersion is null || entry.ReservedGeneration is null)
        {
            return false;
        }

        var generationValue = generation ?? long.MaxValue;
        return generationValue < entry.ReservedGeneration.Value
            || (generationValue == entry.ReservedGeneration.Value && version.Value < entry.ReservedVersion.Value);
    }

    private static bool IsStale(DocumentEntry entry, long? version, long? generation)
    {
        if (version is null)
        {
            return false;
        }

        var generationValue = generation ?? long.MaxValue;
        return generationValue < entry.Generation
            || (generationValue == entry.Generation && version < entry.Version);
    }

    /// <summary>Returns false after workspace disposal.</summary>
    public bool TryGet(string documentId, out DocumentInfo? documentInfo)
    {
        documentInfo = null;
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return false;
            }
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return _snapshots.TryGetValue(documentId, out documentInfo);
    }

    /// <summary>Returns false after workspace disposal.</summary>
    public bool TryGet(string projectId, string documentId, out DocumentInfo? documentInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        documentInfo = null;
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return false;
            }
        }

        return _snapshots.TryGetValue(projectId + "\0" + documentId, out documentInfo);
    }

    /// <summary>Cancels pending requests and invalidates their generation, preventing late completion from restoring deleted documents.</summary>
    public bool Remove(string documentId)
    {
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return false;
            }
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        return RemoveCore(documentId);
    }

    /// <summary>Cancels pending requests and invalidates their generation, preventing late completion from restoring deleted documents.</summary>
    public bool Remove(string projectId, string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return false;
            }
        }

        return RemoveCore(projectId + "\0" + documentId);
    }

    private bool RemoveCore(string key)
    {
        lock (_lifetimeLock)
        {
            RemoveMarkdownFacts(key);
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Epoch++;
                entry.Pending?.Cancel();
                entry.Pending = null;
                entry.Version = -1;
                entry.Generation = -1;
                entry.ReservedVersion = null;
                entry.ReservedGeneration = null;
                entry.LastText = null;
                entry.LastSourcePath = null;
                entry.LastOptions = null;
                // Active work owns its entry locally and is tracked separately
                // for disposal; a closed key does not retain an empty tombstone.
                _entries.TryRemove(key, out _);
            }

            return _snapshots.TryRemove(key, out _);
        }
    }

    /// <summary>Cancels pending work and waits with a bounded timeout; caller-held snapshots remain valid.</summary>
    public async ValueTask DisposeAsync()
    {
        List<Task> active;
        List<CancellationTokenSource> pending;
        lock (_lifetimeLock)
        {
            _disposed = true;
            ClearMarkdownFacts();
            active = _activeAnalyses.ToList();
            pending = _entries.Values.Select(entry => entry.Pending).OfType<CancellationTokenSource>().ToList();
            foreach (var source in pending)
            {
                source.Cancel();
            }
        }

        if (active.Count != 0)
        {
            await Task.WhenAny(Task.WhenAll(active), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        // The bounded wait above may return while a caller is still unwinding; leave the
        // managed semaphore usable so that its finally block can safely release the slot.
        lock (_lifetimeLock)
        {
            _snapshots.Clear();
            _entries.Clear();
        }
    }
}
