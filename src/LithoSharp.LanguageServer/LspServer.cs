using System.Collections.Concurrent;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.Documentation;
using LithoSharp.Inspection;
using LithoSharp.Mdx;

namespace LithoSharp.LanguageServer;

/// <summary>Server options, usually from initialize initializationOptions.</summary>
/// <param name="MdxProjectDirectory">Workspace root for MDX analysis. Defaults to the working directory.</param>
/// <param name="WorkerDirectory">Worker directory with restored dependencies. Defaults to the assembly-adjacent worker.</param>
/// <param name="NodeExecutable">Node executable. Never installed automatically.</param>
public sealed record LspServerOptions(string? MdxProjectDirectory = null, string? WorkerDirectory = null, string? NodeExecutable = null);

/// <summary>Minimal LSP 3.17 server over stdio for unsaved Markdown/MDX buffers.</summary>
/// <remarks>Implements only initialize/initialized/shutdown/exit, document
/// sync (open/change/save/close), publishDiagnostics, documentSymbol, and
/// $/cancelRequest, plus a namespaced project-context notification. Anything
/// else answers MethodNotFound or is ignored. Full-site builds never run here:
/// project context arrives only through explicit inspect/build operations.</remarks>
public sealed class LspServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly StdioTransport transport = new();
    private readonly DocumentWorkspace workspace = new();
    private readonly ConcurrentDictionary<string, Task> background = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> requests = new();
    private readonly Dictionary<string, DocumentBuffer> buffers = new();
    private readonly List<ProjectContextEntry> contexts = new();
    private readonly object stateGate = new();
    private MdxInspectionSession? mdxSession;
    private LspServerOptions serverOptions;
    private bool shutdownRequested;

    private sealed class DocumentBuffer
    {
        public required string Uri;
        public required string LanguageId;
        public required long Version;
        public required string Text;
    }

    private sealed record ProjectContextEntry(string ProjectId, IReadOnlyList<string> Folders, ProjectInspectionSnapshot Snapshot);

    /// <summary>Creates a server with optional startup options.</summary>
    public LspServer(LspServerOptions? options = null)
    {
        serverOptions = options ?? new LspServerOptions();
    }

    /// <summary>Runs the stdio loop until exit or client disconnect.</summary>
    /// <returns>Process exit code: 0 for shutdown/exit or disconnect, 1 otherwise.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            JsonDocument? message;
            try
            {
                message = await transport.ReadMessageAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (message is null)
            {
                break;
            }

            using (message)
            {
                try
                {
                    if (await DispatchAsync(message.RootElement).ConfigureAwait(false))
                    {
                        return shutdownRequested ? 0 : 1;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    StdioTransport.Log($"Discarding a failed LSP message: {exception.Message}");
                }
            }
        }

        await ShutdownOwnedAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>Dispatches one message. Returns true when the server must exit.</summary>
    private async Task<bool> DispatchAsync(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("method", out var methodValue)
            || methodValue.ValueKind != JsonValueKind.String)
        {
            if (TryRawId(root, out var badId))
            {
                ReplyError(badId, -32600, "Invalid request.");
            }

            return false;
        }

        var method = methodValue.GetString()!;
        var hasId = TryRawId(root, out var id);
        root.TryGetProperty("params", out var @params);

        switch (method)
        {
            case "initialize":
                Reply(id!, Initialize(@params));
                return false;
            case "initialized":
                return false;
            case "shutdown":
                shutdownRequested = true;
                await ShutdownOwnedAsync().ConfigureAwait(false);
                if (hasId)
                {
                    ReplyNull(id!);
                }

                return false;
            case "exit":
                await ShutdownOwnedAsync().ConfigureAwait(false);
                return true;
            case "textDocument/didOpen":
                DidOpen(@params);
                return false;
            case "textDocument/didChange":
                DidChange(@params);
                return false;
            case "textDocument/didSave":
                DidSave(@params);
                return false;
            case "textDocument/didClose":
                DidClose(@params);
                return false;
            case "textDocument/documentSymbol":
                if (hasId)
                {
                    DocumentSymbol(id!, @params);
                }

                return false;
            case "$/cancelRequest":
                CancelRequest(@params);
                return false;
            case "lithosharp/projectContext":
                SetProjectContext(@params);
                return false;
            default:
                if (hasId)
                {
                    ReplyError(id!, -32601, $"Unknown method '{method}'.");
                }
                else
                {
                    StdioTransport.Log($"Ignoring unknown LSP notification '{method}'.");
                }

                return false;
        }
    }

    private static bool TryRawId(JsonElement root, out string id)
    {
        // Keep the raw JSON text so numeric ids are not quoted on the wire.
        id = string.Empty;
        if (!root.TryGetProperty("id", out var value))
        {
            return false;
        }

        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            return false;
        }

        id = value.GetRawText();
        return !string.IsNullOrEmpty(id);
    }

    private JsonElement Initialize(JsonElement @params)
    {
        if (@params.ValueKind == JsonValueKind.Object
            && @params.TryGetProperty("initializationOptions", out var options)
            && options.ValueKind == JsonValueKind.Object)
        {
            var mdxRoot = options.TryGetProperty("mdxProjectDirectory", out var root)
                && root.ValueKind == JsonValueKind.String ? root.GetString() : null;
            var workerRoot = options.TryGetProperty("workerDirectory", out var worker)
                && worker.ValueKind == JsonValueKind.String ? worker.GetString() : null;
            var node = options.TryGetProperty("nodeExecutable", out var nodeValue)
                && nodeValue.ValueKind == JsonValueKind.String ? nodeValue.GetString() : null;
            if (mdxRoot is not null || workerRoot is not null || node is not null)
            {
                lock (stateGate)
                {
                    serverOptions = serverOptions with
                    {
                        MdxProjectDirectory = mdxRoot ?? serverOptions.MdxProjectDirectory,
                        WorkerDirectory = workerRoot ?? serverOptions.WorkerDirectory,
                        NodeExecutable = node ?? serverOptions.NodeExecutable,
                    };
                }
            }
        }

        var coreVersion = typeof(DocumentWorkspace).Assembly.GetName().Version?.ToString() ?? "unknown";
        return JsonSerializer.SerializeToElement(new
        {
            capabilities = new
            {
                positionEncoding = "utf-16",
                textDocumentSync = new
                {
                    openClose = true,
                    change = 2,
                    save = new { includeText = false },
                },
                documentSymbolProvider = true,
            },
            serverInfo = new { name = "lithosharp", version = coreVersion },
        }, JsonOptions);
    }

    private void DidOpen(JsonElement @params)
    {
        if (!TryDocument(@params, out var uri, out var languageId, out var version, out var text))
        {
            return;
        }

        lock (stateGate)
        {
            buffers[uri] = new DocumentBuffer { Uri = uri, LanguageId = languageId, Version = version, Text = text };
        }

        Track($"diagnose:{uri}:{version}", (token) => AnalyzeAndPublishAsync(uri, version, token));
    }

    private void DidChange(JsonElement @params)
    {
        if (!TryDocumentId(@params, out var uri, out var version))
        {
            return;
        }

        if (@params.ValueKind != JsonValueKind.Object
            || !@params.TryGetProperty("contentChanges", out var changes)
            || changes.ValueKind != JsonValueKind.Array)
        {
            StdioTransport.Log($"Ignoring a textDocument/didChange without contentChanges for '{uri}'.");
            return;
        }

        lock (stateGate)
        {
            if (!buffers.TryGetValue(uri, out var buffer))
            {
                StdioTransport.Log($"Ignoring a textDocument/didChange for unknown document '{uri}'.");
                return;
            }

            if (version <= buffer.Version)
            {
                StdioTransport.Log($"Ignoring a regressed textDocument/didChange version for '{uri}'.");
                return;
            }

            var current = buffer.Text;
            foreach (var change in changes.EnumerateArray())
            {
                if (!ApplyChange(ref current, change))
                {
                    StdioTransport.Log($"Ignoring a textDocument/didChange batch with an invalid range for '{uri}'.");
                    return;
                }
            }

            buffers[uri] = new DocumentBuffer { Uri = uri, LanguageId = buffer.LanguageId, Version = version, Text = current };
        }

        Track($"diagnose:{uri}:{version}", (token) => AnalyzeAndPublishAsync(uri, version, token));
    }

    private void DidSave(JsonElement @params)
    {
        // didSave carries no version; re-analyze the current buffer revision.
        if (!TryTextDocument(@params, out var uri))
        {
            StdioTransport.Log("Ignoring a textDocument/didSave without uri.");
            return;
        }

        long version;
        lock (stateGate)
        {
            if (!buffers.TryGetValue(uri, out var buffer) || buffer is null)
            {
                return;
            }

            version = buffer.Version;
        }

        Track($"diagnose:{uri}:{version}", (token) => AnalyzeAndPublishAsync(uri, version, token));
    }

    private void DidClose(JsonElement @params)
    {
        // didClose carries no version; it clears by uri alone.
        if (!TryTextDocument(@params, out var uri))
        {
            StdioTransport.Log("Ignoring a textDocument/didClose without uri.");
            return;
        }

        IReadOnlyList<string> projectIds;
        lock (stateGate)
        {
            buffers.Remove(uri);
            projectIds = contexts.Select(entry => entry.ProjectId).Distinct().ToArray();
        }

        Publish(uri, -1, []);
        workspace.Remove(UriPath(uri));
        foreach (var projectId in projectIds)
        {
            workspace.Remove(projectId, UriPath(uri));
        }
    }

    private void DocumentSymbol(string rawId, JsonElement @params)
    {
        if (!TryTextDocument(@params, out var uri))
        {
            ReplyError(rawId, -32602, "A documentSymbol request requires textDocument.uri.");
            return;
        }

        var source = new CancellationTokenSource();
        requests[rawId] = source;
        var task = Task.Run(() => SymbolsFor(uri, source.Token), source.Token);
        background[rawId] = task;
        task.ContinueWith(completed =>
        {
            background.TryRemove(rawId, out _);
            requests.TryRemove(rawId, out _);
            source.Dispose();
            if (completed.IsCanceled)
            {
                ReplyError(rawId, -32800, "Request cancelled.");
            }
            else if (completed.IsFaulted)
            {
                ReplyError(rawId, -32603, "Symbol analysis failed.");
            }
            else
            {
                Reply(rawId, completed.Result);
            }
        }, TaskScheduler.Default);
    }

    private void CancelRequest(JsonElement @params)
    {
        if (@params.ValueKind == JsonValueKind.Object
            && @params.TryGetProperty("id", out var value))
        {
            var id = value.ValueKind switch
            {
                JsonValueKind.String => value.GetRawText(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
            if (id is not null && requests.TryGetValue(id, out var source))
            {
                source.Cancel();
            }
        }
    }

    private void SetProjectContext(JsonElement @params)
    {
        if (@params.ValueKind != JsonValueKind.Object
            || !@params.TryGetProperty("projectId", out var projectIdValue)
            || projectIdValue.ValueKind != JsonValueKind.String)
        {
            StdioTransport.Log("Ignoring a lithosharp/projectContext without a projectId.");
            return;
        }

        var projectId = projectIdValue.GetString()!;
        List<(string Uri, long Version)> pending = [];
        lock (stateGate)
        {
            contexts.RemoveAll(entry => entry.ProjectId == projectId);
            if (@params.TryGetProperty("snapshot", out var snapshotValue)
                && snapshotValue.ValueKind == JsonValueKind.Object)
            {
                ProjectInspectionSnapshot snapshot;
                try
                {
                    snapshot = ProjectInspectionSnapshot.ParseJson(snapshotValue.GetRawText());
                }
                catch (ArgumentException exception)
                {
                    StdioTransport.Log($"Ignoring an unreadable project snapshot for '{projectId}': {exception.Message}");
                    return;
                }

                if (!CoreCompatible(snapshot.CoreVersion))
                {
                    StdioTransport.Log($"Ignoring project context '{projectId}' with an incompatible core version.");
                    return;
                }

                var folders = new List<string>();
                if (@params.TryGetProperty("folders", out var foldersValue) && foldersValue.ValueKind == JsonValueKind.Array)
                {
                    foreach (var folder in foldersValue.EnumerateArray())
                    {
                        if (folder.ValueKind == JsonValueKind.String && folder.GetString() is { } uri)
                        {
                            folders.Add(uri);
                        }
                    }
                }

                contexts.Add(new ProjectContextEntry(projectId, folders, snapshot));
                // Re-analyze open buffers of this project so the new generation applies.
                pending = buffers.Values
                    .Where(buffer => Matches(buffer.Uri, projectId, contexts))
                    .Select(buffer => (buffer.Uri, buffer.Version)).ToList();
            }
        }

        foreach (var (uri, version) in pending)
        {
            Track($"diagnose:{uri}:{version}", (token) => AnalyzeAndPublishAsync(uri, version, token));
        }
    }

    private static bool CoreCompatible(string coreVersion)
    {
        var ownMajor = typeof(DocumentWorkspace).Assembly.GetName().Version?.Major;
        if (ownMajor is null)
        {
            return true;
        }

        try
        {
            return !string.IsNullOrWhiteSpace(coreVersion) && new Version(coreVersion).Major == ownMajor;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool Matches(string uri, string projectId, List<ProjectContextEntry> contexts)
    {
        foreach (var entry in contexts)
        {
            if (entry.ProjectId != projectId)
            {
                continue;
            }

            foreach (var folder in entry.Folders)
            {
                if (uri.Equals(folder, StringComparison.Ordinal)
                    || uri.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private ProjectContextEntry? ContextFor(string uri)
    {
        lock (stateGate)
        {
            foreach (var entry in contexts)
            {
                foreach (var folder in entry.Folders)
                {
                    if (uri.Equals(folder, StringComparison.Ordinal)
                        || uri.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal))
                    {
                        return entry;
                    }
                }
            }

            return null;
        }
    }

    private IReadOnlyList<string> ProjectIds()
    {
        lock (stateGate)
        {
            return contexts.Select(entry => entry.ProjectId).Distinct().ToArray();
        }
    }

    private void Track(string key, Func<CancellationToken, Task> work)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await work(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                StdioTransport.Log($"Discarding a failed analysis: {exception.Message}");
            }
        });
        background[key] = task;
        task.ContinueWith(_ => background.TryRemove(key, out var removed), TaskScheduler.Default);
    }

    private static bool IsMarkdown(string languageId, string uri) =>
        string.Equals(languageId, "markdown", StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrWhiteSpace(languageId) && uri.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

    private static bool IsMdx(string languageId, string uri) =>
        string.Equals(languageId, "mdx", StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrWhiteSpace(languageId) && uri.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase));

    private async Task AnalyzeAndPublishAsync(string uri, long version, CancellationToken cancellationToken)
    {
        DocumentBuffer buffer;
        lock (stateGate)
        {
            if (!buffers.TryGetValue(uri, out buffer!) || buffer.Version != version)
            {
                return;
            }
        }

        if (IsMdx(buffer.LanguageId, buffer.Uri))
        {
            await PublishMdxAsync(buffer, version, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsMarkdown(buffer.LanguageId, buffer.Uri))
        {
            return;
        }

        var entry = ContextFor(uri);
        var path = UriPath(uri);
        DocumentInfo info;
        try
        {
            info = entry is null
                ? await workspace.InspectAsync(path, buffer.Text, null, cancellationToken).ConfigureAwait(false)
                : await workspace.InspectVersionedAsync(path, buffer.Text, version, 0,
                    new DocumentInspectionOptions { Project = entry.Snapshot }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (stateGate)
        {
            if (!buffers.TryGetValue(uri, out var latest) || latest.Version != version)
            {
                return;
            }
        }

        Publish(uri, version, info.Diagnostics
            .Where(diagnostic => diagnostic.Location is null || string.Equals(diagnostic.Location.FilePath, path, StringComparison.Ordinal))
            .Select(ToLspDiagnostic).ToArray());
    }

    private async Task PublishMdxAsync(DocumentBuffer buffer, long version, CancellationToken cancellationToken)
    {
        MdxInspectionSession session;
        lock (stateGate)
        {
            mdxSession ??= new MdxInspectionSession(new MdxOptions(
                serverOptions.MdxProjectDirectory ?? Directory.GetCurrentDirectory(),
                serverOptions.WorkerDirectory)
            {
                NodeExecutable = serverOptions.NodeExecutable ?? "node",
            });
            session = mdxSession;
        }

        MdxAnalysisResult result;
        try
        {
            result = await session.AnalyzeAsync(UriPath(buffer.Uri), buffer.Text,
                new MdxAnalysisOptions(version, 0), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (SiteBuildExtensionException exception)
        {
            // Missing Node or a dead worker: explain on stderr and keep serving
            // Markdown. No diagnostics are fabricated for the MDX buffer.
            foreach (var diagnostic in exception.Diagnostics)
            {
                StdioTransport.Log($"MDX analysis unavailable for '{buffer.Uri}': {diagnostic.Message}");
            }

            lock (stateGate)
            {
                if (!buffers.TryGetValue(buffer.Uri, out var latest) || latest.Version != version)
                {
                    return;
                }
            }

            Publish(buffer.Uri, version, []);
            return;
        }

        lock (stateGate)
        {
            if (!buffers.TryGetValue(buffer.Uri, out var latest) || latest.Version != version)
            {
                return;
            }
        }

        Publish(buffer.Uri, version, result.Diagnostics.Select(ToLspDiagnostic).ToArray());
    }

    private JsonElement SymbolsFor(string uri, CancellationToken cancellationToken)
    {
        DocumentBuffer buffer;
        lock (stateGate)
        {
            if (!buffers.TryGetValue(uri, out buffer!))
            {
                return EmptySymbols();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (IsMdx(buffer.LanguageId, buffer.Uri))
        {
            return MdxSymbols(buffer, cancellationToken);
        }

        if (!IsMarkdown(buffer.LanguageId, buffer.Uri))
        {
            return EmptySymbols();
        }

        IReadOnlyList<DocumentHeadingInfo> headings;
        lock (stateGate)
        {
            var entry = ContextFor(uri);
            DocumentInfo? info = entry is null
                ? workspace.TryGet(UriPath(uri), out var plain) ? plain : null
                : workspace.TryGet(entry.ProjectId, UriPath(uri), out var scoped) ? scoped : null;
            if (info is null)
            {
                return EmptySymbols();
            }

            headings = info.Headings;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return BuildSymbols(headings.Select(heading => (heading.Text, heading.RawLevel, heading.Location)).ToArray());
    }

    private JsonElement MdxSymbols(DocumentBuffer buffer, CancellationToken cancellationToken)
    {
        MdxInspectionSession session;
        lock (stateGate)
        {
            mdxSession ??= new MdxInspectionSession(new MdxOptions(
                serverOptions.MdxProjectDirectory ?? Directory.GetCurrentDirectory(),
                serverOptions.WorkerDirectory)
            {
                NodeExecutable = serverOptions.NodeExecutable ?? "node",
            });
            session = mdxSession;
        }

        MdxAnalysisResult result;
        try
        {
            // Off-loop blocking is safe here: this runs on a pool thread and the
            // request stays cancellable through the token.
            result = session.AnalyzeAsync(UriPath(buffer.Uri), buffer.Text,
                new MdxAnalysisOptions(buffer.Version, 0), cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is OperationCanceledException or SiteBuildExtensionException)
        {
            return EmptySymbols();
        }

        return BuildSymbols(result.Headings.Select(heading => (heading.Text, heading.RawLevel, heading.Location)).ToArray());
    }

    private static JsonElement EmptySymbols() =>
        JsonSerializer.SerializeToElement(Array.Empty<object>(), JsonOptions);

    private static JsonElement BuildSymbols((string Text, int Level, SiteSourceLocation? Location)[] headings)
    {
        var roots = new List<SymbolNode>();
        var stack = new Stack<(int Level, SymbolNode Node)>();
        foreach (var (text, level, location) in headings)
        {
            if (location?.Line is null || location.Column is null)
            {
                continue;
            }

            var node = new SymbolNode
            {
                Name = text,
                Range = LspRange(location),
                Children = [],
            };
            while (stack.Count != 0 && stack.Peek().Level >= level)
            {
                stack.Pop();
            }

            if (stack.Count == 0)
            {
                roots.Add(node);
            }
            else
            {
                stack.Peek().Node.Children.Add(node);
            }

            stack.Push((level, node));
        }

        return JsonSerializer.SerializeToElement(roots.Select(node => node.ToJson()).ToArray(), JsonOptions);
    }

    private sealed class SymbolNode
    {
        public required string Name;
        public required object Range;
        public List<SymbolNode> Children = [];

        public object ToJson() => new
        {
            name = Name,
            kind = 3,
            range = Range,
            selectionRange = Range,
            children = Children.Select(child => child.ToJson()).ToArray(),
        };
    }

    private static object LspRange(SiteSourceLocation location) => new
    {
        start = new { line = location.Line!.Value - 1, character = (location.Column ?? 1) - 1 },
        end = new
        {
            line = (location.EndLine ?? location.Line)!.Value - 1,
            character = (location.EndColumn ?? location.Column ?? 1) - 1,
        },
    };

    private static object ToLspDiagnostic(SiteDiagnostic diagnostic)
    {
        var start = diagnostic.Location is { } location && location.Line.HasValue
            ? new { line = location.Line.Value - 1, character = (location.Column ?? 1) - 1 }
            : new { line = 0, character = 0 };
        var end = diagnostic.Location is { } endLocation && endLocation.EndLine.HasValue
            ? new { line = endLocation.EndLine.Value - 1, character = (endLocation.EndColumn ?? endLocation.Column ?? 1) - 1 }
            : start;
        return new
        {
            range = new { start, end },
            severity = diagnostic.Severity switch
            {
                SiteDiagnosticSeverity.Error => 1,
                SiteDiagnosticSeverity.Warning => 2,
                _ => 3,
            },
            code = diagnostic.Id,
            source = "lithosharp",
            message = diagnostic.Message,
        };
    }

    private void Publish(string uri, long version, object[] diagnostics)
    {
        object notification = version >= 0
            ? new
            {
                jsonrpc = "2.0",
                method = "textDocument/publishDiagnostics",
                @params = new { uri, version, diagnostics },
            }
            : new
            {
                jsonrpc = "2.0",
                method = "textDocument/publishDiagnostics",
                @params = new { uri, diagnostics },
            };
        transport.WriteMessage(JsonSerializer.Serialize(notification, JsonOptions));
    }

    private void Reply(string rawId, JsonElement result)
    {
        var id = JsonDocument.Parse(rawId).RootElement.Clone();
        transport.WriteMessage(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, JsonOptions));
    }

    private void ReplyNull(string rawId)
    {
        var id = JsonDocument.Parse(rawId).RootElement.Clone();
        using var nil = JsonDocument.Parse("null");
        transport.WriteMessage(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = nil.RootElement }, JsonOptions));
    }

    private void ReplyError(string rawId, int code, string message)
    {
        JsonElement id;
        try
        {
            id = JsonDocument.Parse(rawId).RootElement.Clone();
        }
        catch (JsonException)
        {
            transport.WriteMessage(JsonSerializer.Serialize(
                new { jsonrpc = "2.0", id = rawId, error = new { code, message } }, JsonOptions));
            return;
        }

        transport.WriteMessage(JsonSerializer.Serialize(
            new { jsonrpc = "2.0", id, error = new { code, message } }, JsonOptions));
    }

    private async Task ShutdownOwnedAsync()
    {
        List<Task> pending;
        lock (stateGate)
        {
            pending = background.Values.ToList();
            foreach (var source in requests.Values)
            {
                source.Cancel();
            }
        }

        if (pending.Count != 0)
        {
            await Task.WhenAny(Task.WhenAll(pending), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }

        await workspace.DisposeAsync().ConfigureAwait(false);
        MdxInspectionSession? session;
        lock (stateGate)
        {
            session = mdxSession;
            mdxSession = null;
        }

        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool TryDocument(JsonElement @params, out string uri, out string languageId, out long version, out string text)
    {
        uri = languageId = text = string.Empty;
        version = 0;
        if (@params.ValueKind != JsonValueKind.Object
            || !@params.TryGetProperty("textDocument", out var document)
            || document.ValueKind != JsonValueKind.Object
            || !TryString(document, "uri", out var foundUri)
            || !TryString(document, "languageId", out var foundLanguage)
            || !TryVersion(document, out version)
            || !TryString(document, "text", out var foundText))
        {
            StdioTransport.Log("Ignoring a document notification without uri/languageId/version/text.");
            return false;
        }

        uri = foundUri;
        languageId = foundLanguage;
        text = foundText;
        return true;
    }

    private static bool TryDocumentId(JsonElement @params, out string uri, out long version)
    {
        uri = string.Empty;
        version = 0;
        if (@params.ValueKind != JsonValueKind.Object
            || !@params.TryGetProperty("textDocument", out var document)
            || document.ValueKind != JsonValueKind.Object
            || !TryString(document, "uri", out var foundUri)
            || !TryVersion(document, out version))
        {
            StdioTransport.Log("Ignoring a document notification without uri/version.");
            return false;
        }

        uri = foundUri;
        return true;
    }

    private static bool TryTextDocument(JsonElement @params, out string uri)
    {
        uri = string.Empty;
        if (@params.ValueKind != JsonValueKind.Object
            || !@params.TryGetProperty("textDocument", out var document)
            || document.ValueKind != JsonValueKind.Object
            || !TryString(document, "uri", out var foundUri))
        {
            return false;
        }

        uri = foundUri;
        return true;
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } text)
        {
            value = text;
            return true;
        }

        return false;
    }

    private static bool TryVersion(JsonElement element, out long version)
    {
        version = 0;
        return element.TryGetProperty("version", out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out version);
    }

    private static string UriPath(string uri)
    {
        if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return new Uri(uri).LocalPath.Replace('\\', '/');
            }
            catch (Exception exception) when (exception is UriFormatException or ArgumentException)
            {
            }
        }

        return uri;
    }

    /// <summary>Applies one content change in order. Returns false for invalid ranges.</summary>
    internal static bool ApplyChange(ref string text, JsonElement change)
    {
        if (change.ValueKind != JsonValueKind.Object
            || !change.TryGetProperty("text", out var textValue)
            || textValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var replacement = textValue.GetString()!;
        if (!change.TryGetProperty("range", out var range) || range.ValueKind != JsonValueKind.Object)
        {
            text = replacement;
            return true;
        }

        if (!TryPosition(range, "start", out var start) || !TryPosition(range, "end", out var end))
        {
            return false;
        }

        if (!TryOffset(text, start, out var startOffset)
            || !TryOffset(text, end, out var endOffset)
            || startOffset > endOffset)
        {
            return false;
        }

        text = text[..startOffset] + replacement + text[endOffset..];
        return true;
    }

    private static bool TryPosition(JsonElement range, string name, out (int Line, int Character) position)
    {
        position = (0, 0);
        if (!range.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("line", out var line) || line.ValueKind != JsonValueKind.Number || !line.TryGetInt32(out var lineValue)
            || !value.TryGetProperty("character", out var character) || character.ValueKind != JsonValueKind.Number || !character.TryGetInt32(out var characterValue)
            || lineValue < 0 || characterValue < 0)
        {
            return false;
        }

        position = (lineValue, characterValue);
        return true;
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                starts.Add(index + 1);
            }
            else if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return starts;
    }

    private static bool TryOffset(string text, (int Line, int Character) position, out int offset)
    {
        offset = 0;
        var starts = LineStarts(text);
        if (position.Line >= starts.Count)
        {
            return false;
        }

        var start = starts[position.Line];
        var end = position.Line + 1 < starts.Count ? starts[position.Line + 1] : text.Length;
        // Exclude the line break from the addressable range.
        while (end > start && text[end - 1] is '\n' or '\r')
        {
            end--;
        }

        if (start + position.Character > end)
        {
            return false;
        }

        offset = start + position.Character;
        return true;
    }
}
