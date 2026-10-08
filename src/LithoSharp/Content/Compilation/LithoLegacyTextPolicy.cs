using System.Text;

namespace LithoSharp.Content.Compilation;

/// <summary>Site text presentation over the shared syntax; contains no HTML rendering.</summary>
internal static class LithoLegacyTextPolicy
{
    // Legacy headings omit image labels and include the visible inline math delimiters.
    // Generic Markdown facts keep their independent decoded text/mapping contract.
    internal static string Heading(IReadOnlyList<LithoInline> inlines, CancellationToken cancellationToken)
    {
        var writer = new StringBuilder();
        foreach (var (inline, exit) in LithoInline.Walk(inlines, includeImageLabels: false, cancellationToken: cancellationToken))
        {
            if (exit) continue;
            if (inline is LithoMathInline math) writer.Append(@"\(").Append(math.Content).Append(@"\)");
            else writer.Append(MdInlineFacts.LeafText(inline));
        }
        return writer.ToString();
    }

    internal static string Label(IReadOnlyList<LithoInline> inlines)
    {
        var writer = new StringBuilder();
        LabelInto(inlines, writer);
        return writer.ToString();
    }

    internal static string Plain(IReadOnlyList<LithoBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var writer = new StringBuilder();
        foreach (var block in blocks) BlockInto(block, writer);
        return writer.ToString();
    }

    private static void LabelInto(IReadOnlyList<LithoInline> inlines, StringBuilder writer)
    {
        foreach (var (inline, exit) in LithoInline.Walk(inlines))
            if (!exit) writer.Append(MdInlineFacts.LeafText(inline));
    }

    private static void BlockInto(LithoBlock block, StringBuilder writer)
    {
        switch (block)
        {
            case LithoParagraph paragraph:
                LabelInto(paragraph.Inlines, writer);
                writer.Append('\n');
                break;
            case LithoHeading heading:
                LabelInto(heading.Inlines, writer);
                writer.Append('\n');
                break;
            case LithoCode code:
                writer.Append(code.Text.TrimEnd('\n'));
                writer.Append('\n');
                break;
            case LithoQuote quote:
                foreach (var child in quote.Children)
                {
                    BlockInto(child, writer);
                }

                break;
            case LithoAdmonition admonition:
                foreach (var child in admonition.Children)
                {
                    BlockInto(child, writer);
                }

                break;
            case LithoDirective directive:
                foreach (var child in directive.Children)
                {
                    BlockInto(child, writer);
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
                        BlockInto(child, writer);
                    }
                }

                break;
            case LithoTable table:
                foreach (var row in table.Rows)
                {
                    foreach (var cell in row)
                    {
                        LabelInto(cell, writer);
                        writer.Append(' ');
                    }
                }

                break;
            case LithoBreak:
                break;
        }
    }

}
