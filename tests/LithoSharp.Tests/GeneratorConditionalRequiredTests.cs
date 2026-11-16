namespace LithoSharp.Tests;

public sealed partial class GeneratorTests
{
    [Test]
    public async Task ConditionalRequirementsAgreeAcrossStaticGeneratedAndReflectionBinding()
    {
        const string source = """
            #nullable enable
            using System.Collections.Generic;
            using System.Linq;
            using LithoSharp.Content;
            public class Base {
                public string? State { get; set; }
                [ContentRequiredWhen("state", "published")] public virtual string? Summary { get; set; }
            }
            public sealed class Front : Base {
                public bool Released { get; set; }
                [ContentRequiredWhen("released", "true")] public override string? Summary { get; set; }
                [ContentRequiredWhen("state", "tagged")] public IReadOnlyList<string>? Tags { get; set; }
                [ContentRequiredWhen("state", "indexed")] public IReadOnlyDictionary<string, string>? Lookup { get; set; }
            }
            [StaticContentCollection(typeof(Front), typeof(string), "guides")]
            public static partial class Guides { }
            public static class Probe {
                public static string Check() {
                    IReadOnlyDictionary<string, object?>[] cases = [
                        new Dictionary<string, object?> { ["state"] = "published" },
                        new Dictionary<string, object?> { ["state"] = "published", ["summary"] = null },
                        new Dictionary<string, object?> { ["state"] = "published", ["summary"] = " " },
                        new Dictionary<string, object?> { ["state"] = "published", ["summary"] = "ready" },
                        new Dictionary<string, object?> { ["released"] = "true" },
                        new Dictionary<string, object?> { ["state"] = null },
                        new Dictionary<string, object?> { ["state"] = "tagged", ["tags"] = new string[0] },
                        new Dictionary<string, object?> { ["state"] = "indexed", ["lookup"] = new Dictionary<string, string>() },
                        new Dictionary<string, object?>()
                    ];
                    foreach (var values in cases) {
                        var expected = new ReflectionContentFrontMatterBinder<Front>().Bind(values);
                        var actual = Guides.Binder.Bind(values);
                        var left = string.Join("|", expected.Diagnostics.Select(d => d.Id + d.Message));
                        var right = string.Join("|", actual.Diagnostics.Select(d => d.Id + d.Message));
                        if (expected.IsSuccess != actual.IsSuccess || left != right) return left + " != " + right;
                    }
                    return "matched";
                }
            }
            """;
        Microsoft.CodeAnalysis.Compilation? valid = null;
        foreach (var (yaml, rejected) in new[]
        {
            ("state: published\nsummary: ready\n", false),
            ("state: published\n", true),
            ("state: published\nsummary: null\n", true),
            ("state: published\nsummary: ' '\n", true),
            ("released: true\n", true),
            ("state: null\n", false),
            ("state: tagged\ntags: []\n", true),
            ("state: indexed\nlookup: {}\n", true),
        })
        {
            var (compilation, diagnostics) = Generate(source, new Input("C:/site/content/intro.md", "---\n" + yaml + "---\nBody"));
            if (rejected)
            {
                var failure = diagnostics.Single();
                await Assert.That(failure.Id).IsEqualTo("LSG006");
                await Assert.That(failure.GetMessage()).Contains("is required when");
            }
            else
            {
                await Assert.That(Errors(diagnostics)).IsEqualTo(string.Empty);
                valid ??= compilation;
            }
        }
        using var stream = new MemoryStream();
        await Assert.That(Errors(valid!.Emit(stream).Diagnostics)).IsEqualTo(string.Empty);
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        await Assert.That((string)assembly.GetType("Probe")!.GetMethod("Check")!.Invoke(null, null)!).IsEqualTo("matched");
    }

    [Test]
    public async Task ConditionalRequirementsRejectUnknownConditionDeclarations()
    {
        const string source = """
            using LithoSharp.Content;
            public sealed class Front { [ContentRequiredWhen("missing", "yes")] public string? Summary { get; set; } }
            [StaticContentCollection(typeof(Front), typeof(string), "guides")]
            public static partial class Guides { }
            """;
        var (_, diagnostics) = Generate(source);
        await Assert.That(diagnostics.Single().Id).IsEqualTo("LSG001");
    }
}
