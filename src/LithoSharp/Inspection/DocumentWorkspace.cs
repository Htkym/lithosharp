using System.Collections.Concurrent;

namespace LithoSharp.Inspection;

/// <summary>文書検査の利用境界と寿命を表します。</summary>
/// <remarks>T02の静的InspectをSiteGeneratorやCLIの起動から分離し、workspace単位でsnapshotの所有者を固定します。返したDocumentInfoは不変snapshotであり、再検査や破棄で書き換わりません。破棄済みのsnapshot参照は有効なままです。workerやhandleや一時領域を作らず、所有するmemory cacheのみ破棄します。1件の失敗は他件の保存内容を壊しません。</remarks>
public sealed class DocumentWorkspace : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, DocumentInfo> _snapshots = new(StringComparer.Ordinal);
    private readonly object _lifetimeLock = new();
    private bool _disposed;

    /// <summary>workspace識別子を取得します。</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>文書を検査し、workspace所有のsnapshotとして保存します。</summary>
    /// <param name="sourcePath">文書のsource path。位置情報の識別に使います。</param>
    /// <param name="text">文書全体のテキスト。front matterを含む形式です。</param>
    /// <param name="options">routeやversionなどの追加情報。ない場合はnullです。</param>
    /// <param name="cancellationToken">取消token。取消時は保存しません。</param>
    /// <returns>解析結果の不変snapshot。再検査で置き換わる前の参照は有効なままです。</returns>
    /// <remarks>検査は公開出力を更新しません。取消や1件の失敗は別件の保存内容を壊しません。</remarks>
    public Task<DocumentInfo> InspectAsync(string sourcePath, string text, DocumentInspectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (_lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var info = DocumentInspection.Inspect(sourcePath, text, options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _snapshots[info.DocumentId] = info;
            return Task.FromResult(info);
        }
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

    /// <summary>保存済みsnapshotを破棄します。</summary>
    /// <param name="documentId">文書識別子。未設定時はsource pathです。</param>
    /// <returns>保存済みを消した場合にtrueを返します。workspace破棄後はfalseを返します。</returns>
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
        return _snapshots.TryRemove(documentId, out _);
    }

    /// <summary>所有するsnapshot参照を破棄します。</summary>
    /// <remarks>呼び出し側が保持するsnapshotは有効なままです。</remarks>
    public ValueTask DisposeAsync()
    {
        lock (_lifetimeLock)
        {
            _disposed = true;
        }

        _snapshots.Clear();
        return ValueTask.CompletedTask;
    }
}
