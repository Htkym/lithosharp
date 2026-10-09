using System.Text;
using System.Text.Json;
using LithoSharp.HtmlParsing;

namespace LithoSharp.Tests;

public sealed class HtmlTreeBuilderTests
{
    [Test]
    public async Task ObservationCorpus_PreservesSelectedTreesInDocumentAndFragmentModes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HtmlObservation", "html-observation-corpus-v1.json");
        using var corpus = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var count = 0;
        foreach (var item in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var raw = item.GetProperty("html").GetString()!;
            var options = new HtmlTreeOptions(item.GetProperty("scripting").GetBoolean(),
                item.GetProperty("mode").GetString() == "fragment" ? new(item.GetProperty("context").GetString()!) : null);
            foreach (var chunk in new[] { 4096, 1 })
            {
                var tree = Parse(raw, options, chunk);
                try
                {
                    await Assert.That(tree.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
                    await CompareQueries(tree, item.GetProperty("queries"), tree.Root);
                    if (item.TryGetProperty("templateContentQueries", out var contentQueries))
                    {
                        var template = tree.Elements().Single(n => n.Name == "template");
                        await Assert.That(template.Children.Count).IsEqualTo(0);
                        await CompareQueries(tree, contentQueries, template.TemplateContent!.Value);
                    }
                    if (item.TryGetProperty("fragmentRootNames", out var roots))
                        await Assert.That(string.Join(",", tree.Nodes[tree.Root].Children.Select(id => tree.Nodes[id].Name!.ToUpperInvariant())))
                            .IsEqualTo(string.Join(",", roots.EnumerateArray().Select(n => n.GetString())));
                    await CheckLinksAndSpans(tree, raw.Length);
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException($"Observation {item.GetProperty("id").GetString()}, text chunk {chunk}.", error);
                }
            }
            count++;
        }
        await Assert.That(count).IsEqualTo(12);
    }

