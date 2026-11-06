using System.Reflection;
using LithoSharp.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace LithoSharp.Tests;

public sealed partial class GeneratorTests
{
    private const string SelectiveFrontMatterModel = """
        using LithoSharp.Content;
        public sealed class Front { public required string Title { get; set; } public int Count { get; set; } }
        [StaticContentCollection(typeof(Front), typeof(string), "guides", EmitStaticSiteManifest = true)]
        public static partial class Guides { }
        """;

    [Test]
    public async Task CompilerSelectsFrontMatterWhileKeepingStrictYamlAndOriginalOffsets()
    {
        const string raw = "\uFEFF---\r\ntitle: 😀\r\ncount: 3\r\n---\r\n<Component a={x} />\r\n";
        var adapter = typeof(StaticContentGenerator).Assembly.GetType("LithoSharp.Generators.MarkdownSourceHostAdapter")!;
        var analysis = adapter.GetMethod("ParseForCompiler", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [raw, "scope", "input.md", CancellationToken.None])!;
        await Assert.That(FrontMatterProperty(analysis, "RequestedParts")!.ToString()).IsEqualTo("FrontMatter");
        foreach (var part in new[] { "Headings", "Sections", "Links", "Fences", "TextRegions", "Root", "UnsupportedSyntax" })
            await Assert.That(FrontMatterProperty(analysis, part)).IsNull();
        var (valid, validDiagnostics) = Generate(SelectiveFrontMatterModel, new Input("C:/site/content/intro.md", raw));
        await Assert.That(Errors(validDiagnostics)).IsEqualTo(string.Empty);
        await Assert.That(string.Join("\n", valid.SyntaxTrees)).Contains("StaticSiteManifest.Create");

        var invalid = raw.Replace("count: 3", "count: wrong", StringComparison.Ordinal);
        var (_, invalidDiagnostics) = Generate(SelectiveFrontMatterModel, new Input("C:/site/content/intro.md", invalid));
        var conversion = invalidDiagnostics.Single(diagnostic => diagnostic.Id == "LSG006");
        await Assert.That(conversion.Location.SourceSpan).IsEqualTo(new TextSpan(invalid.IndexOf("wrong", StringComparison.Ordinal), 5));
        var (unterminated, strictDiagnostics) = Generate(SelectiveFrontMatterModel,
            new Input("C:/site/content/intro.md", "---\ntitle: Hello\n# Body"));
        await Assert.That(strictDiagnostics.Single().Id).IsEqualTo("LSG005");
        await Assert.That(string.Join("\n", unterminated.SyntaxTrees)).DoesNotContain("StaticSiteManifest.Create");
    }

    [Test]
    public async Task FrontMatterValidationUsesKnownPartialEvidenceWithoutCertifyingMissingEvidence()
    {
        var symbol = CreateCompilation(SelectiveFrontMatterModel).GetTypeByMetadataName("Front")!;
        var validator = typeof(StaticContentGenerator).Assembly.GetType("LithoSharp.Generators.StaticYamlValidation")!
            .GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var (raw, parts, scan, expected, diagnostic) in new[]
        {
            ("---\ntitle: Hello\n---\n" + new string('#', 4096), "FrontMatter", 256, true, ""),
            ("---\ntitle: Hello\ncount: wrong\n---\n" + new string('#', 4096), "FrontMatter", 256, true, "LSG006"),
            ("---\ntitle: [\n---\nBody", "FrontMatter", 16777216, true, "LSG005"),
            ("---\ntitle: Hello\n---\n", "FrontMatter", 4, false, ""),
            ("---\ntitle: Hello\n---\n\uD800", "FrontMatter", 16777216, false, ""),
            ("---\ntitle: Hello\n---\n", "None", 16777216, false, ""),
        })
        {
            var analysis = AnalyzeGeneratorFrontMatter(raw, parts, scan);
            if (expected)
                await Assert.That(FrontMatterProperty(analysis, "Status")!.ToString()).IsEqualTo("Partial");
            var diagnostics = new List<Diagnostic>();
            var available = (bool)validator.Invoke(null,
                ["input.md", analysis, symbol, new Action<Diagnostic>(diagnostics.Add), CancellationToken.None])!;
            await Assert.That(available).IsEqualTo(expected);
            await Assert.That(string.Join(",", diagnostics.Select(item => item.Id))).IsEqualTo(diagnostic);
        }
        var (unavailable, unavailableDiagnostics) = Generate(SelectiveFrontMatterModel,
            new Input("C:/site/content/intro.md", "---\ntitle: Hello\n---\n\uD800"));
        await Assert.That(unavailableDiagnostics.Single().Id).IsEqualTo("LSG002");
        await Assert.That(string.Join("\n", unavailable.SyntaxTrees)).DoesNotContain("StaticSiteManifest.Create");
    }

    private static object AnalyzeGeneratorFrontMatter(string raw, string parts, int scan)
    {
        var assembly = typeof(StaticContentGenerator).Assembly;
        var options = assembly.GetType("Syntamark.Compilation.MdOptions")!
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(constructor => constructor.GetParameters().Length == 6)
            .Invoke([1048576, 131072, 200, scan, "lithosharp-markdown/1", 1]);
        return assembly.GetType("Syntamark.Compilation.MdParser")!
            .GetMethod("Analyze", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [raw, Enum.Parse(assembly.GetType("Syntamark.Compilation.MdAnalysisParts")!, parts),
                "scope", "input.md", null, options, CancellationToken.None])!;
    }

    private static object? FrontMatterProperty(object value, string name)
        => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value);
}
