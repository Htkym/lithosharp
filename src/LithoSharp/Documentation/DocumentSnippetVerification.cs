using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Xml;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using Syntamark.Compilation;

namespace LithoSharp.Documentation;

/// <summary>An explicit mapping from a fenced snippet="id" to tests in a trusted project.</summary>
public sealed class DocumentSnippet
{
    public DocumentSnippet(string id, string documentPath, string projectPath, string? testFilter = null, string? skipReason = null)
    {
        Id = new ContentEntryId(id).Value;
        if (Id.Contains('"'))
            throw new ArgumentException("A snippet ID must be representable as one quoted fence attribute.", nameof(id));
        DocumentPath = ContentDependency.FromFile(documentPath).PhysicalFilePath;
        ProjectPath = ContentDependency.FromFile(projectPath).PhysicalFilePath;
        if (!ProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A snippet must select an explicit trusted .csproj.", nameof(projectPath));
        if (testFilter is not null && string.IsNullOrWhiteSpace(testFilter))
            throw new ArgumentException("A test filter must not be empty.", nameof(testFilter));
        if (skipReason is not null && string.IsNullOrWhiteSpace(skipReason))
            throw new ArgumentException("Skipping a snippet requires a reason.", nameof(skipReason));
        TestFilter = testFilter;
        SkipReason = skipReason;
    }

    public string Id { get; }
    public string DocumentPath { get; }
    public string ProjectPath { get; }
    /// <summary>The optional TUnit tree-node filter passed as one argument.</summary>
    public string? TestFilter { get; }
    public string? SkipReason { get; }
}

public enum DocumentSnippetVerificationState
{
    Success,
    Failure,
    NotRun,
    Skipped,
    OutOfScope,
}

/// <summary>One explicit execution or inspection; a previous success is never implicitly promoted to a new run.</summary>
public sealed class DocumentSnippetVerificationResult
{
    internal DocumentSnippetVerificationResult(DocumentSnippet snippet, string? fingerprint,
        DocumentSnippetVerificationState state, SiteSourceLocation? location,
        IReadOnlyList<SiteSourceLocation> relatedLocations, string message, DateTimeOffset? executedAt = null)
    {
        Snippet = snippet;
        Fingerprint = fingerprint;
        State = state;
        Location = location;
        ExecutedAt = executedAt;
        RelatedLocations = Array.AsReadOnly(relatedLocations.ToArray());
        Message = message;
        if (state != DocumentSnippetVerificationState.Success)
            Diagnostic = new SiteDiagnostic("LSDOC301", state == DocumentSnippetVerificationState.Failure
                ? SiteDiagnosticSeverity.Error : SiteDiagnosticSeverity.Info, message, location,
                "snippet-verification", RelatedLocations);
    }

    public DocumentSnippet Snippet { get; }
    public string? Fingerprint { get; }
    public DocumentSnippetVerificationState State { get; }
    public SiteSourceLocation? Location { get; }
    public IReadOnlyList<SiteSourceLocation> RelatedLocations { get; }
    public string Message { get; }
    public DateTimeOffset? ExecutedAt { get; }
    public SiteDiagnostic? Diagnostic { get; }
}

public static partial class DocumentationVerification
{
    private static readonly UTF8Encoding SnippetUtf8 = new(false, true);

    /// <summary>Captures current inputs without executing projects; previous results only explain invalidation.</summary>
    /// <remarks>Declare all reference, package-lock, SDK configuration, source and test inputs in validationInputs. Asset dependencies are not supported. The effective SDK version is captured separately.</remarks>
    public static Task<IReadOnlyList<DocumentSnippetVerificationResult>> InspectSnippetsAsync(
        string trustedRoot, IEnumerable<DocumentSnippet> snippets, IEnumerable<ContentDependency> validationInputs,
        string configuration, IEnumerable<DocumentSnippetVerificationResult>? previousResults = null,
        CancellationToken cancellationToken = default) =>
        ProcessSnippetsAsync(trustedRoot, snippets, validationInputs, configuration, false, previousResults, cancellationToken);

    /// <summary>Runs only the explicitly mapped projects/tests with no dependency restore and no implicit time limit.</summary>
    /// <remarks>Call only for a trusted root and trusted projects. Success requires flat TRX execution records for passed tests with no skipped, failed or unavailable results. Every declared file is captured before execution and verified again before success is recorded. This does not execute Markdown code or set OKF trust metadata.</remarks>
    public static Task<IReadOnlyList<DocumentSnippetVerificationResult>> VerifySnippetsAsync(
        string trustedRoot, IEnumerable<DocumentSnippet> snippets, IEnumerable<ContentDependency> validationInputs,
        string configuration, CancellationToken cancellationToken = default) =>
        ProcessSnippetsAsync(trustedRoot, snippets, validationInputs, configuration, true, null, cancellationToken);

