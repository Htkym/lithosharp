using System.Collections;
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
    public async Task CompilerScansHeaderWhileKeepingStrictYamlAndOriginalOffsets()
    {
        const string raw = "\uFEFF---\r\ntitle: 😀\r\ncount: 3\r\n---\r\n<Component a={x} />\r\n";
        var adapter = typeof(StaticContentGenerator).Assembly.GetType("LithoSharp.Generators.MarkdownSourceHostAdapter")!;
        var scan = adapter.GetMethod("ParseForCompiler", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [raw, CancellationToken.None])!;
        await Assert.That(FrontMatterProperty(scan, "ContractVersion")!.ToString()).IsEqualTo("frontmatter-scan/1");
        await Assert.That(FrontMatterProperty(scan, "State")!.ToString()).IsEqualTo("Closed");
        await Assert.That(FrontMatterProperty(scan, "Status")!.ToString()).IsEqualTo("Complete");
        foreach (var property in new[] { "RawText", "RawUtf16Hash", "FrontMatter", "Coverage", "RequestedParts",
            "Headings", "Sections", "Links", "Fences", "TextRegions", "Root", "UnsupportedSyntax" })
            await Assert.That(scan.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).IsNull();
        var yamlSpan = FrontMatterSpan(scan, "YamlSpan")!.Value;
        await Assert.That(raw.Substring(yamlSpan.Start, yamlSpan.Length)).IsEqualTo("title: 😀\r\ncount: 3\r\n");
        var (valid, validDiagnostics) = Generate(SelectiveFrontMatterModel, new Input("C:/site/content/intro.md", raw));
        await Assert.That(Errors(validDiagnostics)).IsEqualTo(string.Empty);
        await Assert.That(string.Join("\n", valid.SyntaxTrees)).Contains("StaticSiteManifest.Create");

        var invalid = raw.Replace("count: 3", "count: wrong", StringComparison.Ordinal);
        var (_, invalidDiagnostics) = Generate(SelectiveFrontMatterModel, new Input("C:/site/content/intro.md", invalid));
        var conversion = invalidDiagnostics.Single(diagnostic => diagnostic.Id == "LSG006");
        var expected = new TextSpan(invalid.IndexOf("wrong", StringComparison.Ordinal), 5);
        await Assert.That(conversion.Location.SourceSpan).IsEqualTo(expected);
        await Assert.That(conversion.Location.GetLineSpan().StartLinePosition)
            .IsEqualTo(SourceText.From(invalid).Lines.GetLinePosition(expected.Start));

        const string malformed = "\uFEFF---\r\ntitle: [\r\n---\r\nBody";
        var malformedScan = ScanGeneratorFrontMatter(malformed);
        await Assert.That(FrontMatterProperty(malformedScan, "State")!.ToString()).IsEqualTo("Closed");
        await Assert.That(FrontMatterProperty(malformedScan, "Status")!.ToString()).IsEqualTo("Complete");
        var (malformedOutput, malformedDiagnostics) = Generate(SelectiveFrontMatterModel, new Input("C:/site/content/intro.md", malformed));
        var syntax = malformedDiagnostics.Single();
        await Assert.That(syntax.Id).IsEqualTo("LSG005");
        await Assert.That(syntax.Location.SourceSpan).IsEqualTo(new TextSpan(malformed.IndexOf("[", StringComparison.Ordinal), 3));
        await Assert.That(string.Join("\n", malformedOutput.SyntaxTrees)).DoesNotContain("StaticSiteManifest.Create");
    }

    [Test]
    public async Task ClosedHeaderBudgetAndSchemaIgnoreTheOriginalBody()
    {
        const string header = "\uFEFF---\r\ntitle: 😀\r\ncount: 3\r\n---\r\n";
        var front = CreateCompilation(SelectiveFrontMatterModel).GetTypeByMetadataName("Front")!;
        foreach (var body in new[]
        {
            "<Component a={x} />\n<script>let x=1;</script>\n",
            new string('>', 240) + " deeply nested body\n[unterminated](\n```unclosed\n",
            new string('#', 1048577),
            "\uD800invalid body\uDC00",
        })
        {
            var raw = header + body;
            var scan = ScanGeneratorFrontMatter(raw, maxInput: header.Length, scan: 256);
            await Assert.That(FrontMatterProperty(scan, "State")!.ToString()).IsEqualTo("Closed");
            await Assert.That(FrontMatterProperty(scan, "Status")!.ToString()).IsEqualTo("Complete");
            await Assert.That(FrontMatterSpan(scan, "HeaderSpan")).IsEqualTo(new TextSpan(0, header.Length));
            await Assert.That(FrontMatterSpan(scan, "BodySpan")).IsEqualTo(new TextSpan(header.Length, body.Length));
            await Assert.That(FrontMatterDiagnosticIds(scan)).IsEqualTo(string.Empty);
            var diagnostics = new List<Diagnostic>();
            await Assert.That(ValidateGeneratorFrontMatter(raw, scan, front, diagnostics)).IsTrue();
            await Assert.That(diagnostics.Count).IsEqualTo(0);
            var (output, generatedDiagnostics) = Generate(SelectiveFrontMatterModel, new Input("C:/site/content/intro.md", raw));
            await Assert.That(Errors(generatedDiagnostics)).IsEqualTo(string.Empty);
            // Invalid body Unicode makes the independent manifest fingerprint unavailable.
            if (body[0] != '\uD800')
                await Assert.That(string.Join("\n", output.SyntaxTrees)).Contains("StaticSiteManifest.Create");
            else
                await Assert.That(string.Join("\n", output.SyntaxTrees)).DoesNotContain("StaticSiteManifest.Create");

            var invalid = raw.Replace("count: 3", "count: wrong", StringComparison.Ordinal);
            var invalidScan = ScanGeneratorFrontMatter(invalid, maxInput: header.Length + 4, scan: 256);
            diagnostics.Clear();
            await Assert.That(ValidateGeneratorFrontMatter(invalid, invalidScan, front, diagnostics)).IsTrue();
            var conversion = diagnostics.Single();
            await Assert.That(conversion.Id).IsEqualTo("LSG006");
            await Assert.That(conversion.Location.SourceSpan).IsEqualTo(new TextSpan(invalid.IndexOf("wrong", StringComparison.Ordinal), 5));
        }
    }

    [Test]
    public async Task UnknownHeaderEvidenceCannotCertifyMissingYamlOrConfirmedUntermination()
    {
        var front = CreateCompilation(SelectiveFrontMatterModel).GetTypeByMetadataName("Front")!;
        foreach (var (raw, input, scanUnits, status, diagnostic) in new[]
        {
            ("---\ntitle: Hello\n---\n", 1048576, 4, "Partial", "LMD003"),
            ("---\ntitle: Hello\n---\n", 8, 16777216, "Failed", "LMD002"),
            ("---\ntitle: \uD800\n---\n", 1048576, 16777216, "Failed", "LMD001"),
        })
        {
            var scan = ScanGeneratorFrontMatter(raw, input, scanUnits);
            await Assert.That(FrontMatterProperty(scan, "State")!.ToString()).IsEqualTo("Unknown");
            await Assert.That(FrontMatterProperty(scan, "Status")!.ToString()).IsEqualTo(status);
            await Assert.That(FrontMatterDiagnosticIds(scan)).IsEqualTo(diagnostic);
            foreach (var property in new[] { "OpeningSpan", "YamlSpan", "ClosingSpan", "BodySpan", "HeaderSpan", "FailureSpan", "FailureLine" })
                await Assert.That(FrontMatterProperty(scan, property)).IsNull();
            var diagnostics = new List<Diagnostic>();
            await Assert.That(ValidateGeneratorFrontMatter(raw, scan, front, diagnostics)).IsFalse();
            await Assert.That(diagnostics.Count).IsEqualTo(0);
        }

        foreach (var raw in new[] { "---\ntitle: " + new string('a', 1048576) + "\n---\nBody", "---\ntitle: \uD800\n---\nBody" })
        {
            var (output, diagnostics) = Generate(SelectiveFrontMatterModel, new Input("C:/site/content/intro.md", raw));
            await Assert.That(diagnostics.Single().Id).IsEqualTo("LSG002");
            await Assert.That(string.Join("\n", output.SyntaxTrees)).DoesNotContain("StaticSiteManifest.Create");
        }

        const string unterminated = "\uFEFF---\r\ntitle: 😀\r\n";
        var known = ScanGeneratorFrontMatter(unterminated);
        await Assert.That(FrontMatterProperty(known, "State")!.ToString()).IsEqualTo("Unterminated");
        await Assert.That(FrontMatterProperty(known, "Status")!.ToString()).IsEqualTo("Partial");
        await Assert.That(FrontMatterDiagnosticIds(known)).IsEqualTo("LMD004");
        await Assert.That(FrontMatterSpan(known, "FailureSpan")).IsEqualTo(new TextSpan(unterminated.Length, 0));
        await Assert.That((int)FrontMatterProperty(known, "FailureLine")!).IsEqualTo(3);
        var (strictOutput, strictDiagnostics) = Generate(SelectiveFrontMatterModel, new Input("C:/site/content/intro.md", unterminated));
        var strict = strictDiagnostics.Single();
        await Assert.That(strict.Id).IsEqualTo("LSG005");
        await Assert.That(strict.Location.SourceSpan).IsEqualTo(new TextSpan(6, 3));
        await Assert.That(string.Join("\n", strictOutput.SyntaxTrees)).DoesNotContain("StaticSiteManifest.Create");
    }

    private static object ScanGeneratorFrontMatter(string raw, int maxInput = 1048576, int scan = 16777216)
    {
        var assembly = typeof(StaticContentGenerator).Assembly;
        var adapter = assembly.GetType("LithoSharp.Generators.MarkdownSourceHostAdapter")!;
        var options = assembly.GetType("Syntamark.Compilation.MdOptions")!
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(constructor => constructor.GetParameters().Length == 6)
            .Invoke([maxInput, 131072, 200, scan, "lithosharp-markdown/1", 1]);
        return adapter.GetMethod("AnalyzeForCompiler", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [raw, options, adapter.GetField("ExpectedParserVersion", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue(),
                "2.0", "lithosharp-markdown/1", "native-yaml/1", CancellationToken.None])!;
    }

    private static bool ValidateGeneratorFrontMatter(string raw, object scan, INamedTypeSymbol front, List<Diagnostic> diagnostics)
        => (bool)typeof(StaticContentGenerator).Assembly.GetType("LithoSharp.Generators.StaticYamlValidation")!
            .GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["input.md", raw, scan, front, new Action<Diagnostic>(diagnostics.Add), CancellationToken.None])!;

    private static TextSpan? FrontMatterSpan(object value, string name)
    {
        var range = FrontMatterProperty(value, name);
        return range is null ? null : new TextSpan((int)FrontMatterProperty(range, "Start")!, (int)FrontMatterProperty(range, "Length")!);
    }

    private static string FrontMatterDiagnosticIds(object value)
        => string.Join(",", ((IEnumerable)FrontMatterProperty(value, "Diagnostics")!).Cast<object>().Select(item => FrontMatterProperty(item, "Id")));

    private static object? FrontMatterProperty(object value, string name)
        => value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(value);
}
