using System.Text;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;

namespace LithoSharp.Tests;

/// <summary>C04 robustness: deep nesting, long runs, huge input, cancellation,
/// and malformed documents never crash the Litho frontend.</summary>
public sealed class LithoParserRobustnessTests
{
    [Test]
    public async Task DeepQuoteNesting_Completes()
    {
        var body = string.Concat(Enumerable.Repeat("> ", 500)) + "deep\n";
        var result = new LithoMarkdownCompiler().Analyze(body);

        await Assert.That(result.Syntax!.Blocks.Count).IsEqualTo(1);
        await Assert.That(result.Syntax.Blocks[0].Kind).IsEqualTo(BlockKind.Quote);
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

        var result = new LithoMarkdownCompiler().Analyze(builder.ToString());

        await Assert.That(result.Syntax!.Blocks.Count).IsEqualTo(1);
        await Assert.That(result.Syntax.Blocks[0].Kind).IsEqualTo(BlockKind.List);
    }

    [Test]
    public async Task LongDelimiterRun_Completes()
    {
        var body = new string('*', 20000) + " text\n";
        var result = new LithoMarkdownCompiler().Analyze(body);

        await Assert.That(result.Html).Contains("text");
    }

    [Test]
    public async Task LongBalancedDelimiterRun_RendersAndAnalyzesWithoutRecursion()
    {
        var delimiters = new string('*', 20000);
        var compiler = new LithoMarkdownCompiler();
        var result = compiler.Analyze("## " + delimiters + "[x](/url)" + delimiters);

        await Assert.That(result.Semantics!.PlainText).IsEqualTo("x\n");
        await Assert.That(result.Semantics.Headings.Single().Text).IsEqualTo("x");
        await Assert.That(result.Semantics.Links.Single().Url).IsEqualTo("/url");
        await Assert.That(result.Html).Contains("<a href=\"/url\">x</a>");
        await Assert.That(compiler.Compile("![" + delimiters + "x" + delimiters + "](img.png)").Html)
            .IsEqualTo("<p><img src=\"img.png\" alt=\"x\" /></p>\n");
    }

    [Test]
    public async Task HugeInput_Completes()
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 20000; index++)
        {
            builder.Append("Paragraph ").Append(index).AppendLine(" with stable text.");
            builder.AppendLine();
        }

        var result = new LithoMarkdownCompiler().Analyze(builder.ToString());

        await Assert.That(result.Syntax!.Blocks.Count).IsEqualTo(20000);
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
        var result = new LithoMarkdownCompiler().Analyze(builder.ToString());

        await Assert.That(result.Syntax!.Blocks.Count).IsEqualTo(1);
        await Assert.That(result.Syntax.Blocks[0].Kind).IsEqualTo(BlockKind.Admonition);
    }

    [Test]
    public async Task LoneSurrogateEmphasis_RendersEmphasis()
    {
        // CommonMark flanking classes surrogates (Cs) as neither whitespace nor
        // punctuation, so the run opens and closes. Markdig renders this literal
        // (and throws on surrogate headings); Litho follows the spec here.
        var result = new LithoMarkdownCompiler().Analyze("*\ud800*\n");

        await Assert.That(result.Html).Contains("<em>");
    }

    [Test]
    public async Task LoneSurrogateHeading_Completes()
    {
        // The reference throws during heading ID normalization; Litho degrades
        // to code-unit handling and stays a document.
        var body = "## \ud800 head\n\nBody.\n";
        var result = new LithoMarkdownCompiler().Analyze(body);

        await Assert.That(result.Semantics!.Headings.Count).IsEqualTo(1);
        await Assert.That(result.Semantics.Headings[0].Span.End <= body.Length).IsTrue();
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
        using var source = new CancellationTokenSource();
        source.CancelAfter(TimeSpan.FromMilliseconds(200));
        await Assert.That(() => new LithoMarkdownCompiler().Analyze(body, null, source.Token))
            .Throws<OperationCanceledException>();
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

        var result = new LithoMarkdownCompiler().Analyze(builder.ToString());

        await Assert.That(result.Syntax!.Blocks.Count).IsEqualTo(50000);
    }

    [Test]
    public async Task AllMarkdownCorpora_LithoAloneCompletes()
    {
        // Every markdown target passes through the Litho frontend alone in the
        // normal suite. The comparison project repeats this with the Markdig
        // reference leg. .mdx sources are a different language.
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

        await Assert.That(count).IsEqualTo(35);
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

        await Assert.That(result.Html.Length).IsGreaterThanOrEqualTo(0);
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
