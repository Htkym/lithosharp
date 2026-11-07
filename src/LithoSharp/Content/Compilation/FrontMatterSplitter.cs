using Syntamark.Compilation;
using LithoSharp.Diagnostics;

namespace LithoSharp.Content.Compilation;

/// <summary>Shared front matter scan outcome.</summary>
internal enum FrontMatterSplitStatus
{
    /// <summary>YAML and body were found.</summary>
    Ok,

    /// <summary>The document does not start with a YAML front matter marker.</summary>
    MissingFrontMatter,

    /// <summary>No closing front matter marker was found.</summary>
    UnterminatedFrontMatter,

    /// <summary>The front matter block is empty or whitespace-only.</summary>
    EmptyFrontMatter,

    /// <summary>The bounded header scan could not establish delimiter facts.</summary>
    Incomplete,
}

/// <summary>Offset-aware front matter split result.</summary>
/// <param name="Status">Scan outcome.</param>
/// <param name="Yaml">YAML text (only when <see cref="Status"/> is Ok or EmptyFrontMatter).</param>
/// <param name="Body">Body text (when <see cref="Status"/> is Ok or EmptyFrontMatter).</param>
/// <param name="BodyStartOffset">UTF-16 offset where the body starts (when Ok or EmptyFrontMatter).</param>
/// <param name="FailureLine">1-based line for the failure location (otherwise 1).</param>
internal readonly record struct FrontMatterSplit(
    FrontMatterSplitStatus Status,
    string Yaml,
    string Body,
    int BodyStartOffset,
    int FailureLine)
{
    internal MdDiagnostic? FailureDiagnostic { get; init; }
}

/// <summary>
/// Header-only scan shared by the compatibility splitter and position-aware loader.
/// A leading BOM is included in raw positions. The default header budget is
/// 1,048,576 UTF-16 code units; an incomplete scan maps to LSM013, never a missing header.
/// </summary>
internal static class FrontMatterSplitter
{
    private static readonly MdOptions HeaderOptions = new();

    /// <summary>Applies the legacy split result shape to shared delimiter facts.</summary>
    public static FrontMatterSplit TrySplit(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var envelope = MdParser.ScanFrontMatter(text, HeaderOptions, cancellationToken);
        if (envelope.State == MdFrontMatterScanState.Absent && envelope.Status == MdParseStatus.Complete)
            return new FrontMatterSplit(FrontMatterSplitStatus.MissingFrontMatter, string.Empty, string.Empty, 0, 1);
        if (envelope.State == MdFrontMatterScanState.Unterminated && envelope.Status == MdParseStatus.Partial
            && envelope.FailureSpan is not null && envelope.FailureLine is > 0)
            return new FrontMatterSplit(FrontMatterSplitStatus.UnterminatedFrontMatter, string.Empty, string.Empty, 0, envelope.FailureLine.Value);
        if (envelope.State == MdFrontMatterScanState.Closed && envelope.Status == MdParseStatus.Complete
            && envelope.OpeningSpan is not null && envelope.ClosingSpan is not null
            && envelope.YamlSpan is { } yamlSpan && envelope.BodySpan is { } bodySpan)
        {
            var yaml = text.Substring(yamlSpan.Start, yamlSpan.Length);
            var status = string.IsNullOrWhiteSpace(yaml) ? FrontMatterSplitStatus.EmptyFrontMatter : FrontMatterSplitStatus.Ok;
            return new FrontMatterSplit(status, yaml, text.Substring(bodySpan.Start, bodySpan.Length), bodySpan.Start,
                status == FrontMatterSplitStatus.EmptyFrontMatter ? 2 : 1);
        }

        return new FrontMatterSplit(FrontMatterSplitStatus.Incomplete, string.Empty, string.Empty, 0, 1)
        {
            FailureDiagnostic = envelope.Diagnostics.FirstOrDefault(),
        };
    }

    internal static SiteDiagnostic IncompleteDiagnostic(FrontMatterSplit split, string text, string sourcePath) =>
        new(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId, SiteDiagnosticSeverity.Error,
            "Markdown front matter scanning is incomplete; unavailable facts were not validated."
                + (split.FailureDiagnostic is { } failure ? " " + failure.Message : string.Empty),
            split.FailureDiagnostic?.RawSpan is { } range
                ? new SourceSpan(range.Start, range.Length).ToSourceLocation(sourcePath, new SourceText(text))
                : new SiteSourceLocation(sourcePath));
}
