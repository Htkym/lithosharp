using System.Text;
using System.Text.Json;

namespace LithoSharp.LanguageServer;

/// <summary>LSP Content-Length framing over stdio.</summary>
/// <remarks>Only stdout carries frames; logs go to stderr so stdout stays pure.
/// Reads tolerate split and concatenated messages and resync past garbage.
/// No CLI JSON Lines are involved here.</remarks>
internal sealed class StdioTransport
{
    private readonly Stream input;
    private readonly Stream output;
    private readonly object writeGate = new();
    private readonly int maxMessageBytes;

    public StdioTransport(int maxMessageBytes = 32 * 1024 * 1024)
    {
        input = Console.OpenStandardInput();
        output = Console.OpenStandardOutput();
        this.maxMessageBytes = maxMessageBytes;
    }

    /// <summary>Reads one framed message, or null on stdin EOF (client disconnect).</summary>
    public async Task<JsonDocument?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = await ReadHeadersAsync(cancellationToken).ConfigureAwait(false);
            if (length is null)
            {
                return null;
            }

            if (length < 0)
            {
                // Garbage line: log and resync to the next header block.
                continue;
            }

            if (length.Value > maxMessageBytes)
            {
                await DrainAsync(length.Value, cancellationToken).ConfigureAwait(false);
                continue;
            }

            byte[]? body;
            try
            {
                body = await ReadExactAsync(length.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (SkippedMessageException)
            {
                Log("Discarding an overlong LSP message.");
                continue;
            }

            if (body is null)
            {
                return null;
            }

            JsonDocument message;
            try
            {
                message = JsonDocument.Parse(body);
            }
            catch (JsonException exception)
            {
                Log($"Discarding a malformed JSON-RPC body: {exception.Message}");
                continue;
            }

            return message;
        }
    }

    /// <summary>Reads a header block. Returns the byte count, -1 for garbage, or null on EOF.</summary>
    private async Task<int?> ReadHeadersAsync(CancellationToken cancellationToken)
    {
        int? length = null;
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                // End of headers without a length is garbage (for example the
                // blank half of a split line), never EOF. EOF surfaces only
                // when the stream itself ends.
                return length ?? -1;
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                Log("Discarding a non-header line while resyncing the LSP stream.");
                return -1;
            }

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(colon + 1)..].Trim(), out var parsed) && parsed >= 0)
            {
                length = parsed;
            }
        }
    }

    private readonly byte[] lineBuffer = new byte[8192];
    private readonly List<byte> linePending = [];
    private bool discardingLongLine;

    /// <summary>Reads one CRLF/LF-terminated line as UTF-8 text, or null on EOF.</summary>
    /// <remarks>Overlong lines are discarded up to their newline and reported as
    /// a garbage marker so the reader resyncs instead of misframing.</remarks>
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            for (var index = 0; index < linePending.Count; index++)
            {
                if (linePending[index] == (byte)'\n')
                {
                    var bytes = linePending.GetRange(0, index).ToArray();
                    linePending.RemoveRange(0, index + 1);
                    if (discardingLongLine)
                    {
                        discardingLongLine = false;
                        return "\0garbage\0";
                    }

                    return Encoding.UTF8.GetString(bytes).TrimEnd('\r');
                }
            }

            var read = await input.ReadAsync(lineBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (linePending.Count == 0 && !discardingLongLine)
                {
                    return null;
                }

                linePending.Clear();
                discardingLongLine = false;
                return "\0garbage\0";
            }

            linePending.AddRange(lineBuffer.Take(read));
            if (linePending.Count > maxMessageBytes)
            {
                linePending.Clear();
                discardingLongLine = true;
                Log("Discarding an overlong LSP line while resyncing.");
            }
        }
    }

    private sealed class SkippedMessageException : Exception;

    private async Task<byte[]?> ReadExactAsync(int count, CancellationToken cancellationToken)
    {
        // The line reader may have already buffered body bytes; drain those first.
        while (linePending.Count < count)
        {
            if (linePending.Count > maxMessageBytes)
            {
                linePending.Clear();
                await DrainAsync(count, cancellationToken).ConfigureAwait(false);
                throw new SkippedMessageException();
            }

            var read = await input.ReadAsync(lineBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            linePending.AddRange(lineBuffer.Take(read));
        }

        var body = linePending.GetRange(0, count).ToArray();
        linePending.RemoveRange(0, count);
        return body;
    }

    private async Task DrainAsync(int count, CancellationToken cancellationToken)
    {
        Log("Discarding an overlong LSP message.");
        var remaining = count;
        var buffer = new byte[8192];
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            remaining -= read;
        }
    }

    /// <summary>Writes one framed message. The only stdout writer.</summary>
    public void WriteMessage(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
        lock (writeGate)
        {
            output.Write(header, 0, header.Length);
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
        }
    }

    public static void Log(string message) => Console.Error.WriteLine(message);
}
