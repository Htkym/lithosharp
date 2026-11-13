using System.Text.Json;
using System.Text.Json.Nodes;
using LithoSharp.Content;
using LithoSharp.Documentation;
using LithoSharp.Inspection;

namespace LithoSharp.LanguageServer;

public sealed partial class LspServer
{
    private bool referenceDocumentChanges;
    private bool referenceAtomicTextEdits;
    private bool referenceAnnotations;
    private bool referencePrepareSupport;
    private bool referenceWillRename;

    private void InitializeReferenceCapabilities(JsonElement parameters)
    {
        lock (stateGate)
        {
            referenceDocumentChanges = Capability(parameters, "capabilities", "workspace", "workspaceEdit", "documentChanges").ValueKind == JsonValueKind.True;
            var failure = Capability(parameters, "capabilities", "workspace", "workspaceEdit", "failureHandling");
            referenceAtomicTextEdits = failure.ValueKind == JsonValueKind.String
                && failure.GetString() is "transactional" or "textOnlyTransactional";
            referenceAnnotations = Capability(parameters, "capabilities", "workspace", "workspaceEdit", "changeAnnotationSupport").ValueKind == JsonValueKind.Object
                && Capability(parameters, "capabilities", "textDocument", "rename", "honorsChangeAnnotations").ValueKind == JsonValueKind.True;
            referencePrepareSupport = Capability(parameters, "capabilities", "textDocument", "rename", "prepareSupport").ValueKind == JsonValueKind.True;
            referenceWillRename = Capability(parameters, "capabilities", "workspace", "fileOperations", "willRename").ValueKind == JsonValueKind.True;
        }
    }

    private JsonElement WithReferenceCapabilities(JsonElement response)
    {
        var result = JsonNode.Parse(response.GetRawText())!.AsObject();
        var capabilities = result["capabilities"]!.AsObject();
        capabilities["definitionProvider"] = true;
        capabilities["referencesProvider"] = true;
        var renameEnabled = referenceDocumentChanges && referenceAnnotations;
        capabilities["renameProvider"] = renameEnabled && referencePrepareSupport
            ? new JsonObject { ["prepareProvider"] = true } : JsonValue.Create(renameEnabled);
        capabilities["completionProvider"]!["triggerCharacters"] = new JsonArray(":", " ", "[", "(", "#", "/");
        capabilities["experimental"]!["lithosharp"]!["referenceEditing"] = new JsonObject
        {
            ["scope"] = "captured-static-markdown",
            ["previewMethod"] = "lithosharp/previewRename",
            ["statusNotification"] = "lithosharp/referenceStatus",
            ["scopeMethod"] = "lithosharp/referenceScopes",
            ["versionedDocumentChangesRequired"] = true,
            ["closedFilesAndCSharpRequireSeparateEvidence"] = true,
            ["csharpPreviewCommand"] = "lithosharp preview-csharp-rename",
        };
        if (renameEnabled && referenceWillRename)
            capabilities["workspace"] = new JsonObject
            {
                ["fileOperations"] = new JsonObject
                {
                    ["willRename"] = new JsonObject
                    {
                        ["filters"] = new JsonArray(new JsonObject
                        {
                            ["scheme"] = "file",
                            ["pattern"] = new JsonObject
                            {
                                ["glob"] = "**/*.md",
                                ["matches"] = "file",
                                ["options"] = new JsonObject { ["ignoreCase"] = true },
                            },
                        }),
                    },
                },
            };
        return JsonSerializer.SerializeToElement(result, JsonOptions);
    }

