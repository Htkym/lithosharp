using System.Collections.Immutable;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace LithoSharp.Tests;

public sealed partial class GeneratorTests
{
    private const string InputPolicyModel = """
        using LithoSharp.Content;
        public sealed class Front { public required string Title { get; set; } }
        [StaticContentCollection(typeof(Front), typeof(string), "guides", InputRoot = "content", EmitStaticSiteManifest = true)]
        public static partial class Guides { }
        """;

    [Test]
    public async Task SharedInputPolicyKeepsCompilerAndRuntimeMembershipAndDeclaredIdentitiesTogether()
    {
        using var workspace = new TemporaryWorkspace();
        const string physicalRoot = "cafe\u0301";
        var model = InputPolicyModel.Replace("\"content\"", "\"" + physicalRoot + "\"", StringComparison.Ordinal)
            .Replace("\"guides\"", "\"cafe\u0301-guides\"", StringComparison.Ordinal)
            .Replace("EmitStaticSiteManifest = true", "Site = \"de\u0301mo\", Variant = \"franc\u0327ais\", EmitStaticSiteManifest = true", StringComparison.Ordinal);
        var inputs = new[]
        {
            new Input("C:/site/content/intro.md", "---\ntitle: Intro\n---\nBody", "intro-id", "intro/"),
            new Input("C:/site/content/nested/cafe\u0301.MD", "---\ntitle: Nested\n---\nBody", "nested-id", "nested/"),
            new Input("C:/site/content/_note.md", "No front matter", "", ""),
            new Input("C:/site/content/_parts/fragment.md", "No front matter", "", ""),
            new Input("C:/site/content/nested/ignored.txt", "No front matter", "", ""),
        }.Select((input, index) => input with
        {
            Path = input.Path.Replace("/content/", "/" + physicalRoot + "/", StringComparison.Ordinal),
            Collection = index % 2 == 0 ? "café-guides" : "cafe\u0301-guides",
            Site = index % 2 == 0 ? "démo" : "de\u0301mo",
            Variant = index % 2 == 0 ? "français" : "franc\u0327ais",
        }).ToArray();
        foreach (var input in inputs)
            await WriteInputAsync(workspace.Root, input.Path["C:/site/".Length..], input.Content);

        var (output, diagnostics) = Generate(model + "\nclass Calls { void Check() { Guides.Manifest.GetUrl(\"/excluded/\"); } }", inputs);
        await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
        var analyzerDiagnostics = await output.WithAnalyzers([new LithoSharp.Analyzers.StaticUrlAnalyzer()],
            new AnalyzerOptions(inputs.Select(input => (AdditionalText)new Text(input)).ToImmutableArray(), new OptionsProvider()))
            .GetAnalyzerDiagnosticsAsync();
        await Assert.That(string.Join(",", analyzerDiagnostics.Select(diagnostic => diagnostic.Id))).IsEqualTo("LSA1201");
        var catalog = await ReadInputCatalogAsync(output);
        await Assert.That(catalog.InputRoot).IsEqualTo(physicalRoot);
        await Assert.That(catalog.CollectionId.Value + "/" + catalog.Site + "/" + catalog.Variant).IsEqualTo("café-guides/démo/français");
        await Assert.That(string.Join(",", catalog.Entries.Select(entry => entry.InputPath)))
            .IsEqualTo("intro.md,nested/café.MD");

        var loader = MarkdownContentCollectionLoader<InputPolicyFrontMatter>.FromCatalog(catalog, workspace.Root,
            entry => new PageMetadata(entry.FrontMatter.Title));
        var loaded = await loader.LoadAsync();
        await Assert.That(loaded.IsSuccess).IsTrue();
        await Assert.That(loaded.Collection!.InputRoot).IsEqualTo(Path.Combine(workspace.Root, physicalRoot));
        await Assert.That(string.Join(",", loaded.Collection!.Entries.Select(entry => entry.Id.Value)))
            .IsEqualTo("intro-id,nested-id");
        await Assert.That(ReferenceEquals(loaded.Collection.RouteConvention(loaded.Collection.Entries[0]), catalog.GetRoute(new ContentEntryId("intro-id")))).IsTrue();

        File.Move(Path.Combine(workspace.Root, physicalRoot, "intro.md"), Path.Combine(workspace.Root, physicalRoot, "moved.md"));
        var moved = await loader.LoadAsync();
        await Assert.That(moved.IsSuccess).IsFalse();
        await Assert.That(moved.Diagnostics.Count(diagnostic => diagnostic.Id == "LSC0010")).IsEqualTo(2);
        await Assert.That(string.Join("\n", moved.Diagnostics.Select(diagnostic => diagnostic.Message))).Contains("AdditionalFiles");
    }

