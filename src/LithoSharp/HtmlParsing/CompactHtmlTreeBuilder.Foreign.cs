namespace LithoSharp.HtmlParsing;

internal sealed partial class CompactHtmlTreeBuilder
{
    private static readonly Dictionary<string, string> SvgNames = CaseMap(
        "altGlyph altGlyphDef altGlyphItem animateColor animateMotion animateTransform clipPath feBlend feColorMatrix " +
        "feComponentTransfer feComposite feConvolveMatrix feDiffuseLighting feDisplacementMap feDistantLight feDropShadow " +
        "feFlood feFuncA feFuncB feFuncG feFuncR feGaussianBlur feImage feMerge feMergeNode feMorphology feOffset " +
        "fePointLight feSpecularLighting feSpotLight feTile feTurbulence foreignObject glyphRef linearGradient radialGradient textPath");
    private static readonly Dictionary<string, string> SvgAttributes = CaseMap(
        "attributeName attributeType baseFrequency baseProfile calcMode clipPathUnits diffuseConstant edgeMode filterUnits " +
        "glyphRef gradientTransform gradientUnits kernelMatrix kernelUnitLength keyPoints keySplines keyTimes lengthAdjust " +
        "limitingConeAngle markerHeight markerUnits markerWidth maskContentUnits maskUnits numOctaves pathLength " +
        "patternContentUnits patternTransform patternUnits pointsAtX pointsAtY pointsAtZ preserveAlpha preserveAspectRatio " +
        "primitiveUnits refX refY repeatCount repeatDur requiredExtensions requiredFeatures specularConstant specularExponent " +
        "spreadMethod startOffset stdDeviation stitchTiles surfaceScale systemLanguage tableValues targetX targetY textLength " +
        "viewBox viewTarget xChannelSelector yChannelSelector zoomAndPan");
    private static Dictionary<string, string> CaseMap(string names) => names.Split(' ').ToDictionary(AsciiLower, name => name, StringComparer.Ordinal);
    private static IReadOnlyList<HtmlTreeAttribute> AdjustAttributes(IReadOnlyList<HtmlAttribute> attributes, string ns)
    {
        var adjusted = new List<HtmlTreeAttribute>(attributes.Count);
        foreach (var attribute in attributes)
        {
            var name = attribute.Name.Value;
            string? attributeNs = null, prefix = null;
            if (ns == HtmlNamespaces.Svg && SvgAttributes.TryGetValue(name, out var mixedCase)) name = mixedCase;
            else if (ns == HtmlNamespaces.MathMl && name == "definitionurl") name = "definitionURL";
            if (ns != HtmlNamespaces.Html)
            {
                if (name is "xlink:actuate" or "xlink:arcrole" or "xlink:href" or "xlink:role" or "xlink:show" or "xlink:title" or "xlink:type")
                { attributeNs = HtmlNamespaces.XLink; prefix = "xlink"; name = name[6..]; }
                else if (name is "xml:base" or "xml:lang" or "xml:space")
                { attributeNs = HtmlNamespaces.Xml; prefix = "xml"; name = name[4..]; }
                else if (name == "xmlns:xlink") { attributeNs = HtmlNamespaces.Xmlns; prefix = "xmlns"; name = "xlink"; }
                else if (name == "xmlns") attributeNs = HtmlNamespaces.Xmlns;
            }
            adjusted.Add(new(name, attributeNs, prefix, attribute.Name, attribute.Value));
        }
        return adjusted;
    }
    private bool MathTextPoint(int id) => nodes[id].Namespace == HtmlNamespaces.MathMl && nodes[id].Name is "mi" or "mo" or "mn" or "ms" or "mtext";
    private bool HtmlPoint(int id)
    {
        var node = nodes[id];
        return node.Namespace == HtmlNamespaces.Svg && node.Name is "foreignObject" or "desc" or "title" ||
            node.Namespace == HtmlNamespaces.MathMl && node.Name == "annotation-xml" && node.Attributes.Any(a =>
                a.Name == "encoding" && AsciiLower(a.Value.Value) is "text/html" or "application/xhtml+xml");
    }
    private bool UseForeignRules()
    {
        if (open.Count == 0 || nodes[Current].Namespace == HtmlNamespaces.Html || token.Kind == HtmlTokenKind.EndOfFile) return false;
        if (MathTextPoint(Current) && (token.Kind == HtmlTokenKind.Text || token.Kind == HtmlTokenKind.StartTag && Name is not ("mglyph" or "malignmark"))) return false;
        if (nodes[Current].Namespace == HtmlNamespaces.MathMl && nodes[Current].Name == "annotation-xml" && Start("svg")) return false;
        return !(HtmlPoint(Current) && token.Kind is HtmlTokenKind.Text or HtmlTokenKind.StartTag);
    }
    private bool ProcessForeign()
    {
        if (token.Kind == HtmlTokenKind.Text) { Text(token.Data!, foreign: true); return false; }
        if (Misc() || IgnoreDoctype()) return false;
        if (token.Kind == HtmlTokenKind.StartTag && (Name is "b" or "big" or "blockquote" or "body" or "br" or "center" or
            "code" or "dd" or "div" or "dl" or "dt" or "em" or "embed" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or
            "head" or "hr" or "i" or "img" or "li" or "listing" or "menu" or "meta" or "nobr" or "ol" or "p" or "pre" or
            "ruby" or "s" or "small" or "span" or "strong" or "strike" or "sub" or "sup" or "table" or "tt" or "u" or "ul" or "var" ||
            Name == "font" && token.Attributes?.Any(a => a.Name.Value is "color" or "face" or "size") == true) ||
            token.Kind == HtmlTokenKind.EndTag && Name is "br" or "p")
        {
            Error("html-token-in-foreign-content");
            while (open.Count > 1 && nodes[Current].Namespace != HtmlNamespaces.Html && !MathTextPoint(Current) && !HtmlPoint(Current)) { Check(); Pop(); }
            if (nodes[Current].Namespace != HtmlNamespaces.Html && !MathTextPoint(Current) && !HtmlPoint(Current))
                Error("unsupported-foreign-fragment-breakout", true);
            return ProcessHtmlAfterForeign();
        }
        if (token.Kind == HtmlTokenKind.StartTag)
        {
            var ns = nodes[Current].Namespace!;
            var name = ns == HtmlNamespaces.Svg && SvgNames.TryGetValue(Name, out var adjusted) ? adjusted : Name;
            Insert(name, ns, push: !token.SelfClosing);
            return false;
        }
        if (token.Kind == HtmlTokenKind.EndTag)
        {
            for (var i = open.Count - 1; i > 0; i--)
            {
                Check();
                if (AsciiLower(nodes[open[i]].Name ?? "") == Name) { PopTo(i); return false; }
                if (nodes[open[i]].Namespace == HtmlNamespaces.Html) return ProcessHtmlAfterForeign();
            }
            Error("unmatched-foreign-end-tag");
        }
        return false;
    }
    private bool ProcessHtmlAfterForeign() => mode switch
    {
        Mode.InTable or Mode.InTableBody or Mode.InRow or Mode.InCell or Mode.InCaption or Mode.InColumnGroup => InTable(),
        Mode.InSelect or Mode.InSelectInTable => InSelect(), _ => InBody()
    };
}
