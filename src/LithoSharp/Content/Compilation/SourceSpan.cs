using Syntamark.Compilation;
using LithoSharp.Diagnostics;

namespace LithoSharp.Content.Compilation;

// Product location adapter for the shared runtime span.
internal static class SourceSpanLocationExtensions
{
    /// <summary>Resolves the span to a 1-based file location.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> or <paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The span falls outside the source text.</exception>
    public static SiteSourceLocation ToSourceLocation(this SourceSpan span, string filePath, SourceText source)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(source);
        if (span.End > source.Text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "The span falls outside the source text.");
        }

        var (line, column) = source.GetLineAndColumn(span.Start);
        var (endLine, endColumn) = source.GetLineAndColumn(span.End);
        return new SiteSourceLocation(filePath, line, column, endLine, endColumn);
    }
}
