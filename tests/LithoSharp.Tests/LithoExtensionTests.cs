using LithoSharp.Content.Compilation;

namespace LithoSharp.Tests;

/// <summary>
/// C07 extension fixtures: pinned Litho rendering contracts for admonitions,
/// directives, heading anchors, code metadata, math, and diagrams. Reference
/// agreement for the intentionally matching fixtures lives in the comparison
/// project; intentional divergences are pinned here instead.
/// </summary>
public sealed class LithoExtensionTests
{
    private static readonly LithoMarkdownCompiler Litho = new();

    private static async Task<string> FixtureAsync(string fileName) =>
        await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "LithoExtensions", fileName));

    private static async Task AssertLithoRendersAsync(string fileName, string expectedHtml)
    {
        var actual = Litho.Compile(await FixtureAsync(fileName)).Html;
        await Assert.That(actual).IsEqualTo(expectedHtml);
    }

    [Test]
    public async Task Admonition_RendersAsideWithTitle() =>
        await AssertLithoRendersAsync("admonition.md",
            "<aside class=\"mdx-admonition mdx-note\" role=\"note\"><strong>note</strong>\n" +
            "<p>Important body.</p>\n" +
            "</aside>\n" +
            "<aside class=\"mdx-admonition mdx-warning\" role=\"note\"><strong>Custom Title</strong>\n" +
            "<p>Warning body.</p>\n" +
            "</aside>\n" +
            "<aside class=\"mdx-admonition mdx-tip\" role=\"note\"><strong>tip</strong>\n" +
            "</aside>\n" +
            "<aside class=\"mdx-admonition mdx-note\" role=\"note\"><strong>note</strong>\n" +
            "<p>Alert body.</p>\n" +
            "</aside>\n" +
            "<aside class=\"mdx-admonition mdx-important\" role=\"note\"><strong>important</strong>\n" +
            "<p>Important body.</p>\n" +
            "</aside>\n");

    [Test]
    public async Task UnknownDirective_RendersDiv() =>
        await AssertLithoRendersAsync("directives.md",
            "<div class=\"custom\">\n" +
            "<p>Custom content.</p>\n" +
            "</div>\n" +
            "<aside class=\"mdx-admonition mdx-note\" role=\"note\"><strong>note</strong>\n" +
            "<p>unclosed admonition</p>\n" +
            "</aside>\n");

    /// <summary>
    /// A bare fence closes every open container (reference parity); deeper runs
    /// nest Docusaurus-style instead.
    /// </summary>
    [Test]
    public async Task NestedDirective_MatchesReferenceNesting() =>
        await AssertLithoRendersAsync("directives-nested.md",
            "<aside class=\"mdx-admonition mdx-note\" role=\"note\"><strong>note</strong>\n" +
            "<aside class=\"mdx-admonition mdx-tip\" role=\"note\"><strong>tip</strong>\n" +
            "<p>deep</p>\n" +
            "</aside>\n" +
            "</aside>\n" +
            "<p>back in note?</p>\n" +
            "<div>\n" +
            "</div>\n");

    [Test]
    public async Task HeadingAnchor_KeepsExplicitTextLiteral()
    {
        await AssertLithoRendersAsync("heading-anchor.md",
            "<h2 id=\"a-x\">A {#x}</h2>\n" +
            "<h2 id=\"b-x\">B {#x}</h2>\n" +
            "<h1 id=\"top-title-here\">Top Title Here</h1>\n" +
            "<h2 id=\"section\">日本語見出し</h2>\n");
    }

    [Test]
    public async Task DocumentTitle_IsFirstH1()
    {
        var semantics = Litho.Analyze(await FixtureAsync("heading-anchor.md")).Semantics!;
        await Assert.That(semantics.Title).IsEqualTo("Top Title Here");

        var noH1 = Litho.Analyze("## Only h2\n").Semantics!;
        await Assert.That(noH1.Title).IsNull();
    }

    [Test]
    public async Task CodeMeta_FlowsToDataAttributes()
    {
        await AssertLithoRendersAsync("code-meta.md",
            "<pre data-title=\"Program.cs\"><code class=\"language-csharp\">var x = 1;\n</code></pre>\n" +
            "<pre data-highlight=\"1,3\" data-start=\"5\" data-line-numbers=\"true\"><code class=\"language-js\">code here\n</code></pre>\n" +
            "<pre><code class=\"language-python\">plain\n</code></pre>\n");

        var blocks = Litho.Parse(await FixtureAsync("code-meta.md"));
        var codes = blocks.OfType<LithoCode>().ToArray();
        await Assert.That(codes.Length).IsEqualTo(3);
        await Assert.That(codes[0].Meta!.Title).IsEqualTo("Program.cs");
        await Assert.That(codes[0].Meta!.Highlight).IsNull();
        await Assert.That(codes[1].Meta!.Highlight).IsEqualTo("1,3");
        await Assert.That(codes[1].Meta!.ShowLineNumbers).IsTrue();
        await Assert.That(codes[1].Meta!.StartLine).IsEqualTo(5);
        await Assert.That(codes[2].Meta).IsNull();
    }

    [Test]
    public async Task Math_RendersReferenceShapes()
    {
        await AssertLithoRendersAsync("math.md",
            "<p>Inline <span class=\"math\">\\(x^2 + y\\)</span> here.</p>\n" +
            "<p><span class=\"math\">\\(a &lt; b\\)</span> escaped $5 and $ 6$ literal.</p>\n" +
            "<div class=\"math\">\n\\[\ny = mx + b\n\\]</div>\n" +
            "<p>$$unclosed stays.</p>\n");
    }

    [Test]
    public async Task Diagrams_RenderReferenceShapes()
    {
        await AssertLithoRendersAsync("mermaid.md",
            "<pre class=\"mermaid\">graph TD;\n  A-->B;\n</pre>\n" +
            "<div class=\"nomnoml\">[Hi]\n</div>\n" +
            "<pre><code class=\"language-csharp\">var x = 1;\n</code></pre>\n");
    }

    [Test]
    public async Task ExtensionSemantics_ExposeHeadingsAndLinks()
    {
        var analyzed = Litho.Analyze(
            ":::note\n## Inside\n\n[link](https://example.org/a)\n:::\n> [!TIP]\nBody.\n");
        var semantics = analyzed.Semantics!;

        await Assert.That(semantics.Headings.Single().Text).IsEqualTo("Inside");
        await Assert.That(semantics.Links.Single().Url).IsEqualTo("https://example.org/a");
        await Assert.That(semantics.PlainText).Contains("Inside");
        var kinds = analyzed.Syntax!.Blocks.Select(block => block.Kind).ToArray();
        await Assert.That(kinds).IsEquivalentTo([BlockKind.Admonition, BlockKind.Admonition]);
    }
}
