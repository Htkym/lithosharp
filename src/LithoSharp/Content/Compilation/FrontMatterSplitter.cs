using Syntamark.Compilation;
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
    int FailureLine);

/// <summary>
/// Single scanner shared by the legacy front matter splitter and the
/// position-aware loader. A leading UTF-8 BOM is skipped, matching the
/// collection loader, source generators, and MDX handling.
/// </summary>
internal static class FrontMatterSplitter
{
    /// <summary>Applies the legacy split result shape to shared delimiter facts.</summary>
    public static FrontMatterSplit TrySplit(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var envelope = MdFrontMatterScan.Scan(text, cancellationToken);
        if (!envelope.Opening.HasValue)
            return new FrontMatterSplit(FrontMatterSplitStatus.MissingFrontMatter, string.Empty, string.Empty, 0, 1);
        if (!envelope.Closing.HasValue)
            return new FrontMatterSplit(FrontMatterSplitStatus.UnterminatedFrontMatter, string.Empty, string.Empty, 0, envelope.FailureLine);
        var yamlSpan = envelope.Yaml!.Value; var bodySpan = envelope.Body!.Value;
        var yaml = text.Substring(yamlSpan.Start, yamlSpan.Length);
        var status = string.IsNullOrWhiteSpace(yaml) ? FrontMatterSplitStatus.EmptyFrontMatter : FrontMatterSplitStatus.Ok;
        return new FrontMatterSplit(status, yaml, text.Substring(bodySpan.Start, bodySpan.Length), bodySpan.Start,
            status == FrontMatterSplitStatus.EmptyFrontMatter ? 2 : 1);
    }
}
