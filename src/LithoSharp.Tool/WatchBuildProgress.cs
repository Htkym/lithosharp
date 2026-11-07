namespace LithoSharp.Tool;

internal sealed record WatchGeneration(string OwnerId, string HostId, string? PreviousHostId,
    long InputRevision, int RestartReasons);
internal sealed record WatchBarrier(string SchemaVersion, bool Success, long Generation,
    WatchGeneration Watch, string Scope = "admitted-inputs-only");
internal readonly record struct WatchBuildBatch(long InputRevision, int RestartReasons);

// This owner witness is reliable in-process state, independent of the best-effort sidecar.
// Idle means admitted inputs completed; it cannot prove future native events will not arrive.
internal sealed class WatchBuildProgress
{
    private readonly object gate = new();
    private readonly string ownerId = Guid.NewGuid().ToString("N");
    private long admitted, completed;
    private int restartReasons;
    private bool building = true;
    private WatchBarrier? latest;
    private TaskCompletionSource pulse = NewPulse();
    private static TaskCompletionSource NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Invalidate(int reasons)
    {
        lock (gate)
        {
            admitted = checked(admitted + 1);
            restartReasons |= reasons;
        }
    }

    internal WatchBuildBatch BeginBuild()
    {
        lock (gate)
        {
            building = true;
            var batch = new WatchBuildBatch(admitted, restartReasons);
            restartReasons = 0;
            return batch;
        }
    }

    internal void RememberRestart(int reasons)
    {
        lock (gate)
            restartReasons |= reasons;
    }

    internal WatchGeneration Witness(WatchBuildBatch batch, Guid host, Guid? previousHost) =>
        new(ownerId, host.ToString("N"), previousHost?.ToString("N"), batch.InputRevision, batch.RestartReasons);

    internal void Complete(WatchBuildBatch batch, long generation, WatchGeneration watch, bool success)
    {
        TaskCompletionSource ready;
        lock (gate)
        {
            completed = batch.InputRevision;
            building = false;
            latest = new("1.0", success, generation, watch);
            ready = pulse;
            pulse = NewPulse();
        }
        ready.TrySetResult();
    }

    internal async Task<WatchBarrier> BarrierAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task wait;
            lock (gate)
            {
                if (!building && admitted == completed && latest is not null)
                    return latest;
                wait = pulse.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }
}
