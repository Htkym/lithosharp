using System.Text;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

/// <summary>C04 robustness: deep nesting, long runs, huge input, cancellation,
/// and malformed documents never crash the legacy parser. Strict analysis
/// reports input and resource failures without inventing document facts.</summary>
public sealed class LithoParserRobustnessTests
{
    [Test]
    public async Task DeepQuoteNesting_Completes()
    {
        var body = string.Concat(Enumerable.Repeat("> ", 500)) + "deep\n";
        var blocks = new LithoMarkdownCompiler().Parse(body);

        await Assert.That(blocks.Count).IsEqualTo(1);
        await Assert.That(blocks[0] is LithoQuote).IsTrue();
    }

    [Test]
    public async Task DeepListNesting_Completes()
    {
        var builder = new StringBuilder();
        for (var depth = 0; depth < 300; depth++)
        {
            builder.Append(new string(' ', depth * 2));
            builder.AppendLine("- item");
        }

        var blocks = new LithoMarkdownCompiler().Parse(builder.ToString());

        await Assert.That(blocks.Count).IsEqualTo(1);
        await Assert.That(blocks[0] is LithoList).IsTrue();
    }

    [Test]
    public async Task LongDelimiterRun_Completes()
    {
        var body = new string('*', 20000) + " text\n";
        var result = new LithoMarkdownCompiler().Analyze(body);

        await Assert.That(result.Html).Contains("text");
    }

    [Test]
    public async Task LongBalancedDelimiterRun_RendersAndProjectsTextWithoutRecursion()
    {
        var delimiters = new string('*', 20000);
        var compiler = new LithoMarkdownCompiler();
        var body = "## " + delimiters + "[x](/url)" + delimiters;
        var blocks = compiler.Parse(body);
        var heading = blocks.OfType<LithoHeading>().Single();
        var link = LithoInline.Walk(heading.Inlines).Where(item => !item.Exit)
            .Select(item => item.Node).OfType<LithoLink>().Single();

        await Assert.That(LithoLegacyTextPolicy.Plain(blocks)).IsEqualTo("x\n");
        await Assert.That(LithoLegacyTextPolicy.Heading(heading.Inlines, CancellationToken.None)).IsEqualTo("x");
        await Assert.That(link.Url).IsEqualTo("/url");
        await Assert.That(compiler.Compile(body).Html).Contains("<a href=\"/url\">x</a>");
        await Assert.That(compiler.Compile("![" + delimiters + "x" + delimiters + "](img.png)").Html)
            .IsEqualTo("<p><img src=\"img.png\" alt=\"x\" /></p>\n");
    }

    [Test]
    public async Task DeepDirectiveNesting_Completes()
    {
        var builder = new StringBuilder();
        for (var depth = 0; depth < 300; depth++)
        {
            builder.AppendLine(":::note");
        }

        builder.AppendLine("deep");
        var blocks = new LithoMarkdownCompiler().Parse(builder.ToString());

        await Assert.That(blocks.Count).IsEqualTo(1);
        await Assert.That(blocks[0] is LithoAdmonition).IsTrue();
    }

    [Test]
    public async Task LoneSurrogateEmphasis_RendersEmphasis()
    {
        // CommonMark flanking classes surrogates (Cs) as neither whitespace nor
        // punctuation, so the run opens and closes. Markdig renders this literal
        // (and throws on surrogate headings); Litho follows the spec here.
        var result = new LithoMarkdownCompiler().Compile("*\ud800*\n");

        await Assert.That(result.Html).Contains("<em>");
    }

    [Test]
    public async Task LoneSurrogateHeading_Completes()
    {
        // The reference throws during heading ID normalization; Litho degrades
        // to code-unit handling and stays a document.
        var body = "## \ud800 head\n\nBody.\n";
        var compiler = new LithoMarkdownCompiler();
        var heading = compiler.Parse(body).OfType<LithoHeading>().Single();

        await Assert.That(heading.Span.End <= body.Length).IsTrue();
        await Assert.That(compiler.Compile(body).Html).Contains("<h2 id=");
    }

