namespace LithoSharp.HtmlParsing;

internal sealed partial class CompactHtmlTreeBuilder
{
    private enum Mode { Initial, BeforeHtml, BeforeHead, InHead, InHeadNoscript, AfterHead, InBody,
        Text, InTable, InTableBody, InRow, InCell, InCaption, InColumnGroup, InSelect, InSelectInTable,
        InTemplate, AfterBody, AfterAfterBody, InFrameset, AfterFrameset, AfterAfterFrameset }
    private sealed class Node(int id, HtmlTreeNodeKind kind, string? name, string? ns, HtmlSpan? source)
    {
        public readonly int Id = id;
        public readonly HtmlTreeNodeKind Kind = kind;
        public readonly string? Name = name, Namespace = ns;
        public HtmlSpan? Source = source;
        public int? Parent, TemplateContent;
        public readonly List<int> Children = [];
        public readonly List<HtmlTreeAttribute> Attributes = [];
        public readonly List<HtmlText> TextChunks = [];
    }
    private readonly HtmlTokenizer tokenizer;
    private readonly HtmlTreeOptions options;
    private readonly HtmlTreeLimits limits;
    private readonly CancellationToken cancellationToken;
    private readonly List<Node> nodes = [];
    private readonly List<int> open = [];
    private readonly List<int?> formatting = [];
    private readonly List<Mode> templateModes = [];
    private readonly List<HtmlTreeDiagnostic> diagnostics = [];
    private readonly List<HtmlText> tableText = [];
    private readonly int root;
    private long operations;
    private int? head, body, form;
    private Mode mode, originalMode;
    private bool textModeActive, cdataAllowed, foster, framesetOk = true, ignoreNextLf;
    private HtmlTokenizationStatus status = HtmlTokenizationStatus.Complete;
    private HtmlDocumentMode documentMode;
    private HtmlToken token = new(HtmlTokenKind.EndOfFile, new(0, 0));

    private CompactHtmlTreeBuilder(string source, HtmlTreeOptions options, HtmlTreeLimits limits, CancellationToken cancellationToken)
    {
        this.options = options;
        this.limits = limits;
        this.cancellationToken = cancellationToken;
        limits.Validate();
        tokenizer = new(source, limits.Tokenizer, cancellationToken);
        if (options.Fragment is { } context)
        {
            if (string.IsNullOrWhiteSpace(context.Name) || context.Namespace is not
                (HtmlNamespaces.Html or HtmlNamespaces.Svg or HtmlNamespaces.MathMl))
                throw new ArgumentException("A fragment requires an element name and a supported namespace.", nameof(options));
            var name = context.Namespace == HtmlNamespaces.Html ? AsciiLower(context.Name) : context.Name;
            root = NewNode(HtmlTreeNodeKind.Element, name, context.Namespace, null);
        }
        else root = NewNode(HtmlTreeNodeKind.Document, null, null, null);
    }

    public static CompactHtmlTree Parse(string source, HtmlTreeOptions? options = null,
        HtmlTreeLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new CompactHtmlTreeBuilder(source, options ?? new(), limits ?? new(), cancellationToken).Build();
    }

