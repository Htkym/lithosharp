using System.Reflection;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;

namespace LithoSharp.Tests;

/// <summary>
/// C02: source spans, line mapping, shared front matter splitting, and the
/// syntax/semantic model built from one Litho parse.
/// </summary>
public sealed class MarkdownDocumentModelTests
{
    [Test]
    public async Task SourceSpan_IsHalfOpenZeroBased()
    {
        var span = new SourceSpan(3, 4);

        await Assert.That(span.Start).IsEqualTo(3);
        await Assert.That(span.Length).IsEqualTo(4);
        await Assert.That(span.End).IsEqualTo(7);
        await Assert.That(span.IsEmpty).IsFalse();
        await Assert.That(span.Contains(3)).IsTrue();
        await Assert.That(span.Contains(6)).IsTrue();
        await Assert.That(span.Contains(7)).IsFalse();
        await Assert.That(span.Contains(2)).IsFalse();
        await Assert.That(SourceSpan.Empty.IsEmpty).IsTrue();
        await Assert.That(SourceSpan.FromInclusiveStartEnd(5, 4).IsEmpty).IsTrue();
        await Assert.That(SourceSpan.FromInclusiveStartEnd(5, 7)).IsEqualTo(new SourceSpan(5, 3));

        await Assert.That(() => new SourceSpan(-1, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new SourceSpan(0, -1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task SourceText_MapsLinesForLfCrlfBomJapaneseAndSurrogates()
    {
        var lf = new SourceText("a\nb\n");
        await Assert.That(lf.GetLineAndColumn(0)).IsEqualTo((1, 1));
        await Assert.That(lf.GetLineAndColumn(1)).IsEqualTo((1, 2));
        await Assert.That(lf.GetLineAndColumn(2)).IsEqualTo((2, 1));

        var crlf = new SourceText("a\r\nb");
        await Assert.That(crlf.GetLineAndColumn(0)).IsEqualTo((1, 1));
        await Assert.That(crlf.GetLineAndColumn(3)).IsEqualTo((2, 1));

        var bom = new SourceText("\uFEFF---\nbody");
        await Assert.That(bom.GetLineAndColumn(0)).IsEqualTo((1, 1));
        await Assert.That(bom.GetLineAndColumn(5)).IsEqualTo((2, 1));

        var japanese = new SourceText("あ\nい");
        await Assert.That(japanese.GetLineAndColumn(0)).IsEqualTo((1, 1));
        await Assert.That(japanese.GetLineAndColumn(1)).IsEqualTo((1, 2));
        await Assert.That(japanese.GetLineAndColumn(2)).IsEqualTo((2, 1));

        // Columns count UTF-16 code units: the emoji occupies two units.
        var emoji = new SourceText("\U0001F600\nx");
        await Assert.That(emoji.GetLineAndColumn(2)).IsEqualTo((1, 3));
        await Assert.That(emoji.GetLineAndColumn(3)).IsEqualTo((2, 1));

        var location = new SourceSpan(2, 1).ToSourceLocation("doc.md", japanese);
        await Assert.That(location.Line).IsEqualTo(2);
        await Assert.That(location.Column).IsEqualTo(1);
    }

    [Test]
    public async Task Splitters_AgreeOnFrontMatterRules()
    {
        const string basic = "---\ntitle: T\n---\nBody\n";
        const string crlf = "---\r\ntitle: T\r\n---\r\nBody\r\n";
        const string bom = "\uFEFF---\ntitle: T\n---\nBody\n";

        foreach (var text in new[] { basic, crlf, bom })
        {
            var (yaml, body) = MarkdownDocumentParser.SplitFrontMatter(text, "doc.md");
            var parsed = MarkdownContentCollectionLoader<string>.MarkdownSourceDocument.Parse(
                text, "doc.md", CancellationToken.None);

            await Assert.That(parsed.IsSuccess).IsTrue();
            await Assert.That(parsed.Value!.Yaml).IsEqualTo(yaml);
            await Assert.That(parsed.Value.Body).IsEqualTo(body);
            await Assert.That(text.Substring(FrontMatterBodyOffset(text))).IsEqualTo(body);
        }

        // Missing and unterminated inputs fail on both paths with their own contracts.
        await Assert.That(() => MarkdownDocumentParser.SplitFrontMatter("No marker\n", "doc.md"))
            .Throws<InvalidOperationException>();
        await Assert.That(() => MarkdownDocumentParser.SplitFrontMatter("---\ntitle: T\n", "doc.md"))
            .Throws<InvalidOperationException>();
        var missing = MarkdownContentCollectionLoader<string>.MarkdownSourceDocument.Parse(
            "No marker\n", "doc.md", CancellationToken.None);
        await Assert.That(missing.IsSuccess).IsFalse();
        await Assert.That(missing.Diagnostics[0].Id).IsEqualTo(MarkdownContentDiagnosticIds.MissingFrontMatter);
        var unterminated = MarkdownContentCollectionLoader<string>.MarkdownSourceDocument.Parse(
            "---\ntitle: T\n", "doc.md", CancellationToken.None);
        await Assert.That(unterminated.IsSuccess).IsFalse();
        await Assert.That(unterminated.Diagnostics[0].Id).IsEqualTo(MarkdownContentDiagnosticIds.UnterminatedFrontMatter);
        var empty = MarkdownContentCollectionLoader<string>.MarkdownSourceDocument.Parse(
            "---\n---\nBody\n", "doc.md", CancellationToken.None);
        await Assert.That(empty.IsSuccess).IsFalse();
        await Assert.That(empty.Diagnostics[0].Id).IsEqualTo(MarkdownContentDiagnosticIds.EmptyFrontMatter);

        // Empty front matter keeps the body after the closing marker, matching
        // the previous StringReader implementation.
        var (emptyYaml, emptyBody) = MarkdownDocumentParser.SplitFrontMatter("---\n---\nBody\n", "doc.md");
        await Assert.That(emptyYaml).IsEqualTo(string.Empty);
        await Assert.That(emptyBody).IsEqualTo("Body\n");
    }

    [Test]
    public async Task Analyze_ReturnsSameHtmlAsCompile()
    {
        var compiler = new LithoMarkdownCompiler();
        foreach (var body in new[]
        {
            "# Title\n\nBody text.\n",
            "## A\n\n## A\n\n[link](https://example.org/a)\n",
            await ReadBaselineAsync("body-basics.md"),
            await ReadBaselineAsync("gfm-extensions.md"),
            await ReadBaselineAsync("advanced-extensions.md"),
        })
        {
            var compiled = compiler.Compile(body);
            var analyzed = compiler.Analyze(body);

            await Assert.That(analyzed.Html).IsEqualTo(compiled.Html);
            await Assert.That(analyzed.Syntax).IsNotNull();
            await Assert.That(analyzed.Semantics).IsNotNull();
            await Assert.That(compiled.Syntax).IsNull();
        }
    }

    [Test]
    public async Task Analyze_ExtractsHeadingsWithRawAndOutputLevels()
    {
        var compiler = new LithoMarkdownCompiler();
        const string body = "# Title\n\n## Section\n\n## Section\n";
        var analyzed = compiler.Analyze(body, new DocumentSource("doc.md", 0));
        var headings = analyzed.Semantics!.Headings;

        await Assert.That(headings.Count).IsEqualTo(3);
        await Assert.That(headings[0].RawLevel).IsEqualTo(1);
        await Assert.That(headings[0].OutputLevel).IsEqualTo(2);
        await Assert.That(headings[0].Text).IsEqualTo("Title");
        await Assert.That(headings[1].RawLevel).IsEqualTo(2);
        await Assert.That(headings[1].OutputLevel).IsEqualTo(2);
        await Assert.That(headings[2].Id).IsNotEqualTo(headings[1].Id);

        var syntaxHeadings = analyzed.Syntax!.Blocks
            .Where(block => block.Kind == BlockKind.Heading).ToArray();
        await Assert.That(syntaxHeadings.Length).IsEqualTo(3);
        await Assert.That(syntaxHeadings[0].Level).IsEqualTo(1);

        foreach (var heading in headings)
        {
            await Assert.That(heading.Span.End <= body.Length).IsTrue();
            await Assert.That(body.Substring(heading.Span.Start, heading.Span.Length)).Contains("#");
        }
    }

    [Test]
    public async Task Analyze_ExtractsLinksAssetsAndPlainText()
    {
        var compiler = new LithoMarkdownCompiler();
        const string body = "See [External](https://example.org/a) and [named][r] plus ![Img](pic.png).\n\n[r]: https://example.org/r\n";
        var analyzed = compiler.Analyze(body, new DocumentSource("doc.md", 0));
        var semantics = analyzed.Semantics!;

        var external = semantics.Links.Single(link => link.Url == "https://example.org/a");
        await Assert.That(external.RawText).IsEqualTo("[External](https://example.org/a)");
        await Assert.That(external.IsImage).IsFalse();

        // Raw text keeps the reference form while the URL is resolved.
        var reference = semantics.Links.Single(link => link.Url == "https://example.org/r");
        await Assert.That(reference.RawText).Contains("[named][r]");

        var image = semantics.Links.Single(link => link.IsImage);
        await Assert.That(image.Url).IsEqualTo("pic.png");
        await Assert.That(semantics.Assets.Single().Url).IsEqualTo("pic.png");

        foreach (var link in semantics.Links)
        {
            await Assert.That(link.Span.End <= body.Length).IsTrue();
        }

        await Assert.That(semantics.PlainText).Contains("See External and named plus");
        await Assert.That(semantics.Components.Count).IsEqualTo(0);
        await Assert.That(semantics.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Analyze_SpansSurviveFrontMatterCrlfAndJapanese()
    {
        const string file = "---\r\ntitle: \u65E5\u672C\u8A9E\r\n---\r\n# \u898B\u51FA\u3057\r\n\r\n\u672C\u6587 [link](https://example.org/a)\r\n";
        var fileSource = new SourceText(file);
        var (yaml, body) = MarkdownDocumentParser.SplitFrontMatter(file, "doc.md");
        var bodyOffset = file.Length - body.Length;
        await Assert.That(file.Substring(bodyOffset)).IsEqualTo(body);

        var compiler = new LithoMarkdownCompiler();
        var analyzed = compiler.Analyze(body, new DocumentSource("doc.md", bodyOffset));
        var heading = analyzed.Semantics!.Headings.Single();

        await Assert.That(heading.Text).IsEqualTo("\u898B\u51FA\u3057");
        var (line, column) = fileSource.GetLineAndColumn(heading.Span.Start);
        await Assert.That(line).IsEqualTo(4);
        await Assert.That(column).IsEqualTo(1);
        await Assert.That(heading.Span.ToSourceLocation("doc.md", fileSource).Line).IsEqualTo(4);

        foreach (var block in analyzed.Syntax!.Blocks)
        {
            await Assert.That(block.Span.End <= file.Length).IsTrue();
        }

        foreach (var link in analyzed.Semantics.Links)
        {
            await Assert.That(link.Span.End <= file.Length).IsTrue();
        }

        await Assert.That(yaml).Contains("title:");
    }

    [Test]
    public async Task Analyze_ResultsAreIndependentAndCompilerRetainsNoText()
    {
        var compiler = new LithoMarkdownCompiler();
        var first = compiler.Analyze("## One\n");
        var second = compiler.Analyze("## Two\n");

        await Assert.That(ReferenceEquals(first.Syntax, second.Syntax)).IsFalse();
        await Assert.That(ReferenceEquals(first.Semantics, second.Semantics)).IsFalse();
        await Assert.That(first.Semantics!.Headings[0].Text).IsEqualTo("One");
        await Assert.That(second.Semantics!.Headings[0].Text).IsEqualTo("Two");

        // Compile-time version literals are allowed; per-document text must not be retained.
        var staticTextHolders = typeof(LithoMarkdownCompiler)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => !field.IsLiteral && (field.FieldType == typeof(string)
                || field.FieldType == typeof(SourceText)
                || field.FieldType == typeof(string[])))
            .ToArray();
        await Assert.That(staticTextHolders.Length).IsEqualTo(0);
    }

    private static int FrontMatterBodyOffset(string text)
    {
        var split = FrontMatterSplitter.TrySplit(text);
        if (split.Status != FrontMatterSplitStatus.Ok)
        {
            throw new InvalidOperationException("Expected front matter.");
        }

        return split.BodyStartOffset;
    }

    private static async Task<string> ReadBaselineAsync(string fileName) =>
        await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdigBaseline", fileName));
}
