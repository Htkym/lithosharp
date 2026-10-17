using System.Text.Json;
using LithoSharp.HtmlParsing;

namespace LithoSharp.Tests;

public sealed class HtmlTokenizerTests
{
    [Test]
    public async Task TextModes_KeepFalseTagsAndCloseOnlyTheAppropriateEndTag()
    {
        foreach (var (mode, tag, raw, expected) in new[]
        {
            (HtmlTextMode.RcData, "textarea", "&lt;x&gt;<b>&amp;</wrong></TEXTAREA>", "<x><b>&</wrong>"),
            (HtmlTextMode.RawText, "style", ".x{content:'<p>&amp;'};</stylesheet></STYLE>", ".x{content:'<p>&amp;'};</stylesheet>"),
            (HtmlTextMode.ScriptData, "script", "const x='<a href=fake>';<!--<script></script>--></SCRIPT>", "const x='<a href=fake>';<!--<script></script>-->")
        })
        {
            var reader = new HtmlTokenizer(raw);
            reader.SetContext(mode, tag);
            var tokens = ReadAll(reader);
            await Assert.That(string.Concat(tokens.Where(t => t.Kind == HtmlTokenKind.Text).Select(t => t.Data!.Value))).IsEqualTo(expected);
            await Assert.That(tokens.Count(t => t.Kind == HtmlTokenKind.EndTag)).IsEqualTo(1);
            await Assert.That(tokens.Single(t => t.Kind == HtmlTokenKind.EndTag).Name!.Value).IsEqualTo(tag);
            await Assert.That(tokens.Any(t => t.Kind == HtmlTokenKind.StartTag)).IsFalse();
        }
        var plaintext = new HtmlTokenizer("<b>&amp;\r\n</plaintext>");
        plaintext.SetContext(HtmlTextMode.PlainText);
        await Assert.That(string.Concat(ReadAll(plaintext).Select(t => t.Data?.Value))).IsEqualTo("<b>&amp;\n</plaintext>");
    }

    [Test]
    public async Task Attributes_KeepFirstDuplicateAndDecodeExactlyOnce()
    {
        var tokens = ReadAll(new HtmlTokenizer("<P ID='first' id=second disabled data-x=\"&amp;amp;&#x1f600;\" slash=v/>"));
        var tag = tokens.Single();
        await Assert.That(tag.Name!.Value).IsEqualTo("p");
        await Assert.That(tag.Attributes!.Count).IsEqualTo(4);
        await Assert.That(tag.Attributes![0].Value.Value).IsEqualTo("first");
        await Assert.That(tag.Attributes![1].Value.Value).IsEqualTo("");
        await Assert.That(tag.Attributes![2].Value.Value).IsEqualTo("&amp;😀");
        await Assert.That(tag.Attributes![3].Value.Value).IsEqualTo("v/");
        await Assert.That(tag.SelfClosing).IsFalse();
        var closed = ReadAll(new HtmlTokenizer("<img src=x />")).Single();
        await Assert.That(closed.SelfClosing).IsTrue();
    }

