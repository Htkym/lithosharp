using System.Collections.Immutable;
using LithoSharp.Content;
using LithoSharp.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Tests;

public sealed class GeneratorTests
{
    private const string Model = """
        #nullable enable
        using System.Collections.Generic;
        using LithoSharp.Content;
        public sealed class Front {
            public required string Title { get; init; }
            public int Count { get; set; } = 7;
            public string[] Tags { get; set; } = [];
            public IReadOnlyList<int> Numbers { get; set; } = [];
            public IReadOnlyDictionary<string, int> Lookup { get; set; } = new Dictionary<string, int>();
            public System.Uri? Link { get; set; }
            public Child Nested { get; set; } = new();
        }
        public sealed class Child { public string Name { get; set; } = "default"; }
        [StaticContentCollection(typeof(Front), typeof(string), "guides", EmitJsonSchema = true)]
        public static partial class Guides { }
        public static class Probe {
            public static string Check() {
                IReadOnlyDictionary<string, object?>[] cases = [
                    new Dictionary<string, object?> { ["title"] = "hello", ["count"] = "42", ["nested"] = new Dictionary<string, object?> { ["name"] = "child" } },
                    new Dictionary<string, object?> { ["title"] = "hello", ["tags"] = new List<string> { null! } },
                    new Dictionary<string, object?> { ["title"] = "hello", ["numbers"] = "wrong" },
                    new Dictionary<string, object?> { ["title"] = "hello", ["lookup"] = "wrong" },
                    new Dictionary<string, object?> { ["title"] = "hello", ["lookup"] = new Dictionary<string, object?> { ["item"] = null } },
                    new Dictionary<string, object?> { ["title"] = null, ["count"] = "wrong", ["unknown"] = true },
                    new Dictionary<string, object?>()
                ];
                foreach (var values in cases) {
                    var expected = new ReflectionContentFrontMatterBinder<Front>().Bind(values);
                    var actual = Guides.Binder.Bind(values);
                    if (expected.IsSuccess != actual.IsSuccess) return "success differs";
                    var left = string.Join("|", System.Linq.Enumerable.Select(expected.Diagnostics, d => d.Id + d.Message));
                    var right = string.Join("|", System.Linq.Enumerable.Select(actual.Diagnostics, d => d.Id + d.Message));
                    if (left != right) return left + " != " + right;
                    if (actual.IsSuccess && (actual.Value!.Title != expected.Value!.Title || actual.Value.Count != expected.Value.Count || actual.Value.Nested.Name != expected.Value.Nested.Name)) return "values differ";
                }
                return Guides.Pages.Content_Intro.Route.PublicPath;
            }
        }
        """;

