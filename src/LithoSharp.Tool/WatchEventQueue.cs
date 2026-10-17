using System.Threading.Channels;

namespace LithoSharp.Tool;

// Native watch callbacks must not wait for directory probes, diagnostic files
// or console writes before the next native read can be armed.
internal sealed class WatchEventQueue : IAsyncDisposable
{
    internal const int Capacity = 256;
    private readonly Channel<WatchEvent> events = Channel.CreateBounded<WatchEvent>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
        // TryWrite must report a full queue; raw input loss cannot be DropWrite.
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly string root;
    private readonly Action<string, WatcherChangeTypes, Exception?> changed;
    private readonly Task consumer;
    private int lost;
    private Exception? lostError;
    private int stopped;

    internal WatchEventQueue(string root, Action<string, WatcherChangeTypes, Exception?> changed, Action failed)
    {
        this.root = root;
        this.changed = changed;
        consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in events.Reader.ReadAllAsync())
                {
                    ReportLoss();
                    if (item.Barrier is not null) { item.Barrier.TrySetResult(); continue; }
                    if (item.Path is null) continue; // Loss wake, not a path event.
                    Dispatch(item.Path, item.Kind, item.Error);
                    if (item.OldPath is not null)
                        Dispatch(item.OldPath, WatcherChangeTypes.Renamed, null);
                }
                ReportLoss();
            }
            catch
            {
                // A broken consumer must stop serve, not leave a live watcher
                // accepting inputs which can no longer invalidate the build.
                failed();
                throw;
            }
        });
    }

    internal Task Completion => consumer;

    // Unlike native callbacks, an explicit owner barrier may wait for queue capacity.
    internal async Task FlushAsync(CancellationToken token)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var write = events.Writer.WriteAsync(new(null, null, default, null, barrier), admission.Token).AsTask();
            await (await Task.WhenAny(write, consumer).WaitAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
            await write.ConfigureAwait(false);
            var finished = await Task.WhenAny(barrier.Task, consumer).WaitAsync(token).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
            if (!barrier.Task.IsCompleted) throw new IOException("Watch input consumer ended before the barrier.");
        }
        finally { admission.Cancel(); }
    }

    internal void Enqueue(string path, WatcherChangeTypes kind, string? oldPath = null, Exception? error = null)
    {
        if (Volatile.Read(ref stopped) != 0) return;
        if (events.Writer.TryWrite(new(path, oldPath, kind, error))) return;
        if (error is not null) Interlocked.CompareExchange(ref lostError, error, null);
        Interlocked.Exchange(ref lost, 1);
        // The reader may have drained the queue between rejection and flagging
        // loss. Reinsert a wake if there is now room; otherwise queued work wakes it.
        events.Writer.TryWrite(default);
    }

    private void ReportLoss()
    {
        if (Interlocked.Exchange(ref lost, 0) == 0) return;
        var error = Interlocked.Exchange(ref lostError, null);
        if (error is not null) Dispatch(root, WatcherChangeTypes.All, error);
        // Distinct from native InternalBufferOverflowException in the sidecar.
        Dispatch(root, WatcherChangeTypes.All, new IOException("File watching event queue lost changes."));
    }

    private void Dispatch(string path, WatcherChangeTypes kind, Exception? error)
    {
        // Invalidation and the rebuild wake precede diagnostic console I/O.
        changed(path, kind, error);
        if (error is not null)
            Console.Error.WriteLine($"File watching lost changes: {error.Message} Rebuilding the site.");
    }

    internal void Stop()
    {
        // Reject late producers, then drain admitted records. The serve owner
        // cancels rebuilds/closes its wake channel before awaiting this lifetime.
        if (Interlocked.Exchange(ref stopped, 1) == 0) events.Writer.TryComplete();
    }

    internal ValueTask CompleteAsync()
    {
        Stop();
        return new(consumer);
    }

    public ValueTask DisposeAsync() => CompleteAsync();

    private readonly record struct WatchEvent(string? Path, string? OldPath, WatcherChangeTypes Kind, Exception? Error,
        TaskCompletionSource? Barrier = null);
}
