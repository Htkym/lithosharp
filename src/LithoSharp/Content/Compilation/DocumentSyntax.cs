namespace LithoSharp.Content.Compilation;

/// <summary>Block kinds in the Markdown syntax tree ("as written").</summary>
internal enum BlockKind
{
    /// <summary>A paragraph.</summary>
    Paragraph,

    /// <summary>A heading (ATX or Setext). Raw level is on <see cref="SyntaxBlock.Level"/>.</summary>
    Heading,

    /// <summary>A fenced or indented code block.</summary>
    Code,

    /// <summary>A blockquote.</summary>
    Quote,

    /// <summary>An ordered or unordered list.</summary>
    List,

    /// <summary>A pipe or grid table.</summary>
    Table,

    /// <summary>An admonition (known directive name or alert quote).</summary>
    Admonition,

    /// <summary>A generic container with an unrecognized name.</summary>
    Directive,

    /// <summary>A math block.</summary>
    Math,

    /// <summary>A thematic break.</summary>
    ThematicBreak,

    /// <summary>Any other block (raw HTML, ...).</summary>
    Other,
}

/// <summary>A single top-level block with its original-file span.</summary>
/// <param name="Kind">Block kind.</param>
/// <param name="Span">Span in original-file coordinates.</param>
/// <param name="Level">Raw heading level for <see cref="BlockKind.Heading"/>, otherwise 0.</param>
internal readonly record struct SyntaxBlock(BlockKind Kind, SourceSpan Span, int Level);

/// <summary>Syntax tree: what is written in the document.</summary>
/// <param name="Blocks">Top-level blocks in document order.</param>
internal sealed record DocumentSyntax(IReadOnlyList<SyntaxBlock> Blocks);
