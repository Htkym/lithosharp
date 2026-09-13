namespace LithoSharp.Content.Compilation;

/// <summary>Markdown compiler settings. The Litho frontend is the only compiler.</summary>
/// <param name="Frontend">Compiler implementation name ("lithosharp").</param>
/// <param name="SyntaxProfile">Enabled syntax set (for example, "advanced").</param>
/// <param name="OutputProfile">Output settings (for example, "disable-html").</param>
internal sealed record MarkdownCompilerOptions(
    string Frontend,
    string SyntaxProfile,
    string OutputProfile)
{
    /// <summary>Current behavior: Litho with advanced extensions and disabled HTML.</summary>
    public static MarkdownCompilerOptions Default { get; } =
        new(Frontend: "lithosharp", SyntaxProfile: "advanced", OutputProfile: "disable-html");
}