    [Test]
    public async Task SmallTextChunks_PreserveScriptStateAcrossCalls()
    {
        const string raw = "<!--<ScRiPt>one</SCRIPT>two--></sCrIpT>";
        var reader = new HtmlTokenizer(raw, new HtmlTokenizerLimits { MaxTextChunkChars = 1 });
        reader.SetContext(HtmlTextMode.ScriptData, "script");
        var tokens = ReadAll(reader);
        await Assert.That(string.Concat(tokens.Where(t => t.Kind == HtmlTokenKind.Text).Select(t => t.Data!.Value)))
            .IsEqualTo("<!--<ScRiPt>one</SCRIPT>two-->");
        await Assert.That(tokens.Count(t => t.Kind == HtmlTokenKind.EndTag)).IsEqualTo(1);
        await Assert.That(reader.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
    }

    [Test]
    public async Task ScriptEscapeStartDash_ClosesCommentBeforeTheFollowingScriptText()
    {
        foreach (var limits in new[] { new HtmlTokenizerLimits(), new HtmlTokenizerLimits { MaxTextChunkChars = 1 } })
        {
            var reader = new HtmlTokenizer("<!--><script></script>", limits);
            reader.SetContext(HtmlTextMode.ScriptData, "script");
            var text = new List<HtmlToken>();
            var token = reader.NextToken();
            while (token.Kind == HtmlTokenKind.Text)
            {
                text.Add(token);
                token = reader.NextToken();
            }
            await Assert.That(string.Concat(text.Select(t => t.Data!.Value))).IsEqualTo("<!--><script>");
            await Assert.That(token.Kind).IsEqualTo(HtmlTokenKind.EndTag);
            await Assert.That(token.Name!.Value).IsEqualTo("script");
            await Assert.That(reader.NextToken().Kind).IsEqualTo(HtmlTokenKind.EndOfFile);
            await Assert.That(reader.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
            await Assert.That(reader.Diagnostics.Any(d => d.Code == "eof-in-script-html-comment-like-text")).IsFalse();
        }
    }

    [Test]
    public async Task LiteralAmpersandSemicolon_HasNoErrorWhileUnknownNamesDo()
    {
        foreach (var (input, unknown) in new[] { ("&;", false), ("&missing;", true) })
        {
            var body = new HtmlTokenizer(input);
            await Assert.That(string.Concat(ReadAll(body).Select(t => t.Data!.Value))).IsEqualTo(input);
            await Assert.That(body.Diagnostics.Any(d => d.Code == "unknown-named-character-reference")).IsEqualTo(unknown);
            await Assert.That(body.Diagnostics.Count).IsEqualTo(unknown ? 1 : 0);

            var attribute = new HtmlTokenizer("<p x='" + input + "'>");
            await Assert.That(ReadAll(attribute).Single().Attributes![0].Value.Value).IsEqualTo(input);
            await Assert.That(attribute.Diagnostics.Any(d => d.Code == "unknown-named-character-reference")).IsEqualTo(unknown);
            await Assert.That(attribute.Diagnostics.Count).IsEqualTo(unknown ? 1 : 0);
        }
    }

    [Test]
    public async Task TextContextChanges_AreSuppliedByTheTreeBuilder()
    {
        var reader = new HtmlTokenizer("<textarea>&lt;b&gt;<i></textarea><p>");
        await Assert.That(reader.NextToken().Name!.Value).IsEqualTo("textarea");
        reader.SetContext(HtmlTextMode.RcData, "textarea");
        await Assert.That(reader.NextToken().Data!.Value).IsEqualTo("<b><i>");
        await Assert.That(reader.NextToken().Name!.Value).IsEqualTo("textarea");
        await Assert.That(reader.NextToken().Name!.Value).IsEqualTo("p");
        await Assert.That(reader.NextToken().Kind).IsEqualTo(HtmlTokenKind.EndOfFile);
    }

    [Test]
    public async Task CharacterReferences_RespectAttributeAmbiguityAndNumericRecovery()
    {
        foreach (var (input, text, attribute) in new[]
        {
            ("&notit;", "¬it;", "&notit;"),
            ("&amp=", "&=", "&amp="),
            ("&NotEqualTilde;", "≂̸", "≂̸"),
            ("&#x80;&#0;&#xD800;&#1114112;", "€���", "€���"),
            ("&#13;&#xFDD0;&#xFFFF;", "\r\uFDD0\uFFFF", "\r\uFDD0\uFFFF"),
            ("&#x; &missing; &amp;amp;", "&#x; &missing; &amp;", "&#x; &missing; &amp;")
        })
        {
            var body = ReadAll(new HtmlTokenizer(input));
            await Assert.That(string.Concat(body.Select(t => t.Data!.Value))).IsEqualTo(text);
            var tag = ReadAll(new HtmlTokenizer("<p x=\"" + input + "\">")).Single();
            await Assert.That(tag.Attributes![0].Value.Value).IsEqualTo(attribute);
        }
    }

    [Test]
    public async Task EveryPinnedNamedReference_MatchesIndependentWhatwgExpectations()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HtmlTokenization", "named-character-references.json");
        using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var count = 0;
        foreach (var item in expected.RootElement.EnumerateObject())
        {
            var tokens = ReadAll(new HtmlTokenizer(item.Name));
            await Assert.That(string.Concat(tokens.Select(t => t.Data!.Value))).IsEqualTo(item.Value.GetString());
            var tag = ReadAll(new HtmlTokenizer("<p x='" + item.Name + "'>")).Single();
            await Assert.That(tag.Attributes![0].Value.Value).IsEqualTo(item.Value.GetString());
            count++;
        }
        await Assert.That(count).IsEqualTo(2231);
    }

    [Test]
    public async Task OriginalUtf16Spans_SurviveCrLfEntitiesAndSurrogatePairs()
    {
        const string raw = "😀\r\n<p DATA-X='&amp;&#x1F600;'>😀</p>";
        var reader = new HtmlTokenizer(raw);
        var tokens = ReadAll(reader);
        var initial = tokens[0].Data!;
        await Assert.That(initial.Value).IsEqualTo("😀\n");
        await Assert.That(initial.Source).IsEqualTo(new HtmlSpan(0, 4));
        await Assert.That(initial.Segments[^1].Source).IsEqualTo(new HtmlSpan(2, 2));
        var tag = tokens.Single(t => t.Kind == HtmlTokenKind.StartTag);
        await Assert.That(tag.Source.Start).IsEqualTo(4);
        var attribute = tag.Attributes!.Single();
        await Assert.That(raw.Substring(attribute.Name.Source.Start, attribute.Name.Source.Length)).IsEqualTo("DATA-X");
        await Assert.That(attribute.Value.Value).IsEqualTo("&😀");
        await Assert.That(raw.Substring(attribute.Value.Source.Start, attribute.Value.Source.Length)).IsEqualTo("&amp;&#x1F600;");
        await Assert.That(attribute.Value.Segments[1].ValueLength).IsEqualTo(2);
        await Assert.That(attribute.Value.Segments[1].Source.Length).IsEqualTo(9);
    }

    [Test]
    public async Task CommentsDoctypesCdataAndProcessingInstructions_PreserveTheirSeparateTokens()
    {
        var reader = new HtmlTokenizer("<!DOCTYPE HTML PUBLIC \"pub\" 'sys'><!--a<!--b--!><?target data?><!bogus>");
        var tokens = ReadAll(reader);
        var doc = tokens[0];
        await Assert.That(doc.Kind).IsEqualTo(HtmlTokenKind.Doctype);
        await Assert.That(doc.Name!.Value).IsEqualTo("html");
        await Assert.That(doc.PublicIdentifier!.Value).IsEqualTo("pub");
        await Assert.That(doc.SystemIdentifier!.Value).IsEqualTo("sys");
        await Assert.That(doc.ForceQuirks).IsFalse();
        await Assert.That(tokens[1].Data!.Value).IsEqualTo("a<!--b");
        await Assert.That(tokens[2].Kind).IsEqualTo(HtmlTokenKind.ProcessingInstruction);
        await Assert.That(tokens[2].Name!.Value).IsEqualTo("target");
        await Assert.That(tokens[2].Data!.Value).IsEqualTo("data");
        await Assert.That(tokens[3].Data!.Value).IsEqualTo("bogus");
        await Assert.That(reader.Diagnostics.Any(d => d.Code == "nested-comment")).IsTrue();
        await Assert.That(reader.Diagnostics.Any(d => d.Code == "incorrectly-closed-comment")).IsTrue();
        var foreign = new HtmlTokenizer("<![CDATA[<p>&amp;]]>");
        foreign.SetContext(HtmlTextMode.Data, allowCdata: true);
        await Assert.That(string.Concat(ReadAll(foreign).Select(t => t.Data!.Value))).IsEqualTo("<p>&amp;");
        var html = new HtmlTokenizer("<![CDATA[x]]>");
        await Assert.That(ReadAll(html).Single().Kind).IsEqualTo(HtmlTokenKind.Comment);
        await Assert.That(html.Diagnostics.Any(d => d.Code == "cdata-in-html-content")).IsTrue();
    }

    [Test]
    public async Task EofAtLexicalBoundaries_ReportsRecoveryAndNeverPublishesIncompleteTags()
    {
        foreach (var input in new[] { "<a", "<a ", "<a x", "<a x=", "<a x='v", "<a x=\"v", "<a x=v", "<a x='v' ", "<a/" })
        {
            var reader = new HtmlTokenizer(input);
            await Assert.That(ReadAll(reader).Count).IsEqualTo(0);
            await Assert.That(reader.Diagnostics.Any(d => d.Code == "eof-in-tag")).IsTrue();
        }
        foreach (var input in new[] { "<!--", "<!---", "<!--x", "<!--x-", "<!--x--", "<!--x--!", "<!--x<", "<!--x<!" })
        {
            var reader = new HtmlTokenizer(input);
            await Assert.That(ReadAll(reader).Single().Kind).IsEqualTo(HtmlTokenKind.Comment);
            await Assert.That(reader.Diagnostics.Any(d => d.Code == "eof-in-comment")).IsTrue();
        }
        foreach (var input in new[] { "<!DOCTYPE", "<!DOCTYPE ", "<!DOCTYPE html", "<!DOCTYPE html PUBLIC", "<!DOCTYPE html PUBLIC 'p", "<!DOCTYPE html SYSTEM", "<!DOCTYPE html SYSTEM 's" })
        {
            var reader = new HtmlTokenizer(input);
            await Assert.That(ReadAll(reader).Single().ForceQuirks).IsTrue();
            await Assert.That(reader.Diagnostics.Any(d => d.Code == "eof-in-doctype")).IsTrue();
        }
    }

    [Test]
    public async Task ProcessingInstructionRecovery_AndBogusDoctypeKeepTheirDistinctErrors()
    {
        var reader = new HtmlTokenizer("<?XML x?><?target: x?><!DOCTYPE html SYSTEM 's' unexpected>");
        var tokens = ReadAll(reader);
        await Assert.That(tokens[0].Kind).IsEqualTo(HtmlTokenKind.Comment);
        await Assert.That(tokens[0].Data!.Value).IsEqualTo("?XML x?");
        await Assert.That(tokens[1].Data!.Value).IsEqualTo("?target: x?");
        await Assert.That(tokens[2].Kind).IsEqualTo(HtmlTokenKind.Doctype);
        await Assert.That(tokens[2].ForceQuirks).IsFalse();
        await Assert.That(reader.Diagnostics.Any(d => d.Code == "disallowed-processing-instruction-target")).IsTrue();
        await Assert.That(reader.Diagnostics.Any(d => d.Code == "invalid-processing-instruction-target")).IsTrue();
        foreach (var raw in new[] { "<?", "<?target", "<?target data", "<?target data?" })
        {
            var incomplete = new HtmlTokenizer(raw);
            await Assert.That(ReadAll(incomplete).Count).IsEqualTo(0);
            await Assert.That(incomplete.Diagnostics.Any(d => d.Code == "eof-in-processing-instruction")).IsTrue();
        }
    }

    [Test]
    public async Task InvalidUnicodeAndNull_KeepRawErrorLocations()
    {
        var reader = new HtmlTokenizer("a\r\n\0\uD800\uFFFF<p x='\0'>");
        var tokens = ReadAll(reader);
        await Assert.That(tokens[0].Data!.Value).IsEqualTo("a\n\0\uD800\uFFFF");
        var nul = reader.Diagnostics.First(d => d.Code == "unexpected-null-character");
        await Assert.That(nul.Source).IsEqualTo(new HtmlSpan(3, 1));
        await Assert.That(nul.Line).IsEqualTo(2);
        await Assert.That(nul.Column).IsEqualTo(1);
        await Assert.That(reader.Diagnostics.Any(d => d.Code == "surrogate-in-input-stream")).IsTrue();
        await Assert.That(reader.Diagnostics.Any(d => d.Code == "noncharacter-in-input-stream")).IsTrue();
        await Assert.That(tokens.Single(t => t.Kind == HtmlTokenKind.StartTag).Attributes![0].Value.Value).IsEqualTo("�");
    }

    [Test]
    public async Task Budgets_LeaveIncompleteCoverageAndCancellationPropagates()
    {
        foreach (var (input, limits, code) in new[]
        {
            ("abcdef", new HtmlTokenizerLimits { MaxInputChars = 3 }, "input-char-budget"),
            ("😀😀", new HtmlTokenizerLimits { MaxInputUtf8Bytes = 4 }, "input-byte-budget"),
            ("<a><b>", new HtmlTokenizerLimits { MaxTokens = 1 }, "token-budget"),
            ("<p a b>", new HtmlTokenizerLimits { MaxAttributesPerTag = 1 }, "attribute-budget"),
            ("<p x='abcd'>", new HtmlTokenizerLimits { MaxValueChars = 3 }, "value-budget"),
            ("abcdef", new HtmlTokenizerLimits { MaxDecodedChars = 3 }, "decoded-char-budget"),
            ("&amp;&lt;", new HtmlTokenizerLimits { MaxSourceSegments = 1 }, "source-segment-budget"),
            ("\0\0\0", new HtmlTokenizerLimits { MaxDiagnostics = 1 }, "diagnostic-budget")
        })
        {
            var reader = new HtmlTokenizer(input, limits);
            _ = ReadAll(reader);
            await Assert.That(reader.Status == HtmlTokenizationStatus.Complete).IsFalse();
            await Assert.That(reader.Diagnostics.Any(d => d.Code == code)).IsTrue();
        }
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.That(() => new HtmlTokenizer("<p>", cancellationToken: cancel.Token).NextToken())
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task FixedFuzzSeeds_TerminateAndKeepEveryMappingInsideTheOriginalInput()
    {
        const string alphabet = "<>!?/-='\"&;#x0Aa \r\n\0\uD800\uDC00[]";
        var random = new Random(1702);
        for (var sample = 0; sample < 256; sample++)
        {
            var raw = new string(Enumerable.Range(0, random.Next(1, 160)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var reader = new HtmlTokenizer(raw);
            var tokens = ReadAll(reader);
            await Assert.That(tokens.Count <= raw.Length * 2 + 1).IsTrue();
            foreach (var token in tokens)
            {
                await Assert.That(token.Source.Start >= 0 && token.Source.End <= raw.Length).IsTrue();
                foreach (var text in new[] { token.Name, token.Data, token.PublicIdentifier, token.SystemIdentifier }
                    .Concat(token.Attributes?.SelectMany(a => new[] { a.Name, a.Value }) ?? []))
                {
                    if (text is null) continue;
                    var valueEnd = 0;
                    foreach (var segment in text.Segments)
                    {
                        await Assert.That(segment.ValueStart).IsEqualTo(valueEnd);
                        await Assert.That(segment.Source.Start >= 0 && segment.Source.End <= raw.Length).IsTrue();
                        valueEnd += segment.ValueLength;
                    }
                    await Assert.That(valueEnd).IsEqualTo(text.Value.Length);
                }
            }
        }
    }

    private static List<HtmlToken> ReadAll(HtmlTokenizer reader)
    {
        var tokens = new List<HtmlToken>();
        while (true)
        {
            var token = reader.NextToken();
            if (token.Kind == HtmlTokenKind.EndOfFile) return tokens;
            tokens.Add(token);
        }
    }
}