    private sealed record SnippetCapture(DocumentSnippet Snippet, string Fingerprint,
        SiteSourceLocation Location, IReadOnlyList<SiteSourceLocation> RelatedLocations);

    private static async Task<IReadOnlyList<DocumentSnippetVerificationResult>> ProcessSnippetsAsync(
        string trustedRoot, IEnumerable<DocumentSnippet> snippets, IEnumerable<ContentDependency> validationInputs,
        string configuration, bool execute, IEnumerable<DocumentSnippetVerificationResult>? previousResults,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedRoot);
        ArgumentNullException.ThrowIfNull(snippets);
        ArgumentNullException.ThrowIfNull(validationInputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        var root = Path.GetFullPath(trustedRoot);
        var declarations = snippets.ToArray();
        if (declarations.Any(static snippet => snippet is null))
            throw new ArgumentException("Snippet declarations must not contain null.", nameof(snippets));
        if (declarations.GroupBy(static snippet => (snippet.DocumentPath.Normalize(NormalizationForm.FormC), snippet.Id)).Any(static group => group.Count() > 1))
            throw new ArgumentException("A document's snippet IDs must be unique.", nameof(snippets));
        var inputs = ContentDependency.Snapshot(validationInputs);
        if (inputs.Any(static input => input.Kind == ContentDependencyKind.Asset))
            throw new ArgumentException("Snippet verification requires explicit file/value dependencies, not an asset ID.", nameof(validationInputs));
        var previousSnapshot = previousResults?.ToArray() ?? [];
        if (previousSnapshot.Any(static result => result is null))
            throw new ArgumentException("Previous results must not contain null.", nameof(previousResults));
        var previous = previousSnapshot.ToDictionary(static result => (result.Snippet.DocumentPath, result.Snippet.Id));
        var results = new List<DocumentSnippetVerificationResult>();
        var captures = new List<SnippetCapture>();
        foreach (var snippet in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var captured = await CaptureSnippetAsync(root, snippet, inputs, configuration, cancellationToken).ConfigureAwait(false);
                if (captured is null)
                {
                    results.Add(new(snippet, null, DocumentSnippetVerificationState.OutOfScope,
                        new SiteSourceLocation(snippet.DocumentPath), [],
                        $"Snippet '{snippet.Id}' is not uniquely identified by a closed parsed fence; no tests were run."));
                    continue;
                }
                if (snippet.SkipReason is not null)
                    results.Add(Result(captured, DocumentSnippetVerificationState.Skipped, snippet.SkipReason));
                else if (!execute)
                {
                    var changed = previous.TryGetValue((snippet.DocumentPath, snippet.Id), out var prior)
                        && prior.Fingerprint != captured.Fingerprint;
                    results.Add(Result(captured, DocumentSnippetVerificationState.NotRun,
                        changed ? "Verification inputs changed; the previous execution no longer verifies this snippet."
                            : "No tests were executed for this inspection."));
                }
                else
                    captures.Add(captured);
            }
            catch (Exception exception) when (SnippetCaptureError(exception))
            {
                results.Add(new(snippet, null, DocumentSnippetVerificationState.OutOfScope,
                    new SiteSourceLocation(snippet.DocumentPath), [],
                    $"Snippet '{snippet.Id}' could not be captured; no tests were run: {exception.Message}"));
            }
        }
        foreach (var group in captures.GroupBy(static capture => (capture.Snippet.ProjectPath, capture.Snippet.TestFilter)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var evidence = new SnippetExecutionEvidence();
            var path = Path.GetFullPath(group.Key.ProjectPath, root);
            var arguments = new List<string> { "test", "--project", path, "--no-restore", "--configuration", configuration,
                "--report-trx", "--results-directory", evidence.Directory };
            if (group.Key.TestFilter is { } filter)
                arguments.AddRange(["--treenode-filter", filter]);
            CommandResult execution;
            try
            {
                execution = await RunCommandAsync(path, arguments.ToArray(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (SnippetCaptureError(exception))
            {
                foreach (var captured in group)
                    results.Add(Result(captured, DocumentSnippetVerificationState.Failure,
                        "The explicitly selected test command could not run: " + exception.Message));
                continue;
            }
            var summary = InspectSnippetExecutionReports(evidence.Directory, execution.ExitCode, cancellationToken);
            var executedAt = DateTimeOffset.UtcNow;
            foreach (var captured in group)
            {
                try
                {
                    var after = await CaptureSnippetAsync(root, captured.Snippet, inputs, configuration, cancellationToken).ConfigureAwait(false);
                    if (after is null || captured.Fingerprint != after.Fingerprint)
                    {
                        results.Add(Result(captured, DocumentSnippetVerificationState.NotRun,
                            "Inputs changed during verification; its result does not verify the current snippet.", executedAt));
                        continue;
                    }
                    results.Add(Result(captured, summary.State, summary.Message
                        + (execution.ExitCode == 0 ? string.Empty : $"\n{execution.Error}\n{execution.Output}"), executedAt));
                }
                catch (Exception exception) when (SnippetCaptureError(exception))
                {
                    results.Add(Result(captured, DocumentSnippetVerificationState.NotRun,
                        "Current inputs could not be verified after execution: " + exception.Message, executedAt));
                }
            }
        }
        // A later test group can change inputs used by an earlier group.
        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            if (result.State != DocumentSnippetVerificationState.Success)
                continue;
            var captured = captures.Single(capture => ReferenceEquals(capture.Snippet, result.Snippet));
            try
            {
                var current = await CaptureSnippetAsync(root, result.Snippet, inputs, configuration, cancellationToken).ConfigureAwait(false);
                if (current is null || current.Fingerprint != result.Fingerprint)
                    results[index] = Result(captured, DocumentSnippetVerificationState.NotRun,
                        "Later verification changed these inputs; the earlier execution no longer verifies the current snippet.", result.ExecutedAt);
            }
            catch (Exception exception) when (SnippetCaptureError(exception))
            {
                results[index] = Result(captured, DocumentSnippetVerificationState.NotRun,
                    "Current inputs could not be verified before publication: " + exception.Message, result.ExecutedAt);
            }
        }
        return Array.AsReadOnly(results.OrderBy(result => Array.IndexOf(declarations, result.Snippet)).ToArray());
    }

    private static DocumentSnippetVerificationResult Result(SnippetCapture capture, DocumentSnippetVerificationState state,
        string message, DateTimeOffset? executedAt = null) =>
        new(capture.Snippet, capture.Fingerprint, state, capture.Location, capture.RelatedLocations, message, executedAt);

    internal sealed record SnippetExecutionSummary(DocumentSnippetVerificationState State, string Message);

    internal static SnippetExecutionSummary InspectSnippetExecutionReports(string directory, int exitCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // MTP can use exit 8 for a completely skipped selection. TRX must distinguish it from no matches.
        if (exitCode is not (0 or 8))
            return new(DocumentSnippetVerificationState.Failure, $"The explicitly selected test command failed (exit {exitCode}).");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = Directory.EnumerateFiles(directory, "*.trx", SearchOption.TopDirectoryOnly).Take(65).ToArray();
            if (files.Length == 0 || files.Length > 64)
                return Unavailable("TRX execution reports are missing or exceed the supported report-count limit.");
            long passed = 0;
            long skipped = 0;
            var executions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(directory, file);
                if (stream.Length > 16 * 1024 * 1024)
                    return Unavailable("TRX execution evidence exceeds its reading limit.");
                using var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = 16 * 1024 * 1024,
                    IgnoreComments = true,
                    IgnoreProcessingInstructions = true,
                });
                const string trxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                reader.MoveToContent();
                if (reader.LocalName != "TestRun" || reader.NamespaceURI != trxNamespace)
                    return Unavailable("The test report is not a supported TRX execution report.");
                long records = 0, recordPassed = 0, recordSkipped = 0;
                long total = -1, executed = -1, countedPassed = -1, countedSkipped = -1;
                var counters = 0;
                var summaries = 0;
                var summaryOutcome = string.Empty;
                var section = string.Empty;
                var failed = false;
                var unknown = false;
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != trxNamespace)
                        continue;
                    if (reader.Depth == 1)
                        section = reader.LocalName;
                    if (reader.LocalName == "UnitTestResult")
                    {
                        if (reader.Depth != 2 || section != "Results" || reader.GetAttribute("executionId") is not { Length: > 0 } id
                            || !executions.Add(id))
                            return Unavailable("TRX execution records are nested, duplicated or lack execution identities.");
                        records++;
                        switch (reader.GetAttribute("outcome"))
                        {
                            case "Passed":
                                recordPassed++;
                                break;
                            case "NotExecuted":
                                recordSkipped++;
                                break;
                            case "Failed" or "Error" or "Timeout" or "Aborted" or "NotRunnable" or "Disconnected" or "PassedButRunAborted":
                                failed = true;
                                break;
                            default:
                                unknown = true;
                                break;
                        }
                    }
                    else if (reader.LocalName == "ResultSummary" && reader.Depth == 1)
                    {
                        summaries++;
                        summaryOutcome = reader.GetAttribute("outcome") ?? string.Empty;
                    }
                    else if (reader.LocalName == "Counters" && reader.Depth == 2)
                    {
                        if (section != "ResultSummary")
                            return Unavailable("TRX counters are outside their result summary.");
                        counters++;
                        total = Counter("total");
                        executed = Counter("executed");
                        countedPassed = Counter("passed");
                        countedSkipped = Counter("notExecuted");
                        foreach (var name in new[] { "failed", "error", "timeout", "aborted", "notRunnable", "disconnected", "passedButRunAborted" })
                        {
                            var count = OptionalCounter(name);
                            failed |= count > 0;
                            unknown |= count < 0;
                        }
                        foreach (var name in new[] { "inconclusive", "warning", "completed", "inProgress", "pending" })
                            unknown |= OptionalCounter(name) != 0;
                    }
                }
                if (failed || summaryOutcome is "Failed" or "Error" or "Timeout" or "Aborted" or "NotRunnable" or "Disconnected")
                    return new(DocumentSnippetVerificationState.Failure, "The TRX execution evidence records failed tests or an aborted run.");
                if (unknown || summaries != 1 || counters != 1 || summaryOutcome is not ("Completed" or "Passed" or "NotExecuted")
                    || summaryOutcome == "NotExecuted" && recordPassed != 0
                    || total != records || executed < 0 || executed > total || countedPassed > executed
                    || recordPassed != countedPassed || recordSkipped != countedSkipped
                    || recordPassed + recordSkipped != records || executed != recordPassed)
                    return Unavailable("TRX execution evidence is incomplete or its results and counters disagree.");
                if (records == 0)
                    return Unavailable("No selected tests were executed in a reported test run; the snippet was not verified.");
                passed = checked(passed + recordPassed);
                skipped = checked(skipped + recordSkipped);

