using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Inspection;

namespace LithoSharp.Tests;

/// <summary>V110-09 advisory: opt-in definition-list, generic-attribute, and grid-table
/// diagnostics share the footnote scan (fence/inline-code/escape exclusion).</summary>
public sealed class LithoCompatibilityAdvisoryTests
{
    private static DocumentInfo Inspect(string body, bool advisory) =>
        DocumentInspection.Inspect("a.md", $"---\ntitle: Test\n---\n{body}", new DocumentInspectionOptions
        {
            EnableCompatibilityAdvisory = advisory,
        });

    [Test]
    [Arguments("Term\n\n: definition body\n", "LIT003")]
    [Arguments("Term\n: definition body\n", "LIT003")]
    [Arguments("## A {#x}\n", "LIT004")]
    [Arguments("See {#id .class} here.\n", "LIT004")]
    [Arguments("+---+---+\n| a | b |\n+---+---+\n", "LIT005")]
    public async Task AdvisoryOn_ReportsEachCategory(string body, string id)
    {
        var info = Inspect(body, advisory: true);

        await Assert.That(info.Diagnostics.Count(item => item.Id == id)).IsEqualTo(1);
    }

    [Test]
    [Arguments("- a\n- b\n", "LIT003")]
    [Arguments("Term\n\nNot a definition\n", "LIT003")]
    [Arguments(":::note\nbody\n:::\n", "LIT003")]
    [Arguments("[link](https://example.org/a)\n", "LIT004")]
    [Arguments("{#}\n", "LIT004")]
    [Arguments("{#unclosed here\n", "LIT004")]
    [Arguments("| A | B |\n| --- | --- |\n| 1 | 2 |\n", "LIT005")]
    [Arguments("a+b+c\n", "LIT005")]
    [Arguments("+---+\n", "LIT005")]
    public async Task AdvisoryOn_NegativeStaysSilent(string body, string id)
    {
        var info = Inspect(body, advisory: true);

        await Assert.That(info.Diagnostics.Any(item => item.Id == id)).IsFalse();
    }

    [Test]
    [Arguments("```text\nTerm\n\n: hidden\n```\n", "LIT003")]
    [Arguments("```text\n{#x}\n```\n", "LIT004")]
    [Arguments("```text\n+---+\n| a |\n+---+\n```\n", "LIT005")]
    [Arguments("`Term : hidden` and `{#x}` and `+---+`\n", "LIT003")]
    [Arguments("``Term : hidden``\n", "LIT003")]
    [Arguments("``{#x}``\n", "LIT004")]
    public async Task AdvisoryOn_CodeStaysSilent(string body, string id)
    {
        var info = Inspect(body, advisory: true);

        await Assert.That(info.Diagnostics.Any(item => item.Id == id)).IsFalse();
    }

    [Test]
    public async Task AdvisoryOn_EscapedMarkersStaySilent()
    {
        var attribute = Inspect("\\{#x}\n", advisory: true);
        await Assert.That(attribute.Diagnostics.Any(item => item.Id == "LIT004")).IsFalse();

        var grid = Inspect("\\+---+\n| a |\n\\+---+\n", advisory: true);
        await Assert.That(grid.Diagnostics.Any(item => item.Id == "LIT005")).IsFalse();
    }

    [Test]
    [Arguments("Term\n\n: \n", "LIT003")]
    [Arguments("Term\n\n:: directive\n", "LIT003")]
    [Arguments("{#}\n", "LIT004")]
    [Arguments("+---+\nplain text\n", "LIT005")]
    public async Task PartialInput_StaysSilent(string body, string id)
    {
        var info = Inspect(body, advisory: true);

        await Assert.That(info.Diagnostics.Any(item => item.Id == id)).IsFalse();
    }

    [Test]
    public async Task AdvisoryOn_NewlinesAndUnicode()
    {
        var crlf = DocumentInspection.Inspect("a.md", "---\r\ntitle: Test\r\n---\r\nTerm\r\n\r\n: def\r\n",
            new DocumentInspectionOptions { EnableCompatibilityAdvisory = true });
        await Assert.That(crlf.Diagnostics.Any(item => item.Id == "LIT003")).IsTrue();

        var japanese = Inspect("用語\n\n: 定義文\n", advisory: true);
        await Assert.That(japanese.Diagnostics.Count(item => item.Id == "LIT003")).IsEqualTo(1);

        var emoji = Inspect("## \U0001F600 smile {#emoji}\n", advisory: true);
        await Assert.That(emoji.Diagnostics.Count(item => item.Id == "LIT004")).IsEqualTo(1);

        var surrogate = Inspect("## \ud800 head {#x}\n", advisory: true);
        await Assert.That(surrogate.Diagnostics.Count(item => item.Id == "LIT004")).IsEqualTo(1);
    }

