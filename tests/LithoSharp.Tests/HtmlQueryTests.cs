using System.Text.Json;
using LithoSharp.Testing;

namespace LithoSharp.Tests;

public sealed class HtmlQueryTests
{
    private const string Fixture = "<!doctype html><main id=root><ul id=list>Loose text"
        + "<li id=a class='item first' data-tags='one two' lang=en-US data-value=Alphabet>A</li><!--gap-->"
        + "<li id=b class=item data-value=Beta>B</li><li id=c class=item>C</li><li id=d>D</li></ul>"
        + "<p id=outside>Text<span id=inner>Inner</span></p></main>";

    [Test]
    [Arguments("LI", "a,b,c,d")]
    [Arguments("main > ul > *", "a,b,c,d")]
    [Arguments("#a", "a")]
    [Arguments("li.item.first", "a")]
    [Arguments("[data-tags]", "a")]
    [Arguments("[data-value=Alphabet]", "a")]
    [Arguments("[data-tags~=two]", "a")]
    [Arguments("[lang|=en]", "a")]
    [Arguments("[data-value^=Alpha]", "a")]
    [Arguments("[data-value$=bet]", "a")]
    [Arguments("[data-value*=pha]", "a")]
    [Arguments("[data-value^='']", "")]
    [Arguments("[missing]", "")]
    [Arguments("[data-value=alpha]", "")]
    [Arguments("[data-tags~=on]", "")]
    [Arguments("[data-tags~='']", "")]
    [Arguments("[lang|=e]", "")]
    [Arguments("[data-value^=beta]", "")]
    [Arguments("[data-value$=Alpha]", "")]
    [Arguments("[data-value*=z]", "")]
    [Arguments("[data-value$='']", "")]
    [Arguments("[data-value*='']", "")]
    [Arguments("[data-value=alphabet i]", "a")]
    [Arguments("[data-value=alphabet s]", "")]
    [Arguments("[lang='EN-us']", "a")]
    [Arguments("main li", "a,b,c,d")]
    [Arguments("li + li", "b,c,d")]
    [Arguments("#a ~ li", "b,c,d")]
    [Arguments("#d,#a,li:first-child", "a,d")]
    [Arguments(":scope > body > main", "root")]
    [Arguments("li:first-child", "a")]
    [Arguments("li:last-child", "d")]
    [Arguments("p > span:only-child", "inner")]
    [Arguments("li:only-child", "")]
    [Arguments("li:nth-child(odd)", "a,c")]
    [Arguments("li:nth-child(EVEN)", "b,d")]
    [Arguments("li:nth-child(2n + 1)", "a,c")]
    [Arguments("li:nth-child(-n+3)", "a,b,c")]
    [Arguments("li:nth-child(n-2)", "a,b,c,d")]
    [Arguments("li:nth-child(0n+2)", "b")]
    [Arguments("li:nth-child(+3)", "c")]
    [Arguments(@"li:nth-child(\6e + 2)", "b,c,d")]
    [Arguments("li:not(.first,#d)", "b,c")]
    [Arguments("li:is(ul > li.first,#d)", "a,d")]
    [Arguments("li:where(.first,#d)", "a,d")]
    [Arguments("li:not(:is(.first,#d))", "b,c")]
    [Arguments("li/**/.first", "a")]
    [Arguments("main/**/ li.first", "a")]
    public async Task DeclaredGrammarMatchesExactOrderedElements(string selector, string expectedIds)
    {
        using var document = SiteTestDocument.Parse(Fixture);
        var matches = document.Snapshot.QueryAll(selector);
        await Assert.That(string.Join(",", matches.Select(element => element.GetAttribute("id")))).IsEqualTo(expectedIds);
        await Assert.That(document.Snapshot.Query(selector)?.GetAttribute("id"))
            .IsEqualTo(expectedIds.Length == 0 ? null : expectedIds.Split(',')[0]);
    }

    [Test]
    [Arguments("p[")]
    [Arguments("p,")]
    [Arguments(",p")]
    [Arguments("p >")]
    [Arguments(":not()")]
    [Arguments(":is(p,)")]
    [Arguments("[id='unterminated]")]
    [Arguments("[id='a\nb']")]
    [Arguments("#123")]
    [Arguments("p\\")]
    [Arguments("p:has(")]
    [Arguments("p:has(/*unterminated)")]
    [Arguments("li:nth-child(2 n+1)")]
    [Arguments("li:nth-child(+ 2n)")]
    [Arguments("li:nth-child(2n + -1)")]
    [Arguments("li:nth-child(odd+1)")]
    [Arguments("[id=a z]")]
    public async Task MalformedSelectorsAreNotReportedAsMissingElements(string selector)
    {
        using var document = SiteTestDocument.Parse(Fixture);
        await Assert.That(() => document.Snapshot.Query(selector)).Throws<HtmlSelectorSyntaxException>();
        await Assert.That(() => document.AssertElement(selector)).Throws<HtmlSelectorSyntaxException>();
    }

