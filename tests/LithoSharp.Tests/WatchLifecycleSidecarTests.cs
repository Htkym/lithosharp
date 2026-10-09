using System.Text.Json;
using System.Diagnostics;
using LithoSharp.Internal;

namespace LithoSharp.Tests;

public sealed class WatchLifecycleSidecarTests
{
    [Test]
    public async Task SeparateProcessesRespectExclusiveCaptureAndKeepWholeRecordsWithinByteBound()
    {
        const string rootVariable = "LITHOSHARP_OWNED_SIDECAR_WRITER_ROOT";
        const string slotVariable = "LITHOSHARP_OWNED_SIDECAR_WRITER_SLOT";
        if (Environment.GetEnvironmentVariable(rootVariable) is { } childRoot)
        {
            var slot = Guid.ParseExact(Environment.GetEnvironmentVariable(slotVariable)!, "N").ToString("N");
            var writer = new WatchLifecycleSidecar(Path.Combine(childRoot, "capture.jsonl"),
                DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), Guid.NewGuid());
            // The parent still holds a shared read handle. A Unix shared writer
            // would incorrectly succeed; the exclusive writer must drop it.
            writer.Write(WatchLifecycleEvent.HostBuildReturned);
            await File.WriteAllTextAsync(Path.Combine(childRoot, slot + ".ready"), "");
            await WaitForFileAsync(Path.Combine(childRoot, "go"));
            for (var i = 0; i < 2000; i++) writer.Write(WatchLifecycleEvent.HostBuildReturned, generation: i);
            return;
        }
        using var workspace = new TemporaryWorkspace();
        var file = Path.Combine(workspace.Root, "capture.jsonl");
        await File.WriteAllTextAsync(file, "");
        var children = new List<(Process Process, Task<string> Output, Task<string> Error)>();
        try
        {
            using (var sharedReader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var ready = new List<string>();
                for (var i = 0; i < 4; i++)
                {
                    var slot = Guid.NewGuid().ToString("N");
                    var start = new ProcessStartInfo("dotnet")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    start.ArgumentList.Add(typeof(WatchLifecycleSidecarTests).Assembly.Location);
                    start.ArgumentList.Add("--treenode-filter");
                    start.ArgumentList.Add("/*/*/WatchLifecycleSidecarTests/SeparateProcessesRespectExclusiveCaptureAndKeepWholeRecordsWithinByteBound");
                    start.Environment[rootVariable] = workspace.Root;
                    start.Environment[slotVariable] = slot;
                    var process = Process.Start(start) ?? throw new IOException("Cannot start owned sidecar writer.");
                    children.Add((process, process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync()));
                    ready.Add(Path.Combine(workspace.Root, slot + ".ready"));
                }
                await Task.WhenAll(ready.Select(WaitForFileAsync));
                await Assert.That(sharedReader.Length).IsEqualTo(0);
            }
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, "go"), "");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var child in children)
            {
                await child.Process.WaitForExitAsync(timeout.Token);
                if (child.Process.ExitCode != 0) throw new InvalidOperationException(await child.Output + await child.Error);
            }
            await Assert.That(new FileInfo(file).Length).IsGreaterThan(0);
            await Assert.That(new FileInfo(file).Length).IsLessThanOrEqualTo(WatchLifecycleSidecar.MaximumBytes);
            var rows = (await File.ReadAllLinesAsync(file)).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            await Assert.That(rows.All(row => row.GetProperty("event").GetString() == "HostBuildReturned")).IsTrue();
            await Assert.That(rows.Any(row => row.GetProperty("droppedBefore").GetInt32() > 0)).IsTrue();
        }
        finally
        {
            foreach (var child in children)
            {
                if (!child.Process.HasExited) { child.Process.Kill(entireProcessTree: true); await child.Process.WaitForExitAsync(); }
                await child.Output; await child.Error;
                child.Process.Dispose();
            }
        }
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(path)) await Task.Delay(25, timeout.Token);
    }

    [Test]
    public async Task InvalidOrMissingCaptureNeverCreatesAFile()
    {
        using var workspace = new TemporaryWorkspace();
        var file = Path.Combine(workspace.Root, "capture.jsonl");
        var host = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        new WatchLifecycleSidecar(file, now + 301000, host).Write(WatchLifecycleEvent.WorkerStarted);
        new WatchLifecycleSidecar(file, now - 1, host).Write(WatchLifecycleEvent.WorkerStarted);
        new WatchLifecycleSidecar("relative.jsonl", now + 60000, host).Write(WatchLifecycleEvent.WorkerStarted);
        new WatchLifecycleSidecar(file, now + 60000, host).Write(WatchLifecycleEvent.WorkerStarted);
        await Assert.That(File.Exists(file)).IsFalse();
    }

    [Test]
    public async Task LockedSinkFailsOpenAndReportsDroppedRecordsOnRecovery()
    {
        using var workspace = new TemporaryWorkspace();
        var file = Path.Combine(workspace.Root, "capture.jsonl");
        await File.WriteAllTextAsync(file, "");
        var sidecar = new WatchLifecycleSidecar(file, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), Guid.NewGuid());
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            sidecar.Write(WatchLifecycleEvent.WorkerStarted);
        sidecar.Write(WatchLifecycleEvent.WorkerStopRequested, WatchLifecycleReason.Dispose);
        using var record = JsonDocument.Parse(await File.ReadAllTextAsync(file));
        await Assert.That(record.RootElement.GetProperty("droppedBefore").GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task MultipleWritersKeepWholeRecordsWithinOneCaptureByteBound()
    {
        using var workspace = new TemporaryWorkspace();
        var file = Path.Combine(workspace.Root, "capture.jsonl");
        await File.WriteAllTextAsync(file, "");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        var writers = Enumerable.Range(0, 4).Select(_ => new WatchLifecycleSidecar(file, deadline, Guid.NewGuid())).ToArray();
        await Task.WhenAll(writers.Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++) writer.Write(WatchLifecycleEvent.HostBuildReturned, generation: i);
        })));
        var bytes = new FileInfo(file).Length;
        await Assert.That(bytes).IsGreaterThan(0);
        await Assert.That(bytes).IsLessThanOrEqualTo(WatchLifecycleSidecar.MaximumBytes);
        foreach (var line in await File.ReadAllLinesAsync(file))
        {
            using var record = JsonDocument.Parse(line);
            await Assert.That(record.RootElement.GetProperty("event").GetString()).IsEqualTo("HostBuildReturned");
        }
        writers[0].Write(WatchLifecycleEvent.HostBuildReturned, generation: 1999);
        await Assert.That(new FileInfo(file).Length).IsLessThanOrEqualTo(WatchLifecycleSidecar.MaximumBytes);
    }

    [Test]
    public async Task CaptureStopsAtDeadlineWithoutExtendingTheInterval()
    {
        using var workspace = new TemporaryWorkspace();
        var file = Path.Combine(workspace.Root, "capture.jsonl");
        await File.WriteAllTextAsync(file, "");
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(500).ToUnixTimeMilliseconds();
        var writer = new WatchLifecycleSidecar(file, deadline, Guid.NewGuid());
        writer.Write(WatchLifecycleEvent.WorkerStarted);
        var before = new FileInfo(file).Length;
        await Task.Delay(550);
        writer.Write(WatchLifecycleEvent.WorkerStopRequested);
        await Assert.That(before).IsGreaterThan(0);
        await Assert.That(new FileInfo(file).Length).IsEqualTo(before);
    }

    [Test]
    public async Task SchemaContainsOnlyOpaqueIdentifiersAndKnownScalars()
    {
        using var workspace = new TemporaryWorkspace();
        var file = Path.Combine(workspace.Root, "PRIVATE_PATH_SENTINEL.jsonl");
        await File.WriteAllTextAsync(file, "");
        var host = Guid.NewGuid(); var worker = Guid.NewGuid();
        var sidecar = new WatchLifecycleSidecar(file, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), host);
        sidecar.Write(WatchLifecycleEvent.WorkerExited, WatchLifecycleReason.ProtocolOrIoFailure, 3, 0, workerId: worker, exitCode: 1);
        var text = await File.ReadAllTextAsync(file);
        using var record = JsonDocument.Parse(text);
        var row = record.RootElement;
        await Assert.That(string.Join(",", row.EnumerateObject().Select(item => item.Name))).IsEqualTo(
            "schemaVersion,utcUnixMilliseconds,writerId,hostId,workerId,event,reason,generation,restartReasons,exitCode,droppedBefore");
        await Assert.That(row.GetProperty("hostId").GetString()).IsEqualTo(host.ToString("N"));
        await Assert.That(row.GetProperty("workerId").GetString()).IsEqualTo(worker.ToString("N"));
        await Assert.That(text).DoesNotContain("PRIVATE_PATH_SENTINEL");
    }
}
