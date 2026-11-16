using Syntamark.Compilation;
using System.Text;

namespace LithoSharp.Content.Compilation;

internal readonly record struct MarkdownLinkProjection(string? Url, string? OriginalTarget);

/// <summary>
/// HTML and plain-text renderer for the Litho node tree. Text encoding matches
/// the reference output (&amp;, &lt;, &gt;, &quot;; apostrophes stay literal).
/// </summary>
internal static class LithoHtmlRenderer
{
    /// <summary>Renders blocks to HTML.</summary>
    /// <param name="blocks">Blocks in document order.</param>
    /// <param name="headingIds">Pre-assigned heading ids in pre-order document order.</param>
    /// <param name="resolveLink">Optional resolved document and asset targets.</param>
    /// <param name="componentHtml">Prepared component HTML indexed by directive start.</param>
    /// <param name="footnotes">Prepared footnote identities and references.</param>
    /// <param name="origins">Optional collection of emitted source ranges.</param>
    public static string RenderHtml(IReadOnlyList<LithoBlock> blocks, IReadOnlyList<string> headingIds,
        Func<string, bool, MarkdownLinkProjection?>? resolveLink = null,
        IReadOnlyDictionary<int, string>? componentHtml = null, MarkdownFootnoteRenderPlan? footnotes = null,
        ICollection<MarkdownRenderOrigin>? origins = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(headingIds);
        var writer = new StringBuilder();
        var state = new RenderState(blocks, headingIds, resolveLink, componentHtml,
            footnotes ?? MarkdownFootnoteRenderPlan.Create(blocks, headingIds), origins);
        foreach (var block in blocks)
        {
            RenderBlock(block, writer, state);
        }

        RenderFootnotes(writer, state);
        return writer.ToString();
    }

    private sealed class RenderState
    {
        private readonly Dictionary<LithoHeading, string> headingIds = new(ReferenceEqualityComparer.Instance);
        internal RenderState(IReadOnlyList<LithoBlock> blocks, IReadOnlyList<string> ids,
            Func<string, bool, MarkdownLinkProjection?>? resolveLink, IReadOnlyDictionary<int, string>? componentHtml,
            MarkdownFootnoteRenderPlan footnotes, ICollection<MarkdownRenderOrigin>? origins)
        {
            var index = 0;
            foreach (var heading in LithoMarkdownCompiler.WalkHeadings(blocks))
            {
                headingIds.Add(heading, index < ids.Count ? ids[index] : "section");
                index++;
            }
            ResolveLink = resolveLink;
            ComponentHtml = componentHtml;
            Footnotes = footnotes;
            Origins = origins;
        }
        internal Func<string, bool, MarkdownLinkProjection?>? ResolveLink { get; }
        internal IReadOnlyDictionary<int, string>? ComponentHtml { get; }
        internal MarkdownFootnoteRenderPlan Footnotes { get; }
        internal ICollection<MarkdownRenderOrigin>? Origins { get; }
        internal string IdFor(LithoHeading heading) => headingIds.GetValueOrDefault(heading, "section");
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
    public static string RenderPlainText(IReadOnlyList<LithoBlock> blocks) => LithoLegacyTextPolicy.Plain(blocks);

