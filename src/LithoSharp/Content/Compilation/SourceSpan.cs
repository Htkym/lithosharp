using LithoSharp.Diagnostics;

namespace LithoSharp.Content.Compilation;

// Product adapter; the canonical span itself has no site diagnostic dependency.
internal readonly partial record struct SourceSpan
{
    /// <summary>Resolves the span to a 1-based file location.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> or <paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The span falls outside the source text.</exception>
    public SiteSourceLocation ToSourceLocation(string filePath, SourceText source)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(source);
        if (End > source.Text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "The span falls outside the source text.");
        }

        var (line, column) = source.GetLineAndColumn(Start);
        var (endLine, endColumn) = source.GetLineAndColumn(End);
        return new SiteSourceLocation(filePath, line, column, endLine, endColumn);
    }
}
