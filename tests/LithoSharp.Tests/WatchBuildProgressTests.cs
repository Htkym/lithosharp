using LithoSharp.Tool;

namespace LithoSharp.Tests;

public sealed class WatchBuildProgressTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task BarrierWaitsForInitialAndAllAdmittedBuildsThenFreezesItsHost()
    {
        var owner = new WatchBuildProgress();
        var host = Guid.NewGuid();
        var pending = owner.BarrierAsync(default);
        await Assert.That(pending.IsCompleted).IsFalse();
        owner.Invalidate(144);
        owner.Complete(new(0, 0), 1, owner.Witness(new(0, 0), host, null), true);
        await Assert.That(pending.IsCompleted).IsFalse();
        var first = owner.BeginBuild();
        owner.Invalidate(0); // An edit admitted during recovery requires its own completed generation.
        var recovered = Guid.NewGuid();
        owner.Complete(first, 2, owner.Witness(first, recovered, host), true);
        await Assert.That(pending.IsCompleted).IsFalse();
        var next = owner.BeginBuild();
        owner.Complete(next, 3, owner.Witness(next, recovered, recovered), true);
        var barrier = await pending.WaitAsync(Timeout);
        await Assert.That(first.RestartReasons).IsEqualTo(144);
        await Assert.That(barrier.Generation).IsEqualTo(3L);
        await Assert.That(barrier.Watch.InputRevision).IsEqualTo(2L);
        await Assert.That(barrier.Watch.HostId).IsEqualTo(recovered.ToString("N"));
        owner.Invalidate(144); // A later event cannot mutate the already acknowledged edit baseline.
        await Assert.That(barrier.Generation).IsEqualTo(3L);
        using var cancel = new CancellationTokenSource();
        var later = owner.BarrierAsync(cancel.Token);
        cancel.Cancel();
        var rejected = false;
        try
        {
            await later;
        }
        catch (OperationCanceledException)
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task InputFenceWaitsForPriorInvalidationAndPropagatesConsumerFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var root = Path.GetFullPath(".");
        var errors = 0;
        await using (var queue = new WatchEventQueue(root, (_, _, error) =>
        {
            if (error is not null)
                Interlocked.Increment(ref errors);
            entered.TrySetResult();
            release.Wait();
        }, () => { }))
        {
            Task fence;
            try
            {
                queue.Enqueue(root, WatcherChangeTypes.Changed);
                await entered.Task.WaitAsync(Timeout);
                for (var i = 0; i < WatchEventQueue.Capacity; i++)
                    queue.Enqueue(root, WatcherChangeTypes.Changed);
                queue.Enqueue(root, WatcherChangeTypes.All, error: new InternalBufferOverflowException("owned native loss"));
                fence = queue.FlushAsync(default);
                await Assert.That(fence.IsCompleted).IsFalse();
            }
            finally
            {
                release.Set();
            }
            await fence.WaitAsync(Timeout);
            await Assert.That(errors).IsEqualTo(2); // Native error and admitted-queue loss precede the fence.
        }
        var failed = new WatchEventQueue(root, (_, _, _) => throw new IOException("owned failure"), () => { });
        try
        {
            failed.Enqueue(root, WatcherChangeTypes.Changed);
            var rejected = false;
            try
            {
                await failed.FlushAsync(default).WaitAsync(Timeout);
            }
            catch (IOException)
            {
                rejected = true;
            }
            await Assert.That(rejected).IsTrue();
        }
        finally
        {
            try
            {
                await failed.DisposeAsync();
            }
            catch (IOException) { }
        }
    }
}
