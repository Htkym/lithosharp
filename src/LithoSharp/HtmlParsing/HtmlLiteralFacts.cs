namespace LithoSharp.HtmlParsing;

// Parse-only input for compiler diagnostics. No URI resolution, CSS/srcset splitting or site policy.
// A partial/failed tree must never appear to have a successful empty set of URL observations.
internal sealed record HtmlLiteralFacts(HtmlTokenizationStatus Status,
    IReadOnlyList<HtmlLiteralUrl> Urls, IReadOnlyList<HtmlTreeDiagnostic> Diagnostics,
    IReadOnlyList<HtmlTokenizationDiagnostic> TokenizerDiagnostics, IReadOnlyList<HtmlLiteralCss> CssInputs)
{
    internal static HtmlLiteralFacts Parse(string source, HtmlTreeOptions? options = null,
        HtmlTreeLimits? limits = null, CancellationToken cancellationToken = default)
    {
        var tree = CompactHtmlTreeBuilder.Parse(source, options, limits, cancellationToken);
        var urls = new List<HtmlLiteralUrl>();
        var css = new List<HtmlLiteralCss>();
        var cssWork = limits?.MaxOperations ?? 4_000_000;
        var cssComplete = true;
        if (tree.Status == HtmlTokenizationStatus.Complete)
        {
            var lineStarts = new List<int> { 0 };
            for (var i = 0; i < source.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (source[i] == '\r')
                {
                    if (i + 1 < source.Length && source[i + 1] == '\n') i++;
                    lineStarts.Add(i + 1);
                }
                else if (source[i] == '\n') lineStarts.Add(i + 1);
            }
            var starts = lineStarts.ToArray();
            foreach (var element in tree.Elements())
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Runtime selects style-element text in preference to its style attribute.
                if (cssComplete && element.Name == "style")
                {
                    var text = CssText(element.Id);
                    if (text is null) { cssComplete = false; css.Clear(); }
                    else css.Add(new(text, true));
                }
                foreach (var attribute in element.Attributes)
                {
                    if (cssComplete && element.Name != "style" && attribute.Namespace is null && attribute.Name == "style")
                    {
                        cssWork -= attribute.Value.Value.Length + attribute.Value.Segments.Count + 1;
                        if (cssWork < 0) { cssComplete = false; css.Clear(); }
                        else css.Add(new(attribute.Value, false));
                    }
                    if (attribute.Name is not ("href" or "src" or "srcset" or "poster" or "action" or "formaction")
                        && !(element.Name == "object" && attribute.Name == "data")) continue;
                    var span = attribute.Value.Source;
                    var line = Array.BinarySearch(starts, span.Start);
                    if (line < 0) line = ~line - 1;
                    urls.Add(new(element.Name!, element.Namespace!, element.Source,
                        attribute.Name, attribute.Namespace, attribute.Prefix, attribute.RawName,
                        source.Substring(span.Start, span.Length), attribute.Value,
                        line + 1, span.Start - starts[line] + 1,
                        element.Attributes.FirstOrDefault(a => a.Namespace is null && a.Name == "rel")?.Value.Value));
                }
            }
        }
        return new(tree.Status, urls.AsReadOnly(), tree.Diagnostics, tree.TokenizerDiagnostics, css.AsReadOnly());

        // Same active-tree text traversal as runtime, from this private parse only.
        HtmlText? CssText(int root)
        {
            var value = new System.Text.StringBuilder();
            var segments = new List<HtmlTextSegment>();
            var pending = new Stack<int>();
            pending.Push(root);
            var start = -1;
            var end = -1;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cssWork-- <= 0) return null;
                var node = tree.Nodes[pending.Pop()];
                foreach (var chunk in node.Kind == HtmlTreeNodeKind.Text ? node.TextChunks : Array.Empty<HtmlText>())
                {
                    cssWork -= chunk.Value.Length + chunk.Segments.Count;
                    if (cssWork < 0) return null;
                    if (start < 0) start = chunk.Source.Start;
                    end = chunk.Source.End;
                    foreach (var segment in chunk.Segments)
                        segments.Add(segment with { ValueStart = value.Length + segment.ValueStart });
                    value.Append(chunk.Value);
                }
                for (var i = node.Children.Count - 1; i >= 0; i--) pending.Push(node.Children[i]);
            }
            return new(value.ToString(), new(start < 0 ? 0 : start, start < 0 ? 0 : end - start), segments.AsReadOnly());
        }
    }
}

// Names use the tree's HTML/foreign adjustments; RawName/Value preserve the original UTF-16 mapping.
internal sealed record HtmlLiteralUrl(string ElementName, string ElementNamespace, HtmlSpan? ElementSource,
    string AttributeName, string? AttributeNamespace, string? AttributePrefix, HtmlText RawName,
    string RawValue, HtmlText Value, int Line, int Column, string? Rel = null);

internal sealed record HtmlLiteralCss(HtmlText Input, bool IncludeImports);
