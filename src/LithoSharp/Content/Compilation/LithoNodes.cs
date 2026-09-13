namespace LithoSharp.Content.Compilation;

/// <summary>
/// Node tree for the Litho Markdown frontend (CommonMark 0.31.2 + GFM 0.29 subset).
/// Spans are body-relative <see cref="SourceSpan"/> values.
/// </summary>
internal abstract record LithoNode(SourceSpan Span);

/// <summary>A block node.</summary>
internal abstract record LithoBlock(SourceSpan Span) : LithoNode(Span);

/// <summary>A paragraph with parsed inlines.</summary>
internal sealed record LithoParagraph(IReadOnlyList<LithoInline> Inlines, SourceSpan Span) : LithoBlock(Span);

/// <summary>A heading with parsed inlines.</summary>
internal sealed record LithoHeading(int Level, IReadOnlyList<LithoInline> Inlines, SourceSpan Span) : LithoBlock(Span);

/// <summary>A fenced or indented code block. Text excludes the fence lines.</summary>
internal sealed record LithoCode(string Text, string? Info, bool Fenced, SourceSpan Span, LithoCodeMeta? Meta = null) : LithoBlock(Span);

/// <summary>Code fence metadata mirroring the MDX worker names (title, highlight, line numbers).</summary>
/// <param name="Title">The <c>title="..."</c> value, or null when absent.</param>
/// <param name="Highlight">The raw <c>{...}</c> line specification, or null when absent.</param>
/// <param name="ShowLineNumbers">Whether <c>showLineNumbers</c> was given.</param>
/// <param name="StartLine">The <c>start=N</c> value (1 when absent).</param>
internal sealed record LithoCodeMeta(string? Title, string? Highlight, bool ShowLineNumbers, int StartLine);

/// <summary>
/// An admonition: a known directive name (note, tip, info, warning, danger,
/// caution, important) from either <c>:::name</c> fences or <c>&gt; [!NAME]</c>
/// alert quotes. Title defaults to the kind name (Docusaurus behavior).
/// </summary>
internal sealed record LithoAdmonition(string Kind, string Title, IReadOnlyList<LithoBlock> Children, SourceSpan Span) : LithoBlock(Span);

/// <summary>A generic <c>:::name</c> container with an unrecognized name. Rendered as a div (Markdig parity); never silently dropped.</summary>
internal sealed record LithoDirective(string Name, IReadOnlyList<LithoBlock> Children, SourceSpan Span) : LithoBlock(Span);

/// <summary>Block math content. Renders as a div (Markdig parity).</summary>
internal sealed record LithoMath(string Content, SourceSpan Span) : LithoBlock(Span);

/// <summary>A blockquote.</summary>
internal sealed record LithoQuote(IReadOnlyList<LithoBlock> Children, SourceSpan Span) : LithoBlock(Span);

/// <summary>A list item. Checked holds task state, or null for plain items.</summary>
internal sealed record LithoItem(IReadOnlyList<LithoBlock> Children, bool? Checked, SourceSpan Span) : LithoNode(Span);

/// <summary>An ordered or unordered list.</summary>
internal sealed record LithoList(
    bool Ordered,
    int Start,
    IReadOnlyList<LithoItem> Items,
    bool Tight,
    SourceSpan Span) : LithoBlock(Span);

/// <summary>A GFM pipe table. The first row is the header. Align entries are "", "left", "center", or "right".</summary>
internal sealed record LithoTable(
    IReadOnlyList<string> Align,
    IReadOnlyList<IReadOnlyList<IReadOnlyList<LithoInline>>> Rows,
    SourceSpan Span) : LithoBlock(Span);

/// <summary>A thematic break.</summary>
internal sealed record LithoBreak(SourceSpan Span) : LithoBlock(Span);

