namespace LithoSharp.HtmlParsing;

internal sealed partial class CompactHtmlTreeBuilder
{
    private bool InTable()
    {
        if (token.Kind == HtmlTokenKind.Text && nodes[Current].Namespace == HtmlNamespaces.Html &&
            nodes[Current].Name is "table" or "tbody" or "tfoot" or "thead" or "tr")
        { tableText.Add(token.Data!); return false; }
        if (mode == Mode.InCell)
        {
            if (token.Kind == HtmlTokenKind.EndTag && Name is "td" or "th")
            {
                if (!Scope(Name, "table")) { Error("cell-not-in-scope"); return false; }
                CloseCell(); return false;
            }
            if (token.Kind == HtmlTokenKind.StartTag && Name is "caption" or "col" or "colgroup" or "tbody" or "td" or "tfoot" or "th" or "thead" or "tr" ||
                token.Kind == HtmlTokenKind.EndTag && Name is "table" or "tbody" or "tfoot" or "thead" or "tr")
            {
                if (token.Kind == HtmlTokenKind.EndTag && !Scope(Name, "table")) { Error("table-end-not-in-scope"); return false; }
                if (!Scope("td", "table") && !Scope("th", "table")) { Error("cell-not-in-scope"); return false; }
                CloseCell(); return true;
            }
            if (token.Kind == HtmlTokenKind.EndTag && Name is "body" or "caption" or "col" or "colgroup" or "html") { Error("unexpected-end-in-cell"); return false; }
            return InBody();
        }
        if (mode == Mode.InCaption)
        {
            if (End("caption")) { CloseCaption(); return false; }
            if (token.Kind == HtmlTokenKind.StartTag && Name is "caption" or "col" or "colgroup" or "tbody" or "td" or "tfoot" or "th" or "thead" or "tr" || End("table"))
            {
                if (!Scope("caption", "table")) { Error("caption-not-in-scope"); return false; }
                CloseCaption(); return true;
            }
            if (token.Kind == HtmlTokenKind.EndTag && Name is "body" or "col" or "colgroup" or "html" or "tbody" or "td" or "tfoot" or "th" or "thead" or "tr") { Error("unexpected-end-in-caption"); return false; }
            return InBody();
        }
        if (mode == Mode.InColumnGroup)
        {
            DropLeadingSpaces(insert: true);
            if (White) { Text(token.Data!); return false; }
            if (Misc() || IgnoreDoctype()) return false;
            if (Start("html")) return InBody();
            if (Start("col")) { Insert(push: false); return false; }
            if (Start("template") || End("template")) return InHead();
            if (End("col")) { Error("unexpected-col-end"); return false; }
            if (token.Kind == HtmlTokenKind.EndOfFile) return InBody();
            if (!IsHtml(Current, "colgroup") || open.Count == 1) { Error("unexpected-column-group-token"); return false; }
            Pop(); mode = Mode.InTable; return !End("colgroup");
        }
        if (mode == Mode.InRow)
        {
            if (token.Kind == HtmlTokenKind.StartTag && Name is "td" or "th")
            { ClearTo("tr"); Insert(); formatting.Add(null); mode = Mode.InCell; return false; }
            if (End("tr")) { CloseRow(); return false; }
            if (token.Kind == HtmlTokenKind.StartTag && Name is "caption" or "col" or "colgroup" or "tbody" or "tfoot" or "thead" or "tr" || End("table"))
            {
                if (!Scope("tr", "table")) { Error("row-not-in-scope"); return false; }
                CloseRow(); return true;
            }
            if (token.Kind == HtmlTokenKind.EndTag && Name is "tbody" or "tfoot" or "thead")
            {
                if (!Scope(Name, "table") || !Scope("tr", "table")) { Error("section-not-in-scope"); return false; }
                CloseRow(); return true;
            }
            if (token.Kind == HtmlTokenKind.EndTag && Name is "body" or "caption" or "col" or "colgroup" or "html" or "td" or "th") { Error("unexpected-end-in-row"); return false; }
        }
        if (mode == Mode.InTableBody)
        {
            if (Start("tr")) { ClearTo("tbody", "tfoot", "thead"); Insert(); mode = Mode.InRow; return false; }
            if (token.Kind == HtmlTokenKind.StartTag && Name is "td" or "th")
            { Error("missing-table-row"); ClearTo("tbody", "tfoot", "thead"); Insert("tr", synthetic: true); mode = Mode.InRow; return true; }
            if (token.Kind == HtmlTokenKind.EndTag && Name is "tbody" or "tfoot" or "thead")
            {
                if (!Scope(Name, "table")) { Error("section-not-in-scope"); return false; }
                ClearTo("tbody", "tfoot", "thead"); Pop(); mode = Mode.InTable; return false;
            }
            if (token.Kind == HtmlTokenKind.StartTag && Name is "caption" or "col" or "colgroup" or "tbody" or "tfoot" or "thead" || End("table"))
            {
                if (!Scope("tbody", "table") && !Scope("thead", "table") && !Scope("tfoot", "table")) { Error("section-not-in-scope"); return false; }
                ClearTo("tbody", "tfoot", "thead"); Pop(); mode = Mode.InTable; return true;
            }
            if (token.Kind == HtmlTokenKind.EndTag && Name is "body" or "caption" or "col" or "colgroup" or "html" or "td" or "th" or "tr") { Error("unexpected-end-in-section"); return false; }
        }
        // In-row and in-table-body delegate remaining tokens to in-table without changing the mode.
        if (Misc() || IgnoreDoctype()) return false;
        if (token.Kind == HtmlTokenKind.StartTag)
        {
            switch (Name)
            {
                case "caption": ClearTo("table"); formatting.Add(null); Insert(); mode = Mode.InCaption; return false;
                case "colgroup": ClearTo("table"); Insert(); mode = Mode.InColumnGroup; return false;
                case "col": ClearTo("table"); Insert("colgroup", synthetic: true); mode = Mode.InColumnGroup; return true;
                case "tbody": case "tfoot": case "thead": ClearTo("table"); Insert(); mode = Mode.InTableBody; return false;
                case "td": case "th": case "tr": ClearTo("table"); Insert("tbody", synthetic: true); mode = Mode.InTableBody; return true;
                case "table":
                    Error("nested-table");
                    if (!Scope("table", "table")) return false;
                    PopTo(Find("table")); ResetMode(); return true;
                case "style": case "script": case "template": return InHead();
                case "input" when token.Attributes?.Any(a => a.Name.Value == "type" && AsciiLower(a.Value.Value) == "hidden") == true:
                    Error("hidden-input-in-table"); Insert(push: false); return false;
                case "form":
                    Error("form-in-table");
                    if (form is not null || Find("template") >= 0) return false;
                    form = Insert(push: false); return false;
            }
        }
        if (End("table"))
        {
            if (!Scope("table", "table")) { Error("table-not-in-scope"); return false; }
            PopTo(Find("table")); ResetMode(); return false;
        }
        if (End("template")) return InHead();
        if (token.Kind == HtmlTokenKind.EndTag && Name is "body" or "caption" or "col" or "colgroup" or "html" or "tbody" or "td" or "tfoot" or "th" or "thead" or "tr")
        { Error("unexpected-end-in-table"); return false; }
        if (token.Kind == HtmlTokenKind.EndOfFile) return InBody();
        Error("foster-parented-token");
        var previous = foster; foster = true;
        try { return InBody(); }
        finally { foster = previous; }
    }
    private void FlushTableText()
    {
        // A tokenizer chunk is not a tree-construction character-run boundary.
        var whitespace = tableText.All(text => text.Value.All(c => IsSpace(c) || c == '\0'));
        var previous = foster; foster = !whitespace;
        try
        {
            if (!whitespace) { Error("foster-parented-text", source: tableText[0].Source); ReconstructFormatting(); }
            foreach (var text in tableText) { Check(); Text(text); }
        }
        finally { foster = previous; tableText.Clear(); }
    }
    private void ClearTo(params string[] names)
    {
        while (open.Count > 1 && nodes[Current].Name != "html" && !IsHtml(Current, "template") &&
            !(nodes[Current].Namespace == HtmlNamespaces.Html && names.Contains(nodes[Current].Name))) { Check(); Pop(); }
    }
    private void CloseCell()
    {
        var name = Scope("td", "table") ? "td" : "th";
        Implied(); PopTo(Find(name)); ClearFormatting(); mode = Mode.InRow;
    }
    private void CloseRow()
    {
        if (!Scope("tr", "table")) { Error("row-not-in-scope"); return; }
        ClearTo("tr"); Pop(); mode = Mode.InTableBody;
    }
    private void CloseCaption()
    {
        if (!Scope("caption", "table")) { Error("caption-not-in-scope"); return; }
        Implied(); PopTo(Find("caption")); ClearFormatting(); mode = Mode.InTable;
    }
    private bool InSelect()
    {
        if (mode == Mode.InSelectInTable && token.Kind is HtmlTokenKind.StartTag or HtmlTokenKind.EndTag &&
            Name is "caption" or "table" or "tbody" or "tfoot" or "thead" or "tr" or "td" or "th")
        {
            Error("table-token-in-select");
            if (token.Kind == HtmlTokenKind.EndTag && !Scope(Name, "table")) return false;
            if (!Scope("select", "select")) return false;
            PopTo(Find("select")); ResetMode(); return true;
        }
        if (token.Kind == HtmlTokenKind.Text) { Text(token.Data!); return false; }
        if (Misc() || IgnoreDoctype()) return false;
        if (Start("html")) return InBody();
        if (Start("option")) { if (IsHtml(Current, "option")) Pop(); Insert(); return false; }
        if (Start("optgroup"))
        { if (IsHtml(Current, "option")) Pop(); if (IsHtml(Current, "optgroup")) Pop(); Insert(); return false; }
        if (Start("hr"))
        { if (IsHtml(Current, "option")) Pop(); if (IsHtml(Current, "optgroup")) Pop(); Insert(push: false); return false; }
        if (End("option")) { if (IsHtml(Current, "option")) Pop(); else Error("option-not-current"); return false; }
        if (End("optgroup"))
        {
            if (IsHtml(Current, "option") && open.Count > 1 && IsHtml(open[open.Count - 2], "optgroup")) Pop();
            if (IsHtml(Current, "optgroup")) Pop(); else Error("optgroup-not-current");
            return false;
        }
        if (Start("select") || End("select") || token.Kind == HtmlTokenKind.StartTag && Name is "input" or "keygen" or "textarea")
        {
            if (!Scope("select", "select")) { Error("select-not-in-scope"); return false; }
            var again = Name is "input" or "keygen" or "textarea";
            PopTo(Find("select")); ResetMode(); return again;
        }
        if (Start("script") || Start("template") || End("template")) return InHead();
        if (token.Kind == HtmlTokenKind.EndOfFile) return InBody();
        Error("unexpected-select-token"); return false;
    }
}
