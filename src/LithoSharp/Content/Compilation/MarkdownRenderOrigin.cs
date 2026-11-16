using Syntamark.Compilation;

namespace LithoSharp.Content.Compilation;

// Body-relative source ranges come from parser nodes; HTML ranges are captured at emission.
internal sealed record MarkdownRenderOrigin(int HtmlStart, int HtmlLength, int BodyStart, int BodyLength,
    bool GeneratedCause = false, bool TokenOnly = false)
{
    internal static void Capture(ICollection<MarkdownRenderOrigin>? origins, int htmlStart, int htmlEnd,
        SourceSpan source, bool generatedCause = false, bool tokenOnly = false)
    {
        if (origins is not null && htmlEnd > htmlStart && source.Length > 0)
            origins.Add(new(htmlStart, htmlEnd - htmlStart, source.Start, source.Length, generatedCause, tokenOnly));
    }
}
