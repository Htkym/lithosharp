using LithoSharp.Diagnostics;

namespace LithoSharp.Content.Compilation;

/// <summary>Source identity for a compiled document (metadata).</summary>
/// <param name="FilePath">Original file path, or null when compiling an in-memory snippet.</param>
/// <param name="BodyStartOffset">UTF-16 offset where the Markdown body starts in the original file.</param>
internal sealed record DocumentSource(string? FilePath, int BodyStartOffset)
{
    internal int BodyStartLine { get; init; } = 1;
}

/// <summary>A resolved heading: raw level is as written, output level is as rendered.</summary>
/// <param name="Text">Plain heading text.</param>
/// <param name="Id">Auto-identifier from the parse, or null when absent.</param>
/// <param name="RawLevel">Heading level as written (1-6).</param>
/// <param name="OutputLevel">Heading level as rendered (body h1 is demoted to h2).</param>
/// <param name="Span">Span in original-file coordinates.</param>
internal readonly record struct DocumentHeading(
    string Text,
    string? Id,
    int RawLevel,
    int OutputLevel,
    SourceSpan Span);

/// <summary>A resolved link: raw text is as written, URL is the resolved target.</summary>
/// <param name="RawText">Original Markdown slice (for example, "[text](url)").</param>
/// <param name="Url">Resolved URL.</param>
/// <param name="Title">Link title, or null when absent.</param>
/// <param name="IsImage">Whether the link is an image.</param>
/// <param name="Span">Span in original-file coordinates.</param>
internal readonly record struct DocumentLink(
    string RawText,
    string Url,
    string? Title,
    bool IsImage,
    SourceSpan Span);

/// <summary>An asset reference (currently image links).</summary>
/// <param name="Url">Asset URL as written.</param>
/// <param name="Span">Span in original-file coordinates.</param>
internal readonly record struct DocumentAsset(string Url, SourceSpan Span);

/// <summary>A component reference. Markdown declares none; reserved for MDX (C08).</summary>
/// <param name="Name">Component name.</param>
/// <param name="Span">Span in original-file coordinates.</param>
internal readonly record struct DocumentComponent(string Name, SourceSpan Span);

/// <summary>Semantic model: resolved document information from one parse.</summary>
/// <param name="Source">Source identity.</param>
/// <param name="Title">First level-1 heading text, or null when absent (title behavior rule).</param>
/// <param name="PlainText">Plain text of the body.</param>
/// <param name="Headings">Headings in document order.</param>
/// <param name="Links">Links and images in document order.</param>
/// <param name="Assets">Asset references in document order.</param>
/// <param name="Components">Component references (empty for Markdown).</param>
/// <param name="Diagnostics">Parse diagnostics (footnote LIT001 warnings; Markdown reports none).</param>
internal sealed record DocumentSemantics(
    DocumentSource Source,
    string? Title,
    string PlainText,
    IReadOnlyList<DocumentHeading> Headings,
    IReadOnlyList<DocumentLink> Links,
    IReadOnlyList<DocumentAsset> Assets,
    IReadOnlyList<DocumentComponent> Components,
    IReadOnlyList<SiteDiagnostic> Diagnostics)
{
    /// <summary>Title behavior rule: the first level-1 heading text, or null when absent.</summary>
    internal static string? TitleOf(IReadOnlyList<DocumentHeading> headings)
    {
        ArgumentNullException.ThrowIfNull(headings);
        foreach (var heading in headings)
        {
            if (heading.RawLevel == 1)
            {
                return heading.Text;
            }
        }

        return null;
    }
}
