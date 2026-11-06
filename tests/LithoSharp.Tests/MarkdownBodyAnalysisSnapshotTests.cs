using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class MarkdownBodyAnalysisSnapshotTests
{
    [Test]
    [Arguments("\n", "")]
    [Arguments("\r\n", "\uFEFF")]
    public async Task ExactOriginalBodySharesRenderingSemanticsAndOriginalPositions(string newline, string bom)
    {
        var body = bom + string.Join(newline, "", "# Guide &amp; **text**", "", "See [go](doc.md), [^note] and $x$.");
        var source = new DocumentSource("content/body.md", 47) { BodyStartLine = 6 };
        var snapshot = MarkdownAnalysisAdapter.AnalyzeOriginalBody(body, source.FilePath, CancellationToken.None);
        IMarkdownCompiler compiler = new LithoMarkdownCompiler();
        var compiled = compiler.Analyze(body, source, CancellationToken.None, snapshot);
        var canonical = new LithoMarkdownCompiler().Analyze(body, source);

        await Assert.That(MarkdownAnalysisAdapter.RetainForBody(snapshot, body, source.FilePath)).IsSameReferenceAs(snapshot);
        await Assert.That(compiler.ParseCount).IsEqualTo(0);
        await Assert.That(compiled.Html).IsEqualTo(canonical.Html);
        await Assert.That(compiled.Semantics!.Headings).IsEquivalentTo(canonical.Semantics!.Headings);
        await Assert.That(compiled.Semantics.Links).IsEquivalentTo(canonical.Semantics.Links);
        await Assert.That(compiled.Semantics.Diagnostics).IsEquivalentTo(canonical.Semantics.Diagnostics);
        await Assert.That(compiled.Semantics.Diagnostics).IsEquivalentTo(
            MarkdownAnalysisAdapter.BodyDiagnostics(snapshot, source.FilePath, source.BodyStartLine));
        await Assert.That(compiled.Semantics.Headings.Single().Span.Start)
            .IsEqualTo(source.BodyStartOffset + body.IndexOf("# Guide", StringComparison.Ordinal));
    }

    [Test]
    [Arguments("\n# Guide\n", "# Guide")]
    [Arguments("\uFEFF# Guide", "# Guide")]
    [Arguments("# Guide\r\n", "# Guide\n")]
    [Arguments("```text source=\"./code.txt\"\nplaceholder\n```", "```text\nincluded\n```")]
    public async Task ChangedRawBodyRequiresItsOwnAnalysis(string original, string finalBody)
    {
        var snapshot = MarkdownAnalysisAdapter.AnalyzeOriginalBody(original, "body.md", CancellationToken.None);
        var compiler = new LithoMarkdownCompiler();
        var compiled = compiler.Analyze(finalBody, new DocumentSource("body.md", 0), CancellationToken.None, snapshot);

        await Assert.That(MarkdownAnalysisAdapter.RetainForBody(snapshot, finalBody, "body.md")).IsNull();
        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        await Assert.That(compiled.Html).IsEqualTo(new LithoMarkdownCompiler().Analyze(finalBody).Html);
    }

    [Test]
    [Arguments("document-options")]
    [Arguments("larger-options")]
    [Arguments("profile")]
    [Arguments("parts")]
    [Arguments("superset-parts")]
    [Arguments("scope")]
    [Arguments("source")]
    [Arguments("version")]
    [Arguments("missing-blocks")]
    public async Task DifferentAnalysisRequestCannotSatisfyStrictBodyCompilation(string difference)
    {
        const string body = "# Guide\n\nSee [^note].";
        var options = difference switch
        {
            "document-options" => new MdOptions(),
            "larger-options" => new MdOptions(MdInputKind.Body, maxInputUtf16: 8 * 1024 * 1024),
            "profile" => new MdOptions(MdInputKind.Body, profileId: "commonmark/0.31.2"),
            _ => new MdOptions(MdInputKind.Body),
        };
        var parts = difference switch
        {
            "parts" => MdAnalysisParts.Headings,
            "superset-parts" => MdAnalysisParts.UnsupportedSyntax | MdAnalysisParts.Headings,
            _ => MdAnalysisParts.UnsupportedSyntax,
        };
        var scope = difference == "scope" ? "other" : "lithosharp";
        var path = difference == "source" ? "other.md" : "body.md";
        var version = difference == "version" ? "1" : null;
        var analysis = difference == "missing-blocks"
            ? MdParser.Analyze(body, parts, scope, path, version, options, CancellationToken.None)
            : MdParser.AnalyzeWithBlocks(body, parts, scope, path, version, options, CancellationToken.None);
        var snapshot = new MarkdownBodyAnalysisSnapshot(analysis);
        var compiler = new LithoMarkdownCompiler();
        var compiled = compiler.Analyze(body, new DocumentSource("body.md", 0), CancellationToken.None, snapshot);

        await Assert.That(MarkdownAnalysisAdapter.RetainForBody(snapshot, body, "body.md")).IsNull();
        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        await Assert.That(compiled.Semantics!.Diagnostics.Single().Id).IsEqualTo(LithoLimits.UnsupportedFootnoteDiagnosticId);
    }

    [Test]
    [Arguments("unicode")]
    [Arguments("input")]
    [Arguments("depth")]
    public async Task StrictUnavailableEvidenceIsReusedAsAnErrorWithoutRetry(string failure)
    {
        var body = failure switch
        {
            "unicode" => "# Guide\n\uD800",
            "input" => new string('x', 1024 * 1024 + 1),
            "depth" => string.Concat(Enumerable.Repeat("> ", 500)) + "deep\n",
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var analysis = MarkdownAnalysisAdapter.AnalyzeBody(body, "body.md", CancellationToken.None);
        var snapshot = new MarkdownBodyAnalysisSnapshot(analysis);
        var compiler = new LithoMarkdownCompiler();
        var compiled = compiler.Analyze(body, new DocumentSource("body.md", 0), CancellationToken.None, snapshot);

        await Assert.That(analysis.Status == MdParseStatus.Complete).IsFalse();
        await Assert.That(MarkdownAnalysisAdapter.RetainForBody(snapshot, body, "body.md")).IsSameReferenceAs(snapshot);
        await Assert.That(compiler.ParseCount).IsEqualTo(0);
        await Assert.That(compiled.Semantics!.Diagnostics.Single().Id)
            .IsEqualTo(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId);
        await Assert.That(compiled.Semantics.Diagnostics.Single().Severity).IsEqualTo(SiteDiagnosticSeverity.Error);
        await Assert.That(compiled.Semantics.Headings).IsEmpty();
    }

    [Test]
    public async Task LargerOriginalBudgetDoesNotCertifyStrictFinalBodyCompilation()
    {
        var body = new string('x', 1024 * 1024 + 1);
        var snapshot = MarkdownAnalysisAdapter.AnalyzeOriginalBody(body, "body.md", CancellationToken.None);
        var compiler = new LithoMarkdownCompiler();
        var compiled = compiler.Analyze(body, new DocumentSource("body.md", 0), CancellationToken.None, snapshot);

        await Assert.That(MarkdownAnalysisAdapter.BodyDiagnostics(snapshot, "body.md")).IsEmpty();
        await Assert.That(snapshot.Analysis.OptionsHash == MarkdownAnalysisAdapter.BodyOptionsHash).IsFalse();
        await Assert.That(MarkdownAnalysisAdapter.RetainForBody(snapshot, body, "body.md")).IsNull();
        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        await Assert.That(compiled.Semantics!.Diagnostics.Single().Id)
            .IsEqualTo(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId);
    }

    [Test]
    public async Task CancelledReuseDoesNotReturnACompilation()
    {
        const string body = "# Guide";
        var snapshot = MarkdownAnalysisAdapter.AnalyzeOriginalBody(body, "body.md", CancellationToken.None);
        var compiler = new LithoMarkdownCompiler();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.That(() => compiler.Analyze(body, new DocumentSource("body.md", 0), cancellation.Token, snapshot))
            .Throws<OperationCanceledException>();
        await Assert.That(compiler.ParseCount).IsEqualTo(0);
    }
}
