namespace PageSharp;

/// <summary>
/// The result of generating a static site.
/// </summary>
/// <param name="OutputDirectory">The output directory.</param>
/// <param name="PostCount">Number of posts generated.</param>
/// <param name="GeneratedFiles">The list of generated files.</param>
public sealed record SiteGenerationResult(
    string OutputDirectory,
    int PostCount,
    IReadOnlyList<string> GeneratedFiles);