    [Test]
    public async Task AdvisoryOn_ReportsOriginalFilePosition()
    {
        var info = Inspect("Intro.\n\nTerm\n\n: definition\n", advisory: true);
        var diagnostic = info.Diagnostics.Single(item => item.Id == "LIT003");

        // Front matter occupies lines 1-3; body starts at line 4.
        await Assert.That(diagnostic.Location?.Line).IsEqualTo(8);
        await Assert.That(diagnostic.Location?.Column).IsEqualTo(1);
    }

    [Test]
    public async Task AdvisoryOn_EmitsAtMostOnePerCategory()
    {
        var info = Inspect("Term one\n\n: first\n\nTerm two\n\n: second\n", advisory: true);

        await Assert.That(info.Diagnostics.Count(item => item.Id == "LIT003")).IsEqualTo(1);
    }

    [Test]
    public async Task AdvisoryOff_KeepsDefaultBehaviorAndBytes()
    {
        const string body = "Term\n\n: definition\n\n## A {#x}\n\n+---+---+\n| a | b |\n+---+---+\n";
        var compiler = new LithoMarkdownCompiler();
        var htmlOff = compiler.Compile(body).Html;
        var analyzedOff = compiler.Analyze(body);
        var infoOff = Inspect(body, advisory: false);
        var infoOn = Inspect(body, advisory: true);

        await Assert.That(analyzedOff.Semantics!.Diagnostics.Any(item => item.Id is "LIT003" or "LIT004" or "LIT005")).IsFalse();
        await Assert.That(infoOff.Diagnostics.Any(item => item.Id is "LIT003" or "LIT004" or "LIT005")).IsFalse();
        await Assert.That(infoOn.Diagnostics.Count(item => item.Id == "LIT003")).IsEqualTo(1);
        await Assert.That(infoOn.Diagnostics.Count(item => item.Id == "LIT004")).IsEqualTo(1);
        await Assert.That(infoOn.Diagnostics.Count(item => item.Id == "LIT005")).IsEqualTo(1);
        // Advisory never changes rendering.
        await Assert.That(compiler.Compile(body).Html).IsEqualTo(htmlOff);
        await Assert.That(infoOn.Diagnostics.Any(item => item.Id == "LIT001")).IsFalse();
    }

    [Test]
    public async Task AdvisoryOn_FootnoteKeepsExistingWarning()
    {
        var info = Inspect("See [^a] here.\n\nTerm\n\n: def\n", advisory: true);

        await Assert.That(info.Diagnostics.Count(item => item.Id == "LIT001")).IsEqualTo(1);
        await Assert.That(info.Diagnostics.Count(item => item.Id == "LIT003")).IsEqualTo(1);
    }

    [Test]
    public async Task AdvisoryScan_PathologicalInputCompletes()
    {
        var body = string.Concat(Enumerable.Repeat("Paragraph with stable text.\n\n", 20000));
        var diagnostics = LithoLimits.FindCompatibilityAdvisories(body, "a.md");

        await Assert.That(diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AdvisoryScan_CancelledTokenThrows()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.That(() => LithoLimits.FindCompatibilityAdvisories("Term\n\n: def\n", "a.md", 1, cancelled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(() => LithoLimits.FindDefinitionListAdvisory("Term\n\n: def\n", "a.md", 1, cancelled.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments("on", true, true)]
    [Arguments("off", false, true)]
    [Arguments("ON", false, false)]
    [Arguments("bogus", false, false)]
    [Arguments(null, false, false)]
    public async Task AdvisoryOption_Contract(string? value, bool enabled, bool known)
    {
        var parsed = CompatibilityAdvisoryOption.TryParse(value, out var actual);

        await Assert.That(parsed).IsEqualTo(known);
        await Assert.That(actual).IsEqualTo(enabled);
        await Assert.That(CompatibilityAdvisoryOption.On).IsEqualTo("on");
        await Assert.That(CompatibilityAdvisoryOption.Off).IsEqualTo("off");
    }
}
