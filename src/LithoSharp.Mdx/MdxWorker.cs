using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Diagnostics;

namespace LithoSharp.Mdx;

internal sealed class MdxWorker(MdxOptions options) : IAsyncDisposable
{
    private Process? process;
    private Task? stderrTask;
    private string stderr = string.Empty;
    private readonly char[] buffer = new char[4096];
    private int offset;
    private int buffered;
    private readonly SemaphoreSlim gate = new(1, 1);

    internal async Task<JsonElement> SendAsync(object request, string requestId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try
        {
            if (process is null)
            {
                var start = new ProcessStartInfo(options.NodeExecutable)
                {
                    WorkingDirectory = options.WorkerDirectory, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false, true),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };
                start.ArgumentList.Add(Path.Combine(options.WorkerDirectory, "worker.mjs"));
                start.Environment.Clear();
                foreach (var name in new[] { "PATH", "SystemRoot", "TEMP", "TMP" })
                    if (System.Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
                start.Environment["NODE_ENV"] = "production";
                foreach (var pair in options.Environment)
                {
                    if (pair.Key is "NODE_OPTIONS" or "NODE_PATH") throw new ArgumentException("Node loader overrides must not be passed as MDX environment data.");
                    start.Environment[pair.Key] = pair.Value;
                }
                process = Process.Start(start) ?? throw Failure("LSMDX002", "The Node worker could not be started.");
                stderrTask = DrainErrorsAsync(process.StandardError);
                var ready = await ReadMessageAsync(timeout.Token).ConfigureAwait(false);
                if (ready.GetProperty("protocol").GetInt32() != 1 || ready.GetProperty("type").GetString() != "ready"
                    || ready.GetProperty("node").GetString() != "24.13.0" || ready.GetProperty("mdx").GetString() != "3.1.1"
                    || ready.GetProperty("react").GetString() != "19.2.4" || ready.GetProperty("esbuild").GetString() != "0.25.12")
                    throw Failure("LSMDX003", "The worker protocol or toolchain does not match the supported locked versions.");
            }
            var json = JsonSerializer.Serialize(request, MdxJson.Options);
            if (Encoding.UTF8.GetByteCount(json) > options.MaximumMessageBytes) throw Failure("LSMDX003", "MDX request exceeds the configured message size.");
            await process.StandardInput.WriteLineAsync(json.AsMemory(), timeout.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            var response = await ReadMessageAsync(timeout.Token).ConfigureAwait(false);
            if (response.GetProperty("protocol").GetInt32() != 1 || response.GetProperty("requestId").GetString() != requestId)
                throw Failure("LSMDX003", "The worker returned an unexpected protocol or request ID.");
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException)
        {
            await StopAsync().ConfigureAwait(false);
            throw Failure("LSMDX004", "The MDX worker timed out; the existing site was not published over.");
        }
        catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await StopAsync().ConfigureAwait(false);
            if (error is SiteBuildExtensionException) throw;
            throw Failure("LSMDX003", $"The MDX worker failed: {error.Message}");
        }
        finally { gate.Release(); }
    }

    private async Task<JsonElement> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var message = new StringBuilder();
        while (true)
        {
            if (offset == buffered)
            {
                buffered = await process!.StandardOutput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                offset = 0;
                if (buffered == 0) throw Failure("LSMDX003", "The worker closed its response stream. " + stderr);
            }
            var character = buffer[offset++];
            if (character == '\n')
            {
                var json = message.ToString();
                if (Encoding.UTF8.GetByteCount(json) > options.MaximumMessageBytes) throw Failure("LSMDX003", "Worker response exceeds the configured message size.");
                using var document = JsonDocument.Parse(json);
                return document.RootElement.Clone();
            }
            if (message.Length >= options.MaximumMessageBytes) throw Failure("LSMDX003", "Worker response exceeds the configured message size.");
            message.Append(character);
        }
    }

    private async Task DrainErrorsAsync(StreamReader reader)
    {
        var chars = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(chars).ConfigureAwait(false)) != 0)
        {
            var value = stderr + new string(chars, 0, count);
            stderr = value.Length > 4096 ? value[^4096..] : value;
        }
    }

    private async Task StopAsync()
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            if (stderrTask is not null) await stderrTask.ConfigureAwait(false);
        }
        finally { process.Dispose(); process = null; buffered = offset = 0; stderrTask = null; }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await StopAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    internal static SiteBuildExtensionException Failure(string id, string message, string? file = null, int? line = null, int? column = null) =>
        new([new SiteDiagnostic(id, SiteDiagnosticSeverity.Error, message, file is null ? null : new SiteSourceLocation(file, line, column))]);
}

internal static class MdxJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
