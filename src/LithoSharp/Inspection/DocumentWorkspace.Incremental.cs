using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using Syntamark.Compilation;

namespace LithoSharp.Inspection;

internal readonly record struct DocumentTextChange(int Start, int Length, string Text);

// LSP changes are sequential: each range belongs to the preceding intermediate buffer.
internal sealed class DocumentChangeSet(long previousVersion, string previousText, IEnumerable<DocumentTextChange> changes)
{
    internal long PreviousVersion { get; } = previousVersion;
    internal string PreviousText { get; } = previousText ?? throw new ArgumentNullException(nameof(previousText));
    internal IReadOnlyList<DocumentTextChange> Changes { get; } = Array.AsReadOnly(changes.ToArray());
}

internal sealed record DocumentIncrementalAnalysis(string Mode, string Fallback, int ReusedBlocks,
    int RebasedBlocks, int ParsedBlocks, long ScanUnits, int OutputItems);

internal sealed record IncrementalInspectionAnalysis(MdAnalysisResult Analysis, DocumentIncrementalAnalysis Metrics);

public sealed partial class DocumentWorkspace
{
    private const int InspectionSessionLimit = 64;
    private const long InspectionSessionByteLimit = 16 * 1024 * 1024;
    private readonly Dictionary<string, LinkedListNode<InspectionSessionEntry>> _inspectionSessions = new(StringComparer.Ordinal);
    private readonly LinkedList<InspectionSessionEntry> _inspectionSessionLru = new();
    private long _inspectionSessionBytes;

    private sealed record InspectionParseState(MdAnalysisSession Session, string RawText, int BodyOffset,
        string SourcePath, long Version, long Generation);
    private sealed record InspectionSessionEntry(string Key, InspectionParseState State, long Bytes);

    internal Task<DocumentInfo> InspectVersionedEditsAsync(string sourcePath, string text, long documentVersion,
        long projectGeneration, DocumentChangeSet changes, DocumentInspectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return InspectCoreAsync(sourcePath, text, documentVersion, projectGeneration, options, cancellationToken, changes);
    }

    private InspectionParseState? PreviousInspectionSession(string key)
    {
        if (!_inspectionSessions.TryGetValue(key, out var node))
            return null;
        _inspectionSessionLru.Remove(node);
        _inspectionSessionLru.AddFirst(node);
        return node.Value.State;
    }

    private void RemoveInspectionSession(string key)
    {
        if (!_inspectionSessions.Remove(key, out var node))
            return;
        _inspectionSessionLru.Remove(node);
        _inspectionSessionBytes -= node.Value.Bytes;
    }

    private void ClearInspectionSessions()
    {
        _inspectionSessions.Clear();
        _inspectionSessionLru.Clear();
        _inspectionSessionBytes = 0;
    }

    private void PublishInspectionSession(string key, InspectionParseState? state, DocumentInfo info)
    {
        RemoveInspectionSession(key);
        if (state is null || state.Session.Analysis.Status != MdParseStatus.Complete
            || state.Session.Analysis.TextHash is null || state.Session.Analysis.Blocks is null
            || info.Diagnostics.Any(diagnostic => diagnostic.Id == MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId))
            return;
        // Logical retention budget, not a heap measurement. Include raw buffers and emitted syntax/fact work.
        var bytes = checked(8L * (state.RawText.Length + state.Session.Analysis.RawText.Length)
            + 256L * state.Session.OutputItems);
        if (bytes > InspectionSessionByteLimit)
            return;
        while (_inspectionSessions.Count >= InspectionSessionLimit || _inspectionSessionBytes + bytes > InspectionSessionByteLimit)
            RemoveInspectionSession(_inspectionSessionLru.Last!.Value.Key);
        var node = _inspectionSessionLru.AddFirst(new InspectionSessionEntry(key, state, bytes));
        _inspectionSessions.Add(key, node);
        _inspectionSessionBytes += bytes;
    }

