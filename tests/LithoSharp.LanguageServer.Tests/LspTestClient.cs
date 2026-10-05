using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LithoSharp.Tests;

/// <summary>Spawns the real language server over stdio and speaks LSP framing.</summary>
internal sealed class LspTestClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Process process;
    private readonly List<string> stderrLines = [];
    private readonly object stderrGate = new();
    private readonly List<byte> pendingBytes = [];
    private readonly List<JsonElement> inbox = [];
    private long nextId = 1;

    public List<JsonElement> Received { get; } = [];

    private LspTestClient(Process process)
    {
        this.process = process;
    }

    public static LspTestClient Start()
    {
        var root = FindRepository();
        var server = typeof(LithoSharp.LanguageServer.LspServer).Assembly.Location;
        if (!File.Exists(server))
        {
            throw new InvalidOperationException($"The referenced language server assembly is missing: {server}");
        }

        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        start.ArgumentList.Add(server);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the language server.");
        process.StandardInput.AutoFlush = true;
        var client = new LspTestClient(process);
        // Drain stderr on its own thread: Peek/ReadLine block on open pipes,
        // so synchronous reads here would hang the test run forever.
        var drain = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = process.StandardError.ReadLine()) is not null)
                {
                    lock (client.stderrGate)
                    {
                        client.stderrLines.Add(line);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        })
        {
            IsBackground = true,
        };
        drain.Start();
        return client;
    }

    private static string FindRepository()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
            {
                return path.FullName;
            }
        }

        throw new DirectoryNotFoundException("The language server tests require the repository root.");
    }

    public string NextId() => (nextId++).ToString();

    /// <summary>Sends one JSON-RPC message with Content-Length framing.</summary>
    public void Send(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        process.StandardInput.Write($"Content-Length: {bytes.Length}\r\n\r\n{json}");
    }

    /// <summary>Sends a raw chunk, allowing split and concatenated framing.</summary>
    public void SendRaw(string chunk)
    {
        process.StandardInput.Write(chunk);
    }

    public static string Frame(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return $"Content-Length: {bytes.Length}\r\n\r\n{json}";
    }

    public void SendRequest(string id, string method, object? @params = null) =>
        Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params }, JsonOptions));

    public void SendNotification(string method, object? @params = null) =>
        Send(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params }, JsonOptions));

    /// <summary>Reads messages until match or timeout. Unmatched messages persist for later waits.</summary>
    public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> match, TimeSpan? timeout = null)
    {
        var limit = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        var chunk = new byte[8192];
        Task<int>? pending = null;
        while (DateTimeOffset.UtcNow < limit)
        {
            foreach (var message in inbox.ToArray())
            {
                if (match(message))
                {
                    inbox.Remove(message);
                    return message;
                }
            }

            // A single outstanding read: overlapping reads on one stream lose bytes.
            pending ??= process.StandardOutput.BaseStream.ReadAsync(chunk.AsMemory()).AsTask();
            var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(100))).ConfigureAwait(false);
            if (!ReferenceEquals(completed, pending))
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"The language server exited with code {process.ExitCode} while waiting for a message. Stderr: {string.Join("\n", ReadStderr())}");
                }

                continue;
            }

            var count = await pending.ConfigureAwait(false);
            pending = null;
            if (count == 0)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"The language server exited with code {process.ExitCode} while waiting for a message. Stderr: {string.Join("\n", ReadStderr())}");
                }

                continue;
            }

            pendingBytes.AddRange(chunk.Take(count));
            foreach (var message in Extract(pendingBytes))
            {
                Received.Add(message);
                inbox.Add(message);
            }
        }

        throw new TimeoutException("Timed out waiting for a language server message.");
    }

    private static IEnumerable<JsonElement> Extract(List<byte> pending)
    {
        var messages = new List<JsonElement>();
        while (true)
        {
            var headerEnd = IndexOf(pending, [(byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n']);
            if (headerEnd < 0)
            {
                return messages;
            }

            var length = 0;
            var header = Encoding.ASCII.GetString(pending.GetRange(0, headerEnd).ToArray());
            foreach (var line in header.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line["Content-Length:".Length..].Trim(), out var parsed))
                {
                    length = parsed;
                }
            }

            if (pending.Count - (headerEnd + 4) < length)
            {
                return messages;
            }

            var body = Encoding.UTF8.GetString(pending.GetRange(headerEnd + 4, length).ToArray());
            pending.RemoveRange(0, headerEnd + 4 + length);
            messages.Add(JsonDocument.Parse(body).RootElement.Clone());
        }
    }

    private static int IndexOf(List<byte> haystack, byte[] needle)
    {
        for (var index = 0; index + needle.Length <= haystack.Count; index++)
        {
            var found = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[index + offset] != needle[offset])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return index;
            }
        }

        return -1;
    }

    public List<string> ReadStderr()
    {
        lock (stderrGate)
        {
            return stderrLines.ToList();
        }
    }

    /// <summary>Waits for an stderr line match. Never blocks on an open pipe.</summary>
    public async Task<string> WaitStderrAsync(Func<string, bool> match, TimeSpan? timeout = null)
    {
        var limit = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTimeOffset.UtcNow < limit)
        {
            lock (stderrGate)
            {
                foreach (var line in stderrLines)
                {
                    if (match(line))
                    {
                        return line;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }

        throw new TimeoutException("Timed out waiting for a language server stderr line.");
    }

    public void CloseStdin()
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
    }

    public bool HasExited => process.HasExited;

    public int WaitForExit(TimeSpan? timeout = null) =>
        process.WaitForExit((int)(timeout ?? TimeSpan.FromSeconds(20)).TotalMilliseconds) ? process.ExitCode : throw new TimeoutException("The language server did not exit in time.");

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                // EOF lets the server await cancellation and dispose its owned
                // MDX worker before the fixture removes the worker directory.
                CloseStdin();
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                }
                catch (TimeoutException timeout)
                {
                    // Preserve this failure even if the owned kill/exit wait
                    // itself fails or races the process exit.
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                    catch (Exception cleanupFailure)
                    {
                        timeout.Data["OwnedForcedCleanupFailure"] = cleanupFailure;
                    }
                    finally
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(timeout).Throw();
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}
