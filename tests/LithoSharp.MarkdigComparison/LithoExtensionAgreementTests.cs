using LithoSharp.Content.Compilation;

namespace LithoSharp.Tests;

/// <summary>
/// C13: the extension fixtures where Litho intentionally matches the Markdig
/// reference. Litho-only contracts stay in the normal suite; intentional
/// divergences stay pinned there instead.
/// </summary>
public sealed class LithoExtensionAgreementTests
{
    private static readonly LithoMarkdownCompiler Litho = new();
    private static readonly ReferenceMarkdigCompiler Reference = new();

    private static async Task<string> FixtureAsync(string fileName) =>
        await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "tests", "LithoSharp.Tests", "Fixtures", "LithoExtensions", fileName));

    private static string RepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
            {
                return path.FullName;
            }
        }

        throw new DirectoryNotFoundException("The extension fixtures require the repository root.");
    }

    private static async Task AssertAgreementAsync(string fileName)
    {
        var body = await FixtureAsync(fileName);
        var expected = Reference.Analyze(body);
        var actual = Litho.Analyze(body);
        var normalized = static (string html) =>
            System.Text.RegularExpressions.Regex.Replace(html, @">\s+<", "><").Trim();
        await Assert.That(normalized(actual.Html)).IsEqualTo(normalized(expected.Html));
        await Assert.That(actual.Semantics!.PlainText).IsEqualTo(expected.Semantics!.PlainText);
    }

    [Test]
    public async Task HeadingAnchor_MatchesReference() =>
        await AssertAgreementAsync("heading-anchor.md");

    [Test]
    public async Task Math_MatchesReference() =>
        await AssertAgreementAsync("math.md");

    [Test]
    public async Task Diagrams_MatchReference() =>
        await AssertAgreementAsync("mermaid.md");
}
