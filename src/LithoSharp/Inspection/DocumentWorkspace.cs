using System.Collections.Concurrent;

namespace LithoSharp.Inspection;

/// <summary>文書検査の利用境界と寿命を表します。</summary>
/// <remarks>T02の静的InspectをSiteGeneratorやCLIの起動から分離し、workspace単位でsnapshotの所有者を固定します。返したDocumentInfoは不変snapshotであり、再検査や破棄で書き換わりません。破棄済みのsnapshot参照は有効なままです。workerやhandleや一時領域を作らず、所有するmemory cacheのみ破棄します。1件の失敗は他件の保存内容を壊しません。
/// CPU解析は寿命lockの外で実行し、lockは状態遷移とsnapshot交換に限定します。版数付き検査では予約時と完了時の両方で文書版数とproject generationを照合し、遅い旧要求が新しい結果を上書きしません。</remarks>
public sealed class DocumentWorkspace : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DocumentInfo> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DocumentEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _analysisSlots = new(2, 2);
    private readonly object _lifetimeLock = new();
    private bool _disposed;

    /// <summary>workspace識別子を取得します。</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    private sealed class DocumentEntry
    {
        public long Version = -1;
        public long Generation = -1;
        public long Epoch;
        public string? LastText;
        public CancellationTokenSource? Pending;
        public Task? Active;
    }

    /// <summary>文書を検査し、workspace所有のsnapshotとして保存します。</summary>
    /// <param name="sourcePath">文書のsource path。位置情報の識別に使います。</param>
    /// <param name="text">文書全体のテキスト。front matterを含む形式です。</param>
    /// <param name="options">routeやversionなどの追加情報。ない場合はnullです。</param>
    /// <param name="cancellationToken">取消token。取消時は保存しません。</param>
    /// <returns>解析結果の不変snapshot。再検査で置き換わる前の参照は有効なままです。</returns>
    /// <remarks>検査は公開出力を更新しません。取消や1件の失敗は別件の保存内容を壊しません。</remarks>
    public Task<DocumentInfo> InspectAsync(string sourcePath, string text, DocumentInspectionOptions? options = null, CancellationToken cancellationToken = default) =>
        InspectCoreAsync(sourcePath, text, null, null, options, cancellationToken);

    /// <summary>版数付きで文書を検査し、最新版の結果だけを保存します。</summary>
    /// <param name="sourcePath">文書のsource path。位置情報の識別に使います。</param>
    /// <param name="text">文書全体のテキスト。front matterを含む形式です。不変の入力snapshotとして扱います。</param>
    /// <param name="documentVersion">文書版数。新しい版数が古い結果を上書きします。</param>
    /// <param name="projectGeneration">project generation。新しい世代が古い世代の結果を上書きします。</param>
    /// <param name="options">routeやproject contextなどの追加情報。ない場合はnullです。</param>
    /// <param name="cancellationToken">取消token。取消時は保存しません。</param>
    /// <returns>解析結果の不変snapshot。追い越された旧要求は保存せず取り消します。</returns>
    /// <remarks>文書あたり最新pending要求だけを残す有界queueを使い、.NET解析の同時実行は2件以内に抑えます。検査は公開出力を更新しません。同じキーで旧APIと混用しないでください。</remarks>
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
            entry = _entries.GetOrAdd(key, static _ => new DocumentEntry());
            if (IsSameRevision(entry, documentVersion, projectGeneration, text)
                && _snapshots.TryGetValue(key, out var current))
            {
                return current;
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
                    entry.Active = analysis;
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
                        if (ReferenceEquals(entry.Active, analysis))
                        {
                            entry.Active = null;
                        }
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                lock (_lifetimeLock)
                {
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

    private static bool IsSameRevision(DocumentEntry entry, long? version, long? generation, string text) =>
        entry.LastText is not null
        && string.Equals(entry.LastText, text, StringComparison.Ordinal)
        && (version is null || (entry.Version == version && entry.Generation == (generation ?? long.MaxValue)));

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

    /// <summary>保存済みsnapshotの並行readを試みます。</summary>
    /// <param name="documentId">文書識別子。未設定時はsource pathです。</param>
    /// <param name="documentInfo">保存済みの不変snapshot。ない場合はnullです。</param>
    /// <returns>保存済みの場合にtrueを返します。破棄後はfalseを返します。</returns>
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

    /// <summary>project context付きの保存済みsnapshotの並行readを試みます。</summary>
    /// <param name="projectId">project識別子。</param>
    /// <param name="documentId">文書識別子。未設定時はsource pathです。</param>
    /// <param name="documentInfo">保存済みの不変snapshot。ない場合はnullです。</param>
    /// <returns>保存済みの場合にtrueを返します。破棄後はfalseを返します。</returns>
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

    /// <summary>保存済みsnapshotを破棄します。</summary>
    /// <param name="documentId">文書識別子。未設定時はsource pathです。</param>
    /// <returns>保存済みを消した場合にtrueを返します。workspace破棄後はfalseを返します。</returns>
    /// <remarks>pending要求を取り消し世代を失効させるため、遅延した旧要求の完了で削除文書が復活しません。renameは破棄と再検査で行います。</remarks>
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

    /// <summary>project context付きの保存済みsnapshotを破棄します。</summary>
    /// <param name="projectId">project識別子。</param>
    /// <param name="documentId">文書識別子。未設定時はsource pathです。</param>
    /// <returns>保存済みを消した場合にtrueを返します。workspace破棄後はfalseを返します。</returns>
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
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Epoch++;
                entry.Pending?.Cancel();
                entry.Pending = null;
                entry.Version = -1;
                entry.Generation = -1;
                entry.LastText = null;
            }

            return _snapshots.TryRemove(key, out _);
        }
    }

    /// <summary>所有するsnapshot参照を破棄します。</summary>
    /// <remarks>呼び出し側が保持するsnapshotは有効なままです。pending要求を取り消し、有界timeoutで終了を待ってから破棄します。</remarks>
    public async ValueTask DisposeAsync()
    {
        List<Task> active;
        List<CancellationTokenSource> pending;
        lock (_lifetimeLock)
        {
            _disposed = true;
            active = _entries.Values.Select(entry => entry.Active).OfType<Task>().ToList();
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