    private (DocumentInfo Info, InspectionParseState? State) InspectIncrementally(string key, string sourcePath, string raw,
        long? version, long? generation, DocumentInspectionOptions? inspectionOptions, InspectionParseState? previous,
        DocumentChangeSet? explicitChanges, CancellationToken cancellationToken)
    {
        InspectionParseState? next = null;
        var info = DocumentInspection.InspectWithAnalysis(sourcePath, raw, inspectionOptions, Analyze, cancellationToken);
        return (info, next);

        IncrementalInspectionAnalysis Analyze(string body, int bodyOffset, MarkdownOptions options, CancellationToken token)
        {
            options.Validate();
            var nativeOptions = MarkdownAnalysisAdapter.EditorBodyOptionsFor(options);
            var parts = MarkdownAnalysisAdapter.InspectionPartsFor(options);
            var fallback = "InitialParse";
            MdAnalysisSession? session = null;
            if (previous is not null)
            {
                var old = previous.Session.Analysis;
                fallback = previous.BodyOffset != bodyOffset ? "HeaderChanged"
                    : old.OptionsHash != nativeOptions.Hash || old.RequestedParts != parts ? "OptionsChanged"
                    : old.ScopeId != Id || old.SourceId != key || old.SourceVersion is not null
                        || previous.SourcePath != sourcePath ? "IdentityChanged"
                    : old.Status != MdParseStatus.Complete || old.Blocks is null ? "PreviousNotComplete"
                    : "None";
                if (fallback == "None")
                {
                    var changes = explicitChanges?.Changes ?? [Difference(previous.RawText, raw, token)];
                    if (explicitChanges is not null && (explicitChanges.PreviousVersion != previous.Version
                        || explicitChanges.PreviousText != previous.RawText))
                        fallback = "PreviousRevisionUnavailable";
                    else if (changes.Count > 128)
                        fallback = "EditBatchLimit";
                    else
                        session = ApplyBodyChanges(previous, raw, body, changes, nativeOptions, token, out fallback);
                }
            }
            else if (explicitChanges is not null)
                fallback = "PreviousRevisionUnavailable";
            session ??= MdParseSession.CreateForAnalysis(body, parts, Id, key, null, nativeOptions, token);
            next = new InspectionParseState(session, raw, bodyOffset, sourcePath,
                version ?? long.MaxValue, generation ?? long.MaxValue);
            var native = session.Analysis;
            if (native.RawText != body || native.OptionsHash != nativeOptions.Hash || native.RequestedParts != parts
                || native.ScopeId != Id || native.SourceId != key || native.SourceVersion is not null)
                throw new InvalidOperationException("Incremental inspection returned a different analysis request.");
            var metrics = new DocumentIncrementalAnalysis(session.Mode.ToString(),
                fallback == "None" ? session.Fallback.ToString() : fallback,
                session.ReusedBlocks, session.RebasedBlocks, session.ParsedBlocks, session.ScanUnits, session.OutputItems);
            return new IncrementalInspectionAnalysis(native, metrics);
        }
    }

    private static MdAnalysisSession? ApplyBodyChanges(InspectionParseState previous, string expectedRaw, string expectedBody,
        IReadOnlyList<DocumentTextChange> changes, MdOptions options, CancellationToken cancellationToken, out string fallback)
    {
        var raw = previous.RawText;
        var session = previous.Session;
        fallback = "None";
        if (changes.Count == 0)
            session = session.ApplyEdits(session.Analysis.TextHash, [], null, options, cancellationToken);
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.Start < previous.BodyOffset)
            {
                fallback = "HeaderChanged";
                return null;
            }
            if (change.Text is null || change.Length < 0
                || change.Start > raw.Length || change.Length > raw.Length - change.Start
                || !ScalarBoundary(raw, change.Start) || !ScalarBoundary(raw, change.Start + change.Length))
            {
                fallback = "UnsupportedEdit";
                return null;
            }
            raw = string.Concat(raw.AsSpan(0, change.Start), change.Text, raw.AsSpan(change.Start + change.Length));
            session = session.ApplyEdits(session.Analysis.TextHash,
                [new MdTextEdit(new MdRawRange(change.Start - previous.BodyOffset, change.Length), change.Text)],
                null, options, cancellationToken);
        }
        if (raw != expectedRaw || session.Analysis.RawText != expectedBody)
        {
            fallback = "EditTextMismatch";
            return null;
        }
        return session;
    }

    private static bool ScalarBoundary(string text, int offset) => offset == 0 || offset == text.Length
        || !char.IsHighSurrogate(text[offset - 1]) || !char.IsLowSurrogate(text[offset]);

    private static DocumentTextChange Difference(string previous, string current, CancellationToken cancellationToken)
    {
        var prefix = 0;
        while (prefix < previous.Length && prefix < current.Length && previous[prefix] == current[prefix])
        {
            if ((prefix & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            prefix++;
        }
        while (!ScalarBoundary(previous, prefix) || !ScalarBoundary(current, prefix))
            prefix--;
        var suffix = 0;
        while (suffix < previous.Length - prefix && suffix < current.Length - prefix
            && previous[previous.Length - suffix - 1] == current[current.Length - suffix - 1])
        {
            if ((suffix & 127) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            suffix++;
        }
        while (!ScalarBoundary(previous, previous.Length - suffix) || !ScalarBoundary(current, current.Length - suffix))
            suffix--;
        return new DocumentTextChange(prefix, previous.Length - prefix - suffix,
            current.Substring(prefix, current.Length - prefix - suffix));
    }
}