    [Test]
    public async Task CancelledMidParse_Throws()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 300000; index++)
        {
            builder.Append("Paragraph ").Append(index).AppendLine(" with stable cancellable text.");
            builder.AppendLine();
        }

        var body = builder.ToString();
        var compiler = new LithoMarkdownCompiler();
        using var source = new CancellationTokenSource();
        await Assert.That(() =>
            {
                // Arm cancellation at entry to the canonical parser, without the
                // strict analyzer's input limit rejecting this workload first.
                source.CancelAfter(TimeSpan.FromMilliseconds(200));
                return compiler.Parse(body, source.Token);
            })
            .Throws<OperationCanceledException>();
        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        await Assert.That(source.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task LargeDocument_Completes()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 50000; index++)
        {
            builder.Append("Paragraph ").Append(index).AppendLine(" with stable text.");
            builder.AppendLine();
        }

        var blocks = new LithoMarkdownCompiler().Parse(builder.ToString());

        await Assert.That(blocks.Count).IsEqualTo(50000);
    }

    [Test]
    public async Task AnalyzeInputOverOneMiReportsFailureWithoutInventingFacts()
    {
        const string prefix = "# Unavailable\n\n[link](target.md) ![image](image.png)\n\n";
        var body = prefix + new string('x', 1024 * 1024 + 1 - prefix.Length);
        var diagnostic = await AssertIncompleteWithoutFacts(body);

        await Assert.That(diagnostic.Message).Contains("Input exceeds maxInputUtf16.");
        await Assert.That(diagnostic.Location!.Line).IsNull();
        await Assert.That(diagnostic.Location.Column).IsNull();
    }

    [Test]
    [Arguments("quote")]
    [Arguments("list")]
    [Arguments("directive")]
    [Arguments("delimiters")]
    public async Task AnalyzeDepthLimitReportsFailureWithoutInventingFacts(string kind)
    {
        var body = kind switch
        {
            "quote" => string.Concat(Enumerable.Repeat("> ", 500)) + "deep\n",
            "list" => string.Concat(Enumerable.Range(0, 300).Select(depth => new string(' ', depth * 2) + "- item\n")),
            "directive" => string.Concat(Enumerable.Repeat(":::note\n", 300)) + "deep\n",
            "delimiters" => "## " + new string('*', 20000) + "[x](/url)" + new string('*', 20000),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var diagnostic = await AssertIncompleteWithoutFacts(body);

        await Assert.That(diagnostic.Message).Contains("Parsing stopped at a resource limit.");
        await Assert.That(diagnostic.Location!.Line).IsNull();
        await Assert.That(diagnostic.Location.Column).IsNull();
    }

    [Test]
    [Arguments(0xd800)]
    [Arguments(0xdc00)]
    public async Task AnalyzeUnpairedSurrogateReportsFailureWithoutInventingFacts(int codeUnit)
    {
        var body = "# Unavailable\n\n*" + (char)codeUnit + "* [link](target.md)\n";
        var diagnostic = await AssertIncompleteWithoutFacts(body);
        var (line, column) = new SourceText(body).GetLineAndColumn(body.IndexOf((char)codeUnit));

        await Assert.That(diagnostic.Message).Contains("Input contains an unpaired surrogate.");
        await Assert.That((diagnostic.Location!.Line, diagnostic.Location.Column))
            .IsEqualTo(((int?)line, (int?)column));
    }

    private static async Task<SiteDiagnostic> AssertIncompleteWithoutFacts(string body)
    {
        var result = new LithoMarkdownCompiler().Analyze(body, new DocumentSource("robustness.md", 0));
        var semantics = result.Semantics!;
        var diagnostic = semantics.Diagnostics.Single();

        await Assert.That(diagnostic.Id).IsEqualTo("LSM013");
        await Assert.That(diagnostic.Severity).IsEqualTo(SiteDiagnosticSeverity.Error);
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("robustness.md");
        await Assert.That(result.Html).IsEqualTo(string.Empty);
        await Assert.That(result.Syntax!.Blocks).IsEmpty();
        await Assert.That(semantics.PlainText).IsEqualTo(string.Empty);
        await Assert.That(semantics.Title).IsNull();
        await Assert.That(semantics.Headings).IsEmpty();
        await Assert.That(semantics.Links).IsEmpty();
        await Assert.That(semantics.Assets).IsEmpty();
        await Assert.That(semantics.Components).IsEmpty();
        return diagnostic;
    }

    [Test]
    public async Task AllMarkdownCorpora_LithoAloneCompletes()
    {
        // Every markdown target passes through the Litho frontend alone in the
        // normal suite. .mdx sources are a different language.
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "MarkdigBaseline"),
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "content"),
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "LithoParser"),
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "LithoExtensions"),
            Path.Combine(RepoRoot(), "tests", "fixtures", "docusaurus"),
        };
        var count = 0;
        foreach (var root in roots)
            foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).OrderBy(p => p))
            {
                var text = await File.ReadAllTextAsync(file);
                var analyzed = new LithoMarkdownCompiler().Analyze(text);
                await Assert.That(analyzed.Syntax).IsNotNull();
                await Assert.That(analyzed.Syntax!.Blocks.All(block => block.Span.End <= text.Length)).IsTrue();
                count++;
            }

        // The corpus harness README was documentation, not a parser fixture.
        await Assert.That(count).IsEqualTo(34);
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

        throw new DirectoryNotFoundException("The corpus requires the repository root.");
    }

    [Test]
    public async Task CancelledToken_Throws()
    {
        var compiler = new LithoMarkdownCompiler();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.That(() => compiler.Parse("## Hi\n\nBody.\n", cancelled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(() => compiler.Analyze("## Hi\n", null, cancelled.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments("```\nunclosed\n")]
    [Arguments("*unclosed **nesting\n")]
    [Arguments("[unclosed link\n")]
    [Arguments("[a](unclosed\n")]
    [Arguments("| A\n| ---\n| 1\n")]
    [Arguments("[](<>)\n")]
    [Arguments("Text with \0 nul byte.\n")]
    [Arguments("lone\rcarriage\n")]
    [Arguments("[[[[deep brackets\n")]
    [Arguments("![unclosed image\n")]
    [Arguments("## Heading with [link](\n")]
    [Arguments("a | b\nnot a delimiter\n")]
    [Arguments("- \n- \n")]
    [Arguments("~~single tilde\n")]
    [Arguments(":::note\nunclosed\n")]
    [Arguments("::::::deep run\ntext\n")]
    [Arguments("> [!NOTE\nunclosed tag\n")]
    [Arguments("$$\nunclosed math\n")]
    [Arguments("$" + "$")]
    [Arguments("```csharp source=\"./absent.cs\"\n```\n")]
    public async Task MalformedDocuments_StayDocuments(string body)
    {
        var result = new LithoMarkdownCompiler().Analyze(body);

        foreach (var block in result.Syntax!.Blocks)
        {
            await Assert.That(block.Span.End <= body.Replace("\0", "\uFFFD").Length).IsTrue();
        }

        foreach (var heading in result.Semantics!.Headings)
        {
            await Assert.That(heading.Span.End <= body.Replace("\0", "\uFFFD").Length).IsTrue();
        }
    }

    [Test]
    public async Task LithoFrontend_BuildsSite()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        await File.WriteAllTextAsync(Path.Combine(content, "post.md"), """
            ---
            title: "Litho Post"
            date: "2026-01-02T03:04:05Z"
            summary: "litho summary"
            tags:
              - litho
            ---

            # Body Title

            Body text with [link](https://example.org/a).

            ## Section
            """);
        var posts = await new MarkdownPostReader().ReadAllAsync(content);
        var output = Path.Combine(workspace.Root, "output");
        var generator = new SiteGenerator(new LithoMarkdownCompiler());
        await generator.GenerateAsync(
            new SiteSettings
            {
                Title = "Litho Site",
                Description = "Litho frontend.",
                BaseUrl = "https://example.test/",
                Language = "en",
                TimeZone = "UTC",
            },
            posts,
            output,
            clean: true,
            new SiteCustomization { Template = new BlogSiteTemplate() });

        var post = await File.ReadAllTextAsync(Path.Combine(output, "posts", "post.html"));
        await Assert.That(post).Contains("<h2 id=\"body-title\">");
        await Assert.That(post).Contains("class=\"toc-list\"");
    }
}
