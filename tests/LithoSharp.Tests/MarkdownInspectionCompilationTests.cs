using System.Text.Json;
using LithoSharp.Content.Compilation;

namespace LithoSharp.Tests;

public sealed class MarkdownInspectionCompilationTests
{
    [Test]
    [Arguments("# Alpha &amp; **bold**\n\n[link](https://example.test/x \"title\") ![image](pic.png)\n")]
    [Arguments("# Repeated\n\n# Repeated\n\n> ## Quoted\n\n- **list**\n- second\n")]
    [Arguments("| A | B |\n|---|---|\n| x | y |\n\n```csharp\nvar x = 1;\n```\n")]
    [Arguments("# 日本語😀\r\n\r\n~~deleted~~ and `code`\r\n\r\n<div>raw</div>\r\n")]
    [Arguments("# Incomplete\n\n[unfinished](\n\n```\nunfinished\n")]
    public async Task InspectionKeepsOneParseSyntaxSemanticsAndLocations(string markdown)
    {
        var compiler = new LithoMarkdownCompiler();
        var source = new DocumentSource("content/日本語.md", 37) { BodyStartLine = 4 };
        var rendered = compiler.Analyze(markdown, source, CancellationToken.None);
        var inspection = compiler.AnalyzeForInspection(markdown, source);
        await Assert.That(compiler.ParseCount).IsEqualTo(2);
        await Assert.That(inspection.Html).IsEqualTo(string.Empty);
        await Assert.That(rendered.Html.Length).IsGreaterThan(0);
        await Assert.That(JsonSerializer.Serialize(inspection.Syntax)).IsEqualTo(JsonSerializer.Serialize(rendered.Syntax));
        await Assert.That(JsonSerializer.Serialize(inspection.Semantics)).IsEqualTo(JsonSerializer.Serialize(rendered.Semantics));
    }

    [Test]
    public async Task CancelledInspectionDoesNotReturnAResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => new LithoMarkdownCompiler().AnalyzeForInspection("# Heading", null, cancellation.Token))
            .Throws<OperationCanceledException>();
    }
}
