namespace LithoSharp.HtmlParsing;

internal sealed partial class CompactHtmlTreeBuilder
{
    private bool Process()
    {
        Check();
        if (ignoreNextLf && token.Kind != HtmlTokenKind.Text) ignoreNextLf = false;
        if (mode == Mode.Text)
        {
            if (token.Kind == HtmlTokenKind.Text) { Text(token.Data!); return false; }
            if (token.Kind == HtmlTokenKind.EndOfFile && !IsHtml(Current, "plaintext")) Error("eof-in-text-element");
            Pop(); mode = originalMode; textModeActive = false;
            return token.Kind == HtmlTokenKind.EndOfFile;
        }
        if (UseForeignRules()) return ProcessForeign();
        switch (mode)
        {
            case Mode.Initial:
                if (White) return false;
                if (Misc(root)) return false;
                if (token.Kind == HtmlTokenKind.Doctype)
                {
                    InsertOther(HtmlTreeNodeKind.Doctype, root);
                    documentMode = token.ForceQuirks || Name != "html" ? HtmlDocumentMode.Quirks : HtmlDocumentMode.NoQuirks;
                    if (token.PublicIdentifier is not null || token.SystemIdentifier is not null && token.SystemIdentifier.Value != "about:legacy-compat")
                        Error("unsupported-legacy-doctype-mode", true);
                    mode = Mode.BeforeHtml; return false;
                }
                DropLeadingSpaces(); documentMode = HtmlDocumentMode.Quirks; mode = Mode.BeforeHtml; return true;
            case Mode.BeforeHtml:
                DropLeadingSpaces();
                if (White || IgnoreDoctype()) return false;
                if (Misc(root)) return false;
                if (token.Kind == HtmlTokenKind.EndTag && Name is not ("head" or "body" or "html" or "br")) { Error("unexpected-end-before-html"); return false; }
                var html = NewNode(HtmlTreeNodeKind.Element, "html", HtmlNamespaces.Html, Start("html") ? token.Source : null);
                Attach(html, root); open.Add(html); if (Start("html")) MergeAttributes(html);
                mode = Mode.BeforeHead; return !Start("html");
            case Mode.BeforeHead:
                DropLeadingSpaces();
                if (White || IgnoreDoctype()) return false;
                if (Misc()) return false;
                if (Start("html")) { MergeAttributes(open[0]); return false; }
                if (token.Kind == HtmlTokenKind.EndTag && Name is not ("head" or "body" or "html" or "br")) { Error("unexpected-end-before-head"); return false; }
                head = Insert("head", synthetic: !Start("head")); mode = Mode.InHead; return !Start("head");
            case Mode.InHead: return InHead();
            case Mode.InHeadNoscript:
                if (IgnoreDoctype()) return false;
                DropLeadingSpaces(insert: true);
                if (End("noscript")) { Pop(); mode = Mode.InHead; return false; }
                if (White || token.Kind is HtmlTokenKind.Comment or HtmlTokenKind.ProcessingInstruction ||
                    token.Kind == HtmlTokenKind.StartTag && Name is "basefont" or "bgsound" or "link" or "meta" or "noframes" or "style") return InHead();
                if (Start("html")) return InBody();
                if (Start("head") || Start("noscript") || token.Kind == HtmlTokenKind.EndTag && !End("br")) { Error("unexpected-head-noscript-token"); return false; }
                Error("unexpected-head-noscript-token"); Pop(); mode = Mode.InHead; return true;
            case Mode.AfterHead:
                DropLeadingSpaces(insert: true);
                if (White) { Text(token.Data!); return false; }
                if (Misc() || IgnoreDoctype()) return false;
                if (Start("html")) { MergeAttributes(open[0]); return false; }
                if (Start("body")) { body = Insert(); framesetOk = false; mode = Mode.InBody; return false; }
                if (Start("frameset")) { Insert(); mode = Mode.InFrameset; return false; }
                if (token.Kind == HtmlTokenKind.StartTag && IsHeadTag(Name) && head is int headId)
                {
                    Error("head-token-after-head"); open.Add(headId); InHead(); open.Remove(headId); return false;
                }
                if (Start("head") || token.Kind == HtmlTokenKind.EndTag && Name is not ("body" or "html" or "br")) { Error("unexpected-token-after-head"); return false; }
                body = Insert("body", synthetic: true); mode = Mode.InBody; return true;
            case Mode.InBody: return InBody();
            case Mode.InTable:
            case Mode.InTableBody:
            case Mode.InRow:
            case Mode.InCell:
            case Mode.InCaption:
            case Mode.InColumnGroup: return InTable();
            case Mode.InSelect:
            case Mode.InSelectInTable: return InSelect();
            case Mode.InTemplate:
                if (token.Kind is HtmlTokenKind.Text or HtmlTokenKind.Comment or HtmlTokenKind.ProcessingInstruction or HtmlTokenKind.Doctype) return InBody();
                if (token.Kind == HtmlTokenKind.StartTag && IsHeadTag(Name) || End("template")) return InHead();
                if (token.Kind == HtmlTokenKind.EndTag) { Error("unexpected-end-in-template"); return false; }
                if (token.Kind == HtmlTokenKind.EndOfFile) return EndTemplateAtEof();
                SetTemplateMode(Name switch
                {
                    "caption" or "colgroup" or "tbody" or "tfoot" or "thead" => Mode.InTable,
                    "col" => Mode.InColumnGroup, "tr" => Mode.InTableBody,
                    "td" or "th" => Mode.InRow, _ => Mode.InBody
                });
                return true;
            case Mode.AfterBody:
            case Mode.AfterAfterBody:
                if (White || Start("html")) return InBody();
                if (Misc(mode == Mode.AfterAfterBody ? root : open[0]) || IgnoreDoctype()) return false;
                if (End("html") && options.Fragment is null) { mode = Mode.AfterAfterBody; return false; }
                if (token.Kind == HtmlTokenKind.EndOfFile) return false;
                Error("unexpected-after-body"); mode = Mode.InBody; return true;
            case Mode.InFrameset:
                if (token.Kind == HtmlTokenKind.Text) { FramesetText(); return false; }
                if (Misc() || IgnoreDoctype()) return false;
                if (Start("html")) return InBody();
                if (Start("frameset")) { Insert(); return false; }
                if (Start("frame")) { Insert(push: false); return false; }
                if (Start("noframes")) return InHead();
                if (End("frameset"))
                {
                    if (open.Count == 1) { Error("unexpected-frameset-end"); return false; }
                    Pop(); if (options.Fragment is null && !IsHtml(Current, "frameset")) mode = Mode.AfterFrameset;
                    return false;
                }
                if (token.Kind != HtmlTokenKind.EndOfFile) Error("unexpected-frameset-token");
                return false;
            case Mode.AfterFrameset:
            case Mode.AfterAfterFrameset:
                if (token.Kind == HtmlTokenKind.Text) { FramesetText(); return false; }
                if (Misc(mode == Mode.AfterAfterFrameset ? root : null) || IgnoreDoctype()) return false;
                if (Start("html")) return InBody();
                if (Start("noframes")) return InHead();
                if (End("html")) { mode = Mode.AfterAfterFrameset; return false; }
                if (token.Kind != HtmlTokenKind.EndOfFile) Error("unexpected-after-frameset");
                return false;
            default: throw new InvalidOperationException("Unknown tree insertion mode.");
        }
    }
    private bool IgnoreDoctype()
    {
        if (token.Kind != HtmlTokenKind.Doctype) return false;
        Error("unexpected-doctype"); return true;
    }
    private bool Misc(int? parent = null)
    {
        if (token.Kind == HtmlTokenKind.Comment) { InsertOther(HtmlTreeNodeKind.Comment, parent); return true; }
        if (token.Kind == HtmlTokenKind.ProcessingInstruction) { InsertOther(HtmlTreeNodeKind.ProcessingInstruction, parent); return true; }
        return false;
    }
    private static bool IsHeadTag(string name) => name is "base" or "basefont" or "bgsound" or "link" or "meta" or
        "title" or "style" or "noframes" or "script" or "template";
    private bool InHead()
    {
        if (White) { Text(token.Data!); return false; }
        if (Misc() || IgnoreDoctype()) return false;
        if (Start("html")) return InBody();
        if (token.Kind == HtmlTokenKind.StartTag)
        {
            switch (Name)
            {
                case "base": case "basefont": case "bgsound": case "link": case "meta": Insert(push: false); return false;
                case "title": case "style": case "noframes": case "script": StartText(Name); return false;
                case "noscript":
                    if (options.Scripting) StartText(Name);
                    else { Insert(); mode = Mode.InHeadNoscript; }
                    return false;
                case "template":
                    var template = Insert(); CreateTemplateContent(template); formatting.Add(null); framesetOk = false;
                    templateModes.Add(Mode.InTemplate); mode = Mode.InTemplate;
                    if (token.Attributes?.Any(a => a.Name.Value == "shadowrootmode") == true) Error("unsupported-declarative-shadow-root", true);
                    return false;
                case "head": Error("duplicate-head"); return false;
            }
        }
        if (End("template")) { CloseTemplate(); return false; }
        if (token.Kind == HtmlTokenKind.EndTag && Name is not ("head" or "body" or "html" or "br")) { Error("unexpected-end-in-head"); return false; }
        DropLeadingSpaces(insert: true); Pop(); mode = Mode.AfterHead; return !End("head");
    }
    private void DropLeadingSpaces(bool insert = false)
    {
        if (token.Kind != HtmlTokenKind.Text) return;
        var count = 0;
        while (count < token.Data!.Value.Length && IsSpace(token.Data.Value[count])) { Check(); count++; }
        if (count == 0) return;
        if (insert) Text(Slice(token.Data!, 0, count));
        token = token with { Data = Slice(token.Data!, count, token.Data!.Value.Length - count) };
    }
    private bool InBody()
    {
        if (token.Kind == HtmlTokenKind.Text) { Text(token.Data!, reconstruct: true); return false; }
        if (Misc() || IgnoreDoctype()) return false;
        if (token.Kind == HtmlTokenKind.EndOfFile) return EndTemplateAtEof();
        if (token.Kind == HtmlTokenKind.StartTag)
        {
            var name = Name;
            if (name == "html") { if (Find("template") < 0 && options.Fragment is null) MergeAttributes(open[0]); return false; }
            if (IsHeadTag(name)) return InHead();
            if (name == "body") { if (body is int id && Find("template") < 0) { MergeAttributes(id); framesetOk = false; } return false; }
            if (name == "frameset")
            {
                if (!framesetOk || body is not int bodyId || options.Fragment is not null) { Error("unexpected-frameset-start"); return false; }
                if (nodes[bodyId].Parent is int parent) nodes[parent].Children.Remove(bodyId);
                nodes[bodyId].Parent = null; open.RemoveRange(1, open.Count - 1); Insert(); mode = Mode.InFrameset; return false;
            }
            if (name is "caption" or "col" or "colgroup" or "frame" or "head" or "tbody" or "td" or "tfoot" or "th" or "thead" or "tr")
            { Error("table-token-outside-table"); return false; }
            if (name is "p" || IsBlock(name))
            {
                if (Scope("p", "button")) CloseP(); Insert(); return false;
            }
            if (IsHeading(name))
            {
                if (Scope("p", "button")) CloseP();
                if (nodes[Current].Name is { } current && IsHeading(current)) { Error("nested-heading"); Pop(); }
                Insert(); return false;
            }
            if (name is "pre" or "listing")
            {
                if (Scope("p", "button")) CloseP(); Insert(); ignoreNextLf = true; framesetOk = false; return false;
            }
            if (name == "form")
            {
                if (form is not null && Find("template") < 0) { Error("nested-form"); return false; }
                if (Scope("p", "button")) CloseP();
                var id = Insert(); if (Find("template") < 0) form = id; return false;
            }
            if (name is "li" or "dd" or "dt")
            {
                framesetOk = false;
                for (var i = open.Count - 1; i > 0; i--)
                {
                    Check(); var node = nodes[open[i]];
                    if (node.Namespace == HtmlNamespaces.Html && (node.Name == name || name is "dd" or "dt" && node.Name is "dd" or "dt"))
                    { Implied(node.Name); PopTo(i); break; }
                    if (Special(open[i]) && node.Name is not ("address" or "div" or "p")) break;
                }
                if (Scope("p", "button")) CloseP(); Insert(); return false;
            }
            if (name == "plaintext") { if (Scope("p", "button")) CloseP(); StartText(name); return false; }
            if (name == "button")
            {
                if (Scope("button")) { Error("nested-button"); Implied(); PopTo(Find("button")); }
                ReconstructFormatting(); Insert(); framesetOk = false; return false;
            }
            if (IsFormatting(name))
            {
                if (name == "a" && LastFormatting("a") is int a)
                { Error("nested-anchor"); Adoption("a"); formatting.Remove(a); open.Remove(a); }
                ReconstructFormatting();
                if (name == "nobr" && Scope("nobr")) { Error("nested-nobr"); Adoption("nobr"); ReconstructFormatting(); }
                AddFormatting(Insert()); return false;
            }
            if (name is "applet" or "marquee" or "object") { ReconstructFormatting(); Insert(); formatting.Add(null); framesetOk = false; return false; }
            if (name == "table")
            {
                if (documentMode != HtmlDocumentMode.Quirks && Scope("p", "button")) CloseP();
                Insert(); framesetOk = false; mode = Mode.InTable; return false;
            }
            if (name == "image") { token = token with { Name = token.Name! with { Value = "img" } }; return true; }
            if (IsVoid(name))
            {
                if (name == "hr" && Scope("p", "button")) CloseP();
                ReconstructFormatting(); Insert(push: false);
                if (name != "input" || token.Attributes?.Any(a => a.Name.Value == "type" && AsciiLower(a.Value.Value) == "hidden") != true) framesetOk = false;
                return false;
            }
            if (name is "textarea" or "xmp" or "iframe" or "noembed" || name == "noscript" && options.Scripting)
            {
                if (name == "xmp" && Scope("p", "button")) CloseP();
                if (name == "xmp") ReconstructFormatting();
                StartText(name); framesetOk = false; return false;
            }
            if (name == "select")
            {
                ReconstructFormatting(); Insert(); framesetOk = false;
                mode = mode is Mode.InTable or Mode.InTableBody or Mode.InRow or Mode.InCell or Mode.InCaption ? Mode.InSelectInTable : Mode.InSelect;
                return false;
            }
            if (name is "option" or "optgroup") { if (IsHtml(Current, "option")) Pop(); ReconstructFormatting(); Insert(); return false; }
            if (name is "rb" or "rtc") { if (Scope("ruby")) Implied(); Insert(); return false; }
            if (name is "rp" or "rt") { if (Scope("ruby")) Implied("rtc"); Insert(); return false; }
            if (name is "svg" or "math")
            {
                ReconstructFormatting(); Insert(ns: name == "svg" ? HtmlNamespaces.Svg : HtmlNamespaces.MathMl, push: !token.SelfClosing); return false;
            }
            ReconstructFormatting(); Insert();
            if (token.SelfClosing) Error("non-void-html-element-start-tag-with-trailing-solidus");
            return false;
        }
        if (token.Kind == HtmlTokenKind.EndTag)
        {
            var name = Name;
            if (name == "template") { CloseTemplate(); return false; }
            if (name is "body" or "html")
            {
                if (!Scope("body")) { Error("body-not-in-scope"); return false; }
                mode = Mode.AfterBody; return name == "html";
            }
            if (name == "p")
            {
                if (!Scope("p", "button")) { Error("p-not-in-scope"); Insert("p", synthetic: true); }
                CloseP(); return false;
            }
            if (name == "form" && Find("template") < 0)
            {
                var formId = form; form = null;
                if (formId is not int id || !Scope("form")) { Error("form-not-in-scope"); return false; }
                Implied(); open.Remove(id); return false;
            }
            if (IsFormatting(name)) { Adoption(name); return false; }
            if (name == "br")
            {
                Error("unexpected-br-end");
                token = new(HtmlTokenKind.StartTag, token.Source, Name: token.Name);
                return true;
            }
            if (IsBlock(name) || name is "button" or "pre" or "listing" or "form" or "li" or "dd" or "dt" or "applet" or "marquee" or "object" || IsHeading(name))
            {
                var index = Find(name);
                if (IsHeading(name))
                {
                    index = -1;
                    for (var i = open.Count - 1; i >= 0; i--)
                    {
                        Check();
                        if (nodes[open[i]].Namespace == HtmlNamespaces.Html && IsHeading(nodes[open[i]].Name ?? "")) { index = i; break; }
                    }
                }
                var target = index >= 0 ? nodes[open[index]].Name! : name;
                if (index < 0 || !Scope(target, name == "li" ? "list" : "general")) { Error("end-tag-not-in-scope"); return false; }
                Implied(name); PopTo(index);
                if (name is "applet" or "marquee" or "object") ClearFormatting();
                return false;
            }
            OtherEnd(name);
        }
        return false;
    }
    private void OtherEnd(string name)
    {
        for (var i = open.Count - 1; i >= 0; i--)
        {
            Check();
            if (IsHtml(open[i], name)) { Implied(name); PopTo(i); return; }
            if (Special(open[i])) { Error("unexpected-end-tag"); return; }
        }
    }
    private void FramesetText()
    {
        var text = token.Data!;
        var start = 0;
        var ignored = false;
        for (var i = 0; i < text.Value.Length; i++)
        {
            Check();
            if (IsSpace(text.Value[i])) continue;
            Text(Slice(text, start, i - start)); start = i + 1; ignored = true;
        }
        Text(Slice(text, start, text.Value.Length - start));
        if (ignored) Error("unexpected-frameset-text");
    }
    private static bool IsHeading(string name) => name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6';
    private static bool IsBlock(string name) => name is "address" or "article" or "aside" or "blockquote" or "center" or "details" or
        "dialog" or "dir" or "div" or "dl" or "fieldset" or "figcaption" or "figure" or "footer" or "header" or "hgroup" or
        "main" or "menu" or "nav" or "ol" or "search" or "section" or "summary" or "ul";
    private static bool IsVoid(string name) => name is "area" or "base" or "basefont" or "bgsound" or "br" or "col" or "embed" or
        "hr" or "img" or "input" or "keygen" or "link" or "meta" or "param" or "source" or "track" or "wbr";
    private static bool IsFormatting(string name) => name is "a" or "b" or "big" or "code" or "em" or "font" or "i" or "nobr" or
        "s" or "small" or "strike" or "strong" or "tt" or "u";
    private bool Special(int id)
    {
        var node = nodes[id];
        if (node.Namespace == HtmlNamespaces.Svg) return node.Name is "foreignObject" or "desc" or "title";
        if (node.Namespace == HtmlNamespaces.MathMl) return node.Name is "mi" or "mo" or "mn" or "ms" or "mtext" or "annotation-xml";
        return IsBlock(node.Name ?? "") || IsHeading(node.Name ?? "") || IsVoid(node.Name ?? "") ||
            node.Name is "applet" or "body" or "button" or "caption" or "colgroup" or "dd" or "dt" or "form" or "frameset" or
                "head" or "html" or "iframe" or "li" or "listing" or "marquee" or "noembed" or "noframes" or "noscript" or
                "object" or "p" or "plaintext" or "pre" or "script" or "select" or "style" or "table" or "tbody" or "td" or
                "template" or "textarea" or "tfoot" or "th" or "thead" or "title" or "tr" or "xmp";
    }
    private void SetTemplateMode(Mode value)
    {
        if (templateModes.Count > 0) templateModes[templateModes.Count - 1] = value;
        mode = value;
    }
    private void CloseTemplate()
    {
        var index = Find("template");
        if (index <= 0) { Error("template-not-in-scope"); return; }
        Implied(); PopTo(index); ClearFormatting();
        if (templateModes.Count > 0) templateModes.RemoveAt(templateModes.Count - 1);
        ResetMode();
    }
    private bool EndTemplateAtEof()
    {
        if (Find("template") <= 0) return false;
        Error("eof-in-template"); CloseTemplate(); return true;
    }
    private void ResetMode()
    {
        for (var i = open.Count - 1; i >= 0; i--)
        {
            Check();
            var node = nodes[open[i]];
            if (node.Namespace != HtmlNamespaces.Html) continue;
            var selected = node.Name switch
            {
                "select" => Mode.InSelect, "td" or "th" when i > 0 => Mode.InCell,
                "tr" => Mode.InRow, "tbody" or "thead" or "tfoot" => Mode.InTableBody,
                "caption" => Mode.InCaption, "colgroup" => Mode.InColumnGroup, "table" => Mode.InTable,
                "template" => templateModes.Count > 0 ? templateModes[templateModes.Count - 1] : Mode.InTemplate,
                "head" when i > 0 => Mode.InHead, "body" => Mode.InBody, "frameset" => Mode.InFrameset,
                "html" => head is null ? Mode.BeforeHead : Mode.AfterHead, _ => (Mode?)null
            };
            if (selected is { } found)
            {
                if (found == Mode.InSelect)
                    for (var ancestor = i - 1; ancestor >= 0; ancestor--)
                    {
                        Check();
                        if (IsHtml(open[ancestor], "template")) break;
                        if (IsHtml(open[ancestor], "table")) { found = Mode.InSelectInTable; break; }
                    }
                mode = found; return;
            }
        }
        mode = Mode.InBody;
    }
}
