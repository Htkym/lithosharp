using System.Diagnostics;
using System.Text.Json;

namespace LithoSharp.Tool;

/// <summary>Keeps trusted MDX loaders and their Node worker alive between content edits.</summary>
internal sealed class WatchHostSession : IAsyncDisposable
{
    private Process? process;
    private Task? stdout, stderr;
    private readonly string responsePath = Path.Combine(Path.GetTempPath(), "lithosharp-watch-" + Guid.NewGuid().ToString("N") + ".json");
    internal async Task<HostResponse> BuildAsync(string assembly, string project, CommandOptions options, CancellationToken cancellationToken)
    {
        File.Delete(responsePath);
        if (process is null || process.HasExited)
        {
            await StopAsync().ConfigureAwait(false);
            var start = ProjectCompiler.SelfStartInfo();
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            foreach (var argument in new[] { "__host", "--assembly", assembly, "--project", Path.GetDirectoryName(project)!, "--command", "build", "--watch", "--response", responsePath }) start.ArgumentList.Add(argument);
            if (options.Value("output") is { } output) { start.ArgumentList.Add("--output"); start.ArgumentList.Add(Path.GetFullPath(output)); }
            if (options.Has("clean")) start.ArgumentList.Add("--clean");
            process = Process.Start(start) ?? throw new IOException("Cannot start the site watch host.");
            stdout = CopyAsync(process.StandardOutput, Console.Out); stderr = CopyAsync(process.StandardError, Console.Error);
        }
        else { await process.StandardInput.WriteLineAsync("build".AsMemory(), cancellationToken).ConfigureAwait(false); await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false); }
        try
        {
            while (!File.Exists(responsePath))
            {
                if (process.HasExited) throw new IOException("The site watch host stopped without a build response.");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            return JsonSerializer.Deserialize<HostResponse>(await File.ReadAllTextAsync(responsePath, cancellationToken).ConfigureAwait(false), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new IOException("The watch host returned an empty response.");
        }
        catch { await StopAsync().ConfigureAwait(false); throw; }
    }
    private static async Task CopyAsync(StreamReader reader, TextWriter writer)
    {
        var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0) await writer.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
    }
    internal async Task StopAsync()
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            }
            await Task.WhenAll(stdout ?? Task.CompletedTask, stderr ?? Task.CompletedTask).ConfigureAwait(false);
        }
        finally { process.Dispose(); process = null; File.Delete(responsePath); }
    }
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
