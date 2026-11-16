using System.Text;
using LithoSharp.Inspection;
using LithoSharp.Tool;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace LithoSharp.Tests;

public sealed class CSharpRenamePreviewTests
{
    private const string MetadataType = "Site.Guides+Pages";

    [Test]
    public async Task ProvenCallersCompileAgainstRegeneratedMetadataWithoutEditingDeclarationsOrHomonyms()
    {
        using var workspace = new RenameWorkspace();
        var originalMetadata = workspace.EmitCatalog("original.dll", "Old");
        var renamedMetadata = workspace.EmitCatalog("renamed.dll", "New");
        var raw = "\uFEFF" + """
            using P = Site.Guides.Pages;
            using static Site.Guides.Pages;
            // Old and 😀 remain original text.
            public static class Caller
            {
                public static int Read() => P.Old + Site.Guides.Pages.Old + Old;
                public static string Names() => nameof(P.Old) + nameof(Old);
                public static int Untouched() { int Old = 2; return Old + Other.Old; }
                public static string Text => "Old 😀";
            }
            public static class Other { public static int Old => 2; }
            """.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
        var source = workspace.Write("Caller.cs", raw);
        var generatedText = workspace.Catalog("Old");
        var generated = workspace.Write("Catalog.g.cs", generatedText);
        var snapshot = await ExplicitCSharpSnapshot.LoadAsync([source, generated], [originalMetadata], Options(), default);
        var preview = await CSharpRenamePreview.CreateAsync(snapshot, MetadataType, "Old", "New", default);

        await Assert.That(preview.CanApply).IsTrue();
        await Assert.That(preview.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(preview.Edits.Edits.Count).IsEqualTo(5);
        await Assert.That(preview.GeneratedFiles).Contains(generated);
        await Assert.That(preview.Edits.IsComplete).IsFalse();
        foreach (var edit in preview.Edits.Edits)
        {
            await Assert.That(edit.SourcePath).IsEqualTo(source);
            await Assert.That(edit.ExpectedText).IsEqualTo("Old");
            await Assert.That(raw.Substring(edit.Start, edit.Length)).IsEqualTo(edit.ExpectedText);
        }

        var changed = await preview.TryApplyAsync(default);
        await Assert.That(changed is not null).IsTrue();
        await Assert.That(changed!.Count).IsEqualTo(1);
        var expected = raw.Replace("P.Old", "P.New", StringComparison.Ordinal)
            .Replace("Pages.Old", "Pages.New", StringComparison.Ordinal)
            .Replace(" + Old;", " + New;", StringComparison.Ordinal)
            .Replace("nameof(Old)", "nameof(New)", StringComparison.Ordinal);
        await Assert.That(changed[source]).IsEqualTo(expected);
        await Assert.That(await File.ReadAllTextAsync(source, new UTF8Encoding(false, true))).IsEqualTo(raw[1..]);
        await Assert.That(await File.ReadAllTextAsync(generated)).IsEqualTo(generatedText);
        await Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(source))))
            .IsEqualTo(snapshot.Sources.Single(input => input.File == source).Hash);
        await Assert.That(CompilationErrors(raw, source, renamedMetadata)).IsNotEqualTo(string.Empty);
        await Assert.That(CompilationErrors(changed[source], source, renamedMetadata)).IsEqualTo(string.Empty);

        var changedVersion = new DocumentReferenceDocument(source, raw, 1);
        var unchangedGenerated = new DocumentReferenceDocument(generated, generatedText, 0);
        await Assert.That(preview.Edits.TryApply([changedVersion, unchangedGenerated], out _)).IsFalse();
        var changedText = new DocumentReferenceDocument(source, raw + "changed", 0);
        await Assert.That(preview.Edits.TryApply([changedText, unchangedGenerated], out _)).IsFalse();
    }

    [Test]
    [Arguments("source")]
    [Arguments("reference")]
    public async Task SourceAndMetadataHashesRejectStaleApplication(string changedInput)
    {
        using var workspace = new RenameWorkspace();
        var metadata = workspace.EmitCatalog("original.dll", "Old");
        var source = workspace.Write("Caller.cs", "public static class Caller { public static int Read() => Site.Guides.Pages.Old; }");
        var snapshot = await ExplicitCSharpSnapshot.LoadAsync([source], [metadata], Options(), default);
        var preview = await CSharpRenamePreview.CreateAsync(snapshot, MetadataType, "Old", "New", default);
        await Assert.That(preview.CanApply).IsTrue();
        if (changedInput == "source")
            await File.AppendAllTextAsync(source, "\n// changed after preview");
        else
            workspace.EmitCatalog("original.dll", "New");
        await Assert.That(await preview.TryApplyAsync(default) is null).IsTrue();
        var stalePreview = await CSharpRenamePreview.CreateAsync(snapshot, MetadataType, "Old", "New", default);
        await Assert.That(stalePreview.CanApply).IsFalse();
        await Assert.That(stalePreview.Issues.Select(issue => issue.Reason)).Contains("stale_source_or_metadata");
    }

    [Test]
    public async Task MissingOriginalMetadataAndSourceOwnedDeclarationsNeverBecomeGuessedEdits()
    {
        using var workspace = new RenameWorkspace();
        var unrelated = workspace.Emit("unrelated.dll", "public static class Unrelated { public static int Old => 1; }");
        var source = workspace.Write("Caller.cs", "public static class Caller { public static int Read() => Site.Guides.Pages.Old; }");
        var missing = await ExplicitCSharpSnapshot.LoadAsync([source], [unrelated], Options(), default);
        var unavailable = await CSharpRenamePreview.CreateAsync(missing, MetadataType, "Old", "New", default);
        await Assert.That(unavailable.CanApply).IsFalse();
        await Assert.That(unavailable.Edits.Edits.Count).IsEqualTo(0);
        await Assert.That(unavailable.Diagnostics.Count > 0).IsTrue();
        await Assert.That(unavailable.Issues.Select(issue => issue.Reason)).Contains("unique_metadata_member_required");

        var declaration = workspace.Write("Catalog.cs", workspace.Catalog("Old"));
        var sourceOwned = await ExplicitCSharpSnapshot.LoadAsync([source, declaration], [unrelated], Options(), default);
        var declarationPreview = await CSharpRenamePreview.CreateAsync(sourceOwned, MetadataType, "Old", "New", default);
        await Assert.That(declarationPreview.CanApply).IsFalse();
        await Assert.That(declarationPreview.Edits.Edits.Count).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(declaration)).IsEqualTo(workspace.Catalog("Old"));
    }

    [Test]
    public async Task CapturedAndInferredNamesRemainManualWhileIndependentQualifiedUseIsProven()
    {
        using var workspace = new RenameWorkspace();
        var metadata = workspace.EmitCatalog("original.dll", "Old");
        var raw = """
            using P = Site.Guides.Pages;
            using static Site.Guides.Pages;
            public static class Caller
            {
                public static object Read()
                {
                    int New = 0;
                    var anonymous = new { P.Old };
                    var tuple = (P.Old, 1);
                    return P.Old + Old + New;
                }
            }
            """;
        var source = workspace.Write("Caller.cs", raw);
        var snapshot = await ExplicitCSharpSnapshot.LoadAsync([source], [metadata], Options(), default);
        var preview = await CSharpRenamePreview.CreateAsync(snapshot, MetadataType, "Old", "New", default);
        await Assert.That(preview.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(preview.CanApply).IsTrue();
        await Assert.That(preview.Edits.Edits.Count).IsEqualTo(1);
        await Assert.That(preview.Issues.Count(issue => issue.Reason == "inferred_member_name_requires_review")).IsEqualTo(2);
        await Assert.That(preview.Issues.Select(issue => issue.Reason)).Contains("unqualified_new_name_would_capture_symbol");
        var changed = await preview.TryApplyAsync(default);
        await Assert.That(changed![source]).IsEqualTo(raw.Replace("return P.Old", "return P.New", StringComparison.Ordinal));
        await Assert.That(preview.Edits.ManualScopes.Count > 0).IsTrue();
    }

    [Test]
    public async Task ExplicitParseSymbolsBoundTheInspectedConfiguration()
    {
        using var workspace = new RenameWorkspace();
        var metadata = workspace.EmitCatalog("original.dll", "Old");
        var raw = """
            public static class Caller
            {
            #if SELECTED
                public static int Read() => Site.Guides.Pages.Old;
            #else
                public static int Read() => 0;
            #endif
            }
            """;
        var source = workspace.Write("Caller.cs", raw);
        var selected = await ExplicitCSharpSnapshot.LoadAsync([source], [metadata], Options("SELECTED"), default);
        var selectedPreview = await CSharpRenamePreview.CreateAsync(selected, MetadataType, "Old", "New", default);
        await Assert.That(selected.ParseOptions.PreprocessorSymbolNames).Contains("SELECTED");
        await Assert.That(selectedPreview.Edits.Edits.Count).IsEqualTo(1);
        var unselected = await ExplicitCSharpSnapshot.LoadAsync([source], [metadata], Options(), default);
        var unselectedPreview = await CSharpRenamePreview.CreateAsync(unselected, MetadataType, "Old", "New", default);
        await Assert.That(unselectedPreview.CanApply).IsFalse();
        await Assert.That(unselectedPreview.Edits.Edits.Count).IsEqualTo(0);
        await Assert.That(unselectedPreview.Edits.ManualScopes.Any(scope => scope.Contains("configurations", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task EscapedIdentifiersArePreservedAndBareKeywordNamesAreRejected()
    {
        using var workspace = new RenameWorkspace();
        var metadata = workspace.EmitCatalog("original.dll", "Old");
        var renamed = workspace.EmitCatalog("renamed.dll", "@class");
        var source = workspace.Write("Caller.cs", "public static class Caller { public static int Read() => Site.Guides.Pages.@Old; }");
        var snapshot = await ExplicitCSharpSnapshot.LoadAsync([source], [metadata], Options(), default);
        var preview = await CSharpRenamePreview.CreateAsync(snapshot, MetadataType, "Old", "@class", default);
        var changed = await preview.TryApplyAsync(default);
        await Assert.That(changed is not null).IsTrue();
        await Assert.That(changed![source]).Contains("Pages.@class");
        await Assert.That(CompilationErrors(changed[source], source, renamed)).IsEqualTo(string.Empty);
        await Assert.That(async () => await CSharpRenamePreview.CreateAsync(snapshot, MetadataType, "Old", "class", default))
            .Throws<ArgumentException>();
    }

    private static CSharpParseOptions Options(params string[] symbols) => new(LanguageVersion.CSharp13, preprocessorSymbols: symbols);

    private static MetadataReference[] FrameworkReferences() => ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
        ?? throw new InvalidOperationException("Runtime framework metadata is unavailable."))
        .Split(Path.PathSeparator).Where(file => Path.GetFileName(file).StartsWith("System.", StringComparison.Ordinal)
            || Path.GetFileName(file) is "netstandard.dll" or "Microsoft.CSharp.dll")
        .Select(file => MetadataReference.CreateFromFile(file)).ToArray();

    private static string CompilationErrors(string text, string path, string metadata)
    {
        var tree = CSharpSyntaxTree.ParseText(text.StartsWith('\uFEFF') ? text[1..] : text, Options(), path);
        var compilation = CSharpCompilation.Create("RenamedCaller", [tree],
            FrameworkReferences().Append(MetadataReference.CreateFromImage(File.ReadAllBytes(metadata), filePath: metadata)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        return string.Join(Environment.NewLine, compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    private sealed class RenameWorkspace : IDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "lrc-" + Guid.NewGuid().ToString("N")));

        internal RenameWorkspace() => Directory.CreateDirectory(root);

        internal string Catalog(string member) => "namespace Site { public static class Guides { public static class Pages { public static int "
            + member + " => 1; } } }";

        internal string Write(string name, string text)
        {
            var path = Path.Combine(root, name);
            File.WriteAllBytes(path, new UTF8Encoding(false, true).GetBytes(text));
            return path;
        }

        internal string EmitCatalog(string name, string member) => Emit(name, Catalog(member));

        internal string Emit(string name, string text)
        {
            var compilation = CSharpCompilation.Create("FixtureCatalog", [CSharpSyntaxTree.ParseText(text, Options(), "Catalog.g.cs")],
                FrameworkReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            if (!result.Success)
                throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
            var path = Path.Combine(root, name);
            File.WriteAllBytes(path, stream.ToArray());
            return path;
        }

        public void Dispose()
        {
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(root).StartsWith("lrc-", StringComparison.Ordinal) is false)
                throw new InvalidOperationException("Unexpected rename fixture directory.");
            Directory.Delete(root, recursive: true);
        }
    }
}
