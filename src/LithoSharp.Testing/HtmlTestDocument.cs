using LithoSharp.HtmlParsing;

namespace LithoSharp.Testing;

/// <summary>An immutable parsed HTML snapshot owned by a <see cref="SiteTestDocument"/>.</summary>
/// <remarks>Disposing the owner invalidates the snapshot and all elements obtained from it.</remarks>
public sealed class HtmlTestDocument
{
    internal readonly CompactHtmlTree Tree;
    private readonly HtmlFacts facts;
    private int disposed;
    internal readonly int[] Previous;
    internal readonly int[] Next;
    internal readonly int[] Ordinals;
    internal readonly int[] ChildCounts;

    internal HtmlTestDocument(HtmlFacts facts)
    {
        this.facts = facts;
        Tree = facts.Tree;
        Previous = Enumerable.Repeat(-1, Tree.Nodes.Count).ToArray();
        Next = Enumerable.Repeat(-1, Tree.Nodes.Count).ToArray();
        Ordinals = new int[Tree.Nodes.Count];
        ChildCounts = new int[Tree.Nodes.Count];
        foreach (var parent in Tree.Nodes)
        {
            var previous = -1;
            foreach (var id in parent.Children)
            {
                if (Tree.Nodes[id].Kind != HtmlTreeNodeKind.Element) continue;
                Ordinals[id] = ++ChildCounts[parent.Id];
                Previous[id] = previous;
                if (previous >= 0) Next[previous] = id;
                previous = id;
            }
        }
    }

    /// <summary>Gets the normalized HTML title, or an empty string if absent.</summary>
    public string Title { get { EnsureAlive(); return facts.Title; } }

    /// <summary>Gets exact decoded text from the connected tree, including script and style text.</summary>
    public string TextContent { get { EnsureAlive(); return facts.TextContent(Tree.Root); } }

    /// <summary>Gets the document's element children in source tree order.</summary>
    public IReadOnlyList<HtmlTestElement> Children => ChildrenOf(Tree.Root);

    /// <summary>Returns the first matching connected element, or null.</summary>
    public HtmlTestElement? Query(string selector, CancellationToken cancellationToken = default) =>
        Select(Tree.Root, selector, firstOnly: true, cancellationToken).FirstOrDefault();

    /// <summary>Returns unique matching connected elements in tree order.</summary>
    public IReadOnlyList<HtmlTestElement> QueryAll(string selector, CancellationToken cancellationToken = default) =>
        Select(Tree.Root, selector, firstOnly: false, cancellationToken);

    internal void Invalidate() => Interlocked.Exchange(ref disposed, 1);

    internal void EnsureAlive() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, typeof(SiteTestDocument));

    internal HtmlTestElement Element(int id) => new(this, id);

    internal IReadOnlyList<HtmlTestElement> ChildrenOf(int id)
    {
        EnsureAlive();
        return Array.AsReadOnly(Tree.Nodes[id].Children.Where(child => Tree.Nodes[child].Kind == HtmlTreeNodeKind.Element)
            .Select(Element).ToArray());
    }

    internal string TextOf(int id) { EnsureAlive(); return facts.TextContent(id); }

    internal (int Line, int Column)? PositionOf(int id) { EnsureAlive(); return facts.Position(Tree.Nodes[id]); }

    internal IReadOnlyList<HtmlTestElement> Select(int root, string selector, bool firstOnly, CancellationToken cancellationToken)
    {
        EnsureAlive();
        var parsed = HtmlSelector.Parse(selector, cancellationToken);
        var scope = Tree.Nodes[root].Kind == HtmlTreeNodeKind.Element ? root :
            Tree.Nodes[root].Children.FirstOrDefault(id => Tree.Nodes[id].Kind == HtmlTreeNodeKind.Element, -1);
        var context = new HtmlSelector.MatchContext(this, scope, cancellationToken);
        var result = new List<HtmlTestElement>();
        var pending = new Stack<int>(Tree.Nodes[root].Children.Reverse());
        while (pending.TryPop(out var id))
        {
            context.Step();
            var node = Tree.Nodes[id];
            if (node.Kind == HtmlTreeNodeKind.Element && parsed.Matches(id, context))
            {
                result.Add(Element(id));
                if (firstOnly) break;
            }
            for (var i = node.Children.Count - 1; i >= 0; i--) pending.Push(node.Children[i]);
        }
        return result.AsReadOnly();
    }
}

