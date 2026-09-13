namespace LithoSharp.Content.Compilation;

/// <summary>HTML and optional one-parse document information.</summary>
/// <param name="Html">Rendered HTML.</param>
/// <param name="Syntax">Syntax tree, or null when produced by <see cref="IMarkdownCompiler.Compile"/>.</param>
/// <param name="Semantics">Semantic model, or null when produced by <see cref="IMarkdownCompiler.Compile"/>.</param>
/// <remarks>Kept as a struct so the C01 adapter adds no per-render heap allocation.</remarks>
internal readonly record struct MarkdownCompilationResult(
    string Html,
    DocumentSyntax? Syntax = null,
    DocumentSemantics? Semantics = null);
