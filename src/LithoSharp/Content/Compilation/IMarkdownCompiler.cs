namespace LithoSharp.Content.Compilation;

/// <summary>Internal Markdown compiler boundary. All product rendering goes through this.</summary>
internal interface IMarkdownCompiler
{
    /// <summary>Compiler implementation name ("lithosharp").</summary>
    string Frontend { get; }

    /// <summary>Effective compiler settings.</summary>
    MarkdownCompilerOptions Options { get; }

    /// <summary>
    /// Cache fingerprint reflecting compiler kind, implementation version,
    /// syntax settings, and output settings.
    /// </summary>
    string Fingerprint { get; }

    /// <summary>Compiles Markdown to HTML.</summary>
    MarkdownCompilationResult Compile(string markdown);

    /// <summary>
    /// Parses once and returns HTML with the syntax tree and semantic model
    /// from the same parse result.
    /// </summary>
    /// <param name="markdown">Markdown body.</param>
    /// <param name="source">Source identity for position shifting (file path may be null).</param>
    MarkdownCompilationResult Analyze(string markdown, DocumentSource? source = null);

    /// <summary>
    /// Parses once with cancellation and returns HTML with the syntax tree and
    /// semantic model from the same parse result.
    /// </summary>
    /// <param name="markdown">Markdown body.</param>
    /// <param name="source">Source identity for position shifting (file path may be null).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    MarkdownCompilationResult Analyze(string markdown, DocumentSource? source, CancellationToken cancellationToken);

    /// <summary>Renders Markdown to plain text for search indexing.</summary>
    string RenderPlainText(string markdown);

    /// <summary>Number of underlying parses performed (for one-parse verification).</summary>
    int ParseCount { get; }
}