    private static void RenderBlock(LithoBlock block, StringBuilder writer, RenderState state)
    {
        switch (block)
        {
            case LithoParagraph paragraph:
                writer.Append("<p>");
                RenderInlineTree(paragraph.Inlines, writer, state.ResolveLink, state.Footnotes, state.Origins);

                writer.Append("</p>\n");
                break;
            case LithoHeading heading:
                var id = state.IdFor(heading);
                writer.Append($"<h{heading.Level} id=\"");
                var idStart = writer.Length;
                writer.Append(id);
                MarkdownRenderOrigin.Capture(state.Origins, idStart, writer.Length, heading.Span, tokenOnly: true);
                writer.Append("\">");
                RenderInlineTree(heading.Inlines, writer, state.ResolveLink, state.Footnotes, state.Origins);

                writer.Append($"</h{heading.Level}>\n");
                break;
            case LithoCode code:
                RenderCode(code, writer);
                break;
            case LithoAdmonition admonition:
                // Static shape mirrors the MDX runtime Admonition (aside with a
                // strong title defaulting to the kind); no Node.js involved.
                writer.Append($"<aside class=\"mdx-admonition mdx-{admonition.Kind}\" role=\"note\">");
                writer.Append("<strong>");
                AppendEncoded(writer, admonition.Title);
                writer.Append("</strong>\n");
                foreach (var child in admonition.Children)
                {
                    RenderBlock(child, writer, state);
                }

                writer.Append("</aside>\n");
                break;
            case LithoDirective directive:
                if (state.ComponentHtml?.TryGetValue(directive.Span.Start, out var html) == true)
                {
                    var componentStart = writer.Length;
                    writer.Append(html);
                    MarkdownRenderOrigin.Capture(state.Origins, componentStart, writer.Length, directive.Span, generatedCause: true);
                    break;
                }
                // Unrecognized container names render as plain divs (Markdig parity).
                if (directive.Name.Length == 0)
                {
                    writer.Append("<div>\n");
                }
                else
                {
                    writer.Append("<div class=\"");
                    AppendEncoded(writer, directive.Name);
                    writer.Append("\">\n");
                }

                foreach (var child in directive.Children)
                {
                    RenderBlock(child, writer, state);
                }

                writer.Append("</div>\n");
                break;
            case LithoMath math:
                writer.Append("<div class=\"math\">\n\\[\n");
                AppendEncoded(writer, math.Content);
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
                RenderRow(table.Rows[0], table.Align, true, writer, state);
                writer.Append("</tr>\n</thead>\n<tbody>\n");
                for (var i = 1; i < table.Rows.Count; i++)
                {
                    writer.Append("<tr>\n");
                    RenderRow(table.Rows[i], table.Align, false, writer, state);
                    writer.Append("</tr>\n");
                }

                writer.Append("</tbody>\n</table>\n");
                break;
            case LithoBreak:
                writer.Append("<hr />\n");
                break;
            case LithoHtmlBlock rawHtml:
                writer.Append("<p>");
                AppendEncoded(writer, rawHtml.Text);
                writer.Append("</p>\n");
                break;
            case LithoFootnoteDefinition:
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
            AppendEncodedDiagram(writer, code.Text);
            writer.Append("</pre>\n");
            return;
        }

        if (string.Equals(code.Info, "nomnoml", StringComparison.Ordinal))
        {
            writer.Append("<div class=\"nomnoml\">");
            AppendEncodedDiagram(writer, code.Text);
            writer.Append("</div>\n");
            return;
        }

        writer.Append("<pre");
        if (code.Meta is { } meta)
        {
            // Attribute names mirror the MDX worker's data-* code metadata.
            if (!string.IsNullOrEmpty(meta.Title))
            {
                writer.Append(" data-title=\"");
                AppendEncoded(writer, meta.Title);
                writer.Append('"');
            }

            if (!string.IsNullOrEmpty(meta.Highlight))
            {
                writer.Append(" data-highlight=\"");
                AppendEncoded(writer, meta.Highlight);
                writer.Append('"');
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
            writer.Append(" class=\"language-");
            AppendEncoded(writer, code.Info);
            writer.Append('"');
        }

        writer.Append('>');
        AppendEncoded(writer, code.Text);
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
            var childCount = 0;
            foreach (var child in item.Children)
            {
                if (childCount > 0)
                {
                    writer.Append('\n');
                }

                if (child is LithoParagraph paragraph)
                {
                    ArgumentNullException.ThrowIfNull(paragraph.Inlines, "inlines");
                    RenderInlineTree(paragraph.Inlines, writer, state.ResolveLink, state.Footnotes, state.Origins);
                }
                else
                {
                    var childStart = writer.Length;
                    RenderBlock(child, writer, state);
                    while (writer.Length > childStart && writer[writer.Length - 1] == '\n')
                    {
                        writer.Length--;
                    }
                }

                childCount++;
            }

            if (childCount > 1)
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


    private static void RenderRow(
        IReadOnlyList<IReadOnlyList<LithoInline>> cells,
        IReadOnlyList<string> align,
        bool header,
        StringBuilder writer, RenderState state)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            var style = i < align.Count && align[i].Length != 0 ? $" style=\"text-align: {align[i]};\"" : string.Empty;
            writer.Append(header ? $"<th{style}>" : $"<td{style}>");
            RenderInlineTree(cells[i], writer, state.ResolveLink, state.Footnotes, state.Origins);

            writer.Append(header ? "</th>\n" : "</td>\n");
        }
    }

