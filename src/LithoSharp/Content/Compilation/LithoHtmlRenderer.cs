using System.Text;

namespace LithoSharp.Content.Compilation;

/// <summary>
/// HTML and plain-text renderer for the Litho node tree. Text encoding matches
/// the reference output (&amp;, &lt;, &gt;, &quot;; apostrophes stay literal).
/// </summary>
internal static class LithoHtmlRenderer
{
    /// <summary>Renders blocks to HTML.</summary>
    /// <param name="blocks">Blocks in document order.</param>
    /// <param name="headingIds">Pre-assigned heading ids in pre-order document order.</param>
    public static string RenderHtml(IReadOnlyList<LithoBlock> blocks, IReadOnlyList<string> headingIds)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(headingIds);
        var writer = new StringBuilder();
        var state = new RenderState(headingIds);
        foreach (var block in blocks)
        {
            RenderBlock(block, writer, state);
        }

        return writer.ToString();
    }

    private sealed class RenderState(IReadOnlyList<string> headingIds)
    {
        public int HeadingIndex;
        public string NextId() => HeadingIndex < headingIds.Count ? headingIds[HeadingIndex++] : "section";
    }

    /// <summary>Renders inlines to HTML.</summary>
    public static string RenderInlines(IReadOnlyList<LithoInline> inlines)
    {
        ArgumentNullException.ThrowIfNull(inlines);
        var writer = new StringBuilder();
        RenderInlineTree(inlines, writer);

        return writer.ToString();
    }

    /// <summary>Renders blocks to plain text (search content).</summary>
    /// <remarks>Mirrors the reference plain renderer: paragraphs, headings, and
    /// code append LF; containers concatenate children; tables concatenate cells
    /// with a trailing space each; breaks and thematic breaks append nothing.</remarks>
    public static string RenderPlainText(IReadOnlyList<LithoBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var writer = new StringBuilder();
        foreach (var block in blocks)
        {
            RenderBlockPlain(block, writer);
        }

        return writer.ToString();
    }

    private static void RenderBlock(LithoBlock block, StringBuilder writer, RenderState state)
    {
        switch (block)
        {
            case LithoParagraph paragraph:
                writer.Append("<p>");
                RenderInlineTree(paragraph.Inlines, writer);

                writer.Append("</p>\n");
                break;
            case LithoHeading heading:
                var id = state.NextId();
                writer.Append($"<h{heading.Level} id=\"{id}\">");
                RenderInlineTree(heading.Inlines, writer);

                writer.Append($"</h{heading.Level}>\n");
                break;
            case LithoCode code:
                RenderCode(code, writer);
                break;
            case LithoAdmonition admonition:
                // Static shape mirrors the MDX runtime Admonition (aside with a
                // strong title defaulting to the kind); no Node.js involved.
                writer.Append($"<aside class=\"mdx-admonition mdx-{admonition.Kind}\" role=\"note\">");
                writer.Append($"<strong>{Encode(admonition.Title)}</strong>\n");
                foreach (var child in admonition.Children)
                {
                    RenderBlock(child, writer, state);
                }

                writer.Append("</aside>\n");
                break;
            case LithoDirective directive:
                // Unrecognized container names render as plain divs (Markdig parity).
                if (directive.Name.Length == 0)
                {
                    writer.Append("<div>\n");
                }
                else
                {
                    writer.Append($"<div class=\"{Encode(directive.Name)}\">\n");
                }

                foreach (var child in directive.Children)
                {
                    RenderBlock(child, writer, state);
                }

                writer.Append("</div>\n");
                break;
            case LithoMath math:
                writer.Append("<div class=\"math\">\n\\[\n");
                writer.Append(Encode(math.Content));
                writer.Append("\n\\]</div>\n");
                break;
            case LithoQuote quote:
                writer.Append("<blockquote>\n");
                foreach (var child in quote.Children)
                {
                    RenderBlock(child, writer, state);
                }

                writer.Append("</blockquote>\n");
                break;
            case LithoList list:
                writer.Append(list.Ordered
                    ? list.Start == 1 ? "<ol>\n" : $"<ol start=\"{list.Start}\">\n"
                    : AnyTask(list) ? "<ul class=\"contains-task-list\">\n" : "<ul>\n");
                foreach (var item in list.Items)
                {
                    RenderItem(item, list.Tight, writer, state);
                }

                writer.Append(list.Ordered ? "</ol>\n" : "</ul>\n");
                break;
            case LithoTable table:
                writer.Append("<table>\n<thead>\n<tr>\n");
                RenderRow(table.Rows[0], table.Align, true, writer);
                writer.Append("</tr>\n</thead>\n<tbody>\n");
                for (var i = 1; i < table.Rows.Count; i++)
                {
                    writer.Append("<tr>\n");
                    RenderRow(table.Rows[i], table.Align, false, writer);
                    writer.Append("</tr>\n");
                }

                writer.Append("</tbody>\n</table>\n");
                break;
            case LithoBreak:
                writer.Append("<hr />\n");
                break;
        }
    }

    private static void RenderCode(LithoCode code, StringBuilder writer)
    {
        // Diagram languages have dedicated static shapes with minimally escaped
        // content (Markdig parity: only & and < are escaped, so tags cannot be
        // injected while diagram syntax stays readable); rendering them needs
        // browser-side mermaid.js, which is not bundled.
        if (string.Equals(code.Info, "mermaid", StringComparison.Ordinal))
        {
            writer.Append("<pre class=\"mermaid\">");
            writer.Append(EncodeDiagram(code.Text));
            writer.Append("</pre>\n");
            return;
        }

        if (string.Equals(code.Info, "nomnoml", StringComparison.Ordinal))
        {
            writer.Append("<div class=\"nomnoml\">");
            writer.Append(EncodeDiagram(code.Text));
            writer.Append("</div>\n");
            return;
        }

        writer.Append("<pre");
        if (code.Meta is { } meta)
        {
            // Attribute names mirror the MDX worker's data-* code metadata.
            if (!string.IsNullOrEmpty(meta.Title))
            {
                writer.Append($" data-title=\"{Encode(meta.Title)}\"");
            }

            if (!string.IsNullOrEmpty(meta.Highlight))
            {
                writer.Append($" data-highlight=\"{Encode(meta.Highlight)}\"");
            }

            if (meta.StartLine != 1)
            {
                writer.Append($" data-start=\"{meta.StartLine}\"");
            }

            if (meta.ShowLineNumbers)
            {
                writer.Append(" data-line-numbers=\"true\"");
            }
        }

        writer.Append("><code");
        if (code.Info is not null)
        {
            writer.Append($" class=\"language-{Encode(code.Info)}\"");
        }

        writer.Append('>');
        writer.Append(Encode(code.Text));
        writer.Append("</code></pre>\n");
    }

    private static bool AnyTask(LithoList list)
    {
        foreach (var item in list.Items)
        {
            if (item.Checked is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static void RenderItem(LithoItem item, bool tight, StringBuilder writer, RenderState state)
    {
        writer.Append(item.Checked is null ? "<li>" : "<li class=\"task-list-item\">");
        if (item.Checked is not null)
        {
            writer.Append(item.Checked.Value
                ? "<input disabled=\"disabled\" type=\"checkbox\" checked=\"checked\" /> "
                : "<input disabled=\"disabled\" type=\"checkbox\" /> ");
        }

        if (tight)
        {
            var parts = new List<string>(item.Children.Count);
            foreach (var child in item.Children)
            {
                parts.Add(child is LithoParagraph paragraph ? RenderInlines(paragraph.Inlines) : RenderChild(child, state));
            }

            writer.Append(string.Join("\n", parts));
            if (parts.Count > 1)
            {
                writer.Append('\n');
            }
        }
        else
        {
            foreach (var child in item.Children)
            {
                RenderBlock(child, writer, state);
            }
        }

        writer.Append("</li>\n");
    }

    private static string RenderChild(LithoBlock child, RenderState state)
    {
        var writer = new StringBuilder();
        RenderBlock(child, writer, state);
        return writer.ToString().TrimEnd('\n');
    }

    private static void RenderRow(
        IReadOnlyList<IReadOnlyList<LithoInline>> cells,
        IReadOnlyList<string> align,
        bool header,
        StringBuilder writer)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            var style = i < align.Count && align[i].Length != 0 ? $" style=\"text-align: {align[i]};\"" : string.Empty;
            writer.Append(header ? $"<th{style}>" : $"<td{style}>");
            RenderInlineTree(cells[i], writer);

            writer.Append(header ? "</th>\n" : "</td>\n");
        }
    }

    private static void RenderInlineTree(IReadOnlyList<LithoInline> inlines, StringBuilder writer)
    {
        foreach (var (inline, exit) in LithoInline.Walk(inlines, includeImageLabels: false))
        {
        if (exit)
        {
            writer.Append(inline switch
            {
                LithoEmphasis { Level: 2 } => "</strong>",
                LithoEmphasis => "</em>",
                LithoStrike => "</del>",
                LithoLink => "</a>",
                _ => string.Empty,
            });
            continue;
        }
        switch (inline)
        {
            case LithoText text:
                writer.Append(Encode(text.Text));
                break;
            case LithoEmphasis emphasis:
                writer.Append(emphasis.Level == 2 ? "<strong>" : "<em>");
                break;
            case LithoStrike strike:
                writer.Append("<del>");
                break;
            case LithoCodeSpan code:
                writer.Append("<code>");
                writer.Append(Encode(code.Code));
                writer.Append("</code>");
                break;
            case LithoLink link:
                if (link.Image)
                {
                    writer.Append($"<img src=\"{Encode(link.Url)}\" alt=\"{Encode(RenderLabelText(link.Label))}\"");
                    if (link.Title is not null)
                    {
                        writer.Append($" title=\"{Encode(link.Title)}\"");
                    }

                    writer.Append(" />");
                }
                else
                {
                    writer.Append($"<a href=\"{Encode(link.Url)}\"");
                    if (link.Title is not null)
                    {
                        writer.Append($" title=\"{Encode(link.Title)}\"");
                    }

                    writer.Append('>');
                }

                break;
            case LithoAutolink autolink:
                writer.Append($"<a href=\"{Encode(autolink.Href)}\">{Encode(autolink.Text)}</a>");
                break;
            case LithoMathInline math:
                writer.Append("<span class=\"math\">\\(");
                writer.Append(Encode(math.Content));
                writer.Append("\\)</span>");
                break;
            case LithoLineBreak lineBreak:
                writer.Append(lineBreak.Hard ? "<br />\n" : "\n");
                break;
            case LithoRawText:
                throw new InvalidOperationException("Unresolved raw text reached the renderer.");
        }
        }
    }

    private static string RenderLabelText(IReadOnlyList<LithoInline> label)
    {
        var writer = new StringBuilder();
        RenderLabelInto(label, writer);
        return writer.ToString();
    }

    private static void RenderLabelInto(IReadOnlyList<LithoInline> label, StringBuilder writer)
    {
        foreach (var (inline, exit) in LithoInline.Walk(label))
        {
            if (exit) continue;
            switch (inline)
            {
                case LithoText text:
                    writer.Append(text.Text);
                    break;
                case LithoCodeSpan code:
                    writer.Append(code.Code);
                    break;
                case LithoAutolink autolink:
                    writer.Append(autolink.Text);
                    break;
                case LithoMathInline math:
                    writer.Append(math.Content);
                    break;
                case LithoLineBreak:
                    writer.Append('\n');
                    break;
                case LithoRawText:
                    throw new InvalidOperationException("Unresolved raw text reached the renderer.");
            }
        }
    }

    private static void RenderBlockPlain(LithoBlock block, StringBuilder writer)
    {
        switch (block)
        {
            case LithoParagraph paragraph:
                RenderLabelInto(paragraph.Inlines, writer);
                writer.Append('\n');
                break;
            case LithoHeading heading:
                RenderLabelInto(heading.Inlines, writer);
                writer.Append('\n');
                break;
            case LithoCode code:
                writer.Append(code.Text.TrimEnd('\n'));
                writer.Append('\n');
                break;
            case LithoQuote quote:
                foreach (var child in quote.Children)
                {
                    RenderBlockPlain(child, writer);
                }

                break;
            case LithoAdmonition admonition:
                foreach (var child in admonition.Children)
                {
                    RenderBlockPlain(child, writer);
                }

                break;
            case LithoDirective directive:
                foreach (var child in directive.Children)
                {
                    RenderBlockPlain(child, writer);
                }

                break;
            case LithoMath math:
                writer.Append(math.Content);
                writer.Append('\n');
                break;
            case LithoList list:
                foreach (var item in list.Items)
                {
                    if (item.Checked is true)
                    {
                        writer.Append("[x] ");
                    }
                    else if (item.Checked is false)
                    {
                        writer.Append("[ ] ");
                    }

                    foreach (var child in item.Children)
                    {
                        RenderBlockPlain(child, writer);
                    }
                }

                break;
            case LithoTable table:
                foreach (var row in table.Rows)
                {
                    foreach (var cell in row)
                    {
                        RenderLabelInto(cell, writer);
                        writer.Append(' ');
                    }
                }

                break;
            case LithoBreak:
                break;
        }
    }

    private static string EncodeDiagram(string text)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var writer = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': writer.Append("&amp;"); break;
                case '<': writer.Append("&lt;"); break;
                default: writer.Append(ch); break;
            }
        }

        return writer.ToString();
    }

    private static string Encode(string text)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var writer = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': writer.Append("&amp;"); break;
                case '<': writer.Append("&lt;"); break;
                case '>': writer.Append("&gt;"); break;
                case '"': writer.Append("&quot;"); break;
                default: writer.Append(ch); break;
            }
        }

        return writer.ToString();
    }
}
