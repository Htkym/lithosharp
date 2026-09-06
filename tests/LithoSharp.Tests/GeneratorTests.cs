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
    public async Task GeneratedBinderCompilesAndMatchesReflection()
    {
        var (compilation, diagnostics) = Generate(Model, new Input("C:/site/content/intro.md", "---\ntitle: hello\n---\nBody"));
        await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        await Assert.That(Errors(emitted.Diagnostics)).IsEqualTo(string.Empty);
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var result = (string)assembly.GetType("Probe")!.GetMethod("Check")!.Invoke(null, null)!;
        await Assert.That(result).IsEqualTo("/intro/");
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

    private static (Compilation Compilation, ImmutableArray<Diagnostic> Diagnostics) Generate(string source, params Input[] inputs)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(StaticContentCollectionAttribute).Assembly.Location)
            .Append(typeof(YamlDotNet.Serialization.YamlMemberAttribute).Assembly.Location).Distinct();
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create("Generated_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, parse)], paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var texts = inputs.Select(input => new Text(input)).ToArray();
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new StaticContentGenerator().AsSourceGenerator()], texts, parse, new OptionsProvider());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return (output, diagnostics);
    }

    private static string Errors(IEnumerable<Diagnostic> diagnostics) => string.Join("\n", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    private sealed record Input(string Path, string Content, string Id = "intro", string Route = "intro/", string Collection = "guides");
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
                ["build_metadata.AdditionalFiles.LithoSharpRoute"] = input.Route });
        }
    }
}
