using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class AgentReadyAnalysisTests
{
    [Test]
    [Arguments(MarkdownOptions.LegacyProfile)]
    [Arguments(MarkdownOptions.CommonMarkProfile)]
    [Arguments(MarkdownOptions.DocumentationProfile)]
    public async Task OneBodyRequestRetainsAuthoritativeFactsAndOriginalProfile(string profile)
    {
        const string body = "---\r\n\r\n# Body 😀\r\n\r\n[guide][shared]\r\n\r\n[shared]: old.md \"Title\"\r\n";
        var options = MarkdownCompilerOptions.Default with { Markdown = new() { ProfileId = profile }, RetainAgentReadyFacts = true };
        var compiler = new LithoMarkdownCompiler(options);
        var source = new DocumentSource("docs/body.md", 43) { BodyStartLine = 5 };
        var result = compiler.Analyze(body, source);
        var snapshot = result.BodyAnalysis!;
        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        await Assert.That(snapshot.Analysis.RawText).IsEqualTo(body);
        await Assert.That(snapshot.Analysis.ProfileId).IsEqualTo(profile);
        await Assert.That(snapshot.Analysis.SourceVersion).IsEqualTo(MarkdownAnalysisAdapter.AgentReadySourceVersion(body));
        await Assert.That(snapshot.Analysis.BodySpan!.Value.Start).IsEqualTo(0);
        await Assert.That(snapshot.Analysis.RequestedParts.HasFlag(MdAnalysisParts.Links | MdAnalysisParts.Syntax | MdAnalysisParts.UnsupportedSyntax)).IsTrue();
        await Assert.That(snapshot.Analysis.Links!.Any(link => link.Kind == MdLinkKind.ReferenceDefinition)).IsTrue();
        var repeated = compiler.Analyze(body, source, CancellationToken.None, snapshot);
        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        await Assert.That(ReferenceEquals(repeated.BodyAnalysis!.Analysis, snapshot.Analysis)).IsTrue();
        await Assert.That(result.Html).IsEqualTo(new LithoMarkdownCompiler(options with { RetainAgentReadyFacts = false }).Analyze(body, source).Html);
    }

    [Test]
    public async Task SourceVersionMustMatchTheExactBodyRequest()
    {
        const string body = "[guide](guide.md)";
        var options = new MarkdownOptions();
        var parserOptions = MarkdownAnalysisAdapter.BodyOptionsFor(options);
        var parts = MarkdownAnalysisAdapter.BodyPartsFor(options, true);
        var expectedVersion = MarkdownAnalysisAdapter.AgentReadySourceVersion(body);
        var foreign = new MarkdownBodyAnalysisSnapshot(MarkdownAnalysisAdapter.AnalyzeBody(body, "body.md",
            parserOptions, parts, "another-version", CancellationToken.None));
        await Assert.That(MarkdownAnalysisAdapter.RetainForBody(foreign, body, "body.md", parserOptions, parts, expectedVersion)).IsNull();
        var compiler = new LithoMarkdownCompiler(MarkdownCompilerOptions.Default with { RetainAgentReadyFacts = true });
        var actual = compiler.Analyze(body, new DocumentSource("body.md", 0), CancellationToken.None, foreign).BodyAnalysis!.Analysis;
        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        var source = new MdSourceSnapshot(actual.SourceId, actual.SourceVersion!, actual.RawText);
        await Assert.That(source.Version).IsEqualTo(actual.SourceVersion);
        await Assert.That(source.TextHash).IsEqualTo(actual.TextHash);
        await Assert.That(MarkdownAnalysisAdapter.RetainForBody(new(actual), body, "body.md", parserOptions, parts)).IsNull();
    }

    [Test]
    public async Task DefaultCompilerDoesNotRequestExtraFactsAndMismatchedPartsCannotBeRetained()
    {
        const string body = "# Body\n\n[guide](guide.md)\n";
        var normal = new LithoMarkdownCompiler();
        await Assert.That(normal.Analyze(body).BodyAnalysis).IsNull();
        await Assert.That(MarkdownAnalysisAdapter.BodyPartsFor(new())).IsEqualTo(MdAnalysisParts.UnsupportedSyntax);
        var snapshot = MarkdownAnalysisAdapter.AnalyzeOriginalBody(body, "body.md", CancellationToken.None);
        var markdown = new MarkdownOptions();
        await Assert.That(MarkdownAnalysisAdapter.RetainForBody(snapshot, body, "body.md",
            MarkdownAnalysisAdapter.BodyOptionsFor(markdown), MarkdownAnalysisAdapter.BodyPartsFor(markdown, true))).IsNull();
        var enabled = new LithoMarkdownCompiler(MarkdownCompilerOptions.Default with { RetainAgentReadyFacts = true });
        await Assert.That(enabled.Fingerprint == normal.Fingerprint).IsFalse();
    }
}