    [Test]
    public async Task ExplicitBodyTypeGeneratesTypedMdxCompatibleReferences()
    {
        var source = Model.Replace("EmitJsonSchema = true", "EmitJsonSchema = true, BodyType = typeof(Child)");
        var (compilation, diagnostics) = Generate(source, new Input("C:/site/content/intro.mdx", "---\ntitle: hello\n---\n<Counter />"));
        await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
        await Assert.That(string.Join("\n", compilation.SyntaxTrees.Select(tree => tree.ToString()))).Contains("ContentEntry<global::Front, global::Child>");
        using var stream = new MemoryStream();
        await Assert.That(Errors(compilation.Emit(stream).Diagnostics)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task GeneratedBinderCompilesAndMatchesReflection()
    {
        var source = Model + """

            public sealed class ContextFrontMatter {
                public string Fallback { get; set; } = null!;
                public string SetFallback { get => Fallback ?? string.Empty; set => Fallback = value; }
                public int Count { get; set; } = 7;
            }
            [StaticContentCollection(typeof(ContextFrontMatter), typeof(string), "context-binding")]
            public static partial class ContextBindings { }
            public static class ContextProbe {
                public static string Check() {
                    IReadOnlyDictionary<string, object?>[] cases = [
                        new Dictionary<string, object?> { ["set_fallback"] = "ready", ["count"] = "42" },
                        new Dictionary<string, object?> { ["count"] = "invalid", ["fallback"] = null, ["unknown"] = true },
                        new Dictionary<string, object?>()
                    ];
                    var location = new LithoSharp.Diagnostics.SiteSourceLocation("test.yaml", 2, 1);
                    foreach (var values in cases) {
                        var expected = new ReflectionContentFrontMatterBinder<ContextFrontMatter>().Bind(values, location);
                        var actual = ContextBindings.Binder.Bind(values, location);
                        if (expected.IsSuccess != actual.IsSuccess) return "success differs";
                        var left = string.Join("|", System.Linq.Enumerable.Select(expected.Diagnostics, Describe));
                        var right = string.Join("|", System.Linq.Enumerable.Select(actual.Diagnostics, Describe));
                        if (left != right) return left + " != " + right;
                        if (actual.IsSuccess && (actual.Value!.Fallback != expected.Value!.Fallback || actual.Value.Count != expected.Value.Count)) return "values differ";
                    }
                    return "matched";
                }
                private static string Describe(LithoSharp.Diagnostics.SiteDiagnostic d) => d.Id + "|" + d.Message + "|" + d.Location?.FilePath + ":" + d.Location?.Line + ":" + d.Location?.Column;
            }
            """;
        var (compilation, diagnostics) = Generate(source, new Input("C:/site/content/intro.md", "---\ntitle: hello\n---\nBody"));
        await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        await Assert.That(Errors(emitted.Diagnostics)).IsEqualTo(string.Empty);
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var result = (string)assembly.GetType("Probe")!.GetMethod("Check")!.Invoke(null, null)!;
        await Assert.That(result).IsEqualTo("/intro/");
        var contextResult = (string)assembly.GetType("ContextProbe")!.GetMethod("Check")!.Invoke(null, null)!;
        await Assert.That(contextResult).IsEqualTo("matched");
        var schema = (string)assembly.GetType("Guides")!.GetField("SchemaJson")!.GetRawConstantValue()!;
        using var document = System.Text.Json.JsonDocument.Parse(schema);
        await Assert.That(document.RootElement.GetProperty("$schema").GetString()).IsEqualTo("https://json-schema.org/draft/2020-12/schema");
    }

    [Test]
    public async Task StaticInputsReportInvalidYamlRoutesAndDuplicates()
    {
        foreach (var (yaml, route, expected) in new[] {
            ("---\ntitle: null\n---\nBody", "intro/", "LSG006"),
            ("---\ntitle: hello\nunknown: true\n---\nBody", "intro/", "LSG005"),
            ("---\ntitle: hello", "intro/", "LSG005"),
            ("---\ntitle: hello\n---\nBody", "../escape/", "LSG004") })
        {
            var (_, diagnostics) = Generate(Model, new Input("C:/site/content/intro.md", yaml, Route: route));
            await Assert.That(Errors(diagnostics)).Contains(expected);
        }
        var (_, duplicate) = Generate(Model,
            new Input("C:/site/content/intro.md", "---\ntitle: hello\n---\n"),
            new Input("C:/site/content/other.md", "---\ntitle: hello\n---\n"));
        await Assert.That(duplicate.Any(d => d.Id == "LSG003")).IsTrue();
    }

    [Test]
    public async Task StaticYamlDistinguishesQuotedNullAndReportsValueLocation()
    {
        var (_, quoted) = Generate(Model, new Input("C:/site/content/intro.md",
            "---\ntitle: hello\ntags: [\"null\"]\nlink: https://example.test/docs/\n---\nBody"));
        await Assert.That(Errors(quoted)).IsEqualTo(string.Empty);
        var (_, nullElement) = Generate(Model, new Input("C:/site/content/intro.md",
            "---\ntitle: hello\ntags: [null]\n---\nBody"));
        await Assert.That(Errors(nullElement)).Contains("LSG006");
        var (_, nullTitle) = Generate(Model, new Input("C:/site/content/intro.md", "---\ntitle: null\n---\nBody"));
        var diagnostic = nullTitle.Single(d => d.Id == "LSG006");
        var span = diagnostic.Location.GetLineSpan();
        await Assert.That(span.Path).IsEqualTo("C:/site/content/intro.md");
        await Assert.That(span.StartLinePosition.Line).IsEqualTo(1);
        await Assert.That(span.StartLinePosition.Character).IsEqualTo(7);
    }

    [Test]
    public async Task InvalidMetadataAndCollidingOutputRoutesAreDiagnosed()
    {
        const string yaml = "---\ntitle: hello\n---\nBody";
        foreach (var input in new[] {
            new Input("C:/site/content/intro.md", yaml, Id: ""),
            new Input("C:/site/content/intro.md", yaml, Route: ""),
            new Input("C:/site/content/intro.md", yaml, Collection: "missing") })
        {
            var (_, diagnostics) = Generate(Model, input);
            await Assert.That(Errors(diagnostics)).Contains("LSG002");
        }
        var (_, collision) = Generate(Model,
            new Input("C:/site/content/intro.md", yaml),
            new Input("C:/site/content/other.md", yaml, Id: "other", Route: "intro/index.html"));
        await Assert.That(Errors(collision)).Contains("LSG004");
    }

    [Test]
    public async Task DeletedMovedAndWronglyTypedReferencesFailCompilation()
    {
        var (deleted, _) = Generate(Model);
        await Assert.That(deleted.GetDiagnostics().Any(d => d.Id == "CS0117")).IsTrue();
        var (moved, _) = Generate(Model, new Input("C:/site/content/moved.md", "---\ntitle: hello\n---\n"));
        await Assert.That(moved.GetDiagnostics().Any(d => d.Id == "CS0117")).IsTrue();
        var (wrong, diagnostics) = Generate(Model + "\n public static class Wrong { public static LithoSharp.Pages.PageRef<int> Page => Guides.Pages.Content_Intro; }",
            new Input("C:/site/content/intro.md", "---\ntitle: hello\n---\n"));
        await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
        await Assert.That(wrong.GetDiagnostics().Any(d => d.Id == "CS0029")).IsTrue();
    }

    [Test]
    public async Task InheritedMembersAndIgnoredRequiredMembersMatchReflection()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using LithoSharp.Content;
            using LithoSharp.Diagnostics;
            using YamlDotNet.Serialization;
            public class Base {
                [YamlIgnore] public virtual string IgnoredInBase { get; set; } = "base";
                public virtual string IgnoredInDerived { get; set; } = "base";
                [YamlMember(Alias = "base_field")] public string Field = "base";
                [YamlMember(Alias = "base_property")] public int Variant { get; set; } = 3;
                [YamlMember(Alias = "inherited_alias")] public virtual string Alias { get; set; } = "base";
            }
            public sealed class Front : Base {
                public override string IgnoredInBase { get; set; } = "derived";
                [YamlIgnore] public override string IgnoredInDerived { get; set; } = "derived";
                [YamlMember(Alias = "derived_field")] public new string Field = "derived";
                [YamlMember(Alias = "derived_property")] public new string Variant { get; set; } = "derived";
                public override string Alias { get; set; } = "derived";
                [YamlIgnore] public required string IgnoredRequired { get; init; }
                public required string Title { get; init; }
            }
            [StaticContentCollection(typeof(Front), typeof(string), "guides")]
            public static partial class Guides { }
            public static class Probe {
                public static string Check() {
                    IReadOnlyDictionary<string, object?>[] cases = [
                        new Dictionary<string, object?> { ["title"] = "hello" },
                        new Dictionary<string, object?> { ["title"] = "hello", ["base_field"] = "base assigned", ["derived_field"] = "derived assigned", ["base_property"] = "42", ["derived_property"] = "text", ["inherited_alias"] = "alias assigned" },
                        new Dictionary<string, object?> { ["title"] = "hello", ["ignored_in_base"] = "bad", ["ignored_in_derived"] = "bad", ["ignored_required"] = "bad" },
                        new Dictionary<string, object?>()
                    ];
                    var location = new SiteSourceLocation("input.yaml", 3, 2);
                    foreach (var values in cases) {
                        var expected = new ReflectionContentFrontMatterBinder<Front>().Bind(values, location);
                        var actual = Guides.Binder.Bind(values, location);
                        if (expected.IsSuccess != actual.IsSuccess) return "success differs";
                        var left = string.Join("|", System.Linq.Enumerable.Select(expected.Diagnostics, Describe));
                        var right = string.Join("|", System.Linq.Enumerable.Select(actual.Diagnostics, Describe));
                        if (left != right) return left + " != " + right;
                        if (actual.IsSuccess && DescribeValue(expected.Value!) != DescribeValue(actual.Value!)) return "values differ";
                    }
                    return "matched";
                }
                private static string Describe(SiteDiagnostic d) => d.Id + d.Message + d.Location?.FilePath + d.Location?.Line + d.Location?.Column;
                private static string DescribeValue(Front value) => value.Title + "|" + ((Base)value).Field + "|" + value.Field + "|" + ((Base)value).Variant + "|" + value.Variant + "|" + value.Alias + "|" + value.IgnoredRequired;
            }
            """;
        var (compilation, diagnostics) = Generate(source);
        await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        await Assert.That(Errors(emitted.Diagnostics)).IsEqualTo(string.Empty);
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var result = (string)assembly.GetType("Probe")!.GetMethod("Check")!.Invoke(null, null)!;
        await Assert.That(result).IsEqualTo("matched");
    }

    [Test]
    [Arguments("public int Clash;", "public new string Clash = string.Empty;")]
    [Arguments("public int Clash { get; set; }", "public new string Clash { get; set; } = string.Empty;")]
    public async Task HiddenMembersWithDuplicateYamlNamesAreRejected(string baseMember, string derivedMember)
    {
        var source = $$"""
            using LithoSharp.Content;
            public class Base { {{baseMember}} }
            public sealed class Front : Base { {{derivedMember}} }
            [StaticContentCollection(typeof(Front), typeof(string), "guides")]
            public static partial class Guides { }
            public static class Probe {
                public static bool Check() {
                    try { _ = new ReflectionContentFrontMatterBinder<Front>(); return false; }
                    catch (System.InvalidOperationException) { return true; }
                }
            }
            """;
        var (compilation, diagnostics) = Generate(source);
        await Assert.That(diagnostics.Any(d => d.Id == "LSG001")).IsTrue();
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        await Assert.That(Errors(emitted.Diagnostics)).IsEqualTo(string.Empty);
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        await Assert.That((bool)assembly.GetType("Probe")!.GetMethod("Check")!.Invoke(null, null)!).IsTrue();
    }

    [Test]
    [Arguments("typeof(string)", "typeof(string)")]
    [Arguments("typeof(int?)", "typeof(string)")]
    [Arguments("typeof(List<string>)", "typeof(string)")]
    [Arguments("typeof(Dictionary<string, string>)", "typeof(string)")]
    [Arguments("typeof(Front<>)", "typeof(string)")]
    [Arguments("typeof(Front)", "typeof(List<>)")]
    [Arguments("typeof(Front)", "typeof(void)")]
    [Arguments("typeof(Front)", "typeof(int*)")]
    public async Task UnsupportedDeclaredTypesProduceActionableDiagnostics(string frontType, string pageType)
    {
        var source = $$"""
            using System.Collections.Generic;
            using LithoSharp.Content;
            public class Front { public string Title { get; set; } = string.Empty; }
            public class Front<T> { }
            [StaticContentCollection({{frontType}}, {{pageType}}, "guides")]
            public static unsafe partial class Guides { }
            """;
        var (_, diagnostics) = Generate(source);
        await Assert.That(Errors(diagnostics)).Contains("LSG001");
        await Assert.That(diagnostics.Any(d => d.Id == "CS8785")).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task MetadataAndConstructorDefaultsRemainValid(bool referencedModel)
    {
        var model = referencedModel
            ? "using Front = LithoSharp.Content.PostFrontMatter;"
            : "public sealed class Front { public string Title { get; set; } public string Summary { get; set; } public Front() { Title = \"default\"; Summary = \"constructor default\"; } }";
        var source = $$"""
            #nullable enable
            using System.Collections.Generic;
            using LithoSharp.Content;
            {{model}}
            [StaticContentCollection(typeof(Front), typeof(string), "guides")]
            public static partial class Guides { }
            public static class Probe {
                public static string Check() {
                    var values = new Dictionary<string, object?> { ["title"] = "hello" };
                    var expected = new ReflectionContentFrontMatterBinder<Front>().Bind(values);
                    var actual = Guides.Binder.Bind(values);
                    return expected.IsSuccess && actual.IsSuccess && expected.Value!.Title == actual.Value!.Title
                        && expected.Value.Summary == actual.Value.Summary ? "matched" : "binding differs";
                }
            }
            """;
        var (compilation, diagnostics) = Generate(source, new Input("C:/site/content/intro.md", "---\ntitle: hello\n---\nBody"));
        await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        await Assert.That(Errors(emitted.Diagnostics)).IsEqualTo(string.Empty);
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        await Assert.That((string)assembly.GetType("Probe")!.GetMethod("Check")!.Invoke(null, null)!).IsEqualTo("matched");
    }

    [Test]
    [Arguments("pages")]
    [Arguments("entries")]
    [Arguments("ids")]
    [Arguments("catalog")]
    public async Task ReservedGeneratedMemberNamesAreDiagnosed(string name)
    {
        var (_, diagnostics) = Generate(Model,
            new Input($"C:/site/{name}.md", "---\ntitle: hello\n---\nBody"));
        await Assert.That(Errors(diagnostics)).Contains("LSG003");
    }

    [Test]
    public async Task StaticTemporalValuesInputBoundariesAndObjectSchemaAreValidated()
    {
        const string source = """
            #nullable enable
            using LithoSharp.Content;
            public sealed class Front {
                public System.DateOnly Date { get; set; }
                public System.TimeOnly Time { get; set; }
                public object Payload { get; set; } = new();
                public object? Optional { get; set; }
            }
            [StaticContentCollection(typeof(Front), typeof(string), "guides")]
            public static partial class Guides { }
            """;
        const string valid = "---\ndate: 2026-09-06\ntime: 12:34:56\n---\nBody";
        Compilation? validCompilation = null;
        foreach (var (path, markdown, expected) in new[] {
            ("C:/site/content/intro.md", valid, ""),
            ("C:/site/content/intro.md", "---\ndate: 2026-02-30\ntime: 12:34:56\n---\nBody", "LSG006"),
            ("C:/site/content/intro.md", "---\ndate: 2026-09-06\ntime: 2026-09-06\n---\nBody", "LSG006"),
            ("C:/site/content/intro.md", valid.Replace("---\ndate:", "---junk\ndate:"), "LSG005"),
            ("C:/site/content/intro.md", "\uFEFF" + valid, ""),
            ("C:/site/content/intro.md", valid.Replace("time: 12:34:56", "time: 12:34:56\npayload:\n  null: value"), "LSG005"),
            ("C:/site/../outside.md", valid, "LSG002") })
        {
            var (compilation, diagnostics) = Generate(source, new Input(path, markdown));
            if (expected.Length == 0)
            {
                await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
                validCompilation ??= compilation;
            }
            else await Assert.That(Errors(diagnostics)).Contains(expected);
        }
        using var stream = new MemoryStream();
        var emitted = validCompilation!.Emit(stream);
        await Assert.That(Errors(emitted.Diagnostics)).IsEqualTo(string.Empty);
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var schema = (string)assembly.GetType("Guides")!.GetField("SchemaJson")!.GetRawConstantValue()!;
        using var document = System.Text.Json.JsonDocument.Parse(schema);
        var properties = document.RootElement.GetProperty("$defs").EnumerateObject().Single().Value.GetProperty("properties");
        await Assert.That(properties.GetProperty("payload").GetProperty("not").GetProperty("type").GetString()).IsEqualTo("null");
        await Assert.That(properties.GetProperty("optional").EnumerateObject().Any()).IsFalse();
    }

    [Test]
    public async Task CatalogAndReferencesFollowIncrementalMetadataChanges()
    {
        var source = Model[..Model.IndexOf("public static class Probe", StringComparison.Ordinal)] + """
            [StaticContentCollection(typeof(Front), typeof(string), "other")]
            public static partial class Other { }
            """;
        var compilation = CreateCompilation(source);
        var input = new Input("C:/site/content/intro.md", "---\ntitle: hello\n---\nBody");
        AdditionalText text = new Text(input);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new StaticContentGenerator().AsSourceGenerator()], [text],
            (CSharpParseOptions)compilation.SyntaxTrees.First().Options, new OptionsProvider());
        foreach (var next in new[] { input, input with { Route = "changed/" },
            input with { Id = "changed", Route = "changed/" },
            input with { Id = "changed", Route = "changed/", Collection = "other" } })
        {
            var replacement = new Text(next);
            driver = driver.ReplaceAdditionalText(text, replacement);
            text = replacement;
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
            await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
            using var stream = new MemoryStream();
            await Assert.That(Errors(output.Emit(stream).Diagnostics)).IsEqualTo(string.Empty);
            var assembly = System.Reflection.Assembly.Load(stream.ToArray());
            var type = assembly.GetType(next.Collection == "guides" ? "Guides" : "Other")!;
            var catalog = (StaticContentCatalog)type.GetProperty("Catalog")!.GetValue(null)!;
            var declared = catalog.Entries.Single();
            var page = (LithoSharp.Pages.PageRef<string>)type.GetNestedType("Pages")!.GetProperty("Content_Intro")!.GetValue(null)!;
            var reference = type.GetNestedType("Entries")!.GetProperty("Content_Intro")!.GetValue(null)!;
            var id = (ContentEntryId)type.GetNestedType("Ids")!.GetProperty("Content_Intro")!.GetValue(null)!;
            await Assert.That(catalog.CollectionId.Value).IsEqualTo(next.Collection);
            await Assert.That(declared.Id.Value).IsEqualTo(next.Id);
            await Assert.That(declared.SourcePath).IsEqualTo("content/intro.md");
            await Assert.That(declared.Route.PublicPath).IsEqualTo("/" + next.Route);
            await Assert.That(ReferenceEquals(page.Route, declared.Route)).IsTrue();
            await Assert.That(ReferenceEquals(reference.GetType().GetProperty("Route")!.GetValue(reference), declared.Route)).IsTrue();
            await Assert.That(ReferenceEquals(id, declared.Id)).IsTrue();
            await Assert.That(ReferenceEquals(catalog.GetRoute(id), declared.Route)).IsTrue();
            if (next.Collection == "other")
            {
                var previous = (StaticContentCatalog)assembly.GetType("Guides")!.GetProperty("Catalog")!.GetValue(null)!;
                await Assert.That(previous.Entries.Count).IsEqualTo(0);
            }
        }
        driver = driver.RemoveAdditionalTexts([text]);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var deleted, out var deletedDiagnostics);
        await Assert.That(Errors(deletedDiagnostics)).IsEqualTo(string.Empty);
        await Assert.That(string.Join("\n", deleted.SyntaxTrees.Skip(1))).DoesNotContain("Content_Intro");
    }

    [Test]
    public async Task SiteVariantMembershipScopesRoutesAndKeepsYamlDiagnosticsSingle()
    {
        const string source = """
            using LithoSharp.Content;
            public sealed class Front { public string Title { get; set; } = ""; public int Count { get; set; } }
            [StaticContentCollection(typeof(Front), typeof(string), "guides", Site = "docs", Variant = "en")]
            public static partial class English { }
            [StaticContentCollection(typeof(Front), typeof(string), "guides", Site = "docs", Variant = "ja")]
            public static partial class Japanese { }
            [StaticContentCollection(typeof(Front), typeof(string), "guides", Site = "other", Variant = "en")]
            public static partial class OtherSite { }
            """;
        var inputs = new[] {
            new Input("C:/site/en/intro.md", "---\ntitle: hello\ncount: wrong\n---\n", Site: "docs", Variant: "en"),
            new Input("C:/site/ja/intro.md", "---\ntitle: hello\nunknown: true\n---\n", Site: "docs", Variant: "ja"),
            new Input("C:/site/other/intro.md", "---\ntitle: hello\n---\n", Site: "other", Variant: "en") };
        var (output, diagnostics) = Generate(source, inputs);
        await Assert.That(diagnostics.Count(d => d.Id == "LSG005")).IsEqualTo(1);
        await Assert.That(diagnostics.Count(d => d.Id == "LSG006")).IsEqualTo(1);
        await Assert.That(diagnostics.Any(d => d.Id is "LSG002" or "LSG003" or "LSG004")).IsFalse();
        using var stream = new MemoryStream();
        await Assert.That(Errors(output.Emit(stream).Diagnostics)).IsEqualTo(string.Empty);
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        foreach (var (type, site, variant) in new[] { ("English", "docs", "en"), ("Japanese", "docs", "ja"), ("OtherSite", "other", "en") })
        {
            var catalog = (StaticContentCatalog)assembly.GetType(type)!.GetProperty("Catalog")!.GetValue(null)!;
            await Assert.That(catalog.Site).IsEqualTo(site);
            await Assert.That(catalog.Variant).IsEqualTo(variant);
            await Assert.That(catalog.Entries.Single().Route.PublicPath).IsEqualTo("/intro/");
        }
        var (_, missingScope) = Generate(source, inputs[0] with { Site = "", Variant = "" });
        await Assert.That(missingScope.Count(d => d.Id == "LSG002")).IsEqualTo(1);
        var (_, nullScope) = Generate(source.Replace("Site = \"docs\"", "Site = null"), inputs[0]);
        await Assert.That(nullScope.Count(d => d.Id == "LSG001")).IsEqualTo(2);
        var (_, collision) = Generate(source, inputs[2], inputs[2] with { Path = "C:/site/other/second.md", Id = "second" });
        await Assert.That(collision.Count(d => d.Id == "LSG004")).IsEqualTo(1);
    }

    [Test]
    [Arguments("Catalog", "", false, "Catalog")]
    [Arguments("Binder", "", false, "Binder")]
    [Arguments("Pages", "", false, "Pages")]
    [Arguments("Guides", "public static object Catalog => new();", false, "Catalog")]
    [Arguments("Guides", "public static class Ids { }", false, "Ids")]
    [Arguments("Guides", "private sealed class BinderImpl { }", false, "BinderImpl")]
    [Arguments("Guides", "public const string SchemaJson = \"existing\";", false, "SchemaJson")]
    [Arguments("Guides", "public static void WriteJsonSchema(string path) { }", true, "WriteJsonSchema")]
    public async Task GeneratedNamesCollidingWithDeclarationAreDiagnosed(string typeName, string member, bool emitSchema, string collision)
    {
        var source = CollisionDeclaration(typeName, member, emitSchema);
        // No AdditionalFiles: declaration validation must not depend on inputs.
        var (output, diagnostics) = Generate(source);
        await Assert.That(Errors(diagnostics) + "\n" + Errors(output.GetDiagnostics())).Contains("LSG001");
        var diagnostic = diagnostics.Single(d => d.Id == "LSG001");
        await Assert.That(diagnostic.GetMessage()).Contains(collision);
        await Assert.That(diagnostic.Location.IsInSource).IsTrue();
        await Assert.That(output.SyntaxTrees.Count()).IsEqualTo(1);
        using var stream = new MemoryStream();
        await Assert.That(Errors(output.Emit(stream).Diagnostics)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task DisabledSchemaWriterDoesNotReserveItsName()
    {
        foreach (var source in new[] {
            CollisionDeclaration("WriteJsonSchema", "", false),
            CollisionDeclaration("Guides", "public static void WriteJsonSchema(string path) { }", false) })
        {
            var (output, diagnostics) = Generate(source);
            await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
            using var stream = new MemoryStream();
            await Assert.That(Errors(output.Emit(stream).Diagnostics)).IsEqualTo(string.Empty);
        }
    }

    private static string CollisionDeclaration(string typeName, string member, bool emitSchema) => $$"""
        using LithoSharp.Content;
        public sealed class Front { public string Title { get; set; } = ""; }
        [StaticContentCollection(typeof(Front), typeof(string), "guides", EmitJsonSchema = {{(emitSchema ? "true" : "false")}})]
        public static partial class {{typeName}} { }
        public static partial class {{typeName}} { {{member}} }
        """;

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) Generate(string source, params Input[] inputs)
    {
        var compilation = CreateCompilation(source);
        var texts = inputs.Select(input => new Text(input)).ToArray();
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new StaticContentGenerator().AsSourceGenerator()], texts,
            (CSharpParseOptions)compilation.SyntaxTrees.First().Options, new OptionsProvider());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return (output, diagnostics);
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(StaticContentCollectionAttribute).Assembly.Location)
            .Append(typeof(YamlDotNet.Serialization.YamlMemberAttribute).Assembly.Location).Distinct();
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("Generated_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, parse)], paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        return compilation;
    }

    private static string Errors(IEnumerable<Diagnostic> diagnostics) => string.Join("\n", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    private sealed record Input(string Path, string Content, string Id = "intro", string Route = "intro/", string Collection = "guides", string Site = "", string Variant = "");
    private sealed class Text(Input input) : AdditionalText
    {
        public Input Input { get; } = input;
        public override string Path => Input.Path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(Input.Content);
    }
    private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
    private sealed class OptionsProvider : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(new() { ["build_property.MSBuildProjectDirectory"] = "C:/site" });
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new Options([]);
        public override AnalyzerConfigOptions GetOptions(AdditionalText text)
        {
            var input = ((Text)text).Input;
            return new Options(new() {
                ["build_metadata.AdditionalFiles.LithoSharpCollection"] = input.Collection,
                ["build_metadata.AdditionalFiles.LithoSharpId"] = input.Id,
                ["build_metadata.AdditionalFiles.LithoSharpRoute"] = input.Route,
                ["build_metadata.AdditionalFiles.LithoSharpSite"] = input.Site,
                ["build_metadata.AdditionalFiles.LithoSharpVariant"] = input.Variant });
        }
    }
}
