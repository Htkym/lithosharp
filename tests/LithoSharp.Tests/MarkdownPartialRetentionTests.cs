using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class MarkdownPartialRetentionTests
{
    [Test]
    public async Task PostBlockLimitPreservesKnownInspectionHeadingsAndUnknownFailureLocation()
    {
        const string first = "# A ![hidden](pic.png) $x$ &amp; 😀";
        const string second = "# A ![hidden](other.png) $x$ &amp; 😀";
        var raw = "\uFEFF---\r\ntitle: Test\r\n---\r\n" + first + "\r\n\r\n" + second + "\r\n\r\n"
            + string.Concat(Enumerable.Repeat("A paragraph.\r\n\r\n", 64));
        // Heading projection fits this budget. The fixed paragraph tail exhausts
        // syntax output only after blocks and both heading facts are published.
        var analysis = MdParser.AnalyzeWithBlocks(raw, MdAnalysisParts.Headings | MdAnalysisParts.Syntax,
            "lithosharp", "docs/partial.md", null, new MdOptions(maxOutputItems: 64), CancellationToken.None);
        var headings = MarkdownAnalysisAdapter.InspectionHeadings(analysis, CancellationToken.None);

        await Assert.That(analysis.Status).IsEqualTo(MdParseStatus.Partial);
        await Assert.That(analysis.Blocks).IsNotNull();
        await Assert.That(analysis.GetAvailability(MdAnalysisParts.Headings)).IsEqualTo(MdAnalysisAvailability.Partial);
        await Assert.That(analysis.Headings!.Count).IsEqualTo(2);
        await Assert.That(headings.Count).IsEqualTo(2);
        await Assert.That(analysis.Headings[0].Text.Text).IsEqualTo("A  x & 😀");
        await Assert.That(headings[0].Text).IsEqualTo(@"A  \(x\) & 😀");
        await Assert.That(headings[1].Text).IsEqualTo(headings[0].Text);
        await Assert.That(headings[0].Id).IsEqualTo("a-x");
        await Assert.That(headings[1].Id).IsEqualTo("a-x-1");
        for (var index = 0; index < headings.Count; index++)
        {
            var heading = headings[index];
            var fact = analysis.Headings[index];
            var markup = index == 0 ? first : second;
            await Assert.That((heading.RawLevel, heading.OutputLevel)).IsEqualTo((1, 2));
            await Assert.That(heading.LocalKey).IsEqualTo(fact.LocalKey);
            await Assert.That(heading.Span).IsEqualTo(new SourceSpan(raw.IndexOf(markup, StringComparison.Ordinal), markup.Length));
            await Assert.That((heading.Span.Start, heading.Span.Length)).IsEqualTo((fact.RawSpan.Start, fact.RawSpan.Length));
        }

        var failure = analysis.Diagnostics.Single(diagnostic => diagnostic.Origin == "resource");
        await Assert.That(failure.Reason).IsEqualTo("output");
        await Assert.That(failure.RawSpan).IsNull();
        var diagnostic = MarkdownAnalysisAdapter.IncompleteDiagnostic(analysis, "docs/partial.md")!;
        await Assert.That(diagnostic.Id).IsEqualTo("LSM013");
        await Assert.That(diagnostic.Severity).IsEqualTo(SiteDiagnosticSeverity.Error);
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("docs/partial.md");
        await Assert.That(diagnostic.Location.Line).IsNull();
        await Assert.That(diagnostic.Location.Column).IsNull();
        await Assert.That(diagnostic.Location.EndLine).IsNull();
        await Assert.That(diagnostic.Location.EndColumn).IsNull();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => MarkdownAnalysisAdapter.InspectionHeadings(analysis, cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task BlockStageLimitDoesNotInventInspectionHeadings()
    {
        const string raw = "# Guide\n\nA paragraph.\n";
        // Body mode reaches the block parser directly; its initial input scan
        // exceeds this budget before a complete block list can be retained.
        var analysis = MdParser.AnalyzeWithBlocks(raw, MdAnalysisParts.Headings | MdAnalysisParts.Syntax,
            "lithosharp", "body.md", null, new MdOptions(MdInputKind.Body, maxScanUnits: 1), CancellationToken.None);

        await Assert.That(analysis.Status).IsEqualTo(MdParseStatus.Partial);
        await Assert.That(analysis.BodySpan).IsEqualTo((MdRawRange?)new MdRawRange(0, raw.Length));
        await Assert.That(analysis.Diagnostics.Single(diagnostic => diagnostic.Origin == "resource").Reason).IsEqualTo("scan");
        await Assert.That(analysis.Blocks).IsNull();
        await Assert.That(analysis.Headings!).IsEmpty();
        await Assert.That(MarkdownAnalysisAdapter.InspectionHeadings(analysis, CancellationToken.None)).IsEmpty();
    }
}
