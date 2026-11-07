using System.Collections.Concurrent;
using LithoSharp.Tool;

namespace LithoSharp.Tests;

[NotInParallel]
public sealed class WatchEventQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private sealed record Change(string Path, WatcherChangeTypes Kind, Exception? Error);

    private static void WaitForRelease(ManualResetEventSlim release)
    {
        if (!release.Wait(Timeout))
            throw new TimeoutException("Owned test consumer gate was not released.");
    }

    [Test]
    public async Task SlowConsumerKeepsAcceptedSourceProjectChangesInOrderAndRenameTogetherInOneRemainingSlot()
    {
        using var workspace = new TemporaryWorkspace();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseRename = new ManualResetEventSlim();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renameEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new ConcurrentQueue<Change>();
        var first = Path.Combine(workspace.Root, "First.cs");
        var renamed = Path.Combine(workspace.Root, "Renamed.cs");
        var old = Path.Combine(workspace.Root, "Old.cs");
        var after = Path.Combine(workspace.Root, "Site.csproj");
        var content = Path.Combine(workspace.Root, "intro.md");
        var failed = 0;
        var queue = new WatchEventQueue(workspace.Root, (path, kind, error) =>
        {
            changes.Enqueue(new(path, kind, error));
            if (path == first)
            {
                firstEntered.TrySetResult();
                WaitForRelease(releaseFirst);
            }
            if (path == renamed)
            {
                renameEntered.TrySetResult();
                WaitForRelease(releaseRename);
            }
        }, () => Interlocked.Increment(ref failed));
        Task? producer = null;
        try
        {
            producer = Task.Run(() => queue.Enqueue(first, WatcherChangeTypes.Changed));
            await firstEntered.Task.WaitAsync(Timeout);
            await producer.WaitAsync(Timeout); // The producer returns while its consumer is still held.
            var pending = Enumerable.Range(0, WatchEventQueue.Capacity - 1)
                .Select(i => Path.Combine(workspace.Root, $"edit-{i:D3}." + (i % 2 == 0 ? "cs" : "csproj"))).ToArray();
            producer = Task.Run(() =>
            {
                foreach (var path in pending)
                    queue.Enqueue(path, WatcherChangeTypes.Changed);
                queue.Enqueue(renamed, WatcherChangeTypes.Renamed, old);
            });
            await producer.WaitAsync(Timeout);
            releaseFirst.Set();
            await renameEntered.Task.WaitAsync(Timeout);
            // A real new input arrives while the new half of a rename is being processed.
            // It cannot appear between the new and old invalidations.
            producer = Task.Run(() =>
            {
                queue.Enqueue(after, WatcherChangeTypes.Changed);
                queue.Enqueue(content, WatcherChangeTypes.Changed);
            });
            await producer.WaitAsync(Timeout);
            var completing = queue.CompleteAsync().AsTask();
            queue.Enqueue(Path.Combine(workspace.Root, "LateFactory.cs"), WatcherChangeTypes.Changed);
            releaseRename.Set();
            await completing.WaitAsync(Timeout);
            var expected = new[] { (first, WatcherChangeTypes.Changed) }
                .Concat(pending.Select(path => (path, WatcherChangeTypes.Changed)))
                .Concat(new[] { (renamed, WatcherChangeTypes.Renamed), (old, WatcherChangeTypes.Renamed),
                    (after, WatcherChangeTypes.Changed), (content, WatcherChangeTypes.Changed) });
            await Assert.That(changes.Select(change => (change.Path, change.Kind)).SequenceEqual(expected)).IsTrue();
            await Assert.That(changes.All(change => change.Error is null)).IsTrue();
            await Assert.That(failed).IsEqualTo(0);
        }
        finally
        {
            releaseFirst.Set();
            releaseRename.Set();
            try
            {
                if (producer is not null)
                    await producer.WaitAsync(Timeout);
            }
            finally
            {
                await queue.DisposeAsync().AsTask().WaitAsync(Timeout);
            }
        }
    }

    [Test]
    public async Task FullQueueDoesNotWaitForTheConsumerAndReportsLostProjectChangesWithANativeError()
    {
        using var workspace = new TemporaryWorkspace();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new ConcurrentQueue<Change>();
        var first = Path.Combine(workspace.Root, "First.cs");
        var nativeError = new InternalBufferOverflowException("Native watcher buffer overflow.");
        var laterNativeError = new IOException("A later native error must not erase the retained one.");
        var failed = 0;
        var queue = new WatchEventQueue(workspace.Root, (path, kind, error) =>
        {
            changes.Enqueue(new(path, kind, error));
            if (path == first)
            {
                entered.TrySetResult();
                WaitForRelease(release);
            }
        }, () => Interlocked.Increment(ref failed));
        Task? producer = null;
        try
        {
            producer = Task.Run(() => queue.Enqueue(first, WatcherChangeTypes.Changed));
            await entered.Task.WaitAsync(Timeout);
            await producer.WaitAsync(Timeout);
            var accepted = Enumerable.Range(0, WatchEventQueue.Capacity)
                .Select(i => Path.Combine(workspace.Root, $"pending-{i:D3}.cs")).ToArray();
            producer = Task.Run(() =>
            {
                foreach (var path in accepted)
                    queue.Enqueue(path, WatcherChangeTypes.Changed);
                queue.Enqueue(Path.Combine(workspace.Root, "Site.csproj"), WatcherChangeTypes.Changed);
                queue.Enqueue(workspace.Root, WatcherChangeTypes.All, error: nativeError);
                queue.Enqueue(workspace.Root, WatcherChangeTypes.All, error: laterNativeError);
            });
            await producer.WaitAsync(Timeout); // No draining/release was needed to finish all native-style enqueues.
            await Assert.That(changes.Count).IsEqualTo(1);
            release.Set();
            await queue.CompleteAsync().AsTask().WaitAsync(Timeout);
            var observed = changes.ToArray();
            await Assert.That(observed.Where(change => change.Error is null).Select(change => change.Path)
                .SequenceEqual(new[] { first }.Concat(accepted))).IsTrue();
            await Assert.That(observed.Any(change => change.Path == workspace.Root && change.Kind == WatcherChangeTypes.All
                && ReferenceEquals(change.Error, nativeError))).IsTrue();
            await Assert.That(observed.Any(change => change.Path == workspace.Root && change.Kind == WatcherChangeTypes.All
                && change.Error is IOException { Message: "File watching event queue lost changes." })).IsTrue();
            await Assert.That(failed).IsEqualTo(0);
        }
        finally
        {
            release.Set();
            try
            {
                if (producer is not null)
                    await producer.WaitAsync(Timeout);
            }
            finally
            {
                await queue.DisposeAsync().AsTask().WaitAsync(Timeout);
            }
        }
    }

    [Test]
    public async Task BrokenConsumerSignalsFailureAndExposesItsTerminalFaultThroughCompletionAndDisposal()
    {
        using var workspace = new TemporaryWorkspace();
        var failure = new InvalidOperationException("Build invalidation consumer failed.");
        var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = 0;
        var delivered = 0;
        var queue = new WatchEventQueue(workspace.Root, (_, _, _) =>
        {
            Interlocked.Increment(ref delivered);
            throw failure;
        }, () =>
        {
            Interlocked.Increment(ref failed);
            signalled.TrySetResult();
        });
        try
        {
            queue.Enqueue(Path.Combine(workspace.Root, "Factory.cs"), WatcherChangeTypes.Changed);
            await signalled.Task.WaitAsync(Timeout);
            Exception? observed = null;
            try
            {
                await queue.Completion.WaitAsync(Timeout);
            }
            catch (Exception error)
            {
                observed = error;
            }
            await Assert.That(ReferenceEquals(observed, failure)).IsTrue();
            queue.Enqueue(Path.Combine(workspace.Root, "Site.csproj"), WatcherChangeTypes.Changed);
            await Assert.That(delivered).IsEqualTo(1);
            await Assert.That(failed).IsEqualTo(1);
            observed = null;
            try
            {
                await queue.CompleteAsync().AsTask().WaitAsync(Timeout);
            }
            catch (Exception error)
            {
                observed = error;
            }
            await Assert.That(ReferenceEquals(observed, failure)).IsTrue();
        }
        finally
        {
            try
            {
                await queue.DisposeAsync().AsTask().WaitAsync(Timeout);
            }
            catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
        }
    }
}
