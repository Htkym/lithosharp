using LithoSharp.Content.Compilation;
using Markdig;

namespace LithoSharp.Tests;

/// <summary>
/// Reference-only Markdig compiler for shadow comparison, moved out of the
/// product in C13. Implements the same internal boundary with the pinned
/// Markdig 1.3.2 pipeline (advanced extensions, disabled HTML). Never used by
/// product rendering; normal solution restore never sees the Markdig package.
/// </summary>
internal sealed class ReferenceMarkdigCompiler : IMarkdownCompiler
{
    private readonly MarkdownPipeline _pipeline;
    private int _parseCount;

    public ReferenceMarkdigCompiler(MarkdownCompilerOptions? options = null)
    {
        Options = options ?? MarkdownCompilerOptions.Default;
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .UsePreciseSourceLocation()
            .Build();
        Fingerprint = $"markdig/{MarkdigVersion}/syntax={Options.SyntaxProfile}/output={Options.OutputProfile}";
    }

    public string Frontend => "markdig";

    public MarkdownCompilerOptions Options { get; }

    public string Fingerprint { get; }

    public int ParseCount => Volatile.Read(ref _parseCount);

    public MarkdownCompilationResult Compile(string markdown)
    {
        Interlocked.Increment(ref _parseCount);
        return new(Markdown.ToHtml(markdown, _pipeline));
    }

    public MarkdownCompilationResult Analyze(string markdown, DocumentSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        Interlocked.Increment(ref _parseCount);
        var document = Markdown.Parse(markdown, _pipeline);
        var (html, syntax, semantics) = ReferenceDocumentAnalyzer.Analyze(
            document, _pipeline, markdown, source ?? new DocumentSource(null, 0));
        return new MarkdownCompilationResult(html, syntax, semantics);
    }

    public MarkdownCompilationResult Analyze(string markdown, DocumentSource? source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Analyze(markdown, source);
    }

    public string RenderPlainText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        Interlocked.Increment(ref _parseCount);
        return Markdown.ToPlainText(markdown, _pipeline);
    }

    private static string MarkdigVersion =>
        typeof(Markdown).Assembly.GetName().Version?.ToString() ?? "unknown";
}
