using System.Diagnostics;
using System.Text.Json;
using LithoSharp.Internal;

namespace LithoSharp.Tool;

/// <summary>Keeps trusted MDX loaders and their Node worker alive between content edits.</summary>
internal sealed class WatchHostSession(bool machineOutput = false, WatchLifecycleSidecar? lifecycle = null) : IAsyncDisposable
{
    private Process? process;
    private Task? stdout, stderr;
    internal Guid HostId { get; private set; }
    private long generation;
    private WatchLifecycleReason nextStartReason = WatchLifecycleReason.InitialBuild;
    private readonly string responsePath = Path.Combine(Path.GetTempPath(), "lithosharp-watch-" + Guid.NewGuid().ToString("N") + ".json");
    internal async Task<HostResponse> BuildAsync(string assembly, string project, CommandOptions options, CancellationToken cancellationToken, long buildGeneration = 0)
    {
        generation = buildGeneration;
        File.Delete(responsePath);
        if (process is null || process.HasExited)
        {
            var reason = process is null ? nextStartReason : WatchLifecycleReason.HostAlreadyExited;
            await StopAsync(WatchLifecycleReason.HostAlreadyExited).ConfigureAwait(false);
            HostId = Guid.NewGuid();
            var start = ProjectCompiler.SelfStartInfo();
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            foreach (var argument in new[] { "__host", "--assembly", assembly, "--project", Path.GetDirectoryName(project)!, "--command", "build", "--watch", "--response", responsePath }) start.ArgumentList.Add(argument);
            if (options.Value("output") is { } output) { start.ArgumentList.Add("--output"); start.ArgumentList.Add(Path.GetFullPath(output)); }
            if (options.Has("clean")) start.ArgumentList.Add("--clean");
            start.Environment[WatchLifecycleSidecar.HostVariable] = HostId.ToString("N");
            process = Process.Start(start) ?? throw new IOException("Cannot start the site watch host.");
            lifecycle?.Write(WatchLifecycleEvent.HostStarted, reason, generation, hostId: HostId);
            // In machine mode stdout must stay pure JSON Lines, so forward child output to stderr.
            stdout = CopyAsync(process.StandardOutput, machineOutput ? Console.Error : Console.Out); stderr = CopyAsync(process.StandardError, Console.Error);
        }
        else { await process.StandardInput.WriteLineAsync("build".AsMemory(), cancellationToken).ConfigureAwait(false); await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false); }
        lifecycle?.Write(WatchLifecycleEvent.HostBuildRequested, generation: generation, hostId: HostId);
        try
        {
            while (!File.Exists(responsePath))
            {
                if (process.HasExited) throw new IOException("The site watch host stopped without a build response.");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            var response = JsonSerializer.Deserialize<HostResponse>(await File.ReadAllTextAsync(responsePath, cancellationToken).ConfigureAwait(false), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new IOException("The watch host returned an empty response.");
            lifecycle?.Write(WatchLifecycleEvent.HostBuildReturned, generation: generation, hostId: HostId, exitCode: response.ExitCode);
            return response;
        }
        catch { await StopAsync(cancellationToken.IsCancellationRequested ? WatchLifecycleReason.Cancellation : WatchLifecycleReason.BuildFailure).ConfigureAwait(false); throw; }
    }
    private static async Task CopyAsync(StreamReader reader, TextWriter writer)
    {
        var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0) await writer.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
    }
    internal async Task StopAsync(WatchLifecycleReason reason = WatchLifecycleReason.Dispose)
    {
        if (process is null) return;
        nextStartReason = reason;
        lifecycle?.Write(WatchLifecycleEvent.HostStopRequested, reason, generation, hostId: HostId);
        var exitReason = WatchLifecycleReason.AlreadyExited;
        try
        {
            if (!process.HasExited)
            {
                exitReason = WatchLifecycleReason.GracefulClose;
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { exitReason = WatchLifecycleReason.ForcedAfterTimeout; if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            }
            await Task.WhenAll(stdout ?? Task.CompletedTask, stderr ?? Task.CompletedTask).ConfigureAwait(false);
            lifecycle?.Write(WatchLifecycleEvent.HostExited, exitReason, generation, hostId: HostId, exitCode: process.ExitCode);
        }
        finally { process.Dispose(); process = null; File.Delete(responsePath); }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
