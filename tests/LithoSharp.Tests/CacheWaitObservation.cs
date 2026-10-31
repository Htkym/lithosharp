using System.Diagnostics;
using System.Text.Json;

namespace LithoSharp.Tests;

// Opt-in observations for the two cache-wait fixtures only. No I/O in a renderer
// or timed wait: the record is written outside the workspace after its disposal.
internal sealed class CacheWaitObservation : IDisposable
{
    internal const string DirectoryVariable = "LITHOSHARP_CACHE_WAIT_OBSERVATIONS";
    private readonly TemporaryWorkspace _workspace = new();
    private readonly string _test;
    private readonly string _variant;
    private readonly bool _observeDisposal;
    private readonly string? _recordPath;
    private readonly List<object> _snapshots = [];
    private long _rendererEntered;
    private long _rendererExited;
    private object? _buildResult;
    private object? _disposal;

    internal CacheWaitObservation(string test, string variant, bool observeDisposal = false)
    {
        _test = test;
        _variant = variant;
        _observeDisposal = observeDisposal;
        try
        {
            var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (string.IsNullOrWhiteSpace(directory))
                return;
            if (!Path.IsPathFullyQualified(directory))
                throw new ArgumentException("Observation directory must be absolute.");
            directory = Path.GetFullPath(directory);
            var relative = Path.GetRelativePath(Root, directory);
            if (relative == "." || !Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Observation directory must be outside the test workspace.");
            Directory.CreateDirectory(directory);
            _recordPath = Path.Combine(directory, $"{test}-{variant}-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
            File.WriteAllText(_recordPath + ".started", JsonSerializer.Serialize(new
            {
                schema = "cache-wait-observation/1",
                status = "started",
                test,
                variant,
                workspace = Root,
                timestamp = Stopwatch.GetTimestamp(),
                frequency = Stopwatch.Frequency
            }));
        }
        catch
        {
            _workspace.Dispose();
            throw;
        }
    }

    internal string Root => _workspace.Root;
    internal void RendererEntered()
    {
        if (_recordPath is not null)
            Interlocked.CompareExchange(ref _rendererEntered, Stopwatch.GetTimestamp(), 0);
    }
    internal void RendererExited()
    {
        if (_recordPath is not null)
            Interlocked.CompareExchange(ref _rendererExited, Stopwatch.GetTimestamp(), 0);
    }

    internal void Snapshot(string phase, Task building, Task? clearing = null, Exception? failure = null,
        string? entrySignalStatus = null, string? clearSignalStatus = null)
    {
        if (_recordPath is null)
            return;
        _snapshots.Add(new
        {
            phase,
            timestamp = Stopwatch.GetTimestamp(),
            building = State(building),
            clearingCaptured = _observeDisposal || clearing is not null,
            clearing = _observeDisposal || clearing is not null ? State(clearing) : null,
            rendererEntered = Volatile.Read(ref _rendererEntered),
            rendererExited = Volatile.Read(ref _rendererExited),
            entrySignalStatus,
            clearSignalStatus,
            failure = failure?.ToString()
        });
    }

    internal void BuildResult(Task<SiteGenerationResult> building)
    {
        if (_recordPath is null || !building.IsCompletedSuccessfully)
            return;
        // Completed task and immutable report only; no wait, continuation or new timings.
        var report = building.Result.BuildReport;
        _buildResult = new
        {
            timestamp = Stopwatch.GetTimestamp(),
            report.TransactionOutcome,
            report.CacheHitCount,
            report.CacheMissCount,
            nodes = report.Nodes.Select(node => new { node.NodeId, node.CacheHit, node.CacheMissReason }).ToArray()
        };
    }

    private static object? State(Task? task) => task is null ? null : new
    {
        status = task.Status.ToString(),
        task.IsCompleted,
        task.IsCompletedSuccessfully,
        task.IsCanceled,
        task.IsFaulted,
        terminalException = task.IsFaulted ? task.Exception?.ToString() : null
    };

    public void Dispose()
    {
        var started = _recordPath is not null && _observeDisposal ? Stopwatch.GetTimestamp() : 0;
        try
        {
            _workspace.Dispose();
            if (started != 0)
                _disposal = new { started, finished = Stopwatch.GetTimestamp(), success = true, exception = (string?)null };
        }
        catch (Exception exception)
        {
            if (started != 0)
                _disposal = new { started, finished = Stopwatch.GetTimestamp(), success = false, exception = exception.ToString() };
            throw;
        }
        finally
        {
            if (_recordPath is not null)
                Flush();
        }
    }

    private void Flush()
    {
        try
        {
            File.WriteAllText(_recordPath!, JsonSerializer.Serialize(new
            {
                schema = "cache-wait-observation/1",
                status = "recorded",
                test = _test,
                variant = _variant,
                workspace = Root,
                frequency = Stopwatch.Frequency,
                timestamp = Stopwatch.GetTimestamp(),
                rendererExitCaptured = _observeDisposal,
                workspaceDisposalCaptured = _observeDisposal,
                buildResultObservationEnabled = !_observeDisposal,
                rendererEntered = Volatile.Read(ref _rendererEntered),
                rendererExited = Volatile.Read(ref _rendererExited),
                snapshots = _snapshots,
                buildResult = _buildResult,
                workspaceDisposal = _disposal
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Preserve the fixture's original failure. A missing final record makes
            // the observation incomplete; the separate runner must reject it.
            try
            {
                Console.Error.WriteLine($"Cache observation incomplete: {_recordPath}: {exception.Message}");
            }
            catch (IOException) { }
        }
    }
}
