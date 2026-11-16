using LithoSharp.Content;
using LithoSharp.Proofreading;
using Syntamark;

namespace LithoSharp.Tests;

public sealed class ProofreadingTests
{
    [Test]
    [Arguments(MarkdownOptions.LegacyProfile)]
    [Arguments(MarkdownOptions.CommonMarkProfile)]
    [Arguments(MarkdownOptions.DocumentationProfile)]
    public async Task SelectionUsesNativeSyntaxAndKeepsMetadataCodeUrlsAndIdentifiersOut(string profile)
    {
        const string raw = "\uFEFF---\r\ntitle: Hidden metadata\r\n---\r\n# 見出し &amp; 😀\r\n\r\n"
            + "前 `SecretCode` 後 ![HiddenAlt](image.png) 続 <https://example.test> 終。\r\n\r\n"
            + "[案内](/docs) と System.String を使います。https://example.test/path\r\n\r\n"
            + "```txt\r\nHiddenFence\r\n```\r\n";
        var selection = MarkdownProofreading.Select(new SourceSnapshot("guide.md", "1", raw), profile, ["System.String"]);
        var prose = string.Join("|", selection.Segments.Select(segment => segment.Text));
        await Assert.That(selection.HasSyntax).IsTrue();
        await Assert.That(prose).Contains("見出し & 😀");
        await Assert.That(prose).Contains("案内");
        foreach (var hidden in new[] { "Hidden", "SecretCode", "https://", "image.png", "System.String" })
            await Assert.That(prose).DoesNotContain(hidden);
        await Assert.That(selection.Exclusions.Select(exclusion => exclusion.Reason)).Contains("frontmatter");
        await Assert.That(selection.Exclusions.Select(exclusion => exclusion.Reason)).Contains("inline-code");
        await Assert.That(selection.Exclusions.Select(exclusion => exclusion.Reason)).Contains("protected-identifier");
    }

    [Test]
    public async Task CodePointCoordinatesPreserveBomCrLfAndSurrogateBoundaries()
    {
        var coordinates = new YomiyasuCoordinates("\uFEFFA😀\r\nB\rC\n");
        await Assert.That(coordinates.Text).IsEqualTo("A😀\nB\nC\n");
        await Assert.That(coordinates.ToRaw(1, 1)).IsEqualTo(new RawSpan(2, 2));
        await Assert.That(coordinates.ToRaw(2, 1)).IsEqualTo(new RawSpan(4, 2));
        await Assert.That(coordinates.ToRawLine(1)).IsEqualTo(new RawSpan(1, 3));
        await Assert.That(coordinates.ToRawLine(2)).IsEqualTo(new RawSpan(6, 1));
        await Assert.That(() => coordinates.ToRaw(99, 1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => coordinates.ToRawLine(99)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ExplicitExactPreviewChecksSourceVersionAndExpectedTextWhileEntitiesRemainUneditable()
    {
        const string raw = "\uFEFF---\r\ntitle: T\r\n---\r\n# A😀 &amp;Z\r\n";
        var source = new SourceSnapshot("guide.md", "v1", raw);
        var selection = MarkdownProofreading.Select(source);
        var emoji = selection.PreviewReplacement(0, 1, 1, "絵");
        await Assert.That(emoji.CanApply).IsTrue();
        await Assert.That(emoji.OriginalText).IsEqualTo("😀");
        await Assert.That(emoji.Apply(source)).IsEqualTo(raw.Replace("😀", "絵", StringComparison.Ordinal));
        await Assert.That(emoji.IsApplicable(new SourceSnapshot("guide.md", "v2", raw))).IsFalse();
        await Assert.That(() => emoji.Apply(new SourceSnapshot("guide.md", "v1", raw + "changed")))
            .Throws<InvalidOperationException>();
        var entity = selection.PreviewReplacement(0, 3, 1, "and");
        await Assert.That(entity.CanApply).IsFalse();
        await Assert.That(entity.MappingPrecision).IsEqualTo("CoveringTokens");
        await Assert.That(entity.OriginalText).IsEqualTo("&amp;");
        await Assert.That(() => entity.Apply(source)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task LineOnlyProtocolFindingsMapToOriginalQuoteAndNeverInventAnEditableToken()
    {
        const string raw = "\uFEFF---\r\ntitle: T\r\n---\r\n# 見出し😀\r\n\r\n> 引用😀\r\n> 次の行。\r\n";
        var selection = MarkdownProofreading.Select(new SourceSnapshot("guide.md", "1", raw));
        const string report = """
            {"format":"yomiyasu-lint","format_version":2,"tool_version":"3.0.0","candidate_count":1,
             "counts":{"info":0,"warn":1,"error":0},"findings":[{"rule":"emoji_prohibited","line":3,
             "severity":"warn","message":"Review the emoji.","column":3,"length":1,"replacement":""}]}
            """;
        var result = YomiyasuProofreader.ReadReport(selection, report);
        var finding = result.Findings.Single();
        await Assert.That(finding.Diagnostic.Location!.Line).IsEqualTo(6);
        await Assert.That(finding.Diagnostic.Location.Column).IsEqualTo(3);
        await Assert.That(finding.Diagnostic.Location.EndColumn).IsEqualTo(7);
        await Assert.That(finding.MappingPrecision).IsEqualTo("CoveringTokens");
        var preview = finding.PreviewReplacement("確認");
        await Assert.That(preview.OriginalText).IsEqualTo("引用😀");
        await Assert.That(preview.CanApply).IsFalse();
        await Assert.That(() => YomiyasuProofreader.ReadReport(selection,
            report.Replace("\"candidate_count\":1", "\"candidate_count\":2", StringComparison.Ordinal)))
            .Throws<InvalidDataException>();
        await Assert.That(() => YomiyasuProofreader.ReadReport(selection,
            report.Replace("\"format_version\":2", "\"format_version\":3", StringComparison.Ordinal)))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task NoEligibleProseDoesNotStartAnExecutableAndCanceledSelectionRemainsCanceled()
    {
        var source = new SourceSnapshot("code.md", "1", "```csharp\nnot prose\n```\n");
        var selection = MarkdownProofreading.Select(source);
        var result = await YomiyasuProofreader.CheckAsync(selection, "missing-python", "missing-yomiyasu.py");
        await Assert.That(result.State).IsEqualTo("NotRun");
        await Assert.That(result.WasRun).IsFalse();
        await Assert.That(() => MarkdownProofreading.Select(source, cancellationToken: new CancellationToken(true)))
            .Throws<OperationCanceledException>();
    }
}