    [Test]
    public async Task OkfDiscoveryRetainsReservedDocumentsConceptsAndAuxiliaryFilesWithoutOrdinaryBinding()
    {
        var source = InputPolicyModel.Replace("InputRoot = \"content\"", "InputRoot = \"content\", InputProfile = ContentInputProfile.OpenKnowledgeFormat", StringComparison.Ordinal) + """

            [StaticContentCollection(typeof(Front), typeof(string), "ordinary", InputRoot = "ordinary", EmitStaticSiteManifest = true)]
            public static partial class Ordinary { }
            """;
        var paths = new[] { "index.md", "nested/index.md", "nested/log.md", "README.md", "AGENTS.md", "_notes/_note.md", "query.sql" };
        var inputs = paths.Select((path, index) => new Input("C:/site/content/" + path, "Not ordinary typed front matter", "id-" + index,
            path.EndsWith(".md", StringComparison.Ordinal) ? path[..^3] + ".html" : path))
            .Append(new Input("C:/site/ordinary/intro.md", "---\ntitle: Ordinary\n---\n", "ordinary-id", "ordinary/", "ordinary")).ToArray();
        var (output, diagnostics) = Generate(source, inputs);
        await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
        var catalog = await ReadInputCatalogAsync(output);
        await Assert.That(catalog.InputProfile).IsEqualTo(ContentInputProfile.OpenKnowledgeFormat);
        await Assert.That(catalog.Entries.Count).IsEqualTo(paths.Length);
        foreach (var entry in catalog.Entries)
        {
            var name = entry.InputPath.Split('/')[^1];
            var expected = name == "index.md" ? ContentInputKind.Index : name == "log.md" ? ContentInputKind.Log
                : name.EndsWith(".md", StringComparison.Ordinal) ? ContentInputKind.Concept : ContentInputKind.Auxiliary;
            await Assert.That(entry.Kind).IsEqualTo(expected);
            await Assert.That(entry.ConceptId).IsEqualTo(expected == ContentInputKind.Concept ? entry.InputPath[..^3] : null);
        }
        var generated = output.SyntaxTrees.Where(tree => tree.FilePath.EndsWith(".g.cs", StringComparison.Ordinal)).Select(tree => tree.ToString()).ToArray();
        await Assert.That(generated.Single(text => text.Contains("class Guides", StringComparison.Ordinal))).DoesNotContain("StaticSiteManifest.Create");
        await Assert.That(generated.Single(text => text.Contains("class Ordinary", StringComparison.Ordinal))).Contains("StaticSiteManifest.Create");

        using var workspace = new TemporaryWorkspace();
        foreach (var input in inputs)
            await WriteInputAsync(workspace.Root, input.Path["C:/site/".Length..], input.Content);
        var discovery = ContentPath.Discover(Path.Combine(workspace.Root, "content"), ContentInputProfile.OpenKnowledgeFormat, CancellationToken.None);
        await Assert.That(discovery.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(catalog.ValidateInputs(discovery.Files, CancellationToken.None).Count).IsEqualTo(0);
        await Assert.That(() => MarkdownContentCollectionLoader<InputPolicyFrontMatter>.FromCatalog(catalog, workspace.Root,
            entry => new PageMetadata(entry.FrontMatter.Title))).Throws<ArgumentException>();

        var (_, routeCollision) = Generate(source, inputs[0], inputs[3] with { Route = inputs[0].Route });
        await Assert.That(routeCollision.Single().Id).IsEqualTo("LSG004");
        var reserved = catalog.Entries.Single(entry => entry.InputPath == "index.md");
        await Assert.That(() => new StaticContentCatalog(catalog.CollectionId,
            [reserved, new StaticContentCatalogEntry(new ContentEntryId("conflict"), "content/concept.md", reserved.Route, "concept.md", ContentInputKind.Concept)],
            "content", ContentInputProfile.OpenKnowledgeFormat, site: catalog.Site, variant: catalog.Variant)).Throws<SiteRouteValidationException>();
    }

    [Test]
    public async Task StaticDiscoveryRejectsCaseAndNfcCollisionsAndInputsOutsideItsRoot()
    {
        foreach (var pair in new[] { ("café.md", "cafe\u0301.md"), ("Intro.md", "intro.MD") })
        {
            var (_, diagnostics) = Generate(InputPolicyModel,
                new Input("C:/site/content/" + pair.Item1, "---\ntitle: One\n---\n", "one", "one/"),
                new Input("C:/site/content/" + pair.Item2, "---\ntitle: Two\n---\n", "two", "two/"));
            await Assert.That(diagnostics.Single().Id).IsEqualTo("LSG003");
            await Assert.That(diagnostics.Single().GetMessage()).Contains("normalized relative path");
        }
        foreach (var (model, path) in new[]
        {
            (InputPolicyModel, "C:/site/other/intro.md"),
            (InputPolicyModel.Replace("\"content\"", "\"cafe\u0301\"", StringComparison.Ordinal), "C:/site/café/intro.md"),
        })
        {
            var (_, outside) = Generate(model, new Input(path, "---\ntitle: Intro\n---\n"));
            await Assert.That(outside.Single().Id).IsEqualTo("LSG002");
            await Assert.That(outside.Single().GetMessage()).Contains("outside declared input root");
        }
    }

    private static async Task<StaticContentCatalog> ReadInputCatalogAsync(Microsoft.CodeAnalysis.Compilation compilation)
    {
        using var stream = new MemoryStream();
        await Assert.That(Errors(compilation.Emit(stream).Diagnostics)).IsEqualTo(string.Empty);
        return (StaticContentCatalog)System.Reflection.Assembly.Load(stream.ToArray()).GetType("Guides")!
            .GetProperty("Catalog")!.GetValue(null)!;
    }

    private static async Task WriteInputAsync(string root, string relativePath, string text)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
    }

    private sealed class InputPolicyFrontMatter
    {
        public required string Title { get; set; }
    }
}
