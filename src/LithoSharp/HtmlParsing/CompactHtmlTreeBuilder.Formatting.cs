namespace LithoSharp.HtmlParsing;

internal sealed partial class CompactHtmlTreeBuilder
{
    private int? LastFormatting(string name)
    {
        for (var i = formatting.Count - 1; i >= 0 && formatting[i] is int id; i--)
        { Check(); if (IsHtml(id, name)) return id; }
        return null;
    }
    private void AddFormatting(int id)
    {
        var same = new List<int>();
        for (var i = formatting.Count - 1; i >= 0 && formatting[i] is int previous; i--)
        {
            Check();
            var a = nodes[previous]; var b = nodes[id];
            if (a.Name == b.Name && a.Namespace == b.Namespace && SameAttributes(a, b)) same.Add(i);
        }
        if (same.Count >= 3) formatting.RemoveAt(same[same.Count - 1]);
        formatting.Add(id);
    }
    private bool SameAttributes(Node left, Node right)
    {
        if (left.Attributes.Count != right.Attributes.Count) return false;
        // ponytail: at most 256 attributes by default; retain budgeted scans instead of another index.
        foreach (var attribute in left.Attributes)
        {
            var found = false;
            foreach (var candidate in right.Attributes)
            {
                Check();
                if (candidate.Name != attribute.Name || candidate.Namespace != attribute.Namespace || candidate.Value.Value != attribute.Value.Value) continue;
                found = true; break;
            }
            if (!found) return false;
        }
        return true;
    }
    private void ReconstructFormatting()
    {
        var index = formatting.Count - 1;
        if (index < 0 || formatting[index] is not int last || open.Contains(last)) return;
        while (index > 0 && formatting[index - 1] is int previous && !open.Contains(previous)) { Check(); index--; }
        for (; index < formatting.Count; index++)
        {
            Check();
            var clone = Clone(formatting[index]!.Value);
            var location = Location(); Attach(clone, location.Parent, location.Before);
            if (open.Count >= limits.MaxDepth) throw new TreeBudgetException("tree-depth-budget");
            open.Add(clone); formatting[index] = clone;
        }
    }
    private void Adoption(string name)
    {
        if (IsHtml(Current, name) && !formatting.Contains(Current)) { Pop(); return; }
        for (var outer = 0; outer < 8; outer++)
        {
            Check();
            if (LastFormatting(name) is not int element) { OtherEnd(name); return; }
            var elementIndex = open.IndexOf(element);
            if (elementIndex < 0) { Error("formatting-element-not-open"); formatting.Remove(element); return; }
            if (!Scope(name)) { Error("formatting-element-not-in-scope"); return; }
            if (element != Current) Error("misnested-formatting");
            var blockIndex = -1;
            for (var i = elementIndex + 1; i < open.Count; i++) { Check(); if (Special(open[i])) { blockIndex = i; break; } }
            if (blockIndex < 0) { PopTo(elementIndex); formatting.Remove(element); return; }
            if (elementIndex == 0) { Error("unsupported-fragment-formatting-repair", true); return; }
            var block = open[blockIndex];
            var ancestor = open[elementIndex - 1];
            var bookmark = formatting.IndexOf(element);
            var lastNode = block;
            var inner = 0;
            for (var i = blockIndex - 1; i > elementIndex; i--)
            {
                Check(); inner++;
                var node = open[i]; var active = formatting.IndexOf(node);
                if (inner > 3 && active >= 0)
                { formatting.RemoveAt(active); if (active < bookmark) bookmark--; active = -1; }
                if (active < 0) { open.RemoveAt(i); continue; }
                var clone = Clone(node);
                formatting[active] = clone; open[i] = clone;
                if (lastNode == block) bookmark = active + 1;
                Attach(lastNode, clone); lastNode = clone;
            }
            var oldFoster = foster;
            foster = nodes[ancestor].Namespace == HtmlNamespaces.Html && nodes[ancestor].Name is "table" or "tbody" or "tfoot" or "thead" or "tr";
            try { var location = Location(ancestor); Attach(lastNode, location.Parent, location.Before); }
            finally { foster = oldFoster; }
            var replacement = Clone(element);
            foreach (var child in nodes[block].Children.ToArray()) { Check(); Attach(child, replacement); }
            Attach(replacement, block);
            var oldActive = formatting.IndexOf(element);
            if (oldActive < bookmark) bookmark--;
            formatting.RemoveAt(oldActive);
            formatting.Insert(Math.Min(Math.Max(bookmark, 0), formatting.Count), replacement);
            open.Remove(element);
            open.Insert(open.IndexOf(block) + 1, replacement);
        }
    }
}
