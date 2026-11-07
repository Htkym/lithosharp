using Syntamark.Compilation;

namespace LithoSharp.Content.Compilation;

/// <summary>An immutable parser result tied to one exact Markdown body and analysis request.</summary>
internal sealed class MarkdownBodyAnalysisSnapshot
{
    internal MarkdownBodyAnalysisSnapshot(MdAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        Analysis = analysis;
    }

    internal MdAnalysisResult Analysis { get; }
}
