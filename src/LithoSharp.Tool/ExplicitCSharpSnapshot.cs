using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Tool;

internal sealed record ExplicitCSharpSource(string File, string Hash, SourceText Text, int Offset, SyntaxTree Tree);
internal sealed record ExplicitCSharpReference(string File, string Hash);

// Shared by source-only commands. Metadata is read as images; no user assembly or project is executed.
internal sealed class ExplicitCSharpSnapshot
{
    private ExplicitCSharpSnapshot(IReadOnlyList<ExplicitCSharpSource> sources, IReadOnlyList<MetadataReference> metadata,
        IReadOnlyList<ExplicitCSharpReference> references, IReadOnlyList<string> frameworkFiles, CSharpParseOptions parseOptions)
        => (Sources, Metadata, References, FrameworkFiles, ParseOptions) = (sources, metadata, references, frameworkFiles, parseOptions);

    internal IReadOnlyList<ExplicitCSharpSource> Sources { get; }
    internal IReadOnlyList<MetadataReference> Metadata { get; }
    internal IReadOnlyList<ExplicitCSharpReference> References { get; }
    internal IReadOnlyList<string> FrameworkFiles { get; }
    internal CSharpParseOptions ParseOptions { get; }

    internal static async Task<ExplicitCSharpSnapshot> LoadAsync(IEnumerable<string> files, IEnumerable<string> references,
        CSharpParseOptions? parseOptions, CancellationToken token)
    {
        var inputs = new List<ExplicitCSharpSource>();
        foreach (var file in files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var offset = text.StartsWith('\uFEFF') ? 1 : 0;
            inputs.Add(new(file, Hash(bytes), SourceText.From(text, Encoding.UTF8), offset,
                CSharpSyntaxTree.ParseText(text[offset..], options: parseOptions, path: file, encoding: Encoding.UTF8, cancellationToken: token)));
        }
        var metadata = new List<MetadataReference>();
        var metadataInputs = new List<ExplicitCSharpReference>();
        var platform = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator)
            ?? throw new InvalidOperationException("Runtime framework metadata is unavailable.");
        var framework = platform.Where(file => Path.GetFileName(file).StartsWith("System.", StringComparison.Ordinal)
            || Path.GetFileName(file) is "netstandard.dll" or "Microsoft.CSharp.dll").ToArray();
        metadata.AddRange(framework.Select(file => MetadataReference.CreateFromFile(file)));
        foreach (var file in references.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);
            metadata.Add(MetadataReference.CreateFromImage(bytes, filePath: file));
            metadataInputs.Add(new(file, Hash(bytes)));
        }
        return new(Array.AsReadOnly(inputs.ToArray()), Array.AsReadOnly(metadata.ToArray()),
            Array.AsReadOnly(metadataInputs.ToArray()), Array.AsReadOnly(framework), parseOptions ?? CSharpParseOptions.Default);
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
