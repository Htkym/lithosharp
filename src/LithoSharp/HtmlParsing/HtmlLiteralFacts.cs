namespace LithoSharp.HtmlParsing;

// Parse-only input for compiler diagnostics. No URI resolution, CSS/srcset splitting or site policy.
// A partial/failed tree must never appear to have a successful empty set of URL observations.
internal sealed record HtmlLiteralFacts(HtmlTokenizationStatus Status,
    IReadOnlyList<HtmlLiteralUrl> Urls, IReadOnlyList<HtmlTreeDiagnostic> Diagnostics,
    IReadOnlyList<HtmlTokenizationDiagnostic> TokenizerDiagnostics)
{
    internal static HtmlLiteralFacts Parse(string source, HtmlTreeOptions? options = null,
        HtmlTreeLimits? limits = null, CancellationToken cancellationToken = default)
    {
        var tree = CompactHtmlTreeBuilder.Parse(source, options, limits, cancellationToken);
        var urls = new List<HtmlLiteralUrl>();
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
                foreach (var attribute in element.Attributes)
                {
                    if (attribute.Name is not ("href" or "src" or "srcset" or "poster" or "action" or "formaction")
                        && !(element.Name == "object" && attribute.Name == "data")) continue;
                    var span = attribute.Value.Source;
                    var line = Array.BinarySearch(starts, span.Start);
                    if (line < 0) line = ~line - 1;
                    urls.Add(new(element.Name!, element.Namespace!, element.Source,
                        attribute.Name, attribute.Namespace, attribute.Prefix, attribute.RawName,
                        source.Substring(span.Start, span.Length), attribute.Value,
                        line + 1, span.Start - starts[line] + 1));
                }
            }
        }
        return new(tree.Status, urls.AsReadOnly(), tree.Diagnostics, tree.TokenizerDiagnostics);
    }
}

// Names use the tree's HTML/foreign adjustments; RawName/Value preserve the original UTF-16 mapping.
internal sealed record HtmlLiteralUrl(string ElementName, string ElementNamespace, HtmlSpan? ElementSource,
    string AttributeName, string? AttributeNamespace, string? AttributePrefix, HtmlText RawName,
    string RawValue, HtmlText Value, int Line, int Column);