    [Test]
    public async Task ScriptState_SurvivesRepeatedTreeBuilderReadsAndDoesNotCreateFalseElements()
    {
        foreach (var (script, expected) in new[]
        {
            ("<!--<ScRiPt>one</SCRIPT>two--></sCrIpT>", "<!--<ScRiPt>one</SCRIPT>two-->"),
            ("<!--><script></script>", "<!--><script>")
        })
        {
            var tree = Parse("<script>" + script + "<p id='real'>Real</p>", chunk: 1);
            await Assert.That(tree.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
            await Assert.That(tree.Elements().Count(n => n.Name == "script")).IsEqualTo(1);
            await Assert.That(tree.TextContent(tree.Elements().Single(n => n.Name == "script").Id)).IsEqualTo(expected);
            await Assert.That(tree.TextContent(ById(tree, "real").Id)).IsEqualTo("Real");
            await Assert.That(tree.TokenizerDiagnostics.Any(d => d.Code == "eof-in-script-html-comment-like-text")).IsFalse();
        }
    }

    [Test]
    public async Task CharacterRunBoundaries_KeepTheSameTreeAcrossTokenizerChunkSizes()
    {
        foreach (var raw in new[]
        {
            "<!doctype html><html> \n<head> \n<title>T</title></head> \nX<table> \nY<tr><td>A</td></tr> \n</table>",
            "<table><colgroup> \nX<tr><td>A</td></tr></table>",
            "<html><frameset> \nX<frame src='x'> Y\t</frameset> \nZ</html>",
            "<body><pre>\r\nOne</pre><textarea>\r\n&amp;Two</textarea><p>A\0B</p>",
            "<svg><![CDATA[<p>&amp;]]><foreignObject><p>H</p></foreignObject></svg><p>Tail</p>",
            "<template><table> X<tr><td><b>A<i>B</b>C</i></td></tr></table></template><p>Visible</p>"
        })
        {
            var reference = Parse(raw);
            await Assert.That(reference.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
            foreach (var chunk in new[] { 1, 2, 7 })
            {
                var small = Parse(raw, chunk: chunk);
                await Assert.That(small.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
                await Assert.That(Shape(small)).IsEqualTo(Shape(reference));
                await CheckLinksAndSpans(small, raw.Length);
            }
        }
    }

    [Test]
    public async Task TableCharacters_FosterTheEntireMixedRunButKeepPureWhitespaceInside()
    {
        const string raw = "<table> \nX<tr><td>A</td></tr> \n</table>";
        var tree = Parse(raw, chunk: 1);
        var table = tree.Elements().Single(n => n.Name == "table");
        var body = tree.Elements().Single(n => n.Name == "body");
        await Assert.That(tree.TextContent(table.Id)).IsEqualTo("A \n");
        await Assert.That(tree.TextContent(body.Id)).IsEqualTo(" \nXA \n");
        await Assert.That(tree.Nodes[body.Children[0]].Kind).IsEqualTo(HtmlTreeNodeKind.Text);
        await Assert.That(body.Children[1]).IsEqualTo(table.Id);
        await Assert.That(tree.Diagnostics.Single(d => d.Code == "foster-parented-text").Source.Start).IsEqualTo(7);
    }

    [Test]
    public async Task FormattingRepair_ReparentsTheFurthestBlockAndKeepsSyntheticPositionsUnknown()
    {
        const string raw = "<b id='b'>A<div id='d'>B</b>C</div>D";
        var tree = Parse(raw);
        var body = tree.Elements().Single(n => n.Name == "body");
        var block = ById(tree, "d");
        var bold = tree.Elements().Where(n => n.Name == "b").ToArray();
        await Assert.That(bold.Length).IsEqualTo(2);
        await Assert.That(bold[0].Parent).IsEqualTo((int?)body.Id);
        await Assert.That(bold[1].Parent).IsEqualTo((int?)block.Id);
        await Assert.That(block.Parent).IsEqualTo((int?)body.Id);
        await Assert.That(tree.TextContent(bold[0].Id)).IsEqualTo("A");
        await Assert.That(tree.TextContent(bold[1].Id)).IsEqualTo("B");
        await Assert.That(tree.TextContent(block.Id)).IsEqualTo("BC");
        await Assert.That(tree.TextContent(body.Id)).IsEqualTo("ABCD");
        await Assert.That(bold[0].Source!.Value.Start).IsEqualTo(0);
        await Assert.That(bold[1].Source is null).IsTrue();
        await CheckLinksAndSpans(tree, raw.Length);
    }

    [Test]
    public async Task NestedTemplates_KeepTableRepairsAndHiddenElementsInsideSeparateContentRoots()
    {
        const string raw = "<template id='outer'><table>X<tr><td><template id='inner'><h2 id='hidden'>H</h2></template>A</td></tr></table></template><h2 id='visible'>V</h2>";
        var tree = Parse(raw);
        var outer = ById(tree, "outer");
        var content = outer.TemplateContent!.Value;
        var inner = tree.Elements(content).Single(n => n.Name == "template");
        await Assert.That(string.Join(",", tree.Elements().SelectMany(n => n.Attributes.Where(a => a.Name == "id").Select(a => a.Value.Value))))
            .IsEqualTo("outer,visible");
        await Assert.That(tree.TextContent(outer.Id)).IsEqualTo("");
        await Assert.That(tree.TextContent(content)).IsEqualTo("XA");
        await Assert.That(tree.TextContent(inner.TemplateContent!.Value)).IsEqualTo("H");
        await Assert.That(tree.TextContent(inner.Id)).IsEqualTo("");
        await Assert.That(tree.Nodes[content].Parent is null).IsTrue();
        await CheckLinksAndSpans(tree, raw.Length);
    }

    [Test]
    public async Task ForeignContent_AdjustsNamesAndAttributesAndReturnsThroughIntegrationPoints()
    {
        const string raw = "<svg id='svg' viewbox='0 0 1 1'><lineargradient id='gradient' xlink:href='#x' xml:lang='en' xmlns:xlink='http://www.w3.org/1999/xlink'/><![CDATA[<p>&amp;]]><foreignobject id='fo'><p id='html'>H</p></foreignobject></svg><math><mi><mglyph id='glyph'/><b id='bold'>B</b></mi><annotation-xml encoding='TEXT/HTML'><p id='annotation'>A</p></annotation-xml></math>";
        var tree = Parse(raw, chunk: 1);
        var svg = ById(tree, "svg");
        var gradient = ById(tree, "gradient");
        await Assert.That(svg.Namespace).IsEqualTo(HtmlNamespaces.Svg);
        await Assert.That(svg.Attributes.Any(a => a.Name == "viewBox")).IsTrue();
        await Assert.That(gradient.Name).IsEqualTo("linearGradient");
        var href = gradient.Attributes.Single(a => a.Name == "href");
        await Assert.That(href.Namespace).IsEqualTo(HtmlNamespaces.XLink);
        await Assert.That(href.Prefix).IsEqualTo("xlink");
        await Assert.That(href.RawName.Value).IsEqualTo("xlink:href");
        await Assert.That(gradient.Attributes.Single(a => a.Name == "lang").Namespace).IsEqualTo(HtmlNamespaces.Xml);
        await Assert.That(gradient.Attributes.Single(a => a.Name == "xlink").Namespace).IsEqualTo(HtmlNamespaces.Xmlns);
        await Assert.That(ById(tree, "fo").Name).IsEqualTo("foreignObject");
        await Assert.That(ById(tree, "html").Namespace).IsEqualTo(HtmlNamespaces.Html);
        await Assert.That(ById(tree, "glyph").Namespace).IsEqualTo(HtmlNamespaces.MathMl);
        await Assert.That(ById(tree, "bold").Namespace).IsEqualTo(HtmlNamespaces.Html);
        await Assert.That(ById(tree, "annotation").Namespace).IsEqualTo(HtmlNamespaces.Html);
        await Assert.That(tree.TextContent(svg.Id)).IsEqualTo("<p>&amp;H");
        await CheckLinksAndSpans(tree, raw.Length);

        var breakout = Parse("<svg><g><p id='outside'>P</p></g></svg>");
        await Assert.That(breakout.Nodes[ById(breakout, "outside").Parent!.Value].Name).IsEqualTo("body");
        var math = Parse("<math definitionurl='x'><mi>M</mi></math>");
        await Assert.That(math.Elements().Single(n => n.Name == "math").Attributes.Single().Name).IsEqualTo("definitionURL");
    }

    [Test]
    public async Task ScopeAndSelectTransitions_CloseHeadingsAndResumeTheTableAfterASelect()
    {
        var heading = Parse("<h1><span>A</h2>B");
        var body = heading.Elements().Single(n => n.Name == "body");
        await Assert.That(heading.TextContent(heading.Elements().Single(n => n.Name == "h1").Id)).IsEqualTo("A");
        await Assert.That(heading.TextContent(body.Id)).IsEqualTo("AB");
        var table = Parse("<table><tr><td><select><option>A</select><select><option>B</table><p>C");
        await Assert.That(table.Elements().Count(n => n.Name == "select")).IsEqualTo(2);
        await Assert.That(table.TextContent(table.Elements().Single(n => n.Name == "table").Id)).IsEqualTo("AB");
        await Assert.That(table.Nodes[table.Elements().Single(n => n.Name == "p").Parent!.Value].Name).IsEqualTo("body");
        await Assert.That(table.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
    }

    [Test]
    public async Task SourceMappings_RemainInRawUtf16AfterNewlineSuppressionNullRemovalAndDecoding()
    {
        const string raw = "<textarea>\r\n&amp;😀</textarea><p>A\0B</p>";
        var tree = Parse(raw, chunk: 1);
        var textarea = tree.Elements().Single(n => n.Name == "textarea");
        await Assert.That(tree.TextContent(textarea.Id)).IsEqualTo("&😀");
        var chunks = tree.Nodes[textarea.Children.Single()].TextChunks;
        var entity = chunks.First(c => c.Value == "&");
        await Assert.That(raw.Substring(entity.Source.Start, entity.Source.Length)).IsEqualTo("&amp;");
        await Assert.That(tree.TextContent(tree.Elements().Single(n => n.Name == "p").Id)).IsEqualTo("AB");
        await CheckLinksAndSpans(tree, raw.Length);
    }

    [Test]
    public async Task FragmentTextAndTemplateContexts_DoNotConsumeAnAppropriateEndTagOrHideReturnedRoots()
    {
        foreach (var (name, raw, expected) in new[]
        {
            ("textarea", "\n&amp;</textarea><p>X", "\n&</textarea><p>X"),
            ("title", "&lt;x&gt;</title><b>X", "<x></title><b>X"),
            ("style", "&amp;</style><p>X", "&amp;</style><p>X"),
            ("script", "<!--<script>Y</script>--></script><p>X", "<!--<script>Y</script>--></script><p>X")
        })
        {
            var tree = Parse(raw, new(Fragment: new(name)), chunk: 1);
            await Assert.That(tree.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
            await Assert.That(tree.Elements().Count()).IsEqualTo(0);
            await Assert.That(tree.TextContent(tree.Root)).IsEqualTo(expected);
            await CheckLinksAndSpans(tree, raw.Length);
        }
        var template = Parse("<p id='returned'>R</p><template><b>Hidden</b></template>", new(Fragment: new("template")));
        await Assert.That(template.Nodes[template.Root].TemplateContent is null).IsTrue();
        await Assert.That(ById(template, "returned").Name).IsEqualTo("p");
        await Assert.That(template.TextContent(template.Root)).IsEqualTo("R");
        await Assert.That(template.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
        await Assert.That(() => new HtmlTokenizer("X").SetContext(HtmlTextMode.RcData)).Throws<ArgumentException>();
    }

    [Test]
    public async Task UnsupportedBranches_ReportPartialCoverageInsteadOfComplete()
    {
        foreach (var (raw, options, code) in new[]
        {
            ("<!doctype html PUBLIC '-//W3C//DTD HTML 4.01 Transitional//EN'><p>X", new HtmlTreeOptions(), "unsupported-legacy-doctype-mode"),
            ("<template shadowrootmode='open'><p>X</p></template>", new HtmlTreeOptions(), "unsupported-declarative-shadow-root"),
            ("<p>X", new HtmlTreeOptions(Fragment: new("svg", HtmlNamespaces.Svg)), "unsupported-foreign-fragment-breakout")
        })
        {
            var tree = Parse(raw, options);
            await Assert.That(tree.Status).IsEqualTo(HtmlTokenizationStatus.Partial);
            await Assert.That(tree.Diagnostics.Any(d => d.Code == code && d.IncompleteCoverage)).IsTrue();
        }
        var custom = Parse("<my-element><p>X</p></my-element>");
        await Assert.That(custom.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
        await Assert.That(custom.Elements().Any(n => n.Name == "my-element")).IsTrue();
    }

    [Test]
    public async Task TreeAndTokenizerBudgets_PropagateIncompleteCoverageAndCancellation()
    {
        foreach (var (raw, limits, code) in new[]
        {
            ("<p>X", new HtmlTreeLimits { MaxNodes = 3 }, "tree-node-budget"),
            ("<div><div><div><div>X", new HtmlTreeLimits { MaxDepth = 4 }, "tree-depth-budget"),
            ("<p>X", new HtmlTreeLimits { MaxOperations = 2 }, "tree-operation-budget"),
            ("</x></y>", new HtmlTreeLimits { MaxDiagnostics = 1 }, "tree-diagnostic-budget"),
            ("<html a=1><html b=2>", new HtmlTreeLimits { MaxAttributesPerElement = 1 }, "tree-attribute-budget"),
            ("abcdef", new HtmlTreeLimits { Tokenizer = new() { MaxInputChars = 3 } }, "input-char-budget"),
            ("<p>X<p>Y", new HtmlTreeLimits { Tokenizer = new() { MaxTokens = 1 } }, "token-budget")
        })
        {
            var tree = CompactHtmlTreeBuilder.Parse(raw, limits: limits);
            await Assert.That(tree.Status == HtmlTokenizationStatus.Complete).IsFalse();
            await Assert.That(tree.Diagnostics.Any(d => d.Code == code && d.IncompleteCoverage) || tree.TokenizerDiagnostics.Any(d => d.Code == code)).IsTrue();
            await Assert.That(tree.Nodes.Count <= limits.MaxNodes).IsTrue();
        }
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.That(() => CompactHtmlTreeBuilder.Parse("<p>X", cancellationToken: cancel.Token)).Throws<OperationCanceledException>();
        foreach (var context in new[] { new HtmlFragmentContext("textarea"), new HtmlFragmentContext("svg", HtmlNamespaces.Svg) })
        {
            var failed = CompactHtmlTreeBuilder.Parse("abcdef", new(Fragment: context), new() { Tokenizer = new() { MaxInputChars = 3 } });
            await Assert.That(failed.Status).IsEqualTo(HtmlTokenizationStatus.Failed);
            await Assert.That(failed.TokenizerDiagnostics.Any(d => d.Code == "input-char-budget")).IsTrue();
        }
    }

    [Test]
    public async Task FixedMalformedTreeSeeds_TerminateWithConsistentLinksAndRawMappings()
    {
        string[] parts = ["<b>", "<i>", "</b>", "</i>", "<p>", "</p>", "<div>", "</div>", "<table>", "</table>",
            "<tr>", "<td>", "</td>", "</tr>", "<template>", "</template>", "<select>", "<option>", "</select>",
            "<svg>", "</svg>", "<foreignObject>", "</foreignObject>", "<math>", "<mi>", "</mi>", "</math>",
            "<a href='x'>", "</a>", "<form>", "</form>", "X", " \r\n", "&amp;😀", "\0"];
        var random = new Random(1803);
        for (var sample = 0; sample < 128; sample++)
        {
            var raw = string.Concat(Enumerable.Range(0, 24).Select(_ => parts[random.Next(parts.Length)]));
            var tree = CompactHtmlTreeBuilder.Parse(raw, limits: new()
            {
                MaxNodes = 300, MaxDepth = 40, MaxOperations = 30_000, Tokenizer = new() { MaxTextChunkChars = 3 }
            });
            await Assert.That(tree.Nodes.Count <= 300).IsTrue();
            await CheckLinksAndSpans(tree, raw.Length);
            var visited = new HashSet<int>();
            var pending = new Stack<int>(); pending.Push(tree.Root);
            while (pending.Count > 0)
            {
                var id = pending.Pop();
                await Assert.That(visited.Add(id)).IsTrue();
                foreach (var child in tree.Nodes[id].Children) pending.Push(child);
                if (tree.Nodes[id].TemplateContent is int content) pending.Push(content);
            }
        }
    }

    [Test]
    public async Task HeadNoscriptDoctype_IsIgnoredWithoutPoppingTheNoscriptElement()
    {
        const string raw = "<head><noscript><!doctype html><meta id=x></noscript>";
        foreach (var chunk in new[] { 4096, 1, 2, 7 })
        {
            var tree = Parse(raw, new(Scripting: false), chunk);
            var meta = ById(tree, "x");
            await Assert.That(tree.Nodes[meta.Parent!.Value].Name).IsEqualTo("noscript");
            await Assert.That(meta.Source!.Value.Start).IsEqualTo(raw.IndexOf("<meta", StringComparison.Ordinal));
            await Assert.That(tree.Diagnostics.Count(d => d.Code == "unexpected-doctype")).IsEqualTo(1);
            await Assert.That(tree.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
            await CheckLinksAndSpans(tree, raw.Length);
        }
    }

    [Test]
    public async Task IgnoredHtmlNulls_DoNotReconstructFormattingRejectFramesetsOrFosterWhitespace()
    {
        foreach (var chunk in new[] { 4096, 1, 2, 7 })
        {
            const string formattingRaw = "<p><b></p>\0";
            var formatting = Parse(formattingRaw, chunk: chunk);
            await Assert.That(formatting.Elements().Count(n => n.Name == "b")).IsEqualTo(1);
            await Assert.That(formatting.TokenizerDiagnostics.Any(d => d.Code == "unexpected-null-character" && d.Source.Start == formattingRaw.Length - 1)).IsTrue();
            await CheckLinksAndSpans(formatting, formattingRaw.Length);

            const string framesetRaw = "\0<frameset><frame></frameset>";
            var frameset = Parse(framesetRaw, chunk: chunk);
            await Assert.That(frameset.Elements().Count(n => n.Name == "frameset")).IsEqualTo(1);
            await Assert.That(frameset.Elements().Count(n => n.Name == "frame")).IsEqualTo(1);
            await Assert.That(frameset.Diagnostics.Any(d => d.Code == "unexpected-frameset-start")).IsFalse();
            await CheckLinksAndSpans(frameset, framesetRaw.Length);

            const string tableRaw = "<table> \0<tr><td>A</td></tr></table>";
            var tableTree = Parse(tableRaw, chunk: chunk);
            var table = tableTree.Elements().Single(n => n.Name == "table");
            await Assert.That(tableTree.TextContent(table.Id)).IsEqualTo(" A");
            await Assert.That(tableTree.Diagnostics.Any(d => d.Code == "foster-parented-text")).IsFalse();
            await Assert.That(tableTree.Nodes[table.Children[0]].TextChunks.Single().Segments.Single().Source).IsEqualTo(new HtmlSpan(7, 1));
            await CheckLinksAndSpans(tableTree, tableRaw.Length);

            const string mappingRaw = "<p>A\0&amp;B</p>";
            var mapping = Parse(mappingRaw, chunk: chunk);
            var paragraph = mapping.Elements().Single(n => n.Name == "p");
            await Assert.That(mapping.TextContent(paragraph.Id)).IsEqualTo("A&B");
            var segments = mapping.Nodes[paragraph.Children.Single()].TextChunks.SelectMany(t => t.Segments);
            await Assert.That(segments.Any(s => s.Source == new HtmlSpan(mappingRaw.IndexOf("&amp;", StringComparison.Ordinal), 5))).IsTrue();
            await CheckLinksAndSpans(mapping, mappingRaw.Length);
            foreach (var tree in new[] { formatting, frameset, tableTree, mapping })
                await Assert.That(tree.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
        }
    }

    [Test]
    public async Task OptgroupEnd_ConsumesTheTokenWithoutAnUnexpectedSelectDiagnostic()
    {
        foreach (var raw in new[] { "<select><optgroup><option>A</optgroup></select>", "<select><optgroup></optgroup></select>" })
            foreach (var chunk in new[] { 4096, 1, 2, 7 })
            {
                var tree = Parse(raw, chunk: chunk);
                await Assert.That(tree.Elements().Count(n => n.Name == "optgroup")).IsEqualTo(1);
                await Assert.That(tree.Diagnostics.Any(d => d.Code == "unexpected-select-token" || d.Code == "optgroup-not-current")).IsFalse();
                await Assert.That(tree.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
                await CheckLinksAndSpans(tree, raw.Length);
            }
    }

    [Test]
    public async Task BrEndRecovery_DiscardsEndTagAttributesAndRetainsTheRawSourceSpan()
    {
        foreach (var raw in new[] { "</br id=x><p>Tail</p>", "</BR id=x data-y='y' /><p>Tail</p>" })
            foreach (var chunk in new[] { 4096, 1, 2, 7 })
            {
                var tree = Parse(raw, chunk: chunk);
                var br = tree.Elements().Single(n => n.Name == "br");
                await Assert.That(br.Attributes.Count).IsEqualTo(0);
                await Assert.That(br.Source!.Value).IsEqualTo(new HtmlSpan(0, raw.IndexOf('>') + 1));
                await Assert.That(tree.Diagnostics.Count(d => d.Code == "unexpected-br-end")).IsEqualTo(1);
                await Assert.That(tree.TextContent(tree.Elements().Single(n => n.Name == "p").Id)).IsEqualTo("Tail");
                await Assert.That(tree.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
                await CheckLinksAndSpans(tree, raw.Length);
            }
    }

    private static CompactHtmlTree Parse(string raw, HtmlTreeOptions? options = null, int chunk = 4096) =>
        CompactHtmlTreeBuilder.Parse(raw, options, new() { Tokenizer = new() { MaxTextChunkChars = chunk } });

    private static HtmlTreeNode ById(CompactHtmlTree tree, string id) =>
        tree.Elements().Single(n => n.Attributes.Any(a => a.Name == "id" && a.Value.Value == id));

    // Deliberately limited to the immutable HT-01 fixture grammar; this is not the HT-05 selector engine.
    private static bool FixtureMatches(HtmlTreeNode node, string selector) => selector.Split(',').Any(term =>
        term.StartsWith('#') ? node.Attributes.Any(a => a.Name == "id" && a.Value.Value == term[1..]) :
        term.EndsWith("[id]", StringComparison.Ordinal) ? (term.Length == 4 || node.Name == term[..^4]) && node.Attributes.Any(a => a.Name == "id") :
        node.Name == term);

    private static async Task CompareQueries(CompactHtmlTree tree, JsonElement queries, int root)
    {
        foreach (var query in queries.EnumerateArray())
        {
            var actual = tree.Elements(root).Where(n => FixtureMatches(n, query.GetProperty("selector").GetString()!)).ToArray();
            var expected = query.GetProperty("elements").EnumerateArray().ToArray();
            await Assert.That(actual.Length).IsEqualTo(expected.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                var node = actual[i]; var baseline = expected[i];
                await Assert.That(node.Name).IsEqualTo(baseline.GetProperty("name").GetString());
                await Assert.That(node.Namespace).IsEqualTo(baseline.GetProperty("namespace").GetString());
                await Assert.That(node.Parent is int parent ? tree.Nodes[parent].Name : null).IsEqualTo(baseline.GetProperty("parent").GetString());
                await Assert.That(tree.TextContent(node.Id)).IsEqualTo(baseline.GetProperty("text").GetString());
                var attributes = baseline.GetProperty("attributes").EnumerateArray().ToArray();
                await Assert.That(node.Attributes.Count).IsEqualTo(attributes.Length);
                for (var a = 0; a < attributes.Length; a++)
                {
                    await Assert.That(node.Attributes[a].Name).IsEqualTo(attributes[a].GetProperty("name").GetString());
                    await Assert.That(node.Attributes[a].Value.Value).IsEqualTo(attributes[a].GetProperty("value").GetString());
                }
                var source = baseline.GetProperty("source");
                if (source.ValueKind == JsonValueKind.Object)
                {
                    var index = source.GetProperty("oracleIndex").GetInt32();
                    await Assert.That(node.Source?.Start).IsEqualTo(index < 0 ? (int?)null : index);
                }
                else if (node.Namespace == HtmlNamespaces.Html) await Assert.That(node.Source is null).IsTrue();
                // The old oracle lacks positions for foreign roots; retain our actual token span.
            }
        }
    }

    private static string Shape(CompactHtmlTree tree)
    {
        var text = new StringBuilder();
        void Visit(int id)
        {
            var node = tree.Nodes[id];
            text.Append('(').Append(node.Kind).Append(JsonSerializer.Serialize(node.Name)).Append(JsonSerializer.Serialize(node.Namespace));
            text.Append(node.Source?.Start.ToString() ?? "?");
            foreach (var attribute in node.Attributes) text.Append(JsonSerializer.Serialize(new[] { attribute.Name, attribute.Namespace, attribute.Prefix, attribute.Value.Value }));
            if (node.Kind == HtmlTreeNodeKind.Text) text.Append(JsonSerializer.Serialize(string.Concat(node.TextChunks.Select(c => c.Value))));
            foreach (var child in node.Children) Visit(child);
            if (node.TemplateContent is int content) { text.Append("content:"); Visit(content); }
            text.Append(')');
        }
        Visit(tree.Root);
        return text.ToString();
    }

    private static async Task CheckLinksAndSpans(CompactHtmlTree tree, int rawLength)
    {
        foreach (var node in tree.Nodes)
        {
            if (node.Parent is int parent) await Assert.That(tree.Nodes[parent].Children.Count(id => id == node.Id)).IsEqualTo(1);
            foreach (var child in node.Children) await Assert.That(tree.Nodes[child].Parent).IsEqualTo((int?)node.Id);
            if (node.TemplateContent is int content) await Assert.That(tree.Nodes[content].Parent is null).IsTrue();
            if (node.Source is { } span) await Assert.That(span.Start >= 0 && span.End <= rawLength).IsTrue();
            foreach (var text in node.TextChunks.Concat(node.Attributes.SelectMany(a => new[] { a.RawName, a.Value })))
            {
                var end = 0;
                foreach (var segment in text.Segments)
                {
                    await Assert.That(segment.ValueStart).IsEqualTo(end);
                    await Assert.That(segment.Source.Start >= 0 && segment.Source.End <= rawLength).IsTrue();
                    end += segment.ValueLength;
                }
                await Assert.That(end).IsEqualTo(text.Value.Length);
            }
        }
    }
}