    [Test]
    [Arguments("li:has(> span)")]
    [Arguments("li:hover")]
    [Arguments("li::before")]
    [Arguments(":nth-child(2n of .item)")]
    [Arguments("svg|a")]
    [Arguments("[xlink|href]")]
    [Arguments(":is(li,:hover)")]
    [Arguments(":not(:has(a))")]
    public async Task UnsupportedFeaturesCannotSilentlyMatchNothingOrPartOfAList(string selector)
    {
        using var document = SiteTestDocument.Parse(Fixture);
        await Assert.That(() => document.Snapshot.QueryAll(selector)).Throws<HtmlSelectorUnsupportedException>();
    }

    [Test]
    public async Task EscapesQuotesUnicodeAndHtmlCaseRulesRemainObservable()
    {
        using var document = SiteTestDocument.Parse("<!doctype html><P ID='123' CLASS='a:b 日本語 😀' data-q='a&quot;b' data-line='ab' data-u='Ä' accept='IMAGE/PNG' accept-charset='UTF-8' linK='Mixed'>X</P>"
            + "<svg viewBox='0 0 1 1'><foreignObject><P id=inside>Y</P></foreignObject></svg>");
        foreach (var selector in new[] { @"#\31 23", @".a\:b", ".日本語", ".😀", "[data-q='a\"b']", "[data-line='a\\\nb']", "[accept='image/png']", "[accept-charset='utf-8']", "[data-line='AB'i]", "[data-line=AB/**/i]" })
            await Assert.That(document.Snapshot.Query(selector)?.TextContent).IsEqualTo("X");
        await Assert.That(document.Snapshot.Query("[data-line='AB's]")).IsNull();
        await Assert.That(() => document.Snapshot.Query("[data-line='AB'z]")).Throws<HtmlSelectorSyntaxException>();
        await Assert.That(document.Snapshot.Query("[accept='image/png' s]")).IsNull();
        await Assert.That(document.Snapshot.Query("[linK=Mixed]")?.TextContent).IsEqualTo("X");
        await Assert.That(document.Snapshot.Query("[linK=mixed]")).IsNull();
        await Assert.That(document.Snapshot.Query("[data-u='ä' i]")).IsNull();
        await Assert.That(document.Snapshot.Query("p[ID='123']")?.GetAttribute("ID")).IsEqualTo("123");
        await Assert.That(document.Snapshot.Query("svg[viewBox]")?.NamespaceUri).IsEqualTo("http://www.w3.org/2000/svg");
        await Assert.That(document.Snapshot.Query("svg[viewbox]")).IsNull();
        await Assert.That(document.Snapshot.Query("foreignObject p")?.GetAttribute("id")).IsEqualTo("inside");
        using var quirks = SiteTestDocument.Parse("<p id=Mixed class=Mixed>X</p>");
        await Assert.That(quirks.Snapshot.Query("#mixed.mixed")?.TextContent).IsEqualTo("X");
        using var standards = SiteTestDocument.Parse("<!doctype html><p id=Mixed class=Mixed>X</p>");
        await Assert.That(standards.Snapshot.Query("#mixed.mixed")).IsNull();
    }

    [Test]
    public async Task EscapedAsterisksRemainLiteralNamesInsteadOfSyntaxWildcards()
    {
        using var document = SiteTestDocument.Parse("<!doctype html><main><p *='literal'>Text</p>"
            + "<a id=html-link></a><svg><a id=svg-link></a></svg></main>");
        var snapshot = document.Snapshot;
        await Assert.That(snapshot.QueryAll("*").Count).IsEqualTo(8);
        await Assert.That(string.Join(',', snapshot.QueryAll("*|a").Select(element => element.GetAttribute("id"))))
            .IsEqualTo("html-link,svg-link");
        foreach (var selector in new[] { @"\*", @"\2a", @"*|\*", @"*|\2a" })
            await Assert.That(snapshot.QueryAll(selector).Count).IsEqualTo(0);
        foreach (var selector in new[] { @"[\*]", @"[\2a]", @"[|\*]", @"[*|\*]", @"[\*='literal']" })
            await Assert.That(snapshot.Query(selector)?.TextContent).IsEqualTo("Text");
        await Assert.That(snapshot.Query(@"[\*='other']")).IsNull();
        await Assert.That(snapshot.QueryAll(@":not(\*)").Count).IsEqualTo(8);
        await Assert.That(snapshot.QueryAll(@":is(\*,a)").Count).IsEqualTo(2);
        foreach (var selector in new[] { @"\*|a", @"\2a|a", @"[\*|href]", @"[\2a|href]" })
            await Assert.That(() => snapshot.Query(selector)).Throws<HtmlSelectorUnsupportedException>();
        foreach (var selector in new[] { "[*]", "[|*]", "[*|*]" })
            await Assert.That(() => snapshot.Query(selector)).Throws<HtmlSelectorSyntaxException>();
    }

