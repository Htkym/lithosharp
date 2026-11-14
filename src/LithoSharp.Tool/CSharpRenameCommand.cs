using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;

namespace LithoSharp.Tool;

internal static class CSharpRenameCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        var sources = new List<string>();
        var references = new List<string>();
        var defines = new List<string>();
        string? type = null, member = null, newName = null;
        var format = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 == args.Length)
                throw Usage();
            var option = args[i++];
            var value = args[i];
            switch (option)
            {
                case "--source":
                    sources.Add(Path.GetFullPath(value));
                    break;
                case "--reference":
                    references.Add(Path.GetFullPath(value));
                    break;
                case "--define" when SyntaxFacts.IsValidIdentifier(value):
                    defines.Add(value);
                    break;
                case "--type" when type is null:
                    type = value;
                    break;
                case "--member" when member is null:
                    member = value;
                    break;
                case "--new-name" when newName is null:
                    newName = value;
                    break;
                case "--format" when !format && value == "json":
                    format = true;
                    break;
                default:
                    throw Usage();
            }
        }
        if (sources.Count == 0 || references.Count == 0 || string.IsNullOrWhiteSpace(type)
            || string.IsNullOrWhiteSpace(member) || string.IsNullOrWhiteSpace(newName)
            || sources.Any(file => !Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            || references.Any(file => !Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
            throw Usage();
        var options = new CSharpParseOptions(LanguageVersion.CSharp13, preprocessorSymbols: defines.Distinct(StringComparer.Ordinal));
        var snapshot = await ExplicitCSharpSnapshot.LoadAsync(sources, references, options, token).ConfigureAwait(false);
        CSharpRenamePreview preview;
        try
        {
            preview = await CSharpRenamePreview.CreateAsync(snapshot, type, member, newName, token).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            throw new CliUsageException(exception.Message);
        }
        var inputs = snapshot.Sources.ToDictionary(source => source.File, StringComparer.Ordinal);
        var response = new
        {
            schemaVersion = "1.0",
            success = preview.CanApply,
            previewOnly = true,
            coverage = "Partial",
            scope = "Explicit source files, original metadata references, and the listed parse options only",
            target = preview.Target,
            metadataType = type,
            member,
            newName,
            sources = snapshot.Sources.Select(source => new
            {
                file = source.File,
                sha256 = source.Hash,
                version = "sha256:" + source.Hash,
                generatedExcluded = preview.GeneratedFiles.Contains(source.File, StringComparer.Ordinal)
            }),
            references = snapshot.References.Select(reference => new { file = reference.File, sha256 = reference.Hash }),
            frameworkReferences = snapshot.FrameworkFiles,
            parseOptions = new
            {
                languageVersion = snapshot.ParseOptions.LanguageVersion.ToString(),
                preprocessorSymbols = snapshot.ParseOptions.PreprocessorSymbolNames,
                nullableContext = "Enable",
                outputKind = "DynamicallyLinkedLibrary"
            },
            canApply = preview.CanApply,
            edits = preview.Edits.Edits.Select(edit => new
            {
                file = edit.SourcePath,
                version = "sha256:" + inputs[edit.SourcePath].Hash,
                sha256 = inputs[edit.SourcePath].Hash,
                start = edit.Start,
                length = edit.Length,
                expectedText = edit.ExpectedText,
                replacement = edit.Replacement
            }),
            diagnostics = preview.Diagnostics,
            issues = preview.Issues,
            manualScopes = preview.Edits.ManualScopes,
        };
        token.ThrowIfCancellationRequested();
        Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        return preview.CanApply ? 0 : 1;
    }

    private static CliUsageException Usage() => new("Usage: preview-csharp-rename --source caller.cs [--source other.cs] --reference original.dll [--reference other.dll] --type Exact.Metadata+Type --member OldName --new-name NewName [--define SYMBOL] [--format json]");
}