    private static void RenderInlineTree(IReadOnlyList<LithoInline> inlines, StringBuilder writer,
        Func<string, bool, MarkdownLinkProjection?>? resolveLink = null, MarkdownFootnoteRenderPlan? footnotes = null,
        ICollection<MarkdownRenderOrigin>? origins = null)
    {
        var linkClosings = new Stack<string>();
        var deferredFootnotes = new Stack<List<LithoFootnoteReference>>();
        foreach (var (inline, exit) in LithoInline.Walk(inlines, includeImageLabels: false))
        {
            if (exit)
            {
                if (inline is LithoLink)
                {
                    writer.Append(linkClosings.Pop());
                    var references = deferredFootnotes.Pop();
                    if (deferredFootnotes.TryPeek(out var outer))
                        outer.AddRange(references);
                    else
                        foreach (var reference in references)
                            RenderFootnoteReference(reference, writer, footnotes);
                    continue;
                }
                writer.Append(inline switch
                {
                    LithoEmphasis { Level: 2 } => "</strong>",
                    LithoEmphasis => "</em>",
                    LithoStrike => "</del>",
                    _ => string.Empty,
                });
                continue;
            }
            switch (inline)
            {
                case LithoText text:
                    AppendEncoded(writer, text.Text);
                    break;
                case LithoEmphasis emphasis:
                    writer.Append(emphasis.Level == 2 ? "<strong>" : "<em>");
                    break;
                case LithoStrike strike:
                    writer.Append("<del>");
                    break;
                case LithoCodeSpan code:
                    writer.Append("<code>");
                    AppendEncoded(writer, code.Code);
                    writer.Append("</code>");
                    break;
                case LithoLink link:
                    var projection = resolveLink?.Invoke(link.Url, link.Image);
                    if (projection is { Url: null } unavailable)
                    {
                        writer.Append("<span class=\"okf-unresolved\" data-okf-target=\"");
                        AppendEncoded(writer, unavailable.OriginalTarget ?? link.Url);
                        writer.Append("\">");
                        if (link.Image)
                        {
                            AppendEncoded(writer, RenderLabelText(link.Label));
                            writer.Append("</span>");
                        }
                        else
                        {
                            linkClosings.Push("</span>");
                            deferredFootnotes.Push([]);
                        }
                        break;
                    }
                    var target = projection?.Url ?? link.Url;
                    if (link.Image)
                    {
                        writer.Append("<img src=\"");
                        var sourceStart = writer.Length;
                        AppendEncoded(writer, target);
                        MarkdownRenderOrigin.Capture(origins, sourceStart, writer.Length,
                            link.LinkSyntax?.Target is { Length: > 0 } imageTarget ? imageTarget : link.Span,
                            tokenOnly: link.LinkSyntax?.Target is not { Length: > 0 });
                        writer.Append("\" alt=\"");
                        AppendEncoded(writer, RenderLabelText(link.Label));
                        writer.Append('"');
                        if (link.Title is not null)
                        {
                            writer.Append(" title=\"");
                            AppendEncoded(writer, link.Title);
                            writer.Append('"');
                        }

                        writer.Append(" />");
                    }
                    else
                    {
                        linkClosings.Push("</a>");
                        deferredFootnotes.Push([]);
                        writer.Append("<a href=\"");
                        var targetStart = writer.Length;
                        AppendEncoded(writer, target);
                        MarkdownRenderOrigin.Capture(origins, targetStart, writer.Length,
                            link.LinkSyntax?.Target is { Length: > 0 } linkTarget ? linkTarget : link.Span,
                            tokenOnly: link.LinkSyntax?.Target is not { Length: > 0 });
                        writer.Append('"');
                        if (link.Title is not null)
                        {
                            writer.Append(" title=\"");
                            AppendEncoded(writer, link.Title);
                            writer.Append('"');
                        }

                        writer.Append('>');
                    }

                    break;
                case LithoAutolink autolink:
                    var autolinkProjection = resolveLink?.Invoke(autolink.Href, false);
                    if (autolinkProjection is { Url: null } unavailableAutolink)
                    {
                        writer.Append("<span class=\"okf-unresolved\" data-okf-target=\"");
                        AppendEncoded(writer, unavailableAutolink.OriginalTarget ?? autolink.Href);
                        writer.Append("\">");
                        AppendEncoded(writer, autolink.Text);
                        writer.Append("</span>");
                        break;
                    }
                    writer.Append("<a href=\"");
                    var autolinkStart = writer.Length;
                    AppendEncoded(writer, autolinkProjection?.Url ?? autolink.Href);
                    MarkdownRenderOrigin.Capture(origins, autolinkStart, writer.Length, autolink.Span, tokenOnly: true);
                    writer.Append("\">");
                    AppendEncoded(writer, autolink.Text);
                    writer.Append("</a>");
                    break;
                case LithoMathInline math:
                    writer.Append("<span class=\"math\">\\(");
                    AppendEncoded(writer, math.Content);
                    writer.Append("\\)</span>");
                    break;
                case LithoLineBreak lineBreak:
                    writer.Append(lineBreak.Hard ? "<br />\n" : "\n");
                    break;
                case LithoFootnoteReference reference:
                    if (reference.Number is { } number && footnotes?.ReferenceId(number, reference.Occurrence) is not null
                        && deferredFootnotes.TryPeek(out var deferred))
                        deferred.Add(reference);
                    else
                        RenderFootnoteReference(reference, writer, footnotes);
                    break;
                case LithoHtmlInline html:
                    AppendEncoded(writer, html.Text);
                    break;
                case LithoRawText:
                    throw new InvalidOperationException("Unresolved raw text reached the renderer.");
            }
        }
    }

