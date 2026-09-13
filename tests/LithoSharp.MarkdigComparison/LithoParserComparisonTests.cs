using LithoSharp.Content.Compilation;

namespace LithoSharp.Tests;

/// <summary>C04 comparison: the Litho frontend must agree with the Markdig reference
/// on headings, links, text, block structure, and normalized DOM for supported input.</summary>
public sealed class LithoParserComparisonTests
{
    private static readonly ReferenceMarkdigCompiler Reference = new();
    private static readonly LithoMarkdownCompiler Subject = new();

    private static string Normalize(string html) =>
        System.Text.RegularExpressions.Regex.Replace(html, @">\s+<", "><").Trim();

    private static async Task AssertAgreementAsync(string body)
    {
        var expected = Reference.Analyze(body);
        var actual = Subject.Analyze(body);

        await Assert.That(Normalize(actual.Html)).IsEqualTo(Normalize(expected.Html));
        await Assert.That(actual.Semantics!.PlainText).IsEqualTo(expected.Semantics!.PlainText);
        await Assert.That(Heads(actual)).IsEqualTo(Heads(expected));
        await Assert.That(Links(actual)).IsEqualTo(Links(expected));
        await Assert.That(Blocks(actual)).IsEqualTo(Blocks(expected));
    }

    private static string Heads(MarkdownCompilationResult result) => string.Join("|",
        result.Semantics!.Headings.Select(h => $"{h.RawLevel}/{h.OutputLevel}/{h.Id}/{h.Text}"));

    private static string Links(MarkdownCompilationResult result) => string.Join("|",
        result.Semantics!.Links.Select(l => $"{l.Url}/{l.Title}/{l.IsImage}"));

    private static string Blocks(MarkdownCompilationResult result) => string.Join(",",
        result.Syntax!.Blocks.Select(b => b.Kind switch
        {
            BlockKind.Heading => $"H{((SyntaxBlock)b).Level}",
            _ => b.Kind.ToString(),
        }));

