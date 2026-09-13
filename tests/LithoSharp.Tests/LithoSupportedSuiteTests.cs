using LithoSharp.Content.Compilation;

namespace LithoSharp.Tests;

/// <summary>
/// C04 supported suite: pinned rendering contract for the Litho frontend
/// (CommonMark 0.31.2 + GFM 0.29 subset). Expectations encode the specified
/// behavior, not implementation accidents.
/// </summary>
public sealed class LithoSupportedSuiteTests
{
    private static readonly LithoMarkdownCompiler Compiler = new();

    private static async Task AssertRendersAsync(string markdown, string expectedHtml)
    {
        var actual = Compiler.Compile(markdown).Html;
        await Assert.That(actual).IsEqualTo(expectedHtml);
    }

    [Test]
    [Arguments("Hello.", "<p>Hello.</p>\n")]
    [Arguments("A\n\nB", "<p>A</p>\n<p>B</p>\n")]
    [Arguments("# H1", "<h1 id=\"h1\">H1</h1>\n")]
    [Arguments("###### H6", "<h6 id=\"h6\">H6</h6>\n")]
    [Arguments("## Closed ##", "<h2 id=\"closed\">Closed</h2>\n")]
    [Arguments("#nospace", "<p>#nospace</p>\n")]
    [Arguments("Setext 1\n========\n", "<h1 id=\"setext-1\">Setext 1</h1>\n")]
    [Arguments("Setext 2\n--------\n", "<h2 id=\"setext-2\">Setext 2</h2>\n")]
    [Arguments("***\n", "<hr />\n")]
    [Arguments("- - -\n", "<hr />\n")]
    [Arguments("___\n", "<hr />\n")]
    [Arguments("Text ***\n", "<p>Text ***</p>\n")]
    [Arguments("```\ncode\n```\n", "<pre><code>code\n</code></pre>\n")]
    [Arguments("```csharp\nvar x = 1;\n```\n", "<pre><code class=\"language-csharp\">var x = 1;\n</code></pre>\n")]
    [Arguments("    indented\n", "<pre><code>indented\n</code></pre>\n")]
    [Arguments("> quoted\n", "<blockquote>\n<p>quoted</p>\n</blockquote>\n")]
    [Arguments("> a\n> b\n", "<blockquote>\n<p>a\nb</p>\n</blockquote>\n")]
    [Arguments("> inside\n\noutside", "<blockquote>\n<p>inside</p>\n</blockquote>\n<p>outside</p>\n")]
    [Arguments("> first\n\n> second", "<blockquote>\n<p>first</p>\n</blockquote>\n<blockquote>\n<p>second</p>\n</blockquote>\n")]
    [Arguments("- a\n- b\n", "<ul>\n<li>a</li>\n<li>b</li>\n</ul>\n")]
    [Arguments("1. a\n2. b\n", "<ol>\n<li>a</li>\n<li>b</li>\n</ol>\n")]
    [Arguments("3. a\n4. b\n", "<ol start=\"3\">\n<li>a</li>\n<li>b</li>\n</ol>\n")]
    [Arguments("10. ten\n1. one", "<ol start=\"10\">\n<li>ten</li>\n<li>one</li>\n</ol>\n")]
    [Arguments("10. ten\n1. one\n   continued", "<ol start=\"10\">\n<li>ten</li>\n<li>one\ncontinued</li>\n</ol>\n")]
    [Arguments("- first\n\n  second", "<ul>\n<li><p>first</p>\n<p>second</p>\n</li>\n</ul>\n")]
    [Arguments("  ```python\n  if True:\n      pass\n  ```", "<pre><code class=\"language-python\">if True:\n    pass\n</code></pre>\n")]
    [Arguments("- a\n\n- b\n", "<ul>\n<li><p>a</p>\n</li>\n<li><p>b</p>\n</li>\n</ul>\n")]
    [Arguments("- [x] Done\n- [ ] Todo\n", "<ul class=\"contains-task-list\">\n<li class=\"task-list-item\"><input disabled=\"disabled\" type=\"checkbox\" checked=\"checked\" /> Done</li>\n<li class=\"task-list-item\"><input disabled=\"disabled\" type=\"checkbox\" /> Todo</li>\n</ul>\n")]
    [Arguments("- parent\n  - child\n", "<ul>\n<li>parent\n<ul>\n<li>child</li>\n</ul>\n</li>\n</ul>\n")]
    [Arguments("<div>raw</div>\n", "<p>&lt;div&gt;raw&lt;/div&gt;</p>\n")]
    public async Task SupportedBlocks_Render(string markdown, string expectedHtml) =>
        await AssertRendersAsync(markdown, expectedHtml);

