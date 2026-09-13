using System.Text.RegularExpressions;
using LithoSharp.Content.Compilation;

namespace LithoSharp.Tests;

/// <summary>C04 limits: unsupported constructs stay literal and the unsupported
/// list maps mechanically to the documented fixture.</summary>
public sealed class LithoParserLimitsTests
{
    [Test]
    public async Task UnsupportedIds_MatchDocumentedList()
    {
        var text = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "LithoParser", "Unsupported.md"));
        var documented = Regex.Matches(text, @"U\d{2}-[a-z-]+")
            .Select(match => match.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var declared = LithoLimits.UnsupportedIds.OrderBy(id => id).ToArray();

        await Assert.That(string.Join(",", documented)).IsEqualTo(string.Join(",", declared));
    }

    [Test]
    [Arguments("See [^a] here.\n\n[^a]: note\n", "[^a]")]
    [Arguments("Term\n\n: definition\n", ": definition")]
    [Arguments("\"\"cite\"\" here.\n", "cite")]
    public async Task UnsupportedConstructs_StayLiteral(string markdown, string marker)
    {
        var html = new LithoMarkdownCompiler().Compile(markdown).Html;

        await Assert.That(html).Contains(marker);
    }

    [Test]
    public async Task FootnoteReference_StaysLiteral()
    {
        var html = new LithoMarkdownCompiler().Compile("See [^a] here.\n").Html;

        await Assert.That(html).IsEqualTo("<p>See [^a] here.</p>\n");
    }

    [Test]
    public async Task FootnoteSyntax_ReportsWarningDiagnostic()
    {
        var analyzed = new LithoMarkdownCompiler().Analyze("Note[^a]\n\n[^a]: footnote text\n");

        var diagnostic = analyzed.Semantics!.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo(LithoLimits.UnsupportedFootnoteDiagnosticId);
        await Assert.That(diagnostic.Severity).IsEqualTo(LithoSharp.Diagnostics.SiteDiagnosticSeverity.Warning);
    }

    [Test]
    public async Task FootnoteSyntax_InsideFence_ReportsNothing()
    {
        var analyzed = new LithoMarkdownCompiler().Analyze("```text\n[^a]\n```\n");

        await Assert.That(analyzed.Semantics!.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task PlainDocument_ReportsNothing()
    {
        var analyzed = new LithoMarkdownCompiler().Analyze("See [text](https://example.org/a).\n");

        await Assert.That(analyzed.Semantics!.Diagnostics).IsEmpty();
    }

    [Test]
    [Arguments("```mermaid\ngraph TD;\n```\n")]
    [Arguments("```nomnoml\n[A]->[B]\n```\n")]
    [Arguments("$$\ny = mx + b\n$$\n")]
    [Arguments("Inline $x^2 + y$ here.\n")]
    public async Task BrowserAssets_ReportInfoDiagnostic(string markdown)
    {
        var analyzed = new LithoMarkdownCompiler().Analyze(markdown, new DocumentSource("a.md", 0));

        var diagnostic = analyzed.Semantics!.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo(LithoLimits.BrowserAssetDiagnosticId);
        await Assert.That(diagnostic.Severity).IsEqualTo(LithoSharp.Diagnostics.SiteDiagnosticSeverity.Info);
        await Assert.That(diagnostic.Location!.Line).IsEqualTo(1);
    }

    [Test]
    [Arguments("```js\nconst code = 1;\n```\n")]
    [Arguments("$$\nunclosed math\n")]
    [Arguments("It costs $5 and $6.\n")]
    [Arguments("```text\n$x$\n```\n")]
    public async Task BrowserAssets_AbsentOrLiteral_ReportsNothing(string markdown)
    {
        await Assert.That(LithoLimits.FindBrowserAssetWarning(markdown, "a.md")).IsNull();
    }
}