    private CompactHtmlTree Build()
    {
        try
        {
            if (options.Fragment is { } context)
            {
                CopyAttributes(root, context.Attributes ?? [], context.Namespace);
                open.Add(root);
                if (IsHtml(root, "form")) form = root;
                if (IsHtml(root, "template")) templateModes.Add(Mode.InTemplate);
                ResetMode();
                if (tokenizer.Status == HtmlTokenizationStatus.Complete && context.Namespace == HtmlNamespaces.Html && TextMode(nodes[root].Name!) is { } text)
                {
                    tokenizer.SetContext(text, fragment: true); textModeActive = true;
                }
            }
            while (tokenizer.Status != HtmlTokenizationStatus.Failed)
            {
                Check();
                // SetContext resets script state: never call it while a text element emits chunks.
                var foreign = open.Count > 0 && nodes[Current].Namespace != HtmlNamespaces.Html;
                if (!textModeActive && foreign != cdataAllowed)
                {
                    tokenizer.SetContext(HtmlTextMode.Data, allowCdata: foreign);
                    cdataAllowed = foreign;
                }
                token = tokenizer.NextToken();
                if (tableText.Count > 0 && token.Kind != HtmlTokenKind.Text) FlushTableText();
                while (Process()) Check();
                if (token.Kind == HtmlTokenKind.EndOfFile) break;
            }
            ValidateFinalDepth();
        }
        catch (TreeBudgetException ex)
        {
            status = HtmlTokenizationStatus.Partial;
            var error = new HtmlTreeDiagnostic(ex.Message, token.Source, true);
            if (diagnostics.Count < limits.MaxDiagnostics) diagnostics.Add(error);
            else diagnostics[^1] = error;
        }
        if (tokenizer.Status == HtmlTokenizationStatus.Failed) status = HtmlTokenizationStatus.Failed;
        else if (tokenizer.Status == HtmlTokenizationStatus.Partial) status = HtmlTokenizationStatus.Partial;
        var frozen = new HtmlTreeNode[nodes.Count];
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frozen[node.Id] = new(node.Id, node.Kind, node.Name, node.Namespace, node.Parent,
                Array.AsReadOnly(node.Children.ToArray()), Array.AsReadOnly(node.Attributes.ToArray()),
                Array.AsReadOnly(node.TextChunks.ToArray()), node.Source, node.TemplateContent);
        }
        return new(root, options.Fragment is not null, Array.AsReadOnly(frozen), status, documentMode,
            Array.AsReadOnly(diagnostics.ToArray()), Array.AsReadOnly(tokenizer.Diagnostics.ToArray()));
    }

    private int Current => open.Count > 0 ? open[^1] : root;
    private string Name => token.Name?.Value ?? "";
    private bool Start(string name) => token.Kind == HtmlTokenKind.StartTag && Name == name;
    private bool End(string name) => token.Kind == HtmlTokenKind.EndTag && Name == name;
    private bool White => token.Kind == HtmlTokenKind.Text && token.Data!.Value.All(IsSpace);
    private static bool IsSpace(char c) => c is '\t' or '\n' or '\f' or '\r' or ' ';
    private static string AsciiLower(string value) => string.Concat(value.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c));
    private bool IsHtml(int id, string name) => nodes[id].Namespace == HtmlNamespaces.Html && nodes[id].Name == name;

    private void Check()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++operations > limits.MaxOperations) throw new TreeBudgetException("tree-operation-budget");
    }
    private sealed class TreeBudgetException(string code) : Exception(code);
    private void ValidateFinalDepth()
    {
        var pending = new Stack<(int Id, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            Check();
            var (id, depth) = pending.Pop();
            if (depth > limits.MaxDepth) throw new TreeBudgetException("tree-depth-budget");
            foreach (var child in nodes[id].Children) pending.Push((child, depth + 1));
            if (nodes[id].TemplateContent is int content) pending.Push((content, depth + 1));
        }
    }
    private void Error(string code, bool incomplete = false, HtmlSpan? source = null)
    {
        if (incomplete) status = HtmlTokenizationStatus.Partial;
        if (diagnostics.Count >= limits.MaxDiagnostics) throw new TreeBudgetException("tree-diagnostic-budget");
        diagnostics.Add(new(code, source ?? token.Source, incomplete));
    }
    private int NewNode(HtmlTreeNodeKind kind, string? name, string? ns, HtmlSpan? source)
    {
        Check();
        if (nodes.Count >= limits.MaxNodes) throw new TreeBudgetException("tree-node-budget");
        var id = nodes.Count;
        nodes.Add(new(id, kind, name, ns, source));
        return id;
    }
    private void Attach(int id, int parent, int? before = null)
    {
        Check();
        var depth = 0;
        for (int? ancestor = parent; ancestor is int a; ancestor = nodes[a].Parent)
        {
            Check();
            if (a == id) throw new TreeBudgetException("tree-cycle");
            if (++depth > limits.MaxDepth) throw new TreeBudgetException("tree-depth-budget");
        }
        if (nodes[id].Parent is int previous) nodes[previous].Children.Remove(id);
        var index = before is int sibling ? nodes[parent].Children.IndexOf(sibling) : -1;
        if (index < 0) nodes[parent].Children.Add(id);
        else nodes[parent].Children.Insert(index, id);
        nodes[id].Parent = parent;
    }
    private (int Parent, int? Before) Location(int? target = null)
    {
        var parent = target ?? Current;
        if (foster && nodes[parent].Namespace == HtmlNamespaces.Html &&
            nodes[parent].Name is "table" or "tbody" or "tfoot" or "thead" or "tr")
        {
            var lastTable = -1;
            for (var i = open.Count - 1; i >= 0; i--)
            {
                Check();
                if (IsHtml(open[i], "template")) return (nodes[open[i]].TemplateContent ?? open[i], null);
                if (IsHtml(open[i], "table")) { lastTable = i; break; }
            }
            if (lastTable >= 0)
            {
                var table = nodes[open[lastTable]];
                if (table.Parent is int tableParent) return (tableParent, table.Id);
                parent = lastTable > 0 ? open[lastTable - 1] : root;
            }
            else parent = open.Count > 0 ? open[0] : root;
        }
        if (nodes[parent].TemplateContent is int content) parent = content;
        return (parent, null);
    }
    private int Insert(string? name = null, string ns = HtmlNamespaces.Html, bool synthetic = false, bool push = true)
    {
        var id = NewNode(HtmlTreeNodeKind.Element, name ?? Name, ns, synthetic ? null : token.Source);
        if (!synthetic) CopyAttributes(id, token.Attributes ?? [], ns);
        var location = Location();
        Attach(id, location.Parent, location.Before);
        if (push)
        {
            if (open.Count >= limits.MaxDepth) throw new TreeBudgetException("tree-depth-budget");
            open.Add(id);
        }
        return id;
    }
    private int Clone(int original)
    {
        var node = nodes[original];
        var id = NewNode(node.Kind, node.Name, node.Namespace, null);
        nodes[id].Attributes.AddRange(node.Attributes);
        return id;
    }
    private void InsertOther(HtmlTreeNodeKind kind, int? parent = null)
    {
        var id = NewNode(kind, token.Name?.Value, null, token.Source);
        if (token.Data is not null) nodes[id].TextChunks.Add(token.Data);
        var location = parent is int p ? (p, (int?)null) : Location();
        Attach(id, location.Item1, location.Item2);
    }
    private void InsertText(HtmlText text)
    {
        if (text.Value.Length == 0) return;
        var location = Location();
        if (nodes[location.Parent].Kind == HtmlTreeNodeKind.Document) return;
        var children = nodes[location.Parent].Children;
        var index = location.Before is int before ? children.IndexOf(before) : children.Count;
        var previous = index > 0 ? children[index - 1] : -1;
        if (previous < 0 || nodes[previous].Kind != HtmlTreeNodeKind.Text)
        {
            previous = NewNode(HtmlTreeNodeKind.Text, null, null, null);
            Attach(previous, location.Parent, location.Before);
        }
        nodes[previous].TextChunks.Add(text);
    }
    private static HtmlText Slice(HtmlText text, int start, int length)
    {
        var segments = new List<HtmlTextSegment>();
        foreach (var segment in text.Segments)
        {
            var left = Math.Max(start, segment.ValueStart);
            var right = Math.Min(start + length, segment.ValueStart + segment.ValueLength);
            if (left >= right) continue;
            var source = segment.ValueLength == segment.Source.Length
                ? new HtmlSpan(segment.Source.Start + left - segment.ValueStart, right - left) : segment.Source;
            segments.Add(new(left - start, right - left, source));
        }
        var raw = segments.Count > 0 ? new HtmlSpan(segments[0].Source.Start, segments[^1].Source.End - segments[0].Source.Start) : new(text.Source.Start, 0);
        return new(text.Value.Substring(start, length), raw, segments.AsReadOnly());
    }
    private void Text(HtmlText text, bool foreign = false, bool reconstruct = false)
    {
        if (ignoreNextLf)
        {
            ignoreNextLf = false;
            if (text.Value.StartsWith('\n')) text = Slice(text, 1, text.Value.Length - 1);
        }
        void InsertNonNull(HtmlText part)
        {
            if (part.Value.Length == 0) return;
            if (reconstruct) ReconstructFormatting();
            if (!part.Value.All(IsSpace)) framesetOk = false;
            InsertText(part);
        }
        var start = 0;
        for (var i = 0; i < text.Value.Length; i++)
        {
            Check();
            if (text.Value[i] != '\0') continue;
            InsertNonNull(Slice(text, start, i - start));
            if (foreign) InsertNonNull(Slice(text, i, 1) with { Value = "\uFFFD" });
            start = i + 1;
        }
        InsertNonNull(start == 0 ? text : Slice(text, start, text.Value.Length - start));
    }
    private void PopTo(int index) { if (index > 0) open.RemoveRange(index, open.Count - index); }
    private void Pop() { if (open.Count > 1) open.RemoveAt(open.Count - 1); }
    private int Find(string name)
    {
        for (var i = open.Count - 1; i >= (options.Fragment is null ? 0 : 1); i--) { Check(); if (IsHtml(open[i], name)) return i; }
        return -1;
    }
    private bool Scope(string name, string kind = "general")
    {
        for (var i = open.Count - 1; i >= 0; i--)
        {
            Check();
            var node = nodes[open[i]];
            if (i == 0 && options.Fragment is not null) return false;
            if (node.Namespace == HtmlNamespaces.Html && node.Name == name) return true;
            if (kind == "select") { if (node.Name is not ("option" or "optgroup")) return false; continue; }
            if (node.Namespace == HtmlNamespaces.Html && (node.Name is "html" or "table" or "template" ||
                kind != "table" && node.Name is "applet" or "caption" or "td" or "th" or "marquee" or "object" ||
                kind == "button" && node.Name == "button" || kind == "list" && node.Name is "ol" or "ul")) return false;
            if (node.Namespace == HtmlNamespaces.Svg && node.Name is "foreignObject" or "desc" or "title" ||
                node.Namespace == HtmlNamespaces.MathMl && node.Name is "mi" or "mo" or "mn" or "ms" or "mtext" or "annotation-xml") return false;
        }
        return false;
    }
    private void Implied(string? except = null)
    {
        while (open.Count > 1 && nodes[Current].Namespace == HtmlNamespaces.Html && nodes[Current].Name != except &&
            nodes[Current].Name is "dd" or "dt" or "li" or "optgroup" or "option" or "p" or "rb" or "rp" or "rt" or "rtc") { Check(); Pop(); }
    }
    private void CloseP() { Implied("p"); PopTo(Find("p")); }
    private void MergeAttributes(int id)
    {
        foreach (var attribute in AdjustAttributes(token.Attributes ?? [], HtmlNamespaces.Html))
        {
            Check();
            if (nodes[id].Attributes.All(a => a.Name != attribute.Name))
            {
                if (nodes[id].Attributes.Count >= limits.MaxAttributesPerElement) throw new TreeBudgetException("tree-attribute-budget");
                nodes[id].Attributes.Add(attribute);
            }
        }
    }
    private void CopyAttributes(int id, IReadOnlyList<HtmlAttribute> attributes, string ns)
    {
        if (attributes.Count > limits.MaxAttributesPerElement) throw new TreeBudgetException("tree-attribute-budget");
        foreach (var attribute in AdjustAttributes(attributes, ns)) { Check(); nodes[id].Attributes.Add(attribute); }
    }
    private void CreateTemplateContent(int id) => nodes[id].TemplateContent = NewNode(HtmlTreeNodeKind.Fragment, null, null, null);
    private void ClearFormatting()
    {
        while (formatting.Count > 0)
        {
            var last = formatting[^1]; formatting.RemoveAt(formatting.Count - 1);
            if (last is null) break;
        }
    }
    private HtmlTextMode? TextMode(string name) => name switch
    {
        "title" or "textarea" => HtmlTextMode.RcData,
        "style" or "xmp" or "iframe" or "noembed" or "noframes" => HtmlTextMode.RawText,
        "noscript" when options.Scripting => HtmlTextMode.RawText,
        "script" => HtmlTextMode.ScriptData, "plaintext" => HtmlTextMode.PlainText, _ => null
    };
    private void StartText(string name)
    {
        Insert();
        originalMode = mode;
        mode = Mode.Text;
        tokenizer.SetContext(TextMode(name)!.Value, name);
        textModeActive = true;
        cdataAllowed = false;
        ignoreNextLf = name == "textarea";
    }
}
