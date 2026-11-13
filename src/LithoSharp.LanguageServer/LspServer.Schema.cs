using System.Text.Json;
using LithoSharp.Inspection;
using Syntamark;
using Syntamark.Compilation;

namespace LithoSharp.LanguageServer;

public sealed partial class LspServer
{
    private void SchemaRequest(string rawId, JsonElement parameters, bool hover)
    {
        if (!TryTextDocument(parameters, out var uri) || !TryPosition(parameters, "position", out var position))
        {
            ReplyError(rawId, -32602, "A schema request requires textDocument.uri and a UTF-16 position.");
            return;
        }
        DocumentBuffer buffer;
        ProjectContextEntry? context;
        long generation;
        lock (stateGate)
        {
            if (!buffers.TryGetValue(uri, out buffer!) || !IsMarkdown(buffer.LanguageId, uri)
                || !TryOffset(buffer.Text, position, out _))
            {
                Reply(rawId, hover ? JsonSerializer.SerializeToElement<object?>(null) : EmptySymbols());
                return;
            }
            context = ContextFor(uri);
            generation = clientContextGeneration;
        }
        var cancellation = new CancellationTokenSource();
        requests[rawId] = cancellation;
        var task = Task.Run(async () =>
        {
            var info = await InspectionForAsync(buffer, context, cancellation.Token).ConfigureAwait(false);
            JsonElement schema;
            int offset;
            lock (stateGate)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!buffers.TryGetValue(uri, out var latest) || !ReferenceEquals(latest, buffer)
                    || !ReferenceEquals(ContextFor(uri), context) || clientContextGeneration != generation)
                    throw new OperationCanceledException(cancellation.Token);
                _ = TryOffset(buffer.Text, position, out offset);
                schema = SchemaResult(info, buffer.Text, offset, hover);
            }
            return hover ? schema.ValueKind != JsonValueKind.Null ? new ReferenceResponse(schema, null)
                    : ReferenceFallback(buffer, context, generation, offset, true, cancellation.Token)
                : schema.ValueKind == JsonValueKind.Array && schema.GetArrayLength() != 0 ? new ReferenceResponse(schema, null)
                    : ReferenceFallback(buffer, context, generation, offset, false, cancellation.Token);
        }, cancellation.Token);
        background[rawId] = task;
        _ = task.ContinueWith(completed =>
        {
            background.TryRemove(rawId, out _);
            requests.TryRemove(rawId, out _);
            cancellation.Dispose();
            if (completed.IsCanceled)
                ReplyError(rawId, -32800, "Request cancelled.");
            else if (completed.IsFaulted)
                ReplyError(rawId, -32603, "Static schema inspection failed.");
            else
                lock (stateGate)
                {
                    try
                    {
                        if (!buffers.TryGetValue(uri, out var latest) || !ReferenceEquals(latest, buffer)
                            || !ReferenceEquals(ContextFor(uri), context) || clientContextGeneration != generation)
                            throw new OperationCanceledException();
                        if (completed.Result.Capture is { } capture)
                            ValidateReferences(capture, CancellationToken.None);
                        Reply(rawId, completed.Result.Result);
                    }
                    catch (OperationCanceledException)
                    {
                        ReplyError(rawId, -32800, "Editor inputs changed before the result could be published.");
                    }
                }
        }, TaskScheduler.Default);
    }

    private sealed record SchemaCursor(JsonElement Scope, YamlNode? Mapping, string? Key, bool IsValue, RawSpan? Span, JsonElement? ScalarSchema = null);

    private static JsonElement SchemaResult(DocumentInfo info, string text, int offset, bool hover)
    {
        if (info.MarkdownComponents.Count != 0)
        {
            if (hover && MarkdownComponentEditing.Hover(info, offset, info.MarkdownComponents) is { } component)
                return JsonSerializer.SerializeToElement(new
                {
                    contents = new { kind = "plaintext", value = component.Contents },
                    range = OffsetRange(text, component.Start, component.Length),
                }, JsonOptions);
            if (!hover && MarkdownComponentEditing.Complete(info, offset, info.MarkdownComponents) is { Count: > 0 } candidates)
                return JsonSerializer.SerializeToElement(candidates.Select(candidate => new
                {
                    label = candidate.Label,
                    kind = 10,
                    detail = candidate.Detail,
                    textEdit = new { range = OffsetRange(text, candidate.Start, candidate.Length), newText = candidate.InsertText },
                    deprecated = candidate.Deprecated,
                }).ToArray(), JsonOptions);
        }
        var snapshot = info.StaticSchema;
        var syntax = info.FrontMatterSyntax;
        var position = offset - info.FrontMatterStartOffset;
        if (snapshot is null || syntax is null || position < 0 || position >= syntax.RawText.Length)
            return hover ? JsonSerializer.SerializeToElement<object?>(null) : EmptySymbols();
        var schema = snapshot.Schema;
        var cursor = FindCursor(schema, schema.Root, syntax.Root, position);
        var scope = schema.Resolve(cursor.Scope);
        if (!scope.TryGetProperty("properties", out var properties))
            return hover ? JsonSerializer.SerializeToElement<object?>(null) : EmptySymbols();
        var fields = schema.Describe(cursor.Scope);
        var field = fields.FirstOrDefault(item => item.Key == cursor.Key);
        if (hover)
        {
            if (field is null)
                return JsonSerializer.SerializeToElement<object?>(null);
            return JsonSerializer.SerializeToElement(new
            {
                contents = new { kind = "plaintext", value = DescribeField(field) },
                range = cursor.Span is { } span ? OffsetRange(text, info.FrontMatterStartOffset + span.Start, span.Length) : null,
            }, JsonOptions);
        }
        if (cursor.IsValue)
        {
            if (field is null)
                return EmptySymbols();
            var valueSchema = cursor.ScalarSchema ?? properties.GetProperty(field.Key);
            var scalar = schema.Resolve(valueSchema);
            IReadOnlyList<string> values = scalar.TryGetProperty("x-lithosharp-enumNames", out var names)
                ? names.EnumerateArray().Select(name => name.GetString()!).ToArray()
                : GeneratedFrontMatterSchema.Text(scalar, "type") == "boolean" ? ["true", "false"] : [];
            if (GeneratedFrontMatterSchema.AllowsNull(valueSchema))
                values = values.Concat(["null"]).ToArray();
            var span = cursor.Span ?? new RawSpan(position, 0);
            return JsonSerializer.SerializeToElement(values.Select(value => new
            {
                label = value,
                kind = 12,
                detail = "Static value for " + field.Key,
                documentation = new { kind = "plaintext", value = DescribeField(field) },
                textEdit = new { range = OffsetRange(text, info.FrontMatterStartOffset + span.Start, span.Length), newText = value },
            }).ToArray(), JsonOptions);
        }
        var present = cursor.Mapping?.Entries.Select(entry => entry.Key.Value).ToHashSet(StringComparer.Ordinal) ?? [];
        if (cursor.Key is null && Indent(syntax.RawText, position) >
            (cursor.Mapping?.Entries.FirstOrDefault() is { } first ? Indent(syntax.RawText, first.Key.RawSpan.Start) : 0))
            return EmptySymbols();
        var target = cursor.Span ?? WordAt(syntax.RawText, position);
        return JsonSerializer.SerializeToElement(fields.Where(item => !present.Contains(item.Key) || item.Key == cursor.Key).Select(item => new
        {
            label = item.Key,
            kind = 10,
            detail = item.Type + (item.Required ? " (required)" : ""),
            documentation = new { kind = "plaintext", value = DescribeField(item) },
            textEdit = new
            {
                range = OffsetRange(text, info.FrontMatterStartOffset + target.Start, target.Length),
                newText = QuoteKey(item.Key) + (cursor.Key is null ? ": " : ""),
            },
        }).ToArray(), JsonOptions);
    }

    private static SchemaCursor FindCursor(GeneratedFrontMatterSchema schema, JsonElement declared, YamlNode? node, int offset)
    {
        var value = schema.Resolve(declared);
        if (node?.Kind == MarkdownYamlKind.Mapping && value.TryGetProperty("properties", out var properties))
        {
            foreach (var entry in node.Entries)
            {
                var key = entry.Key.Value;
                if (offset >= entry.Key.RawSpan.Start && offset <= entry.Key.RawSpan.End)
                    return new SchemaCursor(declared, node, key, false, entry.Key.RawSpan);
                if (offset > entry.Key.RawSpan.End && offset <= entry.Value.RawSpan.End
                    && key is not null && properties.TryGetProperty(key, out var field))
                {
                    var target = schema.Resolve(field);
                    if (entry.Value.Kind == MarkdownYamlKind.Mapping)
                        return FindCursor(schema, field, entry.Value, offset);
                    if (entry.Value.Kind == MarkdownYamlKind.Sequence && target.TryGetProperty("items", out var item))
                        foreach (var child in entry.Value.Children)
                            if (offset >= child.RawSpan.Start && offset <= child.RawSpan.End)
                                return child.Kind == MarkdownYamlKind.Mapping ? FindCursor(schema, item, child, offset)
                                    : new SchemaCursor(declared, node, key, true, child.RawSpan, item);
                    return new SchemaCursor(declared, node, key, true, entry.Value.RawSpan, field);
                }
            }
        }
        return new SchemaCursor(declared, node, null, false, null);
    }

    private static string DescribeField(FrontMatterFieldInfo field) =>
        field.Key + ": " + field.Type + (field.Required ? "; required" : "; optional")
        + (field.AllowsNull ? "; nullable" : "")
        + (field.Description is { } description ? "\n" + description : "")
        + (field.EnumValues.Count > 0 ? "\nValues: " + string.Join(", ", field.EnumValues) : "")
        + "\nConstructors, setters and dynamic defaults require a build.";

    private static object OffsetRange(string text, int start, int length)
    {
        if (start < 0 || length < 0 || start > text.Length || length > text.Length - start)
            throw new ArgumentOutOfRangeException(nameof(start));
        var starts = LineStarts(text);
        object Position(int offset)
        {
            var line = starts.BinarySearch(offset);
            if (line < 0)
                line = ~line - 1;
            return new { line, character = offset - starts[line] };
        }
        return new
        {
            start = Position(start),
            end = Position(start + length),
        };
    }

    private static RawSpan WordAt(string text, int offset)
    {
        var start = offset;
        var end = offset;
        while (start > 0 && Word(text[start - 1]))
            start--;
        while (end < text.Length && Word(text[end]))
            end++;
        return new RawSpan(start, end - start);
    }

    private static bool Word(char value) => char.IsLetterOrDigit(value) || value is '_' or '-';

    private static int Indent(string text, int offset)
    {
        var start = offset;
        while (start > 0 && text[start - 1] is not ('\r' or '\n'))
            start--;
        var end = start;
        while (end < text.Length && text[end] is ' ' or '\t')
            end++;
        return end - start;
    }
    private static string QuoteKey(string key) => key.Length > 0 && key.All(Word) ? key : "'" + key.Replace("'", "''", StringComparison.Ordinal) + "'";
}