    [Test]
    public async Task SnapshotPreservesRawPositionsTemplateNamespaceAndExactTextWithoutMutation()
    {
        const string xlink = "http://www.w3.org/1999/xlink";
        using var document = SiteTestDocument.Parse("<!doctype html><title>A\t B</title>\r\n😀"
            + "<main id=root><p id=actual>A<em>B</em><script>C</script> D</p>"
            + "<template><p id=hidden>Inert</p></template>"
            + "<svg><a xlink:href='wrong' href='right'>Foreign</a></svg><p id=last>Last</p></main>");
        var snapshot = document.Snapshot;
        var root = snapshot.Query("#root")!;
        var paragraph = root.Query(":scope > p")!;
        await Assert.That(snapshot.Title).IsEqualTo("A B");
        await Assert.That(paragraph.TextContent).IsEqualTo("ABC D");
        await Assert.That(paragraph.Parent!.GetAttribute("id")).IsEqualTo("root");
        await Assert.That(paragraph.PreviousSibling).IsNull();
        await Assert.That(paragraph.NextSibling!.TagName).IsEqualTo("template");
        await Assert.That(root.Line).IsEqualTo(2);
        await Assert.That(root.Column).IsEqualTo(3);
        await Assert.That(root.SourceStart).IsEqualTo(38);
        await Assert.That(snapshot.Query("body")!.SourceStart).IsNull();
        await Assert.That(snapshot.Query("#hidden")).IsNull();
        var template = snapshot.Query("template")!;
        await Assert.That(template.TextContent).IsEqualTo("");
        await Assert.That(template.TemplateContent.Single().TextContent).IsEqualTo("Inert");
        var link = snapshot.Query("svg a")!;
        await Assert.That(link.GetAttribute("href")).IsEqualTo("right");
        await Assert.That(link.GetAttribute("href", xlink)).IsEqualTo("wrong");
        await Assert.That(snapshot.Query("[*|href=wrong]")!.NamespaceUri).IsEqualTo("http://www.w3.org/2000/svg");
        await Assert.That(snapshot.Query("[|href=wrong]")).IsNull();
        await Assert.That(snapshot.Query("*|a")!.TextContent).IsEqualTo("Foreign");
        await Assert.That(snapshot.Query("|a")).IsNull();
        await Assert.That(root.Query(":scope")).IsNull();
        await Assert.That(root.Query("html body main > p")!.GetAttribute("id")).IsEqualTo("actual");
        var children = (IList<HtmlTestElement>)root.Children;
        await Assert.That(children.IsReadOnly).IsTrue();
        await Assert.That(() => children.Clear()).Throws<NotSupportedException>();
        await Assert.That(root.Children.Count).IsEqualTo(4);
        await Assert.That(() => ((IList<HtmlTestElement>)snapshot.QueryAll("p")).Clear()).Throws<NotSupportedException>();
    }