    private static JsonElement Capability(JsonElement value, params string[] path)
    {
        foreach (var key in path)
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value))
                return default;
        return value;
    }

    private sealed record ReferenceBuffer(string Uri, DocumentBuffer Buffer, string ProjectPath, bool IsDynamic);
    private sealed record ReferenceCapture(DocumentBuffer Source, ProjectContextEntry Context, long Generation,
        IReadOnlyList<ReferenceBuffer> Buffers, bool CatalogComplete, ProjectInspectionSnapshot Snapshot);
    private sealed record ReferenceResponse(JsonElement Result, ReferenceCapture? Capture);
    private sealed class ReferenceRequestException(int code, string message) : Exception(message)
    {
        internal int Code { get; } = code;
    }

    private ReferenceCapture CaptureReferences(string uri, string? collection = null, string? version = null, string? locale = null)
    {
        lock (stateGate)
        {
            if (!buffers.TryGetValue(uri, out var source) || !IsMarkdown(source.LanguageId, uri))
                throw new ReferenceRequestException(-32602, "Reference inspection requires an open Markdown document.");
            var context = ContextFor(uri)
                ?? throw new ReferenceRequestException(-32002, "Acquire project context before resolving project references.");
            var captured = buffers.Values.Where(buffer => ReferenceEquals(ContextFor(buffer.Uri), context)
                    && (IsMarkdown(buffer.LanguageId, buffer.Uri) || IsMdx(buffer.LanguageId, buffer.Uri)))
                .Select(buffer => TryReferenceProjectPath(context, UriPath(buffer.Uri), out var projectPath)
                    ? new ReferenceBuffer(buffer.Uri, buffer, projectPath, IsMdx(buffer.LanguageId, buffer.Uri)) : null)
                .Where(static item => item is not null).Select(static item => item!).ToArray();
            if (!captured.Any(item => ReferenceEquals(item.Buffer, source)))
                throw new ReferenceRequestException(-32002, "The document does not have an unambiguous acquired input path.");
            var sourcePath = captured.Single(item => ReferenceEquals(item.Buffer, source)).ProjectPath;
            var scopes = context.Snapshot.Routes.Where(route => route.SourcePath.Replace('\\', '/').Normalize() == sourcePath.Replace('\\', '/').Normalize()
                    || OperatingSystem.IsWindows() && string.Equals(route.SourcePath.Replace('\\', '/').Normalize(), sourcePath.Replace('\\', '/').Normalize(), StringComparison.OrdinalIgnoreCase))
                .Where(route => collection is null || route.Collection == collection && route.Version == version && route.Locale == locale)
                .Select(static route => (route.Collection, route.Version, route.Locale)).Distinct().ToArray();
            if (scopes.Length != 1)
                throw new ReferenceRequestException(-32602, "Choose one acquired source scope using referenceScope {collection, version, locale}: "
                    + string.Join(", ", scopes.Select(static scope => scope.Collection + "/" + scope.Version + "/" + scope.Locale)));
            var selected = context.Snapshot.ForScope(scopes[0].Collection, scopes[0].Version, scopes[0].Locale);
            captured = captured.Where(item => InReferenceScope(selected, item.ProjectPath)).ToArray();
            var semantics = selected.Semantics;
            var scope = new DocumentSemanticScope(selected.Collection, selected.Version, selected.Locale);
            var complete = semantics?.IsComplete == true && (semantics.CompleteScopes is null || semantics.CompleteScopes.Contains(scope));
            return new ReferenceCapture(source, context, clientContextGeneration, captured, complete, selected);
        }
    }

    private static bool InReferenceScope(ProjectInspectionSnapshot snapshot, string path) => snapshot.Routes.Any(route =>
        route.Collection == snapshot.Collection && route.Version == snapshot.Version && route.Locale == snapshot.Locale
        && string.Equals(route.SourcePath.Replace('\\', '/').Normalize(), path.Replace('\\', '/').Normalize(),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    private static bool TryReferenceProjectPath(ProjectContextEntry context, string sourcePath, out string projectPath)
    {
        projectPath = string.Empty;
        if (!Path.IsPathRooted(sourcePath))
            return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedSource = Path.GetFullPath(sourcePath).Replace('\\', '/').Normalize();
        var matches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in context.Snapshot.Routes)
        {
            if (Path.IsPathRooted(route.SourcePath))
            {
                if (string.Equals(Path.GetFullPath(route.SourcePath).Replace('\\', '/').Normalize(), normalizedSource, comparison))
                    matches.Add(route.SourcePath.Replace('\\', '/').Normalize());
                continue;
            }
            string relative;
            try
            {
                relative = ContentInputPolicy.NormalizePath(route.SourcePath);
            }
            catch (ArgumentException)
            {
                continue;
            }
            foreach (var folder in context.Folders)
            {
                var root = UriPath(folder);
                if (!Path.IsPathRooted(root))
                    continue;
                if (string.Equals(Path.GetFullPath(relative, root).Replace('\\', '/').Normalize(), normalizedSource, comparison))
                    matches.Add(relative);
            }
        }
        if (matches.Count != 1)
            return false;
        projectPath = matches.Single();
        return true;
    }

    private void ValidateReferences(ReferenceCapture capture, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clientContextGeneration != capture.Generation || !ReferenceEquals(ContextFor(capture.Source.Uri), capture.Context)
            || capture.Buffers.Any(item => !buffers.TryGetValue(item.Uri, out var current)
                || !ReferenceEquals(current, item.Buffer) || !ReferenceEquals(ContextFor(item.Uri), capture.Context))
            || buffers.Values.Count(buffer => ReferenceEquals(ContextFor(buffer.Uri), capture.Context)
                && (IsMarkdown(buffer.LanguageId, buffer.Uri) || IsMdx(buffer.LanguageId, buffer.Uri))
                && TryReferenceProjectPath(capture.Context, UriPath(buffer.Uri), out var path)
                && InReferenceScope(capture.Snapshot, path)) != capture.Buffers.Count)
            throw new OperationCanceledException(cancellationToken);
    }

    private static DocumentReferenceSnapshot ReferenceSnapshot(ReferenceCapture capture, CancellationToken cancellationToken) =>
        new(capture.Snapshot, capture.Buffers.Select(item =>
            new DocumentReferenceDocument(UriPath(item.Uri), item.Buffer.Text, item.Buffer.Version, item.ProjectPath, item.IsDynamic)),
            capture.CatalogComplete, cancellationToken);

    private ReferenceResponse ReferenceFallback(DocumentBuffer buffer, ProjectContextEntry? context, long generation, int offset,
        bool hover, CancellationToken cancellationToken)
    {
        if (context is null)
            return new(hover ? JsonSerializer.SerializeToElement<object?>(null) : EmptySymbols(), null);
        ReferenceCapture capture;
        try
        {
            capture = CaptureReferences(buffer.Uri);
        }
        catch (ReferenceRequestException)
        {
            return new(hover ? JsonSerializer.SerializeToElement<object?>(null) : EmptySymbols(), null);
        }
        if (!ReferenceEquals(capture.Source, buffer) || !ReferenceEquals(capture.Context, context) || capture.Generation != generation)
            throw new OperationCanceledException(cancellationToken);
        var snapshot = ReferenceSnapshot(capture, cancellationToken);
        var resolution = snapshot.ResolveAt(UriPath(buffer.Uri), offset, cancellationToken);
        JsonElement result;
        if (hover)
        {
            result = resolution.Reference is null ? JsonSerializer.SerializeToElement<object?>(null)
                : JsonSerializer.SerializeToElement(new
                {
                    contents = new
                    {
                        kind = "plaintext",
                        value = resolution.State + (resolution.RelationKind is { } kind ? " (" + kind + ")" : "")
                            + string.Concat(resolution.Candidates.Select(candidate =>
                            "\n" + candidate.Route.Collection + "/" + candidate.Route.Version + "/" + candidate.Route.Locale + ": "
                            + candidate.InputKind + " " + candidate.Route.PublicPath + (candidate.Anchor is null ? "" : "#" + candidate.Anchor))),
                    },
                    range = OffsetRange(buffer.Text, resolution.Reference.Range.Start, resolution.Reference.Range.Length),
                }, JsonOptions);
        }
        else if (resolution.Reference?.TargetSpan is { } span && offset >= span.Start && offset <= span.End)
        {
            var prefix = buffer.Text.Substring(span.Start, offset - span.Start);
            result = JsonSerializer.SerializeToElement(snapshot.Complete(UriPath(buffer.Uri), prefix, cancellationToken).Select(candidate => new
            {
                label = candidate.Label,
                kind = 18,
                detail = candidate.Target.Route.Collection + "/" + candidate.Target.Route.Version + "/" + candidate.Target.Route.Locale
                    + ": " + candidate.Target.InputKind + " " + candidate.Target.Route.PublicPath + (candidate.Target.Anchor is null ? "" : "#" + candidate.Target.Anchor),
                textEdit = new { range = OffsetRange(buffer.Text, span.Start, span.Length), newText = candidate.InsertText },
            }).ToArray(), JsonOptions);
        }
        else
            result = EmptySymbols();
        lock (stateGate)
        {
            ValidateReferences(capture, cancellationToken);
            PublishReferenceStatus(capture, resolution.State.ToString(), snapshot.ManualScopes, resolution.RelationKind?.ToString());
            return new(result, capture);
        }
    }

    private void PublishReferenceStatus(ReferenceCapture capture, string state, IEnumerable<string> manualScopes, string? relationKind = null)
    {
        var remaining = manualScopes.Distinct(StringComparer.Ordinal).ToArray();
        transport.WriteMessage(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "lithosharp/referenceStatus",
            @params = new
            {
                uri = capture.Source.Uri,
                version = capture.Source.Version,
                state,
                relationKind,
                scope = "captured-static-markdown",
                collection = capture.Snapshot.Collection,
                selectedVersion = capture.Snapshot.Version,
                locale = capture.Snapshot.Locale,
                isComplete = remaining.Length == 0,
                manualScopes = remaining,
                unsupportedScopes = new[] { "C# references", "dynamic MDX expressions" },
                csharpPreviewCommand = "lithosharp preview-csharp-rename --source <file.cs> --reference <assembly.dll> --type <metadata-name> --member <name> --new-name <identifier> --format json",
                projectId = capture.Context.ProjectId,
                projectGeneration = capture.Context.Snapshot.ProjectGeneration,
                lithosharpOpenGeneration = capture.Source.OpenGeneration,
                lithosharpContextGeneration = capture.Generation,
            },
        }, JsonOptions));
    }

    private static object[] ReferenceLocations(ReferenceCapture capture, IEnumerable<DocumentReferenceLocation> locations) =>
        locations.Select(location =>
        {
            var buffer = capture.Buffers.FirstOrDefault(item => UriPath(item.Uri) == location.SourcePath);
            return buffer is null ? null : new
            {
                uri = buffer.Uri,
                range = OffsetRange(buffer.Buffer.Text, location.Start, location.Length),
            };
        }).Where(static value => value is not null).Select(static value => (object)value!).ToArray();

    private void ReferenceRequest(string rawId, JsonElement parameters, string method)
    {
        try
        {
            if (parameters.ValueKind != JsonValueKind.Object)
                throw new ReferenceRequestException(-32602, "Reference requests require an object parameter.");
            if (method == "lithosharp/referenceScopes")
            {
                ReferenceScopeRequest(rawId, parameters);
                return;
            }
            var preview = method == "lithosharp/previewRename";
            var move = method == "workspace/willRenameFiles" || preview && parameters.TryGetProperty("newUri", out _);
            string uri;
            string? newUri = null;
            var offset = 0;
            var newName = string.Empty;
            if (method == "workspace/willRenameFiles")
            {
                if (!parameters.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() != 1
                    || files[0].ValueKind != JsonValueKind.Object || !TryString(files[0], "oldUri", out uri)
                    || !TryString(files[0], "newUri", out newUri))
                    throw new ReferenceRequestException(-32602, "Move one Markdown file at a time so overlapping move effects are not guessed.");
                if (!referenceWillRename)
                    throw new ReferenceRequestException(-32602, "The client did not declare willRenameFiles support.");
            }
            else
            {
                if (!TryTextDocument(parameters, out uri))
                    throw new ReferenceRequestException(-32602, "A textDocument.uri is required.");
                if (move && !TryString(parameters, "newUri", out newUri))
                    throw new ReferenceRequestException(-32602, "A document move requires newUri.");
            }
            var requestedScope = Capability(parameters, "referenceScope");
            string? scopeCollection = null, scopeVersion = null, scopeLocale = null;
            if (requestedScope.ValueKind != JsonValueKind.Undefined)
            {
                if (requestedScope.ValueKind != JsonValueKind.Object || !TryString(requestedScope, "collection", out scopeCollection)
                    || !TryString(requestedScope, "version", out scopeVersion) || !TryString(requestedScope, "locale", out scopeLocale)
                    || string.IsNullOrWhiteSpace(scopeCollection) || string.IsNullOrWhiteSpace(scopeVersion) || string.IsNullOrWhiteSpace(scopeLocale))
                    throw new ReferenceRequestException(-32602, "referenceScope requires collection, version and locale strings.");
            }
            var capture = CaptureReferences(uri, scopeCollection, scopeVersion, scopeLocale);
            var expectedVersion = Capability(parameters, "textDocument", "version");
            if (expectedVersion.ValueKind != JsonValueKind.Undefined
                && (expectedVersion.ValueKind != JsonValueKind.Number || !expectedVersion.TryGetInt64(out _)))
                throw new ReferenceRequestException(-32602, "An expected document version must be an integer.");
            if (expectedVersion.ValueKind == JsonValueKind.Number && expectedVersion.GetInt64() != capture.Source.Version)
                throw new ReferenceRequestException(-32801, "The expected source version is no longer current.");
            if (!move)
            {
                if (!TryPosition(parameters, "position", out var position) || !TryOffset(capture.Source.Text, position, out offset))
                    throw new ReferenceRequestException(-32602, "A valid UTF-16 position is required.");
                if (method == "textDocument/rename" || preview)
                    if (!TryString(parameters, "newName", out newName))
                        throw new ReferenceRequestException(-32602, "A rename preview requires newName.");
            }
            if ((method == "textDocument/rename" || method == "workspace/willRenameFiles") && !referenceDocumentChanges)
                throw new ReferenceRequestException(-32602, "Versioned documentChanges support is required; unversioned edits are not returned.");
            var includeDeclaration = Capability(parameters, "context", "includeDeclaration").ValueKind == JsonValueKind.True;
            var renameKind = Capability(parameters, "kind");
            var targetChange = renameKind.ValueKind == JsonValueKind.String && renameKind.GetString() == "target";
            var newPublicPath = Capability(parameters, "newPublicPath");
            if (newPublicPath.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
                throw new ReferenceRequestException(-32602, "newPublicPath must be an explicit public path string.");
            var movedPublicPath = newPublicPath.ValueKind == JsonValueKind.String ? newPublicPath.GetString() : null;
            var movedPath = move ? FileUriPath(newUri!) : null;
            var movedProjectPath = move ? NewReferenceProjectPath(capture, movedPath!) : null;
            var expectedHashValue = Capability(parameters, "expectedSourceTextHash");
            if (expectedHashValue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.String))
                throw new ReferenceRequestException(-32602, "An expected source text hash must be a string.");
            var expectedHash = expectedHashValue.ValueKind == JsonValueKind.String ? expectedHashValue.GetString() : null;
            RunReferenceRequest(rawId, token =>
            {
                var snapshot = ReferenceSnapshot(capture, token);
                var sourcePath = UriPath(capture.Source.Uri);
                var sourceHash = snapshot.Documents.FirstOrDefault(document => document.SourcePath == sourcePath)?.TextHash;
                if (expectedHash is not null && expectedHash != sourceHash)
                    throw new ReferenceRequestException(-32801, "The expected source text differs from the captured buffer.");
                JsonElement response;
                var resolution = snapshot.ResolveAt(sourcePath, offset, token);
                var state = resolution.State.ToString();
                IReadOnlyList<string> manualScopes = snapshot.ManualScopes;
                if (method == "textDocument/definition")
                    response = JsonSerializer.SerializeToElement(ReferenceLocations(capture,
                        resolution.Candidates.Where(static candidate => candidate.Location is not null).Select(static candidate => candidate.Location!)), JsonOptions);
                else if (method == "textDocument/references")
                {
                    var references = snapshot.FindReferences(sourcePath, offset, includeDeclaration, token);
                    state = references.State.ToString();
                    manualScopes = references.ManualScopes;
                    response = JsonSerializer.SerializeToElement(ReferenceLocations(capture, references.Locations), JsonOptions);
                }
                else if (method == "textDocument/prepareRename")
                {
                    var range = resolution.Reference?.Heading?.TextRawSpan ?? resolution.Reference?.TargetSpan;
                    var target = resolution.State == DocumentReferenceState.Resolved && resolution.Candidates.Count == 1
                        ? resolution.Candidates[0] : null;
                    response = target?.Anchor is null || range is null ? JsonSerializer.SerializeToElement<object?>(null)
                        : JsonSerializer.SerializeToElement(new
                        {
                            range = OffsetRange(capture.Source.Text, range.Value.Start, range.Value.Length),
                            placeholder = resolution.Reference?.Heading?.Text.Text ?? target.Anchor,
                        }, JsonOptions);
                }
                else
                {
                    var plan = move ? snapshot.PlanMove(sourcePath, movedPath!,
                            movedPublicPath, movedProjectPath, token)
                        : targetChange ? snapshot.PlanTargetChange(sourcePath, offset, newName, token)
                        : snapshot.PlanHeadingRename(sourcePath, offset, newName, token);
                    manualScopes = plan.ManualScopes;
                    if (move)
                        state = plan.CanApply ? "Resolved" : "Unavailable";
                    lock (stateGate)
                    {
                        ValidateReferences(capture, token);
                        var current = capture.Buffers.Select(item => new DocumentReferenceDocument(UriPath(item.Uri),
                            item.Buffer.Text, item.Buffer.Version, item.ProjectPath, item.IsDynamic)).ToArray();
                        if (plan.CanApply && !plan.TryApply(current, out _))
                            throw new ReferenceRequestException(-32801, "The expected text or document version changed; acquire a fresh preview.");
                        var requiredReview = manualScopes.Distinct(StringComparer.Ordinal).ToArray();
                        if (preview)
                            response = JsonSerializer.SerializeToElement(new
                            {
                                scope = "captured-static-markdown",
                                state,
                                isComplete = plan.IsComplete,
                                markdownScopeComplete = plan.IsComplete,
                                canApply = plan.CanApply && ReferenceClientCanApply(plan, requiredReview),
                                manualScopes = requiredReview,
                                csharpPreviewCommand = "lithosharp preview-csharp-rename",
                                edits = plan.Edits,
                                moves = plan.Moves,
                                projectId = capture.Context.ProjectId,
                                projectGeneration = capture.Context.Snapshot.ProjectGeneration,
                                sourceVersion = capture.Source.Version,
                                sourceTextHash = sourceHash,
                                lithosharpOpenGeneration = capture.Source.OpenGeneration,
                                lithosharpContextGeneration = capture.Generation,
                            }, JsonOptions);
                        else
                        {
                            if (!plan.CanApply)
                                throw new ReferenceRequestException(-32602, "No safe automatic edit is available; inspect lithosharp/previewRename for remaining scope.");
                            if (!ReferenceClientCanApply(plan, requiredReview))
                                throw new ReferenceRequestException(-32602,
                                    "The client must support versioned, atomic text edits and confirmation annotations for this remaining scope. Use lithosharp/previewRename.");
                            response = ReferenceWorkspaceEdit(capture, plan, requiredReview);
                        }
                    }
                }
                lock (stateGate)
                {
                    ValidateReferences(capture, token);
                    PublishReferenceStatus(capture, state, manualScopes, resolution.RelationKind?.ToString());
                    return new ReferenceResponse(response, capture);
                }
            });
        }
        catch (ReferenceRequestException rejected)
        {
            ReplyError(rawId, rejected.Code, rejected.Message);
        }
        catch (ArgumentException invalid)
        {
            ReplyError(rawId, -32602, invalid.Message);
        }
    }

    private void ReferenceScopeRequest(string rawId, JsonElement parameters)
    {
        if (!TryTextDocument(parameters, out var uri))
            throw new ReferenceRequestException(-32602, "A textDocument.uri is required.");
        lock (stateGate)
        {
            if (!buffers.TryGetValue(uri, out var buffer))
                throw new ReferenceRequestException(-32602, "Reference scope selection requires an open document.");
            var context = ContextFor(uri)
                ?? throw new ReferenceRequestException(-32002, "Acquire project context before selecting a reference scope.");
            var available = TryReferenceProjectPath(context, UriPath(uri), out var sourcePath);
            IEnumerable<ProjectRouteCandidate> candidates = available ? context.Snapshot.Routes.Where(route => string.Equals(route.SourcePath.Replace('\\', '/').Normalize(),
                sourcePath.Replace('\\', '/').Normalize(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) : [];
            var scopes = candidates.GroupBy(static route => (route.Collection, route.Version, route.Locale))
                .OrderBy(static group => group.Key.Collection, StringComparer.Ordinal).ThenBy(static group => group.Key.Version, StringComparer.Ordinal)
                .ThenBy(static group => group.Key.Locale, StringComparer.Ordinal)
                .Select(group => new
                {
                    collection = group.Key.Collection,
                    version = group.Key.Version,
                    locale = group.Key.Locale,
                    publicPaths = group.Select(static route => route.PublicPath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                }).ToArray();
            Reply(rawId, JsonSerializer.SerializeToElement(new
            {
                scopes,
                sourceVersion = buffer.Version,
                projectGeneration = context.Snapshot.ProjectGeneration,
                lithosharpOpenGeneration = buffer.OpenGeneration,
                lithosharpContextGeneration = clientContextGeneration,
            }, JsonOptions));
        }
    }

    private bool ReferenceClientCanApply(DocumentReferenceEditPlan plan, IReadOnlyList<string> manualScopes) =>
        referenceDocumentChanges && (plan.Edits.Select(static edit => edit.SourcePath).Distinct(StringComparer.Ordinal).Count() <= 1 || referenceAtomicTextEdits)
            && (manualScopes.Count == 0 || referenceAnnotations);

    private JsonElement ReferenceWorkspaceEdit(ReferenceCapture capture, DocumentReferenceEditPlan plan, IReadOnlyList<string> manualScopes)
    {
        var documents = new JsonArray();
        foreach (var group in plan.Edits.GroupBy(static edit => edit.SourcePath).OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            var current = capture.Buffers.Single(item => UriPath(item.Uri) == group.Key);
            var edits = new JsonArray();
            foreach (var edit in group.OrderBy(static edit => edit.Start))
            {
                var value = new JsonObject
                {
                    ["range"] = JsonSerializer.SerializeToNode(OffsetRange(current.Buffer.Text, edit.Start, edit.Length), JsonOptions),
                    ["newText"] = edit.Replacement,
                };
                if (manualScopes.Count != 0)
                    value["annotationId"] = "lithosharp-static-markdown";
                edits.Add(value);
            }
            documents.Add(new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = current.Uri, ["version"] = current.Buffer.Version },
                ["edits"] = edits,
            });
        }
        var result = new JsonObject { ["documentChanges"] = documents };
        if (manualScopes.Count != 0)
            result["changeAnnotations"] = new JsonObject
            {
                ["lithosharp-static-markdown"] = new JsonObject
                {
                    ["label"] = "Rename captured static Markdown references",
                    ["needsConfirmation"] = true,
                    ["description"] = string.Join("\n", manualScopes),
                },
            };
        return JsonSerializer.SerializeToElement(result, JsonOptions);
    }

    private static string FileUriPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var value) || !value.IsFile || value.Query.Length != 0 || value.Fragment.Length != 0)
            throw new ReferenceRequestException(-32602, "A document move requires a local file URI.");
        return Path.GetFullPath(value.LocalPath).Replace('\\', '/');
    }

    private static string NewReferenceProjectPath(ReferenceCapture capture, string newPath)
    {
        var source = capture.Buffers.Single(item => ReferenceEquals(item.Buffer, capture.Source));
        var sourcePath = Path.GetFullPath(UriPath(source.Uri));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in capture.Context.Folders)
        {
            var root = UriPath(folder);
            if (!Path.IsPathRooted(root) || !string.Equals(Path.GetFullPath(source.ProjectPath, root).Normalize(), sourcePath.Normalize(), comparison))
                continue;
            try
            {
                _ = ContentInputPolicy.NormalizePath(Path.GetRelativePath(root, sourcePath).Replace('\\', '/'));
                var relative = ContentInputPolicy.NormalizePath(Path.GetRelativePath(root, newPath).Replace('\\', '/'));
                candidates.Add(Path.IsPathRooted(source.ProjectPath) ? newPath : relative);
            }
            catch (ArgumentException) { }
        }
        if (candidates.Count != 1)
            throw new ReferenceRequestException(-32602, "The move does not have one contained acquired project destination.");
        return candidates.Single();
    }

    private void RunReferenceRequest(string rawId, Func<CancellationToken, ReferenceResponse> operation)
    {
        var cancellation = new CancellationTokenSource();
        requests[rawId] = cancellation;
        var task = Task.Run(() => operation(cancellation.Token), cancellation.Token);
        background[rawId] = task;
        _ = task.ContinueWith(completed =>
        {
            background.TryRemove(rawId, out _);
            requests.TryRemove(rawId, out _);
            cancellation.Dispose();
            if (completed.IsCanceled)
                ReplyError(rawId, -32800, "Reference request cancelled because its input is no longer current.");
            else if (completed.Exception?.GetBaseException() is ReferenceRequestException rejected)
                ReplyError(rawId, rejected.Code, rejected.Message);
            else if (completed.Exception?.GetBaseException() is ArgumentException invalid)
                ReplyError(rawId, -32602, invalid.Message);
            else if (completed.IsFaulted)
                ReplyError(rawId, -32603, "Reference inspection failed.");
            else
                lock (stateGate)
                {
                    try
                    {
                        if (completed.Result.Capture is { } capture)
                            ValidateReferences(capture, CancellationToken.None);
                        Reply(rawId, completed.Result.Result);
                    }
                    catch (OperationCanceledException)
                    {
                        ReplyError(rawId, -32800, "Reference inputs changed before the result could be published.");
                    }
                }
        }, TaskScheduler.Default);
    }
}
