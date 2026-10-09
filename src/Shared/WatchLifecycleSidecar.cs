using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LithoSharp.Internal;

// Compiled privately into Tool and Mdx. Only an explicitly configured, already
// created file is touched; diagnostics never create files or write console data.
internal sealed class WatchLifecycleSidecar
{
    internal const string FileVariable = "LITHOSHARP_WATCH_DIAGNOSTICS_FILE";
    internal const string UntilVariable = "LITHOSHARP_WATCH_DIAGNOSTICS_UNTIL";
    internal const string HostVariable = "LITHOSHARP_WATCH_DIAGNOSTICS_HOST";
    internal const int MaximumBytes = 131072;
    private readonly string? path;
    private readonly long until;
    private readonly object gate = new();
    private readonly Guid writer = Guid.NewGuid();
    private int dropped;
    internal Guid HostId { get; }

    internal WatchLifecycleSidecar(string? file, long deadline, Guid hostId)
    {
        HostId = hostId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (file is not null && Path.IsPathFullyQualified(file) && deadline > now && deadline <= now + 300000)
        { path = file; until = deadline; }
    }

    internal static WatchLifecycleSidecar FromEnvironment()
    {
        long.TryParse(Environment.GetEnvironmentVariable(UntilVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var deadline);
        Guid.TryParseExact(Environment.GetEnvironmentVariable(HostVariable), "N", out var host);
        return new(Environment.GetEnvironmentVariable(FileVariable), deadline, host == Guid.Empty ? Guid.NewGuid() : host);
    }

    internal void Write(WatchLifecycleEvent kind, WatchLifecycleReason reason = WatchLifecycleReason.None,
        long generation = 0, int restartReasons = 0, Guid? hostId = null, Guid? workerId = null, int? exitCode = null)
    {
        if (path is null || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= until) return;
        lock (gate)
        {
            try
            {
                // Exclusive writer sharing avoids interleaved JSON and enforces
                // the single capture's byte bound across parent/host processes.
                // Contention is dropped without waiting or affecting the build.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= until) return;
                var record = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, utcUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    writerId = writer.ToString("N"), hostId = (hostId ?? HostId).ToString("N"),
                    workerId = workerId?.ToString("N"), @event = kind.ToString(), reason = reason.ToString(),
                    generation, restartReasons, exitCode, droppedBefore = dropped,
                });
                var bytes = Encoding.UTF8.GetBytes(record + "\n");
                if (stream.Length > MaximumBytes - bytes.Length) return;
                stream.Seek(0, SeekOrigin.End);
                stream.Write(bytes);
                dropped = 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                if (dropped < int.MaxValue) dropped++;
            }
        }
    }
}

internal enum WatchLifecycleEvent
{
    RestartRequested, RestartConsumed, HostStarted, HostBuildRequested,
    HostBuildReturned, HostStopRequested, HostExited, WorkerStarted, WorkerStopRequested, WorkerExited,
}

internal enum WatchLifecycleReason
{
    None, InitialBuild, FirstRequest, Recompile, BuildFailure, HostAlreadyExited,
    Cancellation, Timeout, ProtocolOrIoFailure, Dispose, GracefulClose, ForcedAfterTimeout, AlreadyExited,
}

[Flags]
internal enum WatchRestartReason
{
    Created = 1, Deleted = 2, Changed = 4, Renamed = 8, WatcherError = 16,
    CSharp = 32, Project = 64, Other = 128, RebuildFailure = 256,
}