    [Test]
    public async Task OwnerLifetimeParallelReadsAndIndependentDocumentsHaveNoMutableSharedState()
    {
        await Assert.That(typeof(SiteTestDocument).GetProperty("Document")).IsNull();
        await Assert.That(typeof(SiteTestDocument).Assembly.GetReferencedAssemblies()
            .Any(assembly => assembly.Name == "AngleSharp")).IsFalse();
        var document = SiteTestDocument.Parse(Fixture);
        var snapshot = document.Snapshot;
        var element = snapshot.Query("#a")!;
        var held = snapshot.QueryAll("li");
        using var independent = SiteTestDocument.Parse("<p id=other>Other</p>");
        var reads = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            string.Join(",", snapshot.QueryAll("li").Select(item => item.TextContent)))));
        await Assert.That(reads.All(text => text == "A,B,C,D")).IsTrue();
        document.Dispose();
        document.Dispose();
        await Assert.That(() => _ = snapshot.Title).Throws<ObjectDisposedException>();
        await Assert.That(() => snapshot.Query("li")).Throws<ObjectDisposedException>();
        await Assert.That(() => _ = element.TextContent).Throws<ObjectDisposedException>();
        await Assert.That(() => _ = held[0].TagName).Throws<ObjectDisposedException>();
        await Assert.That(independent.Snapshot.Query("p")!.TextContent).IsEqualTo("Other");
    }

    [Test]
    public async Task QueryBudgetsCancellationAndIncompleteTreeCoverageFailExplicitly()
    {
        using var document = SiteTestDocument.Parse(Fixture);
        await Assert.That(() => document.Snapshot.Query(new string('a', 4097))).Throws<HtmlSelectorUnsupportedException>();
        var nested = string.Concat(Enumerable.Repeat(":not(", 17)) + "li" + new string(')', 17);
        await Assert.That(() => document.Snapshot.Query(nested)).Throws<HtmlSelectorUnsupportedException>();
        await Assert.That(() => document.Snapshot.Query("li", new CancellationToken(true))).Throws<OperationCanceledException>();
        await Assert.That(() => SiteTestDocument.Parse("<template shadowrootmode=open><p>Hidden</p></template>"))
            .Throws<InvalidOperationException>();
        using var large = SiteTestDocument.Parse("<!doctype html>" + string.Concat(Enumerable.Repeat("<p></p>", 25_000)));
        var expensive = string.Join(',', Enumerable.Repeat("*#missing", 64));
        await Assert.That(() => large.Snapshot.QueryAll(expensive)).Throws<HtmlSelectorUnsupportedException>();
        await Assert.That(large.Snapshot.Query("p")!.TagName).IsEqualTo("p");
    }

    [Test]
    public async Task SavedOracleDocumentQueriesRetainNamesParentsTextAndAttributes()
    {
        using var corpus = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "HtmlObservation", "html-observation-corpus-v1.json")));
        var cases = 0;
        foreach (var item in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (item.GetProperty("mode").GetString() != "document" || item.GetProperty("scripting").GetBoolean()) continue;
            using var document = SiteTestDocument.Parse(item.GetProperty("html").GetString()!);
            foreach (var query in item.GetProperty("queries").EnumerateArray())
            {
                var actual = document.Snapshot.QueryAll(query.GetProperty("selector").GetString()!);
                var expected = query.GetProperty("elements").EnumerateArray().ToArray();
                await Assert.That(actual.Count).IsEqualTo(expected.Length);
                for (var i = 0; i < actual.Count; i++)
                {
                    await Assert.That(actual[i].TagName).IsEqualTo(expected[i].GetProperty("name").GetString());
                    await Assert.That(actual[i].NamespaceUri).IsEqualTo(expected[i].GetProperty("namespace").GetString());
                    await Assert.That(actual[i].Parent?.TagName).IsEqualTo(expected[i].GetProperty("parent").GetString());
                    await Assert.That(actual[i].TextContent).IsEqualTo(expected[i].GetProperty("text").GetString());
                    foreach (var attribute in expected[i].GetProperty("attributes").EnumerateArray())
                        await Assert.That(actual[i].GetAttribute(attribute.GetProperty("name").GetString()!,
                            attribute.TryGetProperty("namespace", out var ns) ? ns.GetString() : null)).IsEqualTo(attribute.GetProperty("value").GetString());
                }
            }
            cases++;
        }
        await Assert.That(cases).IsEqualTo(10);
    }

    [Test]
    public async Task ManualMutationMigrationReparsesChangedSourceAndPreservesAssertionFailures()
    {
        const string before = "<p class=expected>Expected</p><a href='/good'>Good</a>";
        using var original = SiteTestDocument.Parse(before);
        original.AssertText("p", "Expected");
        original.AssertAttribute("p", "class", "expected");
        original.AssertLink("/good");
        using var changed = SiteTestDocument.Parse("<p class=changed>Changed</p><a href='/bad'>Bad</a>");
        await Assert.That(() => changed.AssertText("p", "Expected")).Throws<SiteTestException>();
        await Assert.That(() => changed.AssertAttribute("p", "class", "expected")).Throws<SiteTestException>();
        await Assert.That(() => changed.AssertLink("/good")).Throws<SiteTestException>();
        original.AssertText("p", "Expected");
    }
}
