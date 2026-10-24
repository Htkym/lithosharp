using LithoSharp.Content.Compilation;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class LithoHtmlRendererTests
{
    [Test]
    public async Task EscapingPreservesUtf16AndTheSeparateDiagramPolicy()
    {
        var unescaped = new string('x', 32768);
        var ampersands = new string('&', 4096);
        foreach (var (text, encoded, diagram) in new[]
        {
            ("", "", ""),
            ("ab", "ab", "ab"),
            ("\uD800&😀<x>y&\uDC00", "\uD800&amp;😀&lt;x&gt;y&amp;\uDC00", "\uD800&amp;😀&lt;x>y&amp;\uDC00"),
            ("left&mid<end>\"", "left&amp;mid&lt;end&gt;&quot;", "left&amp;mid&lt;end>\""),
            ("&<>\"'é😀\uD800x\uDC00", "&amp;&lt;&gt;&quot;'é😀\uD800x\uDC00", "&amp;&lt;>\"'é😀\uD800x\uDC00"),
            (unescaped, unescaped, unescaped),
            (ampersands, string.Concat(Enumerable.Repeat("&amp;", 4096)), string.Concat(Enumerable.Repeat("&amp;", 4096)))
        })
        {
            await Assert.That(LithoHtmlRenderer.RenderInlines([new LithoText(text, default)]))
                .IsEqualTo(encoded);
            await Assert.That(LithoHtmlRenderer.RenderHtml([new LithoCode(text, null, true, default)], []))
                .IsEqualTo("<pre><code>" + encoded + "</code></pre>\n");
            await Assert.That(LithoHtmlRenderer.RenderHtml([new LithoCode(text, "mermaid", true, default)], []))
                .IsEqualTo("<pre class=\"mermaid\">" + diagram + "</pre>\n");
            await Assert.That(LithoHtmlRenderer.RenderHtml([new LithoCode(text, "nomnoml", true, default)], []))
                .IsEqualTo("<div class=\"nomnoml\">" + diagram + "</div>\n");
        }
    }

    [Test]
    public async Task EncodedValuesKeepTheirInlineAndAttributeDelimiters()
    {
        const string text = "&<>\"'é😀\uD800x\uDC00";
        const string encoded = "&amp;&lt;&gt;&quot;'é😀\uD800x\uDC00";
        LithoInline[] inlines =
        [
            new LithoCodeSpan(text, default),
            new LithoLink([new LithoText(text, default)], text, text, true, default),
            new LithoLink([new LithoText(text, default)], text, text, false, default),
            new LithoAutolink(text, text, default),
            new LithoMathInline(text, default)
        ];
        await Assert.That(LithoHtmlRenderer.RenderInlines(inlines)).IsEqualTo(
            "<code>" + encoded + "</code>"
            + "<img src=\"" + encoded + "\" alt=\"" + encoded + "\" title=\"" + encoded + "\" />"
            + "<a href=\"" + encoded + "\" title=\"" + encoded + "\">" + encoded + "</a>"
            + "<a href=\"" + encoded + "\">" + encoded + "</a>"
            + "<span class=\"math\">\\(" + encoded + "\\)</span>");

        LithoBlock[] blocks =
        [
            new LithoAdmonition("note", text, [], default),
            new LithoDirective(text, [], default),
            new LithoMath(text, default),
            new LithoCode(text, text, true, default, new(text, text, true, 5))
        ];
        await Assert.That(LithoHtmlRenderer.RenderHtml(blocks, [])).IsEqualTo(
            "<aside class=\"mdx-admonition mdx-note\" role=\"note\"><strong>" + encoded + "</strong>\n</aside>\n"
            + "<div class=\"" + encoded + "\">\n</div>\n"
            + "<div class=\"math\">\n\\[\n" + encoded + "\n\\]</div>\n"
            + "<pre data-title=\"" + encoded + "\" data-highlight=\"" + encoded + "\" data-start=\"5\" data-line-numbers=\"true\">"
            + "<code class=\"language-" + encoded + "\">" + encoded + "</code></pre>\n");
    }
}
