using LithoSharp.Content;
using LithoSharp.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Tests;

public sealed partial class GeneratorTests
{
    [Test]
    public async Task NativeYamlFailuresKeepTheirOriginalSpansAndManifestOpen()
    {
        var validation = typeof(LithoSharp.Generators.StaticContentGenerator).Assembly.GetType("LithoSharp.Generators.StaticYamlValidation")!;
        var options = validation.GetField("TypedYamlOptions", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        await Assert.That((string)FrontMatterProperty(options, "Hash")!)
            .IsEqualTo(new Syntamark.YamlParseOptions(maxNestingDepth: 64).OptionsHash);
        foreach (var (yaml, token, id, message) in new[]
        {
            ("title: 😀\r\ncount: !str 3\r\n", "!str 3", "LSG005", "This YAML construct is not supported by native-yaml/1."),
            ("title: 😀\r\ntitle: duplicate\r\n", "title", "LSG005", "A mapping key is duplicated; both entries are retained."),
            ("title: 😀\r\ncount: [\r\n", "[\r\n", "LSG005", "Unterminated YAML flow collection."),
            ("title: 😀\r\ncount: |-\r\n  wrong\r\n  value\r\n", "|-\r\n  wrong\r\n  value\r\n", "LSG006", "count: Value cannot be converted to the declared type."),
        })
        {
            var raw = "\uFEFF---\r\n" + yaml + "---\r\nBody";
            var (output, diagnostics) = Generate(SelectiveFrontMatterModel, new Input("C:/site/content/intro.md", raw));
            var diagnostic = diagnostics.Single();
            await Assert.That(diagnostic.Id).IsEqualTo(id);
            await Assert.That(diagnostic.GetMessage()).IsEqualTo(message);
            var expected = new TextSpan(raw.LastIndexOf(token, StringComparison.Ordinal), token.Length);
            await Assert.That(diagnostic.Location.SourceSpan).IsEqualTo(expected);
            await Assert.That(diagnostic.Location.GetLineSpan().StartLinePosition)
                .IsEqualTo(SourceText.From(raw).Lines.GetLinePosition(expected.Start));
            await Assert.That(string.Join("\n", output.SyntaxTrees)).DoesNotContain("StaticSiteManifest.Create");
        }
    }

    [Test]
    public async Task NativeYamlNamesAndDecodedScalarsAgreeWithGeneratedAndReflectionBinding()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using LithoSharp.Content;
            public sealed class Front {
                [ContentName("heading")] public required string Title { get; set; }
                public string URLValue { get; set; } = "";
                public string Release2Name { get; set; } = "";
                public int Count { get; set; }
                public bool Enabled { get; set; }
                [ContentName("café")] public string Accent { get; set; } = "";
                [ContentIgnore] public string Hidden { get; set; } = "default";
            }
            [StaticContentCollection(typeof(Front), typeof(string), "guides")]
            public static partial class Guides { }
            public static class Probe {
                public static string Check() {
                    var values = new Dictionary<string, object?> {
                        ["heading"] = "null", ["url_value"] = "https://example.test/docs/",
                        ["release2_name"] = "2026-11-13", ["count"] = "42", ["enabled"] = "true", ["café"] = "accent"
                    };
                    var expected = new ReflectionContentFrontMatterBinder<Front>().Bind(values);
                    var actual = Guides.Binder.Bind(values);
                    if (!expected.IsSuccess || !actual.IsSuccess) return "binding failed";
                    return Describe(expected.Value!) == Describe(actual.Value!) ? Describe(actual.Value!) : "values differ";
                }
                private static string Describe(Front value) => value.Title + "|" + value.URLValue + "|" + value.Release2Name
                    + "|" + value.Count + "|" + value.Enabled + "|" + value.Accent + "|" + value.Hidden;
            }
            """;
        const string yaml = "---\nheading: 'null'\nurl_value: https://example.test/docs/\nrelease2_name: 2026-11-13\ncount: '42'\nenabled: 'true'\ncafe\u0301: accent\n---\nBody";
        var (compilation, diagnostics) = Generate(source, new Input("C:/site/content/intro.md", yaml));
        await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
        using var stream = new MemoryStream();
        await Assert.That(Errors(compilation.Emit(stream).Diagnostics)).IsEqualTo(string.Empty);
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        await Assert.That((string)assembly.GetType("Probe")!.GetMethod("Check")!.Invoke(null, null)!)
            .IsEqualTo("null|https://example.test/docs/|2026-11-13|42|True|accent|default");
        var schema = (string)assembly.GetType("Guides")!.GetField("SchemaJson")!.GetRawConstantValue()!;
        using var document = System.Text.Json.JsonDocument.Parse(schema);
        var properties = document.RootElement.GetProperty("$defs").EnumerateObject().Single().Value.GetProperty("properties");
        await Assert.That(properties.EnumerateObject().Select(property => property.Name))
            .IsEquivalentTo(new[] { "heading", "url_value", "release2_name", "count", "enabled", "café" });
        var (_, nullDiagnostics) = Generate(source, new Input("C:/site/content/intro.md", yaml.Replace("heading: 'null'", "heading: null", StringComparison.Ordinal)));
        await Assert.That(nullDiagnostics.Single().Id).IsEqualTo("LSG006");
        var duplicateYaml = yaml.Replace("cafe\u0301: accent", "café: first\ncafe\u0301: second", StringComparison.Ordinal);
        var (_, duplicateDiagnostics) = Generate(source, new Input("C:/site/content/intro.md", duplicateYaml));
        var duplicate = duplicateDiagnostics.Single();
        await Assert.That(duplicate.Id).IsEqualTo("LSG005");
        await Assert.That(duplicate.GetMessage()).IsEqualTo("Duplicate YAML field 'café'.");
        await Assert.That(duplicate.Location.SourceSpan).IsEqualTo(new TextSpan(duplicateYaml.LastIndexOf("cafe\u0301", StringComparison.Ordinal), 5));
    }

    [Test]
    public async Task InvalidContentNamesAreRejectedBeforeStaticBinding()
    {
        const string source = """
            using LithoSharp.Content;
            public sealed class Front { [ContentName(" ")] public string Title { get; set; } = ""; }
            [StaticContentCollection(typeof(Front), typeof(string), "guides")]
            public static partial class Guides { }
            """;
        var (_, diagnostics) = Generate(source);
        await Assert.That(diagnostics.Single().Id).IsEqualTo("LSG001");
    }

    [Test]
    public async Task GeneratedSequenceBindingRetainsNativeItemLocations()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using System.Linq;
            using LithoSharp.Content;
            using LithoSharp.Diagnostics;
            public sealed class Front {
                public required string Title { get; set; }
                public IReadOnlyList<int> Numbers { get; set; } = [];
            }
            [StaticContentCollection(typeof(Front), typeof(string), "guides")]
            public static partial class Guides { }
            public static class Probe {
                public static SiteDiagnostic[] Check(IReadOnlyDictionary<string, object?> values) {
                    var expected = new ReflectionContentFrontMatterBinder<Front>().Bind(values);
                    var actual = Guides.Binder.Bind(values);
                    if (string.Join("|", expected.Diagnostics.Select(Describe)) != string.Join("|", actual.Diagnostics.Select(Describe)))
                        throw new System.InvalidOperationException("Sequence diagnostics differ.");
                    return actual.Diagnostics.ToArray();
                }
                private static string Describe(SiteDiagnostic d) => d.Id + d.Message + d.Location?.Line + ":" + d.Location?.Column;
            }
            """;
        var (compilation, diagnostics) = Generate(source);
        await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
        using var stream = new MemoryStream();
        await Assert.That(Errors(compilation.Emit(stream).Diagnostics)).IsEqualTo(string.Empty);
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        const string yaml = "title: hi\nnumbers:\n  - 1\n  - wrong\n  - 2\n";
        var parse = typeof(MarkdownContentCollectionLoader<PostFrontMatter>).GetMethod("ParseYamlDocument",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic, null,
            [typeof(string), typeof(string), typeof(int), typeof(CancellationToken)], null)!;
        var parsed = (ContentParseResult<object>)parse.Invoke(null, [yaml, "input.md", 2, CancellationToken.None])!;
        await Assert.That(parsed.IsSuccess).IsTrue();
        var materialize = typeof(PostFrontMatter).Assembly.GetType("LithoSharp.Content.ContentValues")!
            .GetMethod("FromYaml", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public)!;
        var values = materialize.Invoke(null, [parsed.Value]);
        var bound = (SiteDiagnostic[])assembly.GetType("Probe")!.GetMethod("Check")!.Invoke(null, [values])!;
        var failure = bound.Single();
        await Assert.That(failure.Id).IsEqualTo(ContentFrontMatterDiagnosticIds.InvalidValue);
        await Assert.That(failure.Location!.FilePath).IsEqualTo("input.md");
        await Assert.That(failure.Location.Line).IsEqualTo(5);
        await Assert.That(failure.Location.Column).IsEqualTo(5);
    }
}
