namespace LithoSharp.HtmlParsing;

internal sealed partial class CompactHtmlTreeBuilder
{
    // WHATWG "initial" insertion mode. Only complete documents reach this classifier.
    // https://html.spec.whatwg.org/multipage/parsing.html#the-initial-insertion-mode
    private static HtmlDocumentMode ClassifyDoctype(HtmlToken doctype)
    {
        if (doctype.ForceQuirks || doctype.Name?.Value != "html") return HtmlDocumentMode.Quirks;
        var publicId = doctype.PublicIdentifier?.Value;
        var systemId = doctype.SystemIdentifier?.Value;
        if (DoctypeIdMatches(publicId, "-//W3O//DTD W3 HTML Strict 3.0//EN//") ||
            DoctypeIdMatches(publicId, "-/W3C/DTD HTML 4.0 Transitional/EN") ||
            DoctypeIdMatches(publicId, "HTML") ||
            DoctypeIdMatches(systemId, "http://www.ibm.com/data/dtd/v11/ibmxhtml1-transitional.dtd"))
            return HtmlDocumentMode.Quirks;
        foreach (var prefix in QuirksPublicIdPrefixes)
            if (DoctypeIdMatches(publicId, prefix, prefix: true)) return HtmlDocumentMode.Quirks;

        var html401 = DoctypeIdMatches(publicId, "-//W3C//DTD HTML 4.01 Frameset//", prefix: true) ||
            DoctypeIdMatches(publicId, "-//W3C//DTD HTML 4.01 Transitional//", prefix: true);
        // The current algorithm groups a missing AND an empty system ID for HTML 4.01.
        if (html401) return string.IsNullOrEmpty(systemId) ? HtmlDocumentMode.Quirks : HtmlDocumentMode.LimitedQuirks;
        if (DoctypeIdMatches(publicId, "-//W3C//DTD XHTML 1.0 Frameset//", prefix: true) ||
            DoctypeIdMatches(publicId, "-//W3C//DTD XHTML 1.0 Transitional//", prefix: true))
            return HtmlDocumentMode.LimitedQuirks;
        return HtmlDocumentMode.NoQuirks;
    }

    private static bool DoctypeIdMatches(string? value, string expected, bool prefix = false)
    {
        if (value is null || value.Length < expected.Length || !prefix && value.Length != expected.Length) return false;
        // OrdinalIgnoreCase also folds non-ASCII letters. HTML requires ASCII-only folding.
        // Compare only the bounded expected ID/prefix; no copy of the input identifier.
        for (var i = 0; i < expected.Length; i++)
        {
            var actual = value[i]; var wanted = expected[i];
            if (actual is >= 'A' and <= 'Z') actual = (char)(actual + 32);
            if (wanted is >= 'A' and <= 'Z') wanted = (char)(wanted + 32);
            if (actual != wanted) return false;
        }
        return true;
    }

    private static readonly string[] QuirksPublicIdPrefixes =
    [
        "+//Silmaril//dtd html Pro v0r11 19970101//",
        "-//AS//DTD HTML 3.0 asWedit + extensions//",
        "-//AdvaSoft Ltd//DTD HTML 3.0 asWedit + extensions//",
        "-//IETF//DTD HTML 2.0 Level 1//",
        "-//IETF//DTD HTML 2.0 Level 2//",
        "-//IETF//DTD HTML 2.0 Strict Level 1//",
        "-//IETF//DTD HTML 2.0 Strict Level 2//",
        "-//IETF//DTD HTML 2.0 Strict//",
        "-//IETF//DTD HTML 2.0//",
        "-//IETF//DTD HTML 2.1E//",
        "-//IETF//DTD HTML 3.0//",
        "-//IETF//DTD HTML 3.2 Final//",
        "-//IETF//DTD HTML 3.2//",
        "-//IETF//DTD HTML 3//",
        "-//IETF//DTD HTML Level 0//",
        "-//IETF//DTD HTML Level 1//",
        "-//IETF//DTD HTML Level 2//",
        "-//IETF//DTD HTML Level 3//",
        "-//IETF//DTD HTML Strict Level 0//",
        "-//IETF//DTD HTML Strict Level 1//",
        "-//IETF//DTD HTML Strict Level 2//",
        "-//IETF//DTD HTML Strict Level 3//",
        "-//IETF//DTD HTML Strict//",
        "-//IETF//DTD HTML//",
        "-//Metrius//DTD Metrius Presentational//",
        "-//Microsoft//DTD Internet Explorer 2.0 HTML Strict//",
        "-//Microsoft//DTD Internet Explorer 2.0 HTML//",
        "-//Microsoft//DTD Internet Explorer 2.0 Tables//",
        "-//Microsoft//DTD Internet Explorer 3.0 HTML Strict//",
        "-//Microsoft//DTD Internet Explorer 3.0 HTML//",
        "-//Microsoft//DTD Internet Explorer 3.0 Tables//",
        "-//Netscape Comm. Corp.//DTD HTML//",
        "-//Netscape Comm. Corp.//DTD Strict HTML//",
        "-//O'Reilly and Associates//DTD HTML 2.0//",
        "-//O'Reilly and Associates//DTD HTML Extended 1.0//",
        "-//O'Reilly and Associates//DTD HTML Extended Relaxed 1.0//",
        "-//SQ//DTD HTML 2.0 HoTMetaL + extensions//",
        "-//SoftQuad Software//DTD HoTMetaL PRO 6.0::19990601::extensions to HTML 4.0//",
        "-//SoftQuad//DTD HoTMetaL PRO 4.0::19971010::extensions to HTML 4.0//",
        "-//Spyglass//DTD HTML 2.0 Extended//",
        "-//Sun Microsystems Corp.//DTD HotJava HTML//",
        "-//Sun Microsystems Corp.//DTD HotJava Strict HTML//",
        "-//W3C//DTD HTML 3 1995-03-24//",
        "-//W3C//DTD HTML 3.2 Draft//",
        "-//W3C//DTD HTML 3.2 Final//",
        "-//W3C//DTD HTML 3.2//",
        "-//W3C//DTD HTML 3.2S Draft//",
        "-//W3C//DTD HTML 4.0 Frameset//",
        "-//W3C//DTD HTML 4.0 Transitional//",
        "-//W3C//DTD HTML Experimental 19960712//",
        "-//W3C//DTD HTML Experimental 970421//",
        "-//W3C//DTD W3 HTML//",
        "-//W3O//DTD W3 HTML 3.0//",
        "-//WebTechs//DTD Mozilla HTML 2.0//",
        "-//WebTechs//DTD Mozilla HTML//"
    ];
}
