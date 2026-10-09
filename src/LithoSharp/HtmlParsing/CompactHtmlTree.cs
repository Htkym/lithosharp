using System.Text;

namespace LithoSharp.HtmlParsing;

internal static class HtmlNamespaces
{
    public const string Html = "http://www.w3.org/1999/xhtml";
    public const string Svg = "http://www.w3.org/2000/svg";
    public const string MathMl = "http://www.w3.org/1998/Math/MathML";
    public const string XLink = "http://www.w3.org/1999/xlink";
    public const string Xml = "http://www.w3.org/XML/1998/namespace";
    public const string Xmlns = "http://www.w3.org/2000/xmlns/";
}

internal enum HtmlTreeNodeKind { Document, Fragment, Element, Text, Comment, Doctype, ProcessingInstruction }
internal enum HtmlDocumentMode { NoQuirks, LimitedQuirks, Quirks }
internal sealed record HtmlFragmentContext(string Name, string Namespace = HtmlNamespaces.Html,
    IReadOnlyList<HtmlAttribute>? Attributes = null);
internal sealed record HtmlTreeOptions(bool Scripting = false, HtmlFragmentContext? Fragment = null);
internal sealed record HtmlTreeAttribute(string Name, string? Namespace, string? Prefix, HtmlText RawName, HtmlText Value);
internal sealed record HtmlTreeDiagnostic(string Code, HtmlSpan Source, bool IncompleteCoverage = false);

// lithosharp-html-tree/1. Tokenizer limits remain independently enforced.
internal sealed record HtmlTreeLimits
{
    public int MaxNodes { get; init; } = 250_000;
    public int MaxDepth { get; init; } = 512;
    public int MaxAttributesPerElement { get; init; } = 256;
    public int MaxOperations { get; init; } = 4_000_000;
    public int MaxDiagnostics { get; init; } = 1024;
    public HtmlTokenizerLimits Tokenizer { get; init; } = new();

    internal void Validate()
    {
        if (MaxNodes < 1 || MaxDepth < 1 || MaxAttributesPerElement < 1 || MaxOperations < 1 || MaxDiagnostics < 1)
            throw new ArgumentOutOfRangeException(nameof(HtmlTreeLimits), "Tree limits must be positive.");
        ArgumentNullException.ThrowIfNull(Tokenizer);
        Tokenizer.Validate();
    }
}

// Parent and child IDs are frozen only after all tree repairs. TemplateContent is a separate root.
internal sealed record HtmlTreeNode(int Id, HtmlTreeNodeKind Kind, string? Name, string? Namespace,
    int? Parent, IReadOnlyList<int> Children, IReadOnlyList<HtmlTreeAttribute> Attributes,
    IReadOnlyList<HtmlText> TextChunks, HtmlSpan? Source, int? TemplateContent = null);

internal sealed record CompactHtmlTree(int Root, bool IsFragment, IReadOnlyList<HtmlTreeNode> Nodes,
    HtmlTokenizationStatus Status, HtmlDocumentMode DocumentMode,
    IReadOnlyList<HtmlTreeDiagnostic> Diagnostics, IReadOnlyList<HtmlTokenizationDiagnostic> TokenizerDiagnostics)
{
    // Internal traversal only. Public selector/query contracts belong to HT-05.
    public IEnumerable<HtmlTreeNode> Elements(int? root = null)
    {
        var pending = new Stack<int>(Nodes[root ?? Root].Children.Reverse());
        while (pending.Count > 0)
        {
            var node = Nodes[pending.Pop()];
            if (node.Kind == HtmlTreeNodeKind.Element) yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--) pending.Push(node.Children[i]);
        }
    }

    public string TextContent(int id)
    {
        var text = new StringBuilder();
        var pending = new Stack<int>();
        pending.Push(id);
        while (pending.Count > 0)
        {
            var node = Nodes[pending.Pop()];
            if (node.Kind == HtmlTreeNodeKind.Text)
                foreach (var chunk in node.TextChunks) text.Append(chunk.Value);
            for (var i = node.Children.Count - 1; i >= 0; i--) pending.Push(node.Children[i]);
        }
        return text.ToString();
    }
}