    [Test]
    [Arguments("*em*", "<p><em>em</em></p>\n")]
    [Arguments("**strong**", "<p><strong>strong</strong></p>\n")]
    [Arguments("~~del~~", "<p><del>del</del></p>\n")]
    [Arguments("*a **b** c*", "<p><em>a <strong>b</strong> c</em></p>\n")]
    [Arguments("a***b**c*", "<p>a<em><strong>b</strong>c</em></p>\n")]
    [Arguments("a_b_c", "<p>a_b_c</p>\n")]
    [Arguments("*unclosed", "<p>*unclosed</p>\n")]
    [Arguments("`code`", "<p><code>code</code></p>\n")]
    [Arguments("``a ` b``", "<p><code>a ` b</code></p>\n")]
    [Arguments("\\*x\\*", "<p>*x*</p>\n")]
    [Arguments("&copy;", "<p>\u00A9</p>\n")]
    [Arguments("&unknown;", "<p>&amp;unknown;</p>\n")]
    [Arguments("[t](https://example.org/u)", "<p><a href=\"https://example.org/u\">t</a></p>\n")]
    [Arguments("[x](foo(bar)baz)", "<p><a href=\"foo(bar)baz\">x</a></p>\n")]
    [Arguments("[x](foo(bar(baz))qux)", "<p><a href=\"foo(bar(baz))qux\">x</a></p>\n")]
    [Arguments("[x](foo(bar)", "<p>[x](foo(bar)</p>\n")]
    [Arguments("[x](a?b=1&amp;c=2)", "<p><a href=\"a?b=1&amp;c=2\">x</a></p>\n")]
    [Arguments("[x](a?b=1\\&amp;c=2)", "<p><a href=\"a?b=1&amp;amp;c=2\">x</a></p>\n")]
    [Arguments("[x](url \"A &amp; B\")", "<p><a href=\"url\" title=\"A &amp; B\">x</a></p>\n")]
    [Arguments("[*a*]\n\n[*a*]: /url", "<p><a href=\"/url\"><em>a</em></a></p>\n")]
    [Arguments("[*a*][]\n\n[*a*]: /url", "<p><a href=\"/url\"><em>a</em></a></p>\n")]
    [Arguments("[x]\n\n[x]: a?b=1&amp;c=2", "<p><a href=\"a?b=1&amp;c=2\">x</a></p>\n")]
    [Arguments("[t](https://example.org/u \"ti\")", "<p><a href=\"https://example.org/u\" title=\"ti\">t</a></p>\n")]
    [Arguments("![a](x.png)", "<p><img src=\"x.png\" alt=\"a\" /></p>\n")]
    [Arguments("[r]", "<p>[r]</p>\n")]
    [Arguments("[f][R]", "<p>[f][R]</p>\n")]
    [Arguments("[undef]", "<p>[undef]</p>\n")]
    [Arguments("<https://example.org/a>", "<p><a href=\"https://example.org/a\">https://example.org/a</a></p>\n")]
    [Arguments("go www.example.org/x.", "<p>go <a href=\"http://www.example.org/x\">www.example.org/x</a>.</p>\n")]
    [Arguments("mail@example.org", "<p>mail@example.org</p>\n")]
    [Arguments("a  \nb", "<p>a<br />\nb</p>\n")]
    [Arguments("a\nb", "<p>a\nb</p>\n")]
    [Arguments("'q' and \"qq\"", "<p>'q' and &quot;qq&quot;</p>\n")]
    public async Task SupportedInlines_Render(string markdown, string expectedHtml) =>
        await AssertRendersAsync(markdown, expectedHtml);

    [Test]
    public async Task ReferenceDefinitions_Resolve()
    {
        const string body = "See [r] and [f][R].\n\n[R]: https://example.org/r \"T\"\n";
        await AssertRendersAsync(body,
            "<p>See <a href=\"https://example.org/r\" title=\"T\">r</a> and <a href=\"https://example.org/r\" title=\"T\">f</a>.</p>\n");
    }

    [Test]
    [Arguments("> [x]: /url\n> [x]\n\n[x]", "<blockquote>\n<p><a href=\"/url\">x</a></p>\n</blockquote>\n<p><a href=\"/url\">x</a></p>\n")]
    [Arguments("- [x]: /url\n  [x]\n\n[x]", "<ul>\n<li><a href=\"/url\">x</a></li>\n</ul>\n<p><a href=\"/url\">x</a></p>\n")]
    public async Task ContainerReferences_ResolveAcrossDocument(string markdown, string expectedHtml) =>
        await AssertRendersAsync(markdown, expectedHtml);

    [Test]
    public async Task PipeTable_Renders()
    {
        await AssertRendersAsync(
            "| A | B |\n| --- | :---: |\n| 1 | 2 |\n",
            "<table>\n<thead>\n<tr>\n<th>A</th>\n<th style=\"text-align: center;\">B</th>\n</tr>\n</thead>\n<tbody>\n<tr>\n<td>1</td>\n<td style=\"text-align: center;\">2</td>\n</tr>\n</tbody>\n</table>\n");
    }

    [Test]
    [Arguments("A\r\n\r\nB", "<p>A</p>\n<p>B</p>\n")]
    [Arguments("a\r\nb", "<p>a\nb</p>\n")]
    [Arguments("a\rb", "<p>a\nb</p>\n")]
    [Arguments("a  \r\nb", "<p>a<br />\nb</p>\n")]
    [Arguments("a\\\r\nb", "<p>a<br />\nb</p>\n")]
    [Arguments("# H1\r\n\r\nBody\r\ntext.\r\n", "<h1 id=\"h1\">H1</h1>\n<p>Body\ntext.</p>\n")]
    public async Task CrlfAndCr_NormalizeToLf(string markdown, string expectedHtml) =>
        await AssertRendersAsync(markdown, expectedHtml);
}
