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
    private readonly Dictionary<string, DocumentAnalysis> documentAnalyses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DocumentBuffer> buffers = new();
    private readonly List<ProjectContextEntry> contexts = new();
    private readonly object stateGate = new();
    private MdxInspectionSession? mdxSession;
    private LspServerOptions serverOptions;
    private bool shutdownRequested;
    private long clientContextGeneration;

    private sealed class DocumentBuffer
    {
        public required string Uri;
        public required string LanguageId;
        public required long Version;
        public long? OpenGeneration;
        public required string Text;
        public DocumentInfo? Inspection;
        public MdxAnalysisResult? MdxInspection;
        public bool MdxInspectionCompleted;
        public ProjectContextEntry? InspectionContext;
    }

    private sealed record DocumentAnalysis(CancellationTokenSource Source, Task Task);

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
                experimental = new { lithosharp = new {
                    schemaVersion = "1.0", coreVersion,
                    markdownInspection = true, mdxInspection = "on-demand-worker",
                    preflightStaticInputs = false, preflightTrustedCatalog = false,
                    project = ToolingCapabilities.NotEvaluatedProject,
                } },
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

        if (!TryClientGeneration(@params, "lithosharpOpenGeneration", out var openGeneration))
        {
            return;
        }

        lock (stateGate)
        {
            buffers[uri] = new DocumentBuffer { Uri = uri, LanguageId = languageId, Version = version, Text = text, OpenGeneration = openGeneration };
        }

        TrackDocumentAnalysis(uri, version);
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

            buffers[uri] = new DocumentBuffer { Uri = uri, LanguageId = buffer.LanguageId, Version = version, Text = current, OpenGeneration = buffer.OpenGeneration };
        }

        TrackDocumentAnalysis(uri, version);
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

        TrackDocumentAnalysis(uri, version);
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

        CancelDocumentAnalysis(uri);

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

        DocumentBuffer buffer;
        ProjectContextEntry? context;
        lock (stateGate)
        {
            if (!buffers.TryGetValue(uri, out buffer!))
            {
                Reply(rawId, EmptySymbols());
                return;
            }

            context = ContextFor(uri);
        }

        var source = new CancellationTokenSource();
        requests[rawId] = source;
        var task = Task.Run(() => SymbolsForAsync(buffer, context, source.Token), source.Token);
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
                try { source.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    private void SetProjectContext(JsonElement @params)
    {
        if (@params.ValueKind != JsonValueKind.Object)
        {
            StdioTransport.Log("Ignoring a non-object lithosharp/projectContext.");
            return;
        }

        // A valid client epoch is the publication boundary even if its snapshot
        // is rejected. Invalid payloads must not strand the client on a new epoch.
        if (!TryClientGeneration(@params, "lithosharpContextGeneration", out var contextGeneration))
        {
            return;
        }

        if (!@params.TryGetProperty("projectId", out var projectIdValue)
            || projectIdValue.ValueKind != JsonValueKind.String)
        {
            StdioTransport.Log("Ignoring a lithosharp/projectContext without a projectId.");
            AdvanceRejectedContextGeneration(contextGeneration);
            return;
        }

        var projectId = projectIdValue.GetString()!;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            StdioTransport.Log("Ignoring an empty lithosharp/projectContext projectId.");
            AdvanceRejectedContextGeneration(contextGeneration);
            return;
        }

        ProjectInspectionSnapshot? snapshot = null;
        if (@params.TryGetProperty("snapshot", out var snapshotValue)
            && snapshotValue.ValueKind != JsonValueKind.Null)
        {
            if (snapshotValue.ValueKind != JsonValueKind.Object)
            {
                StdioTransport.Log($"Ignoring a non-object project snapshot for '{projectId}'.");
                AdvanceRejectedContextGeneration(contextGeneration);
                return;
            }

            try
            {
                snapshot = ProjectInspectionSnapshot.ParseJson(snapshotValue.GetRawText());
            }
            catch (ArgumentException exception)
            {
                StdioTransport.Log($"Ignoring an unreadable project snapshot for '{projectId}': {exception.Message}");
                AdvanceRejectedContextGeneration(contextGeneration);
                return;
            }

            if (!string.Equals(snapshot.ProjectId, projectId, StringComparison.Ordinal))
            {
                StdioTransport.Log($"Ignoring project context '{projectId}' whose snapshot has a different projectId.");
                AdvanceRejectedContextGeneration(contextGeneration);
                return;
            }

            if (!CoreCompatible(snapshot.CoreVersion))
            {
                StdioTransport.Log($"Ignoring project context '{projectId}' with an incompatible core version.");
                AdvanceRejectedContextGeneration(contextGeneration);
                return;
            }
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

        List<(string Uri, long Version)> affected;
        lock (stateGate)
        {
            if (contextGeneration is { } receivedGeneration &&
                (receivedGeneration < clientContextGeneration ||
                 (receivedGeneration == clientContextGeneration && buffers.Count != 0)))
            {
                return;
            }
            var generationChanged = contextGeneration is { } nextGeneration && nextGeneration != clientContextGeneration;
            if (contextGeneration is { } generation) clientContextGeneration = generation;
            var previousOwners = buffers.Values.ToDictionary(buffer => buffer.Uri, buffer => ContextFor(buffer.Uri));
            contexts.RemoveAll(entry => entry.ProjectId == projectId);
            if (snapshot is not null)
            {
                contexts.Add(new ProjectContextEntry(projectId, folders, snapshot));
            }

            affected = buffers.Values
                .Where(buffer => generationChanged || !ReferenceEquals(previousOwners[buffer.Uri], ContextFor(buffer.Uri)))
                .Select(buffer => (buffer.Uri, buffer.Version)).ToList();
            foreach (var (uri, _) in affected)
            {
                // Ownership can change to another project or to syntax-only.
                // Clear each previous/current cache before reserving new analysis.
                var path = UriPath(uri);
                workspace.Remove(path);
                workspace.Remove(projectId, path);
                if (previousOwners[uri] is { } previous) workspace.Remove(previous.ProjectId, path);
                if (ContextFor(uri) is { } current) workspace.Remove(current.ProjectId, path);
            }
            foreach (var (uri, version) in affected)
            {
                TrackDocumentAnalysis(uri, version);
            }
        }
    }

    private void AdvanceRejectedContextGeneration(long? generation)
    {
        lock (stateGate)
        {
            if (generation is not { } next || next <= clientContextGeneration) return;
            clientContextGeneration = next;
            // Retain the last valid snapshots. Invalidate owned work before new
            // publication so an obsolete analysis cannot acquire the new tag.
            foreach (var buffer in buffers.Values)
            {
                var path = UriPath(buffer.Uri);
                workspace.Remove(path);
                if (ContextFor(buffer.Uri) is { } owner) workspace.Remove(owner.ProjectId, path);
            }
            foreach (var buffer in buffers.Values) TrackDocumentAnalysis(buffer.Uri, buffer.Version);
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

    private static bool ClaimsSource(ProjectContextEntry entry, string uri)
    {
        var path = UriPath(uri).Replace('\\', '/');
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var route in entry.Snapshot.Routes)
        {
            var source = route.SourcePath.Replace('\\', '/');
            if (string.Equals(source, path, comparison)) return true;
            // Relative sources are scoped to the declared folders, never matched
            // by filename alone across unrelated roots.
            if (!Path.IsPathRooted(source) && entry.Folders.Any(folder =>
                string.Equals(UriPath(folder).TrimEnd('/') + "/" + source, path, comparison))) return true;
        }
        return false;
    }

    private ProjectContextEntry? ContextFor(string uri)
    {
        lock (stateGate)
        {
            ProjectContextEntry? sourceOwner = null;
            foreach (var entry in contexts.Where(entry => ClaimsSource(entry, uri)))
            {
                if (sourceOwner is not null) return null;
                sourceOwner = entry;
            }
            if (sourceOwner is not null) return sourceOwner;

            ProjectContextEntry? best = null;
            var bestFolderLength = -1;
            var ambiguous = false;
            foreach (var entry in contexts)
            {
                foreach (var folder in entry.Folders)
                {
                    if (uri.Equals(folder, StringComparison.Ordinal)
                        || uri.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal))
                    {
                        if (folder.Length > bestFolderLength)
                        {
                            best = entry;
                            bestFolderLength = folder.Length;
                            ambiguous = false;
                        }
                        else if (folder.Length == bestFolderLength && !ReferenceEquals(best, entry))
                        {
                            ambiguous = true;
                        }
                    }
                }
            }
            return ambiguous ? null : best;
        }
    }

    private IReadOnlyList<string> ProjectIds()
    {
        lock (stateGate)
        {
            return contexts.Select(entry => entry.ProjectId).Distinct().ToArray();
        }
    }

    private void TrackDocumentAnalysis(string uri, long version)
    {
        CancellationTokenSource source;
        Task task;
        var taskId = "analysis:" + Guid.NewGuid().ToString("N");
        lock (stateGate)
        {
            if (documentAnalyses.TryGetValue(uri, out var previous))
            {
                previous.Source.Cancel();
            }
            if (buffers.TryGetValue(uri, out var buffer) && IsMdx(buffer.LanguageId, uri))
            {
                // Explicit save/context reanalysis retries even an unavailable
                // result without changing the buffer text or version.
                buffer.MdxInspection = null;
                buffer.MdxInspectionCompleted = false;
                buffer.InspectionContext = null;
            }
            source = new CancellationTokenSource();
            task = Task.Run(async () =>
            {
                try
                {
                    await AnalyzeAndPublishAsync(uri, version, source.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    StdioTransport.Log($"Discarding a failed analysis: {exception.Message}");
                    throw;
                }
            }, source.Token);
            documentAnalyses[uri] = new DocumentAnalysis(source, task);
            background[taskId] = task;
        }

        _ = task.ContinueWith(completed =>
        {
            _ = completed.Exception; // Observe background faults even without symbol waiters.
            background.TryRemove(taskId, out var removed);
            lock (stateGate)
            {
                if (documentAnalyses.TryGetValue(uri, out var current) && ReferenceEquals(current.Source, source))
                {
                    documentAnalyses.Remove(uri);
                }
            }

            source.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void CancelDocumentAnalysis(string uri)
    {
        lock (stateGate)
        {
            if (documentAnalyses.TryGetValue(uri, out var source))
            {
                source.Source.Cancel();
            }
        }
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
        ProjectContextEntry? entry;
        Task<DocumentInfo>? inspection = null;
        long contextGeneration;
        var path = UriPath(uri);
        lock (stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!buffers.TryGetValue(uri, out buffer!) || buffer.Version != version)
            {
                return;
            }

            entry = ContextFor(uri);
            contextGeneration = clientContextGeneration;
            if (IsMarkdown(buffer.LanguageId, buffer.Uri))
            {
                // Reservation and buffer/context changes use the same lock, so
                // obsolete work cannot supersede the new revision's inspection.
                inspection = entry is null
                    ? workspace.InspectAsync(path, buffer.Text, null, cancellationToken)
                    : workspace.InspectVersionedAsync(path, buffer.Text, version, entry.Snapshot.ProjectGeneration,
                        new DocumentInspectionOptions { Project = entry.Snapshot }, cancellationToken);
            }
        }

        if (IsMdx(buffer.LanguageId, buffer.Uri))
        {
            await PublishMdxAsync(buffer, entry, contextGeneration, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (inspection is null)
        {
            return;
        }

        var info = await inspection.ConfigureAwait(false);
        var documentDiagnostics = info.Diagnostics
            .Where(diagnostic => diagnostic.Location is null || string.Equals(diagnostic.Location.FilePath, path, StringComparison.Ordinal))
            .ToArray();
        foreach (var diagnostic in documentDiagnostics.Where(item => item.Location?.Line is null))
        {
            StdioTransport.Log($"Diagnostic {diagnostic.Id} for '{uri}' has no reliable source position and was not published to Problems.");
        }

        var diagnostics = documentDiagnostics.Where(diagnostic => diagnostic.Location?.Line is not null)
            .Select(ToLspDiagnostic).ToArray();
        lock (stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!buffers.TryGetValue(uri, out var latest) || !ReferenceEquals(latest, buffer)
                || !ReferenceEquals(ContextFor(uri), entry) || clientContextGeneration != contextGeneration)
            {
                return;
            }

            buffer.Inspection = info;
            buffer.InspectionContext = entry;
            Publish(uri, version, diagnostics, buffer.OpenGeneration, contextGeneration);
        }
    }

    private async Task PublishMdxAsync(DocumentBuffer buffer, ProjectContextEntry? entry, long contextGeneration, CancellationToken cancellationToken)
    {
        MdxInspectionSession session;
        lock (stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!buffers.TryGetValue(buffer.Uri, out var current) || !ReferenceEquals(current, buffer)
                || !ReferenceEquals(ContextFor(buffer.Uri), entry) || clientContextGeneration != contextGeneration)
            {
                return;
            }

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
                new MdxAnalysisOptions(buffer.Version, entry?.Snapshot.ProjectGeneration ?? 0), cancellationToken).ConfigureAwait(false);
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
                cancellationToken.ThrowIfCancellationRequested();
                if (!buffers.TryGetValue(buffer.Uri, out var latest) || !ReferenceEquals(latest, buffer)
                    || !ReferenceEquals(ContextFor(buffer.Uri), entry) || clientContextGeneration != contextGeneration)
                {
                    return;
                }

                buffer.MdxInspection = null;
                buffer.MdxInspectionCompleted = true;
                buffer.InspectionContext = entry;
                Publish(buffer.Uri, buffer.Version, [], buffer.OpenGeneration, contextGeneration);
                PublishAnalysisStatus(buffer, contextGeneration, "unavailable",
                    "MDX analysis is unavailable. Restore the locked worker or correct the Node/worker setting, then restart the language server. Markdown diagnostics remain available.");
            }
            return;
        }

        foreach (var diagnostic in result.Diagnostics.Where(item => item.Location?.Line is null))
        {
            StdioTransport.Log($"MDX diagnostic {diagnostic.Id} for '{buffer.Uri}' has no reliable source position and was not published to Problems.");
        }

        var diagnostics = result.Diagnostics.Where(diagnostic => diagnostic.Location?.Line is not null)
            .Select(ToLspDiagnostic).ToArray();
        lock (stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!buffers.TryGetValue(buffer.Uri, out var latest) || !ReferenceEquals(latest, buffer)
                || !ReferenceEquals(ContextFor(buffer.Uri), entry) || clientContextGeneration != contextGeneration)
            {
                return;
            }

            buffer.MdxInspection = result;
            buffer.MdxInspectionCompleted = true;
            buffer.InspectionContext = entry;
            Publish(buffer.Uri, buffer.Version, diagnostics, buffer.OpenGeneration, contextGeneration);
            PublishAnalysisStatus(buffer, contextGeneration, "ready", "MDX syntax analysis completed; modules, plugins and SSR were not executed.");
        }
    }

    private void PublishAnalysisStatus(DocumentBuffer buffer, long contextGeneration, string state, string message)
    {
        transport.WriteMessage(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "lithosharp/analysisStatus",
            @params = new { uri = buffer.Uri, version = buffer.Version, language = "mdx", state, message,
                lithosharpOpenGeneration = buffer.OpenGeneration, lithosharpContextGeneration = contextGeneration } }, JsonOptions));
    }

    private async Task<JsonElement> SymbolsForAsync(DocumentBuffer buffer, ProjectContextEntry? context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            if (!buffers.TryGetValue(buffer.Uri, out var latest) || !ReferenceEquals(latest, buffer)
                || !ReferenceEquals(ContextFor(buffer.Uri), context))
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        if (IsMdx(buffer.LanguageId, buffer.Uri))
        {
            var symbols = await MdxSymbolsAsync(buffer, context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (stateGate)
            {
                if (!buffers.TryGetValue(buffer.Uri, out var latest) || !ReferenceEquals(latest, buffer)
                    || !ReferenceEquals(ContextFor(buffer.Uri), context))
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            return symbols;
        }

        if (!IsMarkdown(buffer.LanguageId, buffer.Uri))
        {
            return EmptySymbols();
        }

        DocumentInfo info;
        for (;;)
        {
            Task pending;
            lock (stateGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!buffers.TryGetValue(buffer.Uri, out var latest) || !ReferenceEquals(latest, buffer)
                    || !ReferenceEquals(ContextFor(buffer.Uri), context))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (buffer.Inspection is not null && ReferenceEquals(buffer.InspectionContext, context))
                {
                    info = buffer.Inspection;
                    break;
                }

                if (!documentAnalyses.TryGetValue(buffer.Uri, out var analysis)
                    || analysis.Task.IsCompletedSuccessfully || analysis.Task.IsCanceled)
                {
                    // Missing inspection is retried as shared, server-owned work.
                    // Concurrent symbol requests join it instead of superseding it.
                    TrackDocumentAnalysis(buffer.Uri, buffer.Version);
                    analysis = documentAnalyses[buffer.Uri];
                }
                pending = analysis.Task;
            }

            try
            {
                // Cancelling this request never cancels shared diagnostic work.
                await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A save can replace a queued analysis without changing this buffer.
                // Revalidate identity and join the replacement on the next iteration.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (stateGate)
        {
            if (!buffers.TryGetValue(buffer.Uri, out var latest) || !ReferenceEquals(latest, buffer)
                || !ReferenceEquals(ContextFor(buffer.Uri), context))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            buffer.Inspection = info;
            buffer.InspectionContext = context;
        }

        return BuildSymbols(info.Headings.Select(heading => (heading.Text, heading.RawLevel, heading.Location)).ToArray());
    }

    private async Task<JsonElement> MdxSymbolsAsync(DocumentBuffer buffer, ProjectContextEntry? context, CancellationToken cancellationToken)
    {
        MdxAnalysisResult? result;
        for (;;)
        {
            Task pending;
            lock (stateGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!buffers.TryGetValue(buffer.Uri, out var latest) || !ReferenceEquals(latest, buffer)
                    || !ReferenceEquals(ContextFor(buffer.Uri), context))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (buffer.MdxInspectionCompleted && ReferenceEquals(buffer.InspectionContext, context))
                {
                    // A terminal unavailable result is also shared. Its primary
                    // error was logged by diagnostics; do not spin/restart here.
                    result = buffer.MdxInspection;
                    break;
                }

                if (!documentAnalyses.TryGetValue(buffer.Uri, out var analysis)
                    || analysis.Task.IsCompletedSuccessfully || analysis.Task.IsCanceled)
                {
                    TrackDocumentAnalysis(buffer.Uri, buffer.Version);
                    analysis = documentAnalyses[buffer.Uri];
                }
                pending = analysis.Task;
            }

            try
            {
                // This token belongs only to the waiter; the owned analysis has
                // the document lifecycle token passed by TrackDocumentAnalysis.
                await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Same-buffer save/context work can supersede the queued task.
                // Revalidate ownership before joining its replacement.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result is null ? EmptySymbols()
            : BuildSymbols(result.Headings.Select(heading => (heading.Text, heading.RawLevel, heading.Location)).ToArray());
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
        var location = diagnostic.Location;
        if (location?.Line is null)
        {
            throw new InvalidOperationException("A document diagnostic without a reliable line cannot be mapped to an LSP range.");
        }

        var start = new { line = location.Line.Value - 1, character = (location.Column ?? 1) - 1 };
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

    private void Publish(string uri, long version, object[] diagnostics, long? openGeneration = null, long? contextGeneration = null)
    {
        object notification = version >= 0
            ? new
            {
                jsonrpc = "2.0",
                method = "textDocument/publishDiagnostics",
                @params = new { uri, version, diagnostics, lithosharpOpenGeneration = openGeneration, lithosharpContextGeneration = contextGeneration },
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
            foreach (var analysis in documentAnalyses.Values)
            {
                analysis.Source.Cancel();
            }

            foreach (var source in requests.Values)
            {
                try { source.Cancel(); }
                catch (ObjectDisposedException) { }
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

    private static bool TryClientGeneration(JsonElement element, string name, out long? generation)
    {
        generation = null;
        if (!element.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)
            || number < 0 || number > 9007199254740991) return false;
        generation = number;
        return true;
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