    [Test]
    public async Task BaselineFixture_Agrees()
    {
        // body-basics.md stays inside the supported subset. The GFM/advanced
        // fixtures exercise deliberately unsupported constructs (U01 footnotes,
        // U02 definition lists, U03 math, U05 alerts, U06 containers) and are
        // covered by MarkdigBaselineTests (reference side) and the robustness
        // test (Litho side never crashes) instead.
        var body = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "tests", "LithoSharp.Tests", "Fixtures", "MarkdigBaseline", "body-basics.md"));
        await AssertAgreementAsync(body);
    }

    [Test]
    public async Task DocusaurusPlainMarkdown_Agrees()
    {
        // Only notes.md is excluded: C07-extension containers and alerts diverge
        // by design (aside vs container rendering) and are pinned in the C11 corpus.
        var excluded = new HashSet<string>(StringComparer.Ordinal) { Path.Combine("admonitions", "source", "docs", "notes.md") };
        var root = Path.Combine(RepoRoot(), "tests", "fixtures", "docusaurus");
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).OrderBy(p => p))
        {
            if (excluded.Contains(Path.GetRelativePath(root, file))) continue;
            var text = await File.ReadAllTextAsync(file);
            var split = FrontMatterSplitter.TrySplit(text);
            await AssertAgreementAsync(split.Status == FrontMatterSplitStatus.Ok ? split.Body : text);
            count++;
        }

        // Exact count forces a conscious scope review when fixtures change.
        await Assert.That(count).IsEqualTo(19);
    }

    private static string RepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
            {
                return path.FullName;
            }
        }

        throw new DirectoryNotFoundException("The comparison corpus requires the repository root.");
    }

    [Test]
    public async Task AllMarkdownCorpora_LithoAloneCompletes()
    {
        // Every markdown target passes through the Litho frontend alone: no
        // reference success can mask a Litho failure here, and the reference
        // only has to complete as well. .mdx sources are a different language.
        var repo = RepoRoot();
        var roots = new[]
        {
            Path.Combine(repo, "tests", "LithoSharp.Tests", "Fixtures", "MarkdigBaseline"),
            Path.Combine(repo, "tests", "LithoSharp.Tests", "Fixtures", "Compatibility", "content"),
            Path.Combine(repo, "tests", "LithoSharp.Tests", "Fixtures", "LithoParser"),
            Path.Combine(repo, "tests", "LithoSharp.Tests", "Fixtures", "LithoExtensions"),
            Path.Combine(repo, "tests", "fixtures", "docusaurus"),
        };
        var count = 0;
        foreach (var root in roots)
            foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).OrderBy(p => p))
            {
                var text = await File.ReadAllTextAsync(file);
                var analyzed = new LithoMarkdownCompiler().Analyze(text);
                await Assert.That(analyzed.Syntax).IsNotNull();
                await Assert.That(analyzed.Syntax!.Blocks.All(block => block.Span.End <= text.Length)).IsTrue();
                _ = new ReferenceMarkdigCompiler().Analyze(text);
                count++;
            }

        // Exact count forces a conscious scope review when corpora change.
        // The corpus README reads as markdown too and stays in scope.
        await Assert.That(count).IsEqualTo(35);
    }

    [Test]
    public async Task CompatibilityContents_Agree()
    {
        var root = Path.Combine(RepoRoot(), "tests", "LithoSharp.Tests", "Fixtures", "Compatibility", "content");
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).OrderBy(p => p))
        {
            await AssertAgreementAsync(await File.ReadAllTextAsync(file));
        }
    }

    [Test]
    public async Task HeavyDocuments_Agree()
    {
        var linkHeavy = new System.Text.StringBuilder("## Links\n\n");
        for (var i = 1; i <= 20; i++)
        {
            linkHeavy.Append($"[external {i}](https://example.org/docs/{i}) and [internal {i}](/posts/{i}.html).\n");
        }

        var nested = new System.Text.StringBuilder("## Nested\n\n");
        for (var i = 1; i <= 10; i++)
        {
            nested.Append(new string(' ', (i - 1) * 2)).Append($"- level {i}\n");
        }

        var codeHeavy = new System.Text.StringBuilder("## Code\n\n");
        for (var i = 1; i <= 10; i++)
        {
            codeHeavy.Append($"```csharp\nvar value{i} = {i};\n```\n\nInline `code-{i}` sample.\n\n");
        }

        const string pathological = "## Pathological\n\n***Bold italic tail***\n\n- nest nest nest\n  - deep deep\n\n[link](https://example.org/a) text with \\*escaped\\* and &amp; entity.\n";

        var hugeLink = new System.Text.StringBuilder("## Huge link\n\n[");
        hugeLink.Append('x', 5000).Append("](https://example.org/").Append('y', 5000).Append(")\n");

        var hugeCode = new System.Text.StringBuilder("## Huge code\n\n`");
        hugeCode.Append('z', 5000).Append("`\n\n```text\n").Append('w', 100000).Append("\n```\n");

        var emphasisRun = new System.Text.StringBuilder();
        for (var i = 0; i < 500; i++) emphasisRun.Append("*a ");
        emphasisRun.Append('\n');

        foreach (var body in new[] { linkHeavy.ToString(), nested.ToString(), codeHeavy.ToString(), pathological,
            hugeLink.ToString(), hugeCode.ToString(), emphasisRun.ToString() })
        {
            await AssertAgreementAsync(body);
        }
    }

    [Test]
    [Arguments("# Title\n\nBody text.\n")]
    [Arguments("# Top\n\n## A & B\n\nBody.\n")]
    [Arguments("## `code` head\n\nBody.\n")]
    [Arguments("## [linked](https://example.org/x) head\n\nBody.\n")]
    [Arguments("## 日本語見出し\n\n本文。\n")]
    [Arguments("## Dup\n\n## Dup\n\nBody.\n")]
    [Arguments("Setext H1\n========\n\nSetext H2\n--------\n")]
    [Arguments("> quote\n>\n> - item\n")]
    [Arguments("1. one\n2. two\n   - nested\n")]
    [Arguments("- tight a\n- tight b\n\n- loose\n")]
    [Arguments("- [x] Done\n- [ ] Todo\n")]
    [Arguments("| A | B |\n| --- | :---: |\n| 1 | 2 |\n")]
    [Arguments("See [r] and [f][R].\n\n[R]: https://example.org/r \"T\"\n")]
    [Arguments("Visit www.example.org/x and <https://x.org/y>.\n")]
    [Arguments("Escaped \\*x\\* and &copy;.\n")]
    [Arguments("A  \nB\n")]
    [Arguments("```csharp\nvar x = 1;\n```\n")]
    [Arguments("    indented\n")]
    [Arguments("Text ***\n\n---\n")]
    [Arguments("#nospace\n\n###closed###\n")]
    [Arguments("*a **b** c* and [a [b](c)](d).\n")]
    [Arguments("*foo**bar**baz*\n")]
    [Arguments("**a *b* c**\n")]
    [Arguments("***a***\n")]
    [Arguments("****a****\n")]
    [Arguments("__a __b__\n")]
    [Arguments("a*b*c\n")]
    [Arguments("_a_b_c_\n")]
    [Arguments("5*6*7\n")]
    [Arguments("___a___\n")]
    [Arguments("**a**b**c**\n")]
    [Arguments(":::NOTE\ntext\n:::\n")]
    [Arguments(":::\ntext\n:::\n")]
    [Arguments("## Caf\u00e9 \u00e9\n\nBody.\n")]
    [Arguments("## \U0001F600 smile\n\nBody.\n")]
    [Arguments("a\ud800b\n")]
    [Arguments("|\ud800|\n|---|\n|1|\n")]
    [Arguments("```\n\ud800\n```\n")]
    [Arguments("- \ud800\n")]
    [Arguments("a\r\nb\n")]
    [Arguments("a  \r\nb\n")]
    [Arguments("Para line\n    indented continuation\n")]
    [Arguments("Para with trailing space \nnext\n")]
    [Arguments("- item\n```\ncode\n```\n")]
    [Arguments("- item\n# Heading\n")]
    [Arguments("- item\n> quote\n")]
    [Arguments("- nest nest\n  - deep deep\n[link](https://example.org/a)\n```\ncode\n```\n")]
    [Arguments(":::custom\ntext\n:::\n")]
    [Arguments("Inline $x^2$ here.\n")]
    [Arguments("$$\ny = mx\n$$\n")]
    [Arguments("## A {#x}\n")]
    [Arguments("$$unclosed stays.\n")]
    [Arguments("$5 and $ 6$ stay.\n")]
    public async Task CuratedDocuments_Agree(string body) => await AssertAgreementAsync(body);
}
