using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Analyzers.Tests;

public sealed partial class AnalyzerPackageHostTests
{
    [Test]
    public async Task ManifestFreshClosedAndOpenDynamicCompareTheSameOriginalReference()
    {
        var source = ManifestModel + """

            public static class Calls {
                public static StaticSiteManifest Dynamic { get; } = StaticSiteManifest.Open(Guides.Catalog, p => throw new System.Exception("user getter must not execute in analyzer"));
                public static void M(string unknown, bool flag) {
                    Guides.Manifest.GetUrl("/missing/");
                    Guides.Manifest.GetUrl("/intro/");
                    Dynamic.GetUrl("/missing/");
                    Guides.Manifest.GetUrl(unknown);
                    Guides.Manifest.GetUrl(flag ? "/intro/" : "/missing/");
                    Guides.Manifest.GetUrl(flag ? "/missing/" : "/also-missing/");
                    var unproven = Guides.Manifest; unproven.GetUrl("/missing/");
                }
            }
            """;
        var generated = ManifestGenerate(source);
        Check(!generated.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error), "Fresh generator input");
        Check(!generated.Compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Real metadata compilation binds");
        var ds = await ManifestAnalyze(generated.Compilation);
        Check(ds.Count(d => d.Id == "LSA1201" && d.Severity == DiagnosticSeverity.Error) == 2
            && ds.Count(d => d.Id == "LSA1401" && d.Severity == DiagnosticSeverity.Info) == 1 && ds.Length == 3,
            "Missing Error and one equivalent literal recommendation are separate: " + string.Join("\n", ds));
        var first = ds.Single(d => d.Location.SourceSpan.Start == source.IndexOf("\"/missing/\"", StringComparison.Ordinal));
        Check(first.Location.SourceSpan.Length == "\"/missing/\"".Length && first.Properties["coverage"] == "Closed"
            && first.Properties["currentFingerprint"] == first.Properties["expectedFingerprint"] && first.Properties["candidateCount"] == "1", "Original span and freshness provenance");
        ManifestEvidence("manifest-closed-open", ds.Select(ManifestDiagnostic));

        // Reuse the actual packed Generator/Analyzer fixture for the finite typed pair rule.
        var typedSource = ManifestModel + """

            // 😀 original UTF16 positions
            public static class TypedCalls {
                public static StaticContentCatalog UnknownCatalog => throw new System.Exception("getter must not execute");
                public static string UnknownCatalogBase => throw new System.Exception("base getter must not execute");
                public static void M(string unknown) {
                    var good = new ContentRef<string>(Guides.Catalog.CollectionId, new ContentEntryId("intro"),
                        LithoSharp.Routing.SiteRoute.ForDirectoryIndex("intro", baseUrl: null));
                    var bad = new ContentRef<string>(route: LithoSharp.Routing.SiteRoute.ForDirectoryIndex("Intro/", baseUrl: null),
                        collectionId: Guides.Catalog.CollectionId, id: new ContentEntryId("intro"));
                    var missing = new ContentRef<string>(Guides.Catalog.CollectionId, new ContentEntryId("missing"),
                        LithoSharp.Routing.SiteRoute.ForDirectoryIndex("other/"));
                    var unknownId = new ContentRef<string>(Guides.Catalog.CollectionId, new ContentEntryId(unknown),
                        LithoSharp.Routing.SiteRoute.ForDirectoryIndex("other/"));
                    var unknownBase = new ContentRef<string>(Guides.Catalog.CollectionId, new ContentEntryId("intro"),
                        LithoSharp.Routing.SiteRoute.ForDirectoryIndex("other/", baseUrl: unknown));
                    var nonnullBase = new ContentRef<string>(Guides.Catalog.CollectionId, new ContentEntryId("intro"),
                        LithoSharp.Routing.SiteRoute.ForDirectoryIndex("other/", baseUrl: "https://example.test/"));
                    var unselected = new ContentRef<string>(new ContentCollectionId("guides"), new ContentEntryId("intro"),
                        LithoSharp.Routing.SiteRoute.ForDirectoryIndex("other/"));
                    var alias = Guides.Catalog;
                    var aliased = new ContentRef<string>(alias.CollectionId, new ContentEntryId("intro"),
                        LithoSharp.Routing.SiteRoute.ForDirectoryIndex("other/"));
                    var manual = new ContentRef<string>(UnknownCatalog.CollectionId, new ContentEntryId("intro"),
                        LithoSharp.Routing.SiteRoute.ForDirectoryIndex("other/"));
                    var lookalike = new Demo.ContentRef<string>(Guides.Catalog.CollectionId, new ContentEntryId("intro"),
                        LithoSharp.Routing.SiteRoute.ForDirectoryIndex("other/"));
                    var invalid = new ContentRef<string>(Guides.Catalog.CollectionId, new ContentEntryId("intro"),
                        LithoSharp.Routing.SiteRoute.ForDirectoryIndex("../bad/"));
                    Guides.Manifest.GetUrl("/missing/");
                    Guides.Manifest.GetUrl("/intro/");
                }
            }
            namespace Demo {
                public sealed class ContentRef<T> {
                    public ContentRef(ContentCollectionId collectionId, ContentEntryId id, LithoSharp.Routing.SiteRoute route) { }
                }
            }
            """;
        var typed = ManifestGenerate(typedSource);
        Check(!typed.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)
            && !typed.Compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Typed pairs bind real Core constructors");
        var typedDiagnostics = await ManifestAnalyze(typed.Compilation);
        Check(typedDiagnostics.Length == 4 && typedDiagnostics.Count(d => d.Id == "LSA1402") == 1
            && typedDiagnostics.Count(d => d.Id == "LSA1201") == 1 && typedDiagnostics.Count(d => d.Id == "LSA1401") == 1
            && typedDiagnostics.Count(d => d.Id == "LSA1002") == 1, "One proven contradiction; existing owners retain their diagnostics: " + string.Join("\n", typedDiagnostics));
        var mismatch = typedDiagnostics.Single(d => d.Id == "LSA1402");
        const string routeExpression = "LithoSharp.Routing.SiteRoute.ForDirectoryIndex(\"Intro/\", baseUrl: null)";
        var routeStart = typedSource.IndexOf(routeExpression, StringComparison.Ordinal);
        var idStart = typedSource.IndexOf("\"intro\"", routeStart, StringComparison.Ordinal);
        Check(mismatch.Severity == DiagnosticSeverity.Error && mismatch.Location.SourceSpan == new TextSpan(routeStart, routeExpression.Length)
            && mismatch.AdditionalLocations.Count == 1 && mismatch.AdditionalLocations[0].SourceSpan == new TextSpan(idStart, "\"intro\"".Length), "Named ordinals preserve original route/related ID UTF16 locations");
        Check(mismatch.Properties["coverage"] == "Closed" && mismatch.Properties["site"] == "docs" && mismatch.Properties["variant"] == "ja"
            && mismatch.Properties["entryId"] == "intro" && mismatch.Properties["expectedPublicPath"] == "/intro/"
            && mismatch.Properties["actualPublicPath"] == "/Intro/" && mismatch.Properties["currentFingerprint"] == mismatch.Properties["expectedFingerprint"], "Selected catalog Ordinal mismatch and current provenance");
        var typedCases = new List<object> { new { name = "fresh-pairs", diagnostics = typedDiagnostics.Select(ManifestDiagnostic).ToArray(),
            relatedLocations = mismatch.AdditionalLocations.Select(location => new { location.SourceSpan.Start, location.SourceSpan.Length }).ToArray() } };
        async Task NoMismatch(string name, CSharpCompilation compilation, ManifestInput[]? inputs = null)
        {
            var observations = await ManifestAnalyze(compilation, inputs);
            Check(!observations.Any(d => d.Id == "LSA1402"), "No typed contradiction Error without unique fresh Closed proof: " + name);
            Check(observations.Count(d => d.Id == "LSA1002") == 1, "Invalid route remains owned once: " + name);
            typedCases.Add(new { name, diagnostics = observations.Select(ManifestDiagnostic).ToArray() });
        }
        await NoMismatch("stale-input", typed.Compilation, [new(Content: "changed")]);
        var openInputs = new ManifestInput[] { new(Content: "---\ntitle: Hello\ncount: wrong\n---\nBody") };
        var open = ManifestGenerate(typedSource, openInputs);
        Check(open.Diagnostics.Count(d => d.Id == "LSG006") == 1,
            "Generator retains scalar conversion error ownership: " + string.Join("\n", open.Diagnostics));
        await NoMismatch("open-generator-error", open.Compilation, openInputs);
        var unproved = ManifestGenerate(typedSource.Replace(", EmitStaticSiteManifest = true", "", StringComparison.Ordinal)
            .Replace("Guides.Manifest.GetUrl", "StaticSiteManifest.Create(Guides.Catalog, new string('a', 64)).GetUrl", StringComparison.Ordinal));
        await NoMismatch("no-generated-manifest", unproved.Compilation);
        var generatedTree = typed.Compilation.SyntaxTrees.Single(tree => tree.FilePath != "canary.cs");
        var unknownCatalogBase = generatedTree.ToString().Replace("SiteRoute.ForDirectoryIndex(\"intro/\")",
            "SiteRoute.ForDirectoryIndex(\"intro/\", global::TypedCalls.UnknownCatalogBase)", StringComparison.Ordinal);
        Check(unknownCatalogBase != generatedTree.ToString(), "Actual catalog route received an unknown base operand");
        await NoMismatch("unknown-catalog-base", typed.Compilation.ReplaceSyntaxTree(generatedTree,
            CSharpSyntaxTree.ParseText(unknownCatalogBase, (CSharpParseOptions)generatedTree.Options, generatedTree.FilePath)));
        var generatedLines = generatedTree.ToString().Split('\n');
        var manifestLine = Array.FindIndex(generatedLines, line => line.Contains("StaticSiteManifest Manifest {", StringComparison.Ordinal));
        Check(manifestLine > 0 && generatedLines[manifestLine - 1].Contains("StaticSiteManifestSource", StringComparison.Ordinal), "Actual generator manifest declaration and its provenance");
        var aliasDeclaration = generatedLines[manifestLine - 1] + "\n" + generatedLines[manifestLine].Replace("Manifest {", "SecondManifest {", StringComparison.Ordinal);
        var ambiguousText = generatedTree.ToString().Replace(generatedLines[manifestLine], generatedLines[manifestLine] + "\n" + aliasDeclaration, StringComparison.Ordinal);
        await NoMismatch("two-proved-manifests", typed.Compilation.ReplaceSyntaxTree(generatedTree,
            CSharpSyntaxTree.ParseText(ambiguousText, (CSharpParseOptions)generatedTree.Options, generatedTree.FilePath)));
        var scopeSource = typedSource + """

            [StaticContentCollection(typeof(Front), typeof(string), "guides", Site="other", Variant="ja", EmitStaticSiteManifest=true)]
            public static partial class Other { }
            class ScopedCalls { void M() {
                var correct = new ContentRef<string>(Other.Catalog.CollectionId, new ContentEntryId("intro"),
                    LithoSharp.Routing.SiteRoute.ForDirectoryIndex("other/"));
            } }
            """;
        ManifestInput[] scopeInputs = [new(), new(Path: "D:/site/other.md", Route: "other/", Site: "other")];
        var scopedTyped = ManifestGenerate(scopeSource, scopeInputs);
        var scopedDiagnostics = await ManifestAnalyze(scopedTyped.Compilation, scopeInputs);
        Check(scopedTyped.Diagnostics.Length == 0 && scopedDiagnostics.Count(d => d.Id == "LSA1402") == 1
            && scopedDiagnostics.Single(d => d.Id == "LSA1402").Properties["site"] == "docs", "Same collection ID in another site does not change the selected catalog proof");
        typedCases.Add(new { name = "same-id-other-site", diagnostics = scopedDiagnostics.Select(ManifestDiagnostic).ToArray() });
        Check(!AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name!).Any(Forbidden), "Typed pair checks never CLR-load Core or execute user getters");
        ManifestEvidence("typed-contentref-pairs", typedCases);
    }

    [Test]
    public async Task ManifestStaleTracksBuffersMetadataSourceConfigAndDependencyMvid()
    {
        var source = ManifestModel + "\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/missing/\"); } }";
        var input = new ManifestInput();
        var fresh = ManifestGenerate(source);
        var cases = new List<object>();
        async Task StaleCase(string name, CSharpCompilation compilation, ManifestInput[] inputs, ManifestProvider? provider = null)
        {
            var ds = await ManifestAnalyze(compilation, inputs, provider);
            Check(ds.Length == 1 && ds[0].Id == "LSA1205" && ds[0].Severity == DiagnosticSeverity.Info, "Stale must defer missing Error: " + name);
            Check(ds[0].Properties["currentFingerprint"] != ds[0].Properties["expectedFingerprint"], "Changed compiler inputs: " + name);
            cases.Add(new { name, diagnostic = ManifestDiagnostic(ds[0]) });
        }
        foreach (var (name, changed) in new[] {
            ("unsaved-body", input with { Content = input.Content + "\nunsaved" }), ("route", input with { Route = "other/" }),
            ("id", input with { Id = "other" }), ("site", input with { Site = "other" }), ("variant", input with { Variant = "other" }),
            ("collection", input with { Collection = "other" }), ("rename", input with { Path = "D:/site/renamed.md" }) })
            await StaleCase(name, fresh.Compilation, [changed]);
        await StaleCase("delete", fresh.Compilation, []);
        await StaleCase("add", fresh.Compilation, [input, input with { Path = "D:/site/second.md", Id = "second", Route = "second/" }]);
        await StaleCase("profile", fresh.Compilation, [input], new(profile: "custom"));
        await StaleCase("project-root", fresh.Compilation, [input], new(root: "D:/different"));
        await StaleCase("target-framework", fresh.Compilation, [input], new(target: "net9.0"));
        var original = fresh.Compilation.SyntaxTrees.First(t => t.FilePath == "canary.cs");
        await StaleCase("source-edit", fresh.Compilation.ReplaceSyntaxTree(original,
            CSharpSyntaxTree.ParseText(source + "\n// unsaved", (CSharpParseOptions)original.Options, original.FilePath)), [input]);
        await StaleCase("parse-symbol", fresh.Compilation.ReplaceSyntaxTree(original,
            CSharpSyntaxTree.ParseText(source, ((CSharpParseOptions)original.Options).WithPreprocessorSymbols("CHANGED"), original.FilePath)), [input]);
        MetadataReference Dependency(int value)
        {
            using var stream = new MemoryStream();
            var compilation = Compile("public class Dependency { public const int Value = " + value + "; }", false).WithAssemblyName("Dependency");
            Check(compilation.Emit(stream).Success, "Dependency canary emits");
            return MetadataReference.CreateFromImage(stream.ToArray());
        }
        var left = Dependency(1); var right = Dependency(2);
        var dependent = ManifestGenerate(source, dependency: left);
        Check((await ManifestAnalyze(dependent.Compilation)).Single().Id == "LSA1201", "Dependency canary fresh before MVID change");
        await StaleCase("same-identity-different-MVID", dependent.Compilation.ReplaceReference(left, right), [input]);
        ManifestEvidence("manifest-stale-invalidation", cases);
    }

    [Test]
    public async Task ManifestUnprovenTamperedUnreadableAndBudgetInputsAreDeferred()
    {
        var source = ManifestModel + "\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/missing/\"); } }";
        var fresh = ManifestGenerate(source);
        var cases = new List<object>();
        async Task Deferred(string name, CSharpCompilation compilation, ManifestInput[]? inputs = null)
        {
            var ds = await ManifestAnalyze(compilation, inputs);
            Check(!ds.Any(d => d.Id == "LSA1201"), "No absence Error without usable proof: " + name);
            cases.Add(new { name, diagnostics = ds.Select(ManifestDiagnostic).ToArray() });
        }
        var generated = fresh.Compilation.SyntaxTrees.Single(t => t.FilePath != "canary.cs");
        foreach (var (name, code, file) in new[] {
            ("handwritten-origin", generated.ToString(), "manual.cs"),
            ("tampered-marker-and-runtime", generated.ToString().Replace("intro/", "other/"), generated.FilePath),
            ("tampered-runtime", System.Text.RegularExpressions.Regex.Replace(generated.ToString(), "\\.Create\\(Catalog, \\\"[0-9a-f]+\\\"\\)", ".Open(Catalog)"), generated.FilePath),
            ("null-marker-array", generated.ToString().Replace("new string[] {\"/intro/\"}", "null"), generated.FilePath) })
            await Deferred(name, fresh.Compilation.ReplaceSyntaxTree(generated, CSharpSyntaxTree.ParseText(code, (CSharpParseOptions)generated.Options, file)));
        await Deferred("unreadable-buffer", fresh.Compilation, [new(Content: null)]);
        await Deferred("fingerprint-budget", fresh.Compilation, [new(Content: new string('x', 8 * 1024 * 1024 + 1))]);
        var defaultCatalog = ManifestGenerate(source.Replace(", EmitStaticSiteManifest = true", "").Replace("Guides.Manifest", "StaticSiteManifest.Create(Guides.Catalog, new string('a', 64))"));
        await Deferred("existing-catalog-is-not-closed-proof", defaultCatalog.Compilation);
        ManifestEvidence("manifest-deferred", cases);
    }

    [Test]
    public async Task ManifestGeneratorOwnsRouteAndYamlErrorsWithoutAnalyzerDuplicates()
    {
        var source = ManifestModel + "\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/missing/\"); } }";
        var cases = new List<object>();
        foreach (var (name, inputs, expected) in new[] {
            ("route-collision", new ManifestInput[] { new(), new(Path: "D:/site/second.md", Id: "second") }, new[] { "LSG004" }),
            ("yaml-shape-and-unknown", new ManifestInput[] { new(Content: "---\ntitle: Hello\ncount: wrong\nunknown: true\n---\nBody") }, new[] { "LSG005", "LSG006" }) })
        {
            var output = ManifestGenerate(source, inputs);
            var ds = await ManifestAnalyze(output.Compilation, inputs);
            Check(output.Diagnostics.Select(d => d.Id).Order().SequenceEqual(expected.Order()), "Single generator ownership: " + name);
            Check(!ds.Any(d => d.Id is "LSA1201" or "LSA1204" or "LSA1301" or "LSA1302"), "No duplicate or Closed absence after generator errors");
            cases.Add(new { name, generator = output.Diagnostics.Select(ManifestDiagnostic).ToArray(), analyzer = ds.Select(ManifestDiagnostic).ToArray() });
        }
        ManifestEvidence("manifest-diagnostic-ownership", cases);
    }

    [Test]
    public async Task ManifestSeverityOverridesAndOriginalSourcePragmasUseRoslynPolicy()
    {
        var source = ManifestModel + "\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/missing/\"); } }";
        var generated = ManifestGenerate(source);
        var warning = generated.Compilation.WithOptions(generated.Compilation.Options.WithSpecificDiagnosticOptions(
            new Dictionary<string, ReportDiagnostic> { ["LSA1201"] = ReportDiagnostic.Warn }));
        var ds = await ManifestAnalyze(warning);
        Check(ds.Single().Severity == DiagnosticSeverity.Warning, "Standard explicit severity override");
        var suppressed = warning.WithOptions(warning.Options.WithSpecificDiagnosticOptions(
            new Dictionary<string, ReportDiagnostic> { ["LSA1201"] = ReportDiagnostic.Suppress }));
        Check((await ManifestAnalyze(suppressed)).Length == 0, "Standard per-ID suppression");
        var pragma = ManifestGenerate(source.Replace("class Calls", "#pragma warning disable LSA1201\nclass Calls"));
        Check((await ManifestAnalyze(pragma.Compilation)).Length == 0, "Original source pragma");
        var stalePragma = ManifestGenerate(source.Replace("class Calls", "#pragma warning disable LSA1205\nclass Calls"));
        Check((await ManifestAnalyze(stalePragma.Compilation, [new(Content: "changed")])).Length == 0, "Stale Info original source pragma");
        ManifestEvidence("manifest-severity-suppression", new { defaultMissing = "Error", defaultStale = "Info", explicitWarning = ds.Select(ManifestDiagnostic), perIdSuppressed = true, originalPragmasSuppressed = true });
    }

    [Test]
    public async Task ManifestScopeEmptyMembershipAndMetadataOnlyProvenanceStaySeparate()
    {
        var source = ManifestModel + """

            [StaticContentCollection(typeof(Front), typeof(string), "guides", Site="other", Variant="ja", EmitStaticSiteManifest=true)]
            public static partial class Other { }
            class Calls { void M() {
                Guides.Manifest.GetUrl("/other/"); Other.Manifest.GetUrl("/intro/"); Other.Manifest.GetUrl("/other/");
            } }
            """;
        ManifestInput[] inputs = [new(), new(Path: "D:/site/other.md", Id: "other", Route: "other/", Site: "other")];
        var scoped = ManifestGenerate(source, inputs);
        Check(scoped.Diagnostics.Length == 0, "Separate site manifests generate from existing declarations");
        var ds = await ManifestAnalyze(scoped.Compilation, inputs);
        Check(ds.Count(d => d.Id == "LSA1201") == 2 && ds.Count(d => d.Id == "LSA1401") == 1
            && ds.Where(d => d.Id == "LSA1201").Select(d => d.Properties["site"]).Order().SequenceEqual(new[] { "docs", "other" }), "Membership does not leak across site scope");
        var empty = ManifestGenerate(ManifestModel + "\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/missing/\"); } }", []);
        Check((await ManifestAnalyze(empty.Compilation, [])).Single().Id == "LSA1201", "Explicit empty immutable lookup can prove absence");
        var library = ManifestGenerate(ManifestModel);
        using var stream = new MemoryStream();
        Check(library.Compilation.Emit(stream).Success, "Actual generated manifest can be consumed as metadata");
        var external = Compile("class External { void M() { Guides.Manifest.GetUrl(\"/missing/\"); } }", true)
            .WithAssemblyName("ExternalConsumer").AddReferences(MetadataReference.CreateFromImage(stream.ToArray()));
        Check((await ManifestAnalyze(external, [])).Length == 0, "Cross-project metadata lacks current declaring buffer provenance; Deferred");
        ManifestEvidence("manifest-scopes-empty-metadata", new { scoped = ds.Select(ManifestDiagnostic), emptyClosedMissing = true, metadataOnlyDeferred = true });
    }

    [Test]
    public async Task ManifestExplicitStaticConstructorsCannotProveImmutableMembership()
    {
        var expectedRed = Environment.GetEnvironmentVariable("RA05_EXPECT_CTOR_RED") == "true";
        var cases = new List<object>();
        foreach (var (name, body) in new[] {
            ("manifest-open-reassignment", "Manifest = StaticSiteManifest.Open(Catalog, p => LithoSharp.SiteUrl.FromAbsolute(\"https://example.test/\"));"),
            ("catalog-and-manifest-reassignment", "Catalog = new StaticContentCatalog(new ContentCollectionId(\"guides\"), new[] { new StaticContentCatalogEntry(new ContentEntryId(\"runtime\"), \"runtime.md\", LithoSharp.Routing.SiteRoute.ForDirectoryIndex(\"missing/\")) }, \"docs\", \"ja\"); Manifest = StaticSiteManifest.Create(Catalog, new string('a', 64));"),
            ("catalog-only-reassignment", "Catalog = new StaticContentCatalog(new ContentCollectionId(\"guides\"), new[] { new StaticContentCatalogEntry(new ContentEntryId(\"runtime\"), \"runtime.md\", LithoSharp.Routing.SiteRoute.ForDirectoryIndex(\"missing/\")) }, \"docs\", \"ja\");"),
            ("explicit-empty-constructor", "") })
        {
            // A separate original partial declaration tests all declarations of the same symbol.
            var source = ManifestModel + "\npublic static partial class Guides { static Guides() { " + body + " } }\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/missing/\"); } }";
            var generated = ManifestGenerate(source);
            Check(generated.Diagnostics.Length == 0 && !generated.Compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Valid fresh constructor reassignment: " + name);
            var ds = await ManifestAnalyze(generated.Compilation);
            if (expectedRed) Check(ds.Length == 1 && ds[0].Id == "LSA1201" && ds[0].Properties["expectedFingerprint"] == ds[0].Properties["currentFingerprint"], "Old package falsely trusts original initializer: " + name);
            else Check(ds.Length == 0, "Explicit static constructor makes membership Unavailable: " + name);
            cases.Add(new { name, expectedRed, diagnostics = ds.Select(ManifestDiagnostic).ToArray() });
        }
        var ordinary = ManifestGenerate(ManifestModel + "\nclass Calls { void M() { Guides.Manifest.GetUrl(\"/missing/\"); } }");
        Check((await ManifestAnalyze(ordinary.Compilation)).Single().Id == "LSA1201", "Ordinary implicit initialization remains fresh Closed Error");
        ManifestEvidence(expectedRed ? "constructor-old-package-red" : "constructor-fixed-package", cases);
    }
}