/// <summary>A read-only element view whose lifetime belongs to its snapshot owner.</summary>
public sealed class HtmlTestElement
{
    private readonly HtmlTestDocument owner;
    private readonly int id;

    internal HtmlTestElement(HtmlTestDocument owner, int id) { this.owner = owner; this.id = id; }

    private HtmlTreeNode Node { get { owner.EnsureAlive(); return owner.Tree.Nodes[id]; } }

    /// <summary>Gets the parser's canonical local tag name.</summary>
    public string TagName => Node.Name!;

    /// <summary>Gets the element namespace URI.</summary>
    public string NamespaceUri => Node.Namespace!;

    /// <summary>Gets exact decoded descendant text, without applying visibility rules.</summary>
    public string TextContent => owner.TextOf(id);

    /// <summary>Gets the element parent, or null for a document or template fragment parent.</summary>
    public HtmlTestElement? Parent => Node.Parent is int parent && owner.Tree.Nodes[parent].Kind == HtmlTreeNodeKind.Element
        ? owner.Element(parent) : null;

    /// <summary>Gets the element children in tree order.</summary>
    public IReadOnlyList<HtmlTestElement> Children => owner.ChildrenOf(id);

    /// <summary>Gets inert template content's element children; these are excluded from ordinary queries.</summary>
    public IReadOnlyList<HtmlTestElement> TemplateContent => Node.TemplateContent is int content
        ? owner.ChildrenOf(content) : Array.AsReadOnly(Array.Empty<HtmlTestElement>());

    /// <summary>Gets the preceding element sibling, or null.</summary>
    public HtmlTestElement? PreviousSibling { get { _ = Node; return owner.Previous[id] is >= 0 and var previous ? owner.Element(previous) : null; } }

    /// <summary>Gets the following element sibling, or null.</summary>
    public HtmlTestElement? NextSibling { get { _ = Node; return owner.Next[id] is >= 0 and var next ? owner.Element(next) : null; } }

    /// <summary>Gets the raw UTF-16 source start, or null for a synthetic element.</summary>
    public int? SourceStart => Node.Source?.Start;

    /// <summary>Gets the raw UTF-16 source length, or null for a synthetic element.</summary>
    public int? SourceLength => Node.Source?.Length;

    /// <summary>Gets the one-based raw source line, or null for a synthetic element.</summary>
    public int? Line => owner.PositionOf(id)?.Line;

    /// <summary>Gets the one-based raw UTF-16 column, or null for a synthetic element.</summary>
    public int? Column => owner.PositionOf(id)?.Column;

    /// <summary>Gets a decoded attribute by local name and namespace URI, or null if absent.</summary>
    /// <remarks>A null namespace selects ordinary attributes. HTML attribute names are ASCII case insensitive.</remarks>
    public string? GetAttribute(string name, string? namespaceUri = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var node = Node;
        return node.Attributes.FirstOrDefault(attribute => attribute.Namespace == namespaceUri &&
            HtmlSelector.NameEquals(attribute.Name, name, node.Namespace == HtmlNamespaces.Html))?.Value.Value;
    }

    /// <summary>Returns the first matching descendant, or null; :scope refers to this element.</summary>
    public HtmlTestElement? Query(string selector, CancellationToken cancellationToken = default) =>
        owner.Select(id, selector, firstOnly: true, cancellationToken).FirstOrDefault();

    /// <summary>Returns unique matching descendants in tree order; :scope refers to this element.</summary>
    public IReadOnlyList<HtmlTestElement> QueryAll(string selector, CancellationToken cancellationToken = default) =>
        owner.Select(id, selector, firstOnly: false, cancellationToken);
}
