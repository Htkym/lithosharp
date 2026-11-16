using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LithoSharp.Diagnostics;
using Syntamark;

namespace LithoSharp.Proofreading;

/// <summary>A reviewable replacement. Applying it returns text and never writes files.</summary>
public sealed class ProofreadingEditPreview
{
    private readonly SourceEdit? edit;

    internal ProofreadingEditPreview(ProseMapping mapping, string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        Fragments = Array.AsReadOnly(mapping.Origins.ToArray());
        MappingPrecision = mapping.Precision.ToString();
        OriginalText = string.Concat(Fragments.Select(fragment => fragment.Source.Text.Substring(fragment.Span.Start, fragment.Span.Length)));
        Replacement = replacement;
        if (mapping.Precision == MarkdownMappingPrecision.Exact && Fragments.Count == 1)
            edit = new SourceEdit(Fragments[0], OriginalText, replacement);
        Reason = edit is null ? "The diagnostic does not identify one exact editable source range." : null;
    }

    public IReadOnlyList<SourceRange> Fragments { get; }
    public string MappingPrecision { get; }
    public string OriginalText { get; }
    public string Replacement { get; }
    public bool CanApply => edit is not null;
    public string? Reason { get; }
    public bool IsApplicable(SourceSnapshot current) => edit?.IsApplicable(current) == true;

    /// <summary>Requires the same source identity, version, text hash and expected original text.</summary>
    public string Apply(SourceSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (edit is null || !edit.IsApplicable(current))
            throw new InvalidOperationException("The preview is ambiguous or no longer matches the current source snapshot.");
        return string.Concat(current.Text.AsSpan(0, edit.Target.Span.Start), Replacement,
            current.Text.AsSpan(edit.Target.Span.End));
    }
}

/// <summary>One editorial finding. Line-only findings deliberately cannot certify an exact replacement.</summary>
public sealed class ProofreadingFinding
{
    private readonly ProseMapping mapping;

    internal ProofreadingFinding(string rule, string severity, string message, ProseMapping mapping,
        SourceSnapshot source, Syntamark.Compilation.SourceText sourceText)
    {
        Rule = rule;
        Severity = severity;
        Message = message;
        this.mapping = mapping;
        Fragments = Array.AsReadOnly(mapping.Origins.ToArray());
        MappingPrecision = mapping.Precision.ToString();
        var locations = Fragments.Select(fragment => Location(fragment, sourceText)).ToArray();
        Diagnostic = new SiteDiagnostic("LSPROOF001", severity switch
        {
            "error" => SiteDiagnosticSeverity.Error,
            "warn" => SiteDiagnosticSeverity.Warning,
            _ => SiteDiagnosticSeverity.Info,
        }, rule + ": " + message, locations.FirstOrDefault(), "proofreading", locations.Skip(1))
        {
            MappingPrecision = MappingPrecision,
            SnapshotIdentity = source.SourceId + "\0" + source.Version + "\0" + source.TextHash,
        };
    }

    public string Rule { get; }
    public string Severity { get; }
    public string Message { get; }
    public string MappingPrecision { get; }
    public IReadOnlyList<SourceRange> Fragments { get; }
    public SiteDiagnostic Diagnostic { get; }
    public ProofreadingEditPreview PreviewReplacement(string replacement) => new(mapping, replacement);

    private static SiteSourceLocation Location(SourceRange range, Syntamark.Compilation.SourceText text)
    {
        var from = text.GetLineAndColumn(range.Span.Start);
        var to = text.GetLineAndColumn(range.Span.End);
        return new(range.Source.SourceId, from.Line, from.Column, to.Line, to.Column);
    }
}

/// <summary>Editorial findings for the explicitly selected prose, not a technical or trust verification.</summary>
public sealed class ProofreadingResult
{
    internal ProofreadingResult(ProofreadingSelection selection, string? toolVersion, bool wasRun,
        IReadOnlyList<ProofreadingFinding> findings)
    {
        Selection = selection;
        ToolVersion = toolVersion;
        WasRun = wasRun;
        Findings = Array.AsReadOnly(findings.ToArray());
        State = !wasRun ? "NotRun" : selection.ParseStatus == MarkdownParseStatus.Complete
            && selection.Exclusions.All(exclusion => exclusion.Range is not null && exclusion.Reason != "unknown-projection")
            && findings.All(finding => finding.MappingPrecision is not ("Unknown" or "Partial")) ? "Complete" : "Partial";
    }

    public ProofreadingSelection Selection { get; }
    public string? ToolVersion { get; }
    public bool WasRun { get; }
    public string State { get; }
    public IReadOnlyList<ProofreadingFinding> Findings { get; }
}

/// <summary>Explicit optional Yomiyasu 3.x JSON-format-2 integration. No executable is discovered or installed automatically.</summary>
public static class YomiyasuProofreader
{
    private sealed record BatchSegment(int FirstLine, ProofreadingSegment Segment, YomiyasuCoordinates Coordinates);
    private sealed record Batch(string Text, IReadOnlyList<BatchSegment> Segments, int LineCount);