/// <summary>An inline node.</summary>
internal abstract record LithoInline(SourceSpan Span) : LithoNode(Span)
{
    /// <summary>Visits inline nodes without using the call stack for document nesting.</summary>
    internal static IEnumerable<(LithoInline Node, bool Exit)> Walk(
        IReadOnlyList<LithoInline> nodes, bool includeImageLabels = true,
        CancellationToken cancellationToken = default)
    {
        var pending = new Stack<(IReadOnlyList<LithoInline> Nodes, int Index, LithoInline? Parent)>();
        pending.Push((nodes, 0, null));
        while (pending.TryPop(out var frame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.Index == frame.Nodes.Count)
            {
                if (frame.Parent is not null) yield return (frame.Parent, true);
                continue;
            }

            var node = frame.Nodes[frame.Index];
            pending.Push((frame.Nodes, frame.Index + 1, frame.Parent));
            yield return (node, false);
            var children = node switch
            {
                LithoEmphasis emphasis => emphasis.Children,
                LithoStrike strike => strike.Children,
                LithoLink link when includeImageLabels || !link.Image => link.Label,
                _ => null,
            };
            if (children is not null) pending.Push((children, 0, node));
        }
    }
}

/// <summary>
/// Unparsed inline source with a line map back to body offsets.
/// Replaced by the resolve pass once reference definitions are complete,
/// so forward references resolve. The renderer rejects unresolved trees.
/// </summary>
internal sealed record LithoRawText(string Text, LithoLineMap Map) : LithoInline(SourceSpan.Empty);

/// <summary>Maps joined-text offsets back to body offsets across stripped prefixes.</summary>
/// <param name="LocalStarts">Joined-text offset of each source line.</param>
/// <param name="BodyStarts">Body offset of each source line.</param>
/// <param name="TextLength">Joined text length.</param>
/// <param name="BodyEnd">Body offset one past the joined text.</param>
internal sealed record LithoLineMap(int[] LocalStarts, int[] BodyStarts, int TextLength, int BodyEnd)
{
    /// <summary>Single-line map over contiguous text.</summary>
    public static LithoLineMap Contiguous(int bodyStart, int length) =>
        new([0], [bodyStart], length, bodyStart + length);

    /// <summary>Resolves a joined-text offset to a body offset.</summary>
    public int Global(int local)
    {
        if (TextLength == 0 || local <= 0)
        {
            return BodyStarts.Length == 0 ? BodyEnd : BodyStarts[0];
        }

        if (local >= TextLength)
        {
            return BodyEnd;
        }

        var index = Array.BinarySearch(LocalStarts, local);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return BodyStarts[index] + (local - LocalStarts[index]);
    }
}

/// <summary>Plain text (entities already decoded).</summary>
internal sealed record LithoText(string Text, SourceSpan Span) : LithoInline(Span);

/// <summary>Emphasis (level 1) or strong (level 2).</summary>
internal sealed record LithoEmphasis(int Level, IReadOnlyList<LithoInline> Children, SourceSpan Span) : LithoInline(Span);

/// <summary>GFM strikethrough.</summary>
internal sealed record LithoStrike(IReadOnlyList<LithoInline> Children, SourceSpan Span) : LithoInline(Span);

/// <summary>A code span.</summary>
internal sealed record LithoCodeSpan(string Code, SourceSpan Span) : LithoInline(Span);

/// <summary>A link or image.</summary>
internal sealed record LithoLink(
    IReadOnlyList<LithoInline> Label,
    string Url,
    string? Title,
    bool Image,
    SourceSpan Span) : LithoInline(Span);

/// <summary>An autolink. Href is the link target; Text is the display text.</summary>
internal sealed record LithoAutolink(string Href, string Text, SourceSpan Span) : LithoInline(Span);

/// <summary>Inline math content. Renders as a span (Markdig parity).</summary>
internal sealed record LithoMathInline(string Content, SourceSpan Span) : LithoInline(Span);

/// <summary>A line break.</summary>
internal sealed record LithoLineBreak(bool Hard, SourceSpan Span) : LithoInline(Span);