                long Counter(string name) => long.TryParse(reader.GetAttribute(name), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var value) ? value : -1;
                long OptionalCounter(string name) => reader.GetAttribute(name) is null ? 0 : Counter(name);
            }
            if (skipped > 0)
                return new(DocumentSnippetVerificationState.Skipped, passed == 0
                    ? "All explicitly selected tests were skipped; the snippet was not verified."
                    : "Some explicitly selected tests were skipped; the selection did not fully verify the snippet.");
            if (exitCode != 0)
                return Unavailable("The runner reported no executed tests; passed records alone do not verify this run.");
            return passed > 0
                ? new(DocumentSnippetVerificationState.Success, $"TRX evidence confirms {passed} explicitly selected tests passed for these exact declared inputs.")
                : Unavailable("No selected tests were executed; the snippet was not verified.");
        }
        catch (Exception exception) when (SnippetCaptureError(exception) || exception is XmlException or OverflowException)
        {
            return Unavailable("TRX execution evidence could not be verified: " + exception.Message);
        }

        static SnippetExecutionSummary Unavailable(string message) => new(DocumentSnippetVerificationState.NotRun, message);
    }

    private sealed class SnippetExecutionEvidence : IDisposable
    {
        internal string Directory { get; } = SiteGenerator.CreateTemporaryDirectory("ds-");

        public void Dispose()
        {
            try
            {
                DeleteOwned(Directory);
            }
            catch (Exception exception) when (SnippetCaptureError(exception))
            {
                // Cleanup must not replace cancellation or the captured verification result.
            }
        }

        private void DeleteOwned(string path)
        {
            SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(Directory)!, path);
            var relative = Path.GetRelativePath(Directory, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("Snippet evidence cleanup escaped its owned directory.");
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                        System.IO.Directory.Delete(entry, recursive: false);
                    else
                        File.Delete(entry);
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                    DeleteOwned(entry);
                else
                    File.Delete(entry);
            }
            System.IO.Directory.Delete(path, recursive: false);
        }
    }

    private static bool SnippetCaptureError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception;

    private static async Task<SnippetCapture?> CaptureSnippetAsync(string root, DocumentSnippet snippet,
        IReadOnlyList<ContentDependency> inputs, string configuration, CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(snippet.DocumentPath, root);
        var original = await ContentPath.ReadAllBytesAsync(root, path, cancellationToken).ConfigureAwait(false);
        var raw = SnippetUtf8.GetString(original);
        var analysis = MdParser.Analyze(raw, MdAnalysisParts.Fences, "snippet-verification", snippet.DocumentPath,
            null, new MdOptions(MdFrontMatterRecovery.TreatAsBody), cancellationToken);
        if (analysis.Status != MdParseStatus.Complete || analysis.Fences is null)
            return null;
        var fences = analysis.Fences.Where(fence => SnippetId().Matches(fence.Info ?? string.Empty)
            .Select(match => match.Groups[1].Value.Normalize(NormalizationForm.FormC)).Contains(snippet.Id, StringComparer.Ordinal)).ToArray();
        if (fences.Length != 1 || !fences[0].Closed || SnippetId().Matches(fences[0].Info ?? string.Empty).Count != 1)
            return null;
        var fence = fences[0];
        var source = new SourceText(raw);
        SiteSourceLocation Locate(int start, int length)
        {
            var from = source.GetLineAndColumn(start);
            var to = source.GetLineAndColumn(checked(start + length));
            return new SiteSourceLocation(snippet.DocumentPath, from.Line, from.Column, to.Line, to.Column);
        }
        var location = Locate(fence.RawSpan.Start, fence.RawSpan.Length);
        var bodyStart = analysis.BodySpan?.Start ?? 0;
        var inclusion = await MarkdownCodeInclusion.ResolveAsync(raw, raw[bodyStart..], Path.GetDirectoryName(path)!, root,
            snippet.DocumentPath, cancellationToken).ConfigureAwait(false);
        if (inclusion.Diagnostics.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error))
            throw new InvalidOperationException(string.Join("\n", inclusion.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var selectedInclude = inclusion.Resolved.SingleOrDefault(item => bodyStart + item.SpanStart == fence.RawSpan.Start);
        var related = new List<SiteSourceLocation>();
        if (selectedInclude is not null)
        {
            var index = inclusion.Resolved.ToList().IndexOf(selectedInclude);
            if (inclusion.SourceMap is not null && index < inclusion.Origins.Count)
            {
                foreach (var origin in inclusion.SourceMap.Map(inclusion.Origins[index].OutputSpan, cancellationToken).Origins)
                {
                    if (origin.Source.SourceId != selectedInclude.TargetRelativePath)
                        continue;
                    var targetSource = new SourceText(origin.Source.Text);
                    var from = targetSource.GetLineAndColumn(origin.Span.Start);
                    var to = targetSource.GetLineAndColumn(origin.Span.End);
                    related.Add(new SiteSourceLocation(origin.Source.SourceId, from.Line, from.Column, to.Line, to.Column));
                }
            }
            if (related.Count == 0)
                related.Add(new SiteSourceLocation(selectedInclude.TargetRelativePath));
        }
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["document/" + snippet.DocumentPath] = Hash(original),
            ["code"] = selectedInclude is null ? Hash(SnippetUtf8.GetBytes(fence.Content.Text ?? string.Empty)) : Hash(selectedInclude.TargetBytes),
        };
        var project = Path.GetFullPath(snippet.ProjectPath, root);
        hashes["project/" + snippet.ProjectPath] = Hash(await ContentPath.ReadAllBytesAsync(root, project, cancellationToken).ConfigureAwait(false));
        if (selectedInclude is not null)
            hashes["include/" + selectedInclude.TargetRelativePath] = selectedInclude.RawHash;
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hashes[input.Kind + "/" + input.Key] = input.Kind == ContentDependencyKind.File
                ? BuildInputFingerprint.FromContainedFile(root, input.PhysicalFilePath) : input.Value!;
        }
        var sdk = await RunCommandAsync(project, ["--version"], cancellationToken).ConfigureAwait(false);
        if (sdk.ExitCode != 0 || string.IsNullOrWhiteSpace(sdk.Output))
            throw new InvalidOperationException("The effective .NET SDK could not be identified.");
        var fingerprint = Hash(SnippetUtf8.GetBytes(JsonSerializer.Serialize(new
        {
            version = 1,
            snippet.Id,
            snippet.DocumentPath,
            snippet.ProjectPath,
            snippet.TestFilter,
            configuration,
            sdk = sdk.Output.Trim(),
            parser = MdParserVersion.Value,
            inputs = hashes,
        })));
        return new(snippet, fingerprint, location, related);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [GeneratedRegex("(?:^|\\s)snippet=\"([^\"]+)\"(?=\\s|$)")]
    private static partial Regex SnippetId();
}