    /// <summary>Runs the caller-selected local Python and lint script once, with no implicit timeout, word files or source writes.</summary>
    public static async Task<ProofreadingResult> CheckAsync(ProofreadingSelection selection, string pythonExecutable,
        string lintScript, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(lintScript);
        cancellationToken.ThrowIfCancellationRequested();
        if (!selection.HasSyntax || selection.Segments.Count == 0)
            return new(selection, null, false, []);
        lintScript = Path.GetFullPath(lintScript);
        if (!File.Exists(lintScript))
            throw new FileNotFoundException("The explicitly selected Yomiyasu lint script does not exist.", lintScript);
        var batch = MakeBatch(selection, cancellationToken);
        var start = new ProcessStartInfo(pythonExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(lintScript)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false, true),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
        };
        start.ArgumentList.Add(lintScript);
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add("--no-words");
        start.ArgumentList.Add("-");
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.Environment["PYTHONUTF8"] = "1";
        using var process = Process.Start(start) ?? throw new IOException("Could not start the explicitly selected proofreader.");
        var output = ReadBounded(process.StandardOutput, 4 * 1024 * 1024);
        var error = ReadBounded(process.StandardError, 64 * 1024);
        try
        {
            await process.StandardInput.WriteAsync(batch.Text.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var json = await output.ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException($"Yomiyasu did not complete successfully (exit {process.ExitCode}): {stderr}");
            return ReadReport(selection, batch, json, cancellationToken);
        }
        finally
        {
            Stop();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(output, error).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or DecoderFallbackException) { }
        }

        async Task<string> ReadBounded(StreamReader reader, int limit)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (text.Length > limit - count)
                {
                    Stop();
                    throw new IOException("Yomiyasu output exceeds its reading limit.");
                }
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }

        void Stop()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
        }
    }

    internal static ProofreadingResult ReadReport(ProofreadingSelection selection, string json, CancellationToken cancellationToken = default)
        => ReadReport(selection, MakeBatch(selection, cancellationToken), json, cancellationToken);

    private static ProofreadingResult ReadReport(ProofreadingSelection selection, Batch batch, string json, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != "yomiyasu-lint"
            || root.GetProperty("format_version").GetInt32() != 2
            || !Version.TryParse(root.GetProperty("tool_version").GetString(), out var version) || version.Major != 3
            || root.GetProperty("findings").ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Unsupported Yomiyasu output protocol.");
        var findings = new List<ProofreadingFinding>();
        var sourceText = new Syntamark.Compilation.SourceText(selection.Source.Text);
        foreach (var finding in root.GetProperty("findings").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rule = finding.GetProperty("rule").GetString();
            var severity = finding.GetProperty("severity").GetString();
            var message = finding.GetProperty("message").GetString();
            if (string.IsNullOrWhiteSpace(rule) || string.IsNullOrWhiteSpace(message) || severity is not ("info" or "warn" or "error"))
                throw new InvalidDataException("Yomiyasu returned an unsupported finding.");
            var line = finding.GetProperty("line").GetInt32();
            if (line < 1 || line > batch.LineCount)
                throw new InvalidDataException("Yomiyasu returned a line outside the submitted prose.");
            var low = 0;
            var high = batch.Segments.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (batch.Segments[middle].FirstLine <= line)
                    low = middle + 1;
                else
                    high = middle;
            }
            var entry = low == 0 ? null : batch.Segments[low - 1];
            if (entry is not null && line >= entry.FirstLine + entry.Coordinates.LineCount)
                entry = null;
            var mapping = entry is null ? new ProseMapping([], MarkdownMappingPrecision.Unknown)
                : entry.Segment.Map(entry.Coordinates.ToRawLine(line - entry.FirstLine + 1), cancellationToken);
            // Protocol 2 supplies a line, never an exact replacement range.
            if (mapping.Precision == MarkdownMappingPrecision.Exact)
                mapping = mapping with { Precision = MarkdownMappingPrecision.CoveringTokens };
            findings.Add(new ProofreadingFinding(rule, severity, message, mapping, selection.Source, sourceText));
        }
        var counts = root.GetProperty("counts");
        if (root.GetProperty("candidate_count").GetInt32() != findings.Count
            || counts.GetProperty("info").GetInt32() != findings.Count(finding => finding.Severity == "info")
            || counts.GetProperty("warn").GetInt32() != findings.Count(finding => finding.Severity == "warn")
            || counts.GetProperty("error").GetInt32() != findings.Count(finding => finding.Severity == "error"))
            throw new InvalidDataException("Yomiyasu finding counts do not agree with its output.");
        cancellationToken.ThrowIfCancellationRequested();
        return new(selection, root.GetProperty("tool_version").GetString(), true, findings);
    }

    private static Batch MakeBatch(ProofreadingSelection selection, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var segments = new List<BatchSegment>();
        var firstLine = 1;
        foreach (var segment in selection.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coordinates = new YomiyasuCoordinates(segment.Text);
            segments.Add(new(firstLine, segment, coordinates));
            foreach (var character in coordinates.Text)
            {
                if ("\\`*_[]<>#|".Contains(character))
                    text.Append('\\');
                text.Append(character);
            }
            text.Append("\n\n");
            firstLine += coordinates.LineCount + 1;
        }
        return new(text.ToString(), segments.AsReadOnly(), firstLine);
    }
}
