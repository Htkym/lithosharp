using System.Text;
using LithoSharp.Search;
using ParsedHtmlText = global::LithoSharp.HtmlParsing.HtmlText;

namespace LithoSharp.HtmlParsing;

// One immutable, bounded tree per HTML input. No selector, mutation or cross-build cache contract.
internal sealed partial class HtmlFacts
{
    private readonly CompactHtmlTree tree;
    private readonly int[] lineStarts;
    private readonly CancellationToken cancellationToken;

    private HtmlFacts(string source, CompactHtmlTree tree, CancellationToken cancellationToken)
    {
        this.tree = tree;
        this.cancellationToken = cancellationToken;
        Elements = Array.AsReadOnly(tree.Elements().ToArray());
        var starts = new List<int> { 0 };
        for (var i = 0; i < source.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source[i] == '\r')
            {
                if (i + 1 < source.Length && source[i + 1] == '\n')
                    i++;
                starts.Add(i + 1);
            }
            else if (source[i] == '\n')
                starts.Add(i + 1);
        }
        lineStarts = starts.ToArray();
    }

    public IReadOnlyList<HtmlTreeNode> Elements { get; }

    internal CompactHtmlTree Tree => tree;

    public static HtmlFacts Parse(string source, CancellationToken cancellationToken = default, HtmlTreeOptions? options = null)
    {
        var tree = CompactHtmlTreeBuilder.Parse(source, options, cancellationToken: cancellationToken);
        if (tree.Status != HtmlTokenizationStatus.Complete)
            throw new HtmlFactsIncompleteException(tree);
        return new(source, tree, cancellationToken);
    }

    public static string? Attribute(HtmlTreeNode element, string name) =>
        element.Attributes.FirstOrDefault(attribute => attribute.Namespace is null && attribute.Name == name)?.Value.Value;

    public string? BaseHref => Elements.FirstOrDefault(element => element.Name == "base" && Attribute(element, "href") is not null) is { } node
        ? Attribute(node, "href") : null;

    public string Title => Elements.FirstOrDefault(element => element.Name == "title" && element.Namespace == HtmlNamespaces.Html) is { } title
        ? string.Join(" ", TextContent(title.Id).Split([' ', '\t', '\r', '\n', '\f'], StringSplitOptions.RemoveEmptyEntries)) : string.Empty;

    public IEnumerable<string> Anchors => Elements.SelectMany(element =>
        new[] { Attribute(element, "id"), element.Name == "a" ? Attribute(element, "name") : null })
        .Where(value => !string.IsNullOrEmpty(value)).Select(value => value!);

    public (int Line, int Column)? Position(HtmlTreeNode? element)
    {
        if (element?.Source is not { } span)
            return null;
        var line = Array.BinarySearch(lineStarts, span.Start);
        if (line < 0)
            line = ~line - 1;
        return (line + 1, span.Start - lineStarts[line] + 1);
    }

    private int? Body => Elements.FirstOrDefault(element => element.Name == "body" && element.Namespace == HtmlNamespaces.Html)?.Id;

    // DOM body text for the existing Markdown/MDX fallback.
    public string BodyText => Body is int body ? TextContent(body, search: true) : string.Empty;

    // Core indexing formerly separated stripped tags with spaces. Keep those word boundaries.
    public string SearchText => Body is int body ? TextContent(body, search: true, separateElements: true) : string.Empty;

    public string TextContent(int root, bool search = false, bool separateElements = false)
    {
        var text = new StringBuilder();
        foreach (var chunk in TextContentChunks(root, search, separateElements))
            text.Append(chunk?.Value ?? " ");
        return text.ToString();
    }

    private IEnumerable<ParsedHtmlText?> TextContentChunks(int root, bool search, bool separateElements)
    {
        var pending = new Stack<(int Id, bool Closing)>();
        pending.Push((root, false));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Closing)
            {
                yield return null;
                continue;
            }
            var node = tree.Nodes[item.Id];
            if (search && ExcludedFromSearch(node))
            {
                if (separateElements)
                    yield return null;
                continue;
            }
            if (separateElements && node.Kind is HtmlTreeNodeKind.Comment or HtmlTreeNodeKind.ProcessingInstruction)
                yield return null;
            if (node.Kind == HtmlTreeNodeKind.Text)
                foreach (var chunk in node.TextChunks)
                    yield return chunk;
            if (separateElements && node.Kind == HtmlTreeNodeKind.Element)
            {
                yield return null;
                pending.Push((node.Id, true));
            }
            for (var i = node.Children.Count - 1; i >= 0; i--)
                pending.Push((node.Children[i], false));
        }
    }

    public IReadOnlyList<SearchSection> SearchSections()
        => Array.AsReadOnly(SectionRoots().Select(section => new SearchSection(TextContent(section.Heading.Id, search: true),
            Attribute(section.Heading, "id")!, SiteGenerator.NormalizeForIndex(string.Concat(section.Body
                .Select(root => TextContent(root, search: true) + " "))))).ToArray());

    private IEnumerable<(HtmlTreeNode Heading, int[] Body)> SectionRoots()
    {
        // Each child's position is indexed once, so adjacent headings do not
        // repeatedly scan their parent's preceding siblings.
        var siblingPositions = new int[tree.Nodes.Count];
        foreach (var parent in tree.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < parent.Children.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                siblingPositions[parent.Children[i]] = i;
            }
        }
        foreach (var heading in SearchElements())
        {
            if (heading.Name is not ("h1" or "h2" or "h3" or "h4" or "h5" or "h6") || Attribute(heading, "id") is null)
                continue;
            var body = new List<int>();
            var siblings = tree.Nodes[heading.Parent!.Value].Children;
            for (var i = siblingPositions[heading.Id] + 1; i < siblings.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sibling = tree.Nodes[siblings[i]];
                if (sibling.Kind != HtmlTreeNodeKind.Element || ExcludedFromSearch(sibling))
                    continue;
                // Preserve the old sibling stop rule, including h0/h7..h9.
                if (sibling.Name is { Length: 2 } name && name[0] == 'h' && char.IsDigit(name[1]))
                    break;
                body.Add(sibling.Id);
            }
            yield return (heading, body.ToArray());
        }
    }

    private IEnumerable<HtmlTreeNode> SearchElements()
    {
        var pending = new Stack<int>();
        pending.Push(tree.Root);
        while (pending.TryPop(out var id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = tree.Nodes[id];
            if (ExcludedFromSearch(node))
                continue;
            if (node.Kind == HtmlTreeNodeKind.Element)
                yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
                pending.Push(node.Children[i]);
        }
    }

    private static bool ExcludedFromSearch(HtmlTreeNode node) =>
        node.Kind == HtmlTreeNodeKind.Element && node.Name is "script" or "style" or "nav" or "noscript";
}

// Consumers must not publish/index facts from an incomplete tree as though they were complete.
internal sealed class HtmlFactsIncompleteException(CompactHtmlTree tree) : InvalidOperationException(
    $"HTML facts require complete tree coverage; parser status={tree.Status} ({tree.Diagnostics.FirstOrDefault()?.Code ?? tree.TokenizerDiagnostics.FirstOrDefault()?.Code ?? "incomplete"}).")
{
    public HtmlTokenizationStatus Status { get; } = tree.Status;
}
