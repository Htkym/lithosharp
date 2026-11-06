using LithoSharp.Content.Compilation;
using LithoSharp.Inspection;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class MarkdownAnalysisInspectionTests
{
    [Test]
    [Arguments("\n", "")]
    [Arguments("\r\n", "\uFEFF")]
    public async Task InspectionUsesOriginalFactsAndRetainsSiteHeadingPolicy(string newline, string bom)
    {
        var body = string.Join(newline, "# A ![hidden](pic.png) $x$", "", "> ## Quoted &amp; 😀", "",
            "See [one](a&amp;b.md) and [two](a&amp;b.md).", "");
        var prefix = bom + string.Join(newline, "---", "title: Test", "---", "");
        var raw = prefix + body;
        var analysis = MarkdownAnalysisAdapter.InspectDocument(raw, "docs/a.md", CancellationToken.None);
        var info = DocumentInspection.Inspect("docs/a.md", raw);
        var legacy = new LithoMarkdownCompiler().Analyze(body).Semantics!;
        await Assert.That(analysis.Root).IsNull();
        await Assert.That(analysis.TextRegions).IsNull();
        await Assert.That(analysis.Blocks).IsNotNull();
        await Assert.That(info.Headings.Select(heading => (heading.Text, heading.Id)))
            .IsEquivalentTo(legacy.Headings.Select(heading => (heading.Text, heading.Id)));
        await Assert.That(info.Headings.All(heading => heading.LocalKey is not null)).IsTrue();
        await Assert.That(info.Sections.Count(section => section.HeadingLocalKey is not null)).IsEqualTo(2);
        var source = new SourceText(raw);
        foreach (var label in new[] { "one", "two" })
        {
            var start = raw.IndexOf("[" + label + "]", StringComparison.Ordinal);
            var link = info.Links.Single(link => link.RawText.StartsWith("[" + label + "]", StringComparison.Ordinal));
            var (line, column) = source.GetLineAndColumn(start);
            await Assert.That((link.Location!.Line, link.Location.Column)).IsEqualTo(((int?)line, (int?)column));
            await Assert.That(link.Url).IsEqualTo("a&b.md");
        }
    }

    [Test]
    public async Task UnterminatedRecoveryKeepsFailureAndBodyInOneSnapshot()
    {
        const string raw = "\uFEFF---\r\ntitle: Test\r\n# Editing\r\n\r\nSee [one](target.md)";
        var analysis = MarkdownAnalysisAdapter.InspectDocument(raw, "a.md", CancellationToken.None);
        var info = DocumentInspection.Inspect("a.md", raw);
        var failure = info.Diagnostics.Single(diagnostic => diagnostic.Id == "LSM002");
        var (line, column) = new SourceText(raw).GetLineAndColumn(raw.Length);
        await Assert.That(analysis.Status).IsEqualTo(MdParseStatus.Partial);
        await Assert.That(analysis.BodySpan!.Value.Start).IsEqualTo(0);
        await Assert.That((failure.Location!.Line, failure.Location.Column)).IsEqualTo(((int?)line, (int?)column));
        await Assert.That(failure.Location.EndLine).IsEqualTo(failure.Location.Line);
        await Assert.That(failure.Location.EndColumn).IsEqualTo(failure.Location.Column);
        await Assert.That(info.Headings.Single().Text).IsEqualTo("Editing");
        await Assert.That(info.Links.Single().Url).IsEqualTo("target.md");
        await Assert.That(info.FrontMatter.Count).IsEqualTo(0);
    }
}