    private static string RenderLabelText(IReadOnlyList<LithoInline> label) => LithoLegacyTextPolicy.Label(label);

    private static void RenderFootnoteReference(LithoFootnoteReference reference, StringBuilder writer,
        MarkdownFootnoteRenderPlan? footnotes)
    {
        if (reference.Number is { } number && footnotes?.DefinitionId(number) is { } definitionId
            && footnotes.ReferenceId(number, reference.Occurrence) is { } referenceId)
        {
            writer.Append("<sup class=\"footnote-ref\" id=\"").Append(referenceId)
                .Append("\"><a href=\"#").Append(definitionId).Append("\" role=\"doc-noteref\">")
                .Append(MarkdownFootnoteRenderPlan.Number(number)).Append("</a></sup>");
        }
        else
        {
            writer.Append("[^");
            AppendEncoded(writer, reference.Label);
            writer.Append(']');
        }
    }

    private static void RenderFootnotes(StringBuilder writer, RenderState state)
    {
        if (state.Footnotes.Definitions.Count == 0)
            return;
        writer.Append("<section class=\"footnotes\" role=\"doc-endnotes\">\n<ol>\n");
        foreach (var definition in state.Footnotes.Definitions)
        {
            var number = definition.Number!.Value;
            writer.Append("<li id=\"").Append(state.Footnotes.DefinitionId(number)).Append("\" value=\"")
                .Append(MarkdownFootnoteRenderPlan.Number(number)).Append("\">\n");
            foreach (var child in definition.Children)
                RenderBlock(child, writer, state);
            var backlinks = state.Footnotes.ReferencesFor(number);
            if (backlinks.Count != 0)
            {
                writer.Append("<p class=\"footnote-backlinks\">");
                foreach (var reference in backlinks)
                {
                    writer.Append("<a href=\"#").Append(state.Footnotes.ReferenceId(number, reference.Occurrence))
                        .Append("\" role=\"doc-backlink\" aria-label=\"Back to reference ")
                        .Append(MarkdownFootnoteRenderPlan.Number(number)).Append('-')
                        .Append(MarkdownFootnoteRenderPlan.Number(reference.Occurrence)).Append("\">&#8617;</a> ");
                }
                writer.Append("</p>\n");
            }
            writer.Append("</li>\n");
        }
        writer.Append("</ol>\n</section>\n");
    }

    private static void AppendEncodedDiagram(StringBuilder writer, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var replacement = text[i] switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                _ => null
            };
            if (replacement is null)
            {
                continue;
            }

            var length = i - start;
            if (length != 0)
            {
                if (length == 1)
                {
                    writer.Append(text[start]);
                }
                else
                {
                    writer.Append(text, start, length);
                }
            }
            writer.Append(replacement);
            start = i + 1;
        }
        var remaining = text.Length - start;
        if (remaining == 1)
        {
            writer.Append(text[start]);
        }
        else if (remaining > 1)
        {
            writer.Append(text, start, remaining);
        }
    }

    private static void AppendEncoded(StringBuilder writer, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var replacement = text[i] switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                _ => null
            };
            if (replacement is null)
            {
                continue;
            }

            var length = i - start;
            if (length != 0)
            {
                if (length == 1)
                {
                    writer.Append(text[start]);
                }
                else
                {
                    writer.Append(text, start, length);
                }
            }
            writer.Append(replacement);
            start = i + 1;
        }
        var remaining = text.Length - start;
        if (remaining == 1)
        {
            writer.Append(text[start]);
        }
        else if (remaining > 1)
        {
            writer.Append(text, start, remaining);
        }
    }
}
