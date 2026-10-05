using System.Text.Json;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Mdx;

namespace LithoSharp.Tests;

public sealed class MdxInspectionFrontMatterTests
{
    private static string FindWorker()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
                return Path.Combine(path.FullName, "src/LithoSharp.Mdx/worker");
        throw new DirectoryNotFoundException("The inspection fixture requires the restored repository worker.");
    }

    [Test]
    public async Task MalformedYamlRetainsStrictParserDiagnosticAndBodyLocations()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(new(workspace.Root, FindWorker()));
        const string yaml = "title: [\n";
        const string text = "---\n" + yaml + "---\n# Body\n";
        var expected = MarkdownContentCollectionLoader<object>.ParseYaml(yaml, "broken.mdx", 2, default);
        await Assert.That(expected.IsSuccess).IsFalse();
        var result = await session.AnalyzeAsync("broken.mdx", text);
        var diagnostic = result.Diagnostics.Single(item => item.Id == MarkdownContentDiagnosticIds.InvalidYaml);
        var original = expected.Diagnostics.Single();
        await Assert.That(diagnostic.Severity).IsEqualTo(SiteDiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).IsEqualTo(original.Message);
        await Assert.That(diagnostic.Location?.Line).IsEqualTo(original.Location?.Line);
        await Assert.That(diagnostic.Location?.Column).IsEqualTo(original.Location?.Column);
        await Assert.That(result.Headings.Single().Location?.Line).IsEqualTo(4);
    }

    [Test]
    public async Task EmptyFrontMatterBomCrlfAndUnicodeKeepOriginalBodyOffsets()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(new(workspace.Root, FindWorker()));
        const string link = "[日本 😀](./image.png)";
        const string text = "\uFEFF---\r\n\r\n---\r\n# 日本 😀\r\n\r\n" + link + "\r\n";
        var result = await session.AnalyzeAsync("empty.mdx", text);
        var diagnostic = result.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo(MarkdownContentDiagnosticIds.EmptyFrontMatter);
        await Assert.That(diagnostic.Location?.Line).IsEqualTo(2);
        await Assert.That(result.Headings.Single().Location?.Line).IsEqualTo(4);
        await Assert.That(result.Headings.Single().Location?.Column).IsEqualTo(1);
        await Assert.That(result.Links.Single().Location?.Line).IsEqualTo(6);
        await Assert.That(result.Links.Single().Location?.EndColumn).IsEqualTo(link.Length + 1);
    }

    [Test]
    public async Task UnknownIslandLocationDoesNotInventFirstLine()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(new(workspace.Root, FindWorker()));
        const string text = "import Counter from './Counter.jsx';\n\n# Page\n\n<Island component={Counter} />\n";
        var result = await session.AnalyzeAsync("island.mdx", text);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        var component = result.Components.Single();
        await Assert.That(component.Name).IsEqualTo("./Counter.jsx#default");
        await Assert.That(component.Location).IsNull();
        await Assert.That(File.Exists(Path.Combine(workspace.Root, "Counter.jsx"))).IsFalse();
    }

    [Test]
    public async Task HeaderlessMdxRemainsSupported()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(new(workspace.Root, FindWorker()));
        var result = await session.AnalyzeAsync("headerless.mdx", "# Headerless\n\n[link](./other.mdx)\n");
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Title).IsEqualTo("Headerless");
        await Assert.That(result.Headings.Single().Location?.Line).IsEqualTo(1);
        await Assert.That(result.Links.Single().Location?.Line).IsEqualTo(3);
    }

    [Test]
    public async Task YamlValidationDoesNotExecuteImportsComponentsOrPlugins()
    {
        using var workspace = new TemporaryWorkspace();
        var sentinel = Path.Combine(workspace.Root, "evil-ran.txt");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "evil.mjs"),
            "import {writeFileSync} from 'node:fs';\nwriteFileSync(" + JsonSerializer.Serialize(sentinel) + ", 'executed');\nexport default () => null;\n");
        using var pluginOptions = JsonDocument.Parse("{}");
        await using var session = new MdxInspectionSession(new(workspace.Root, FindWorker())
        {
            Plugins = [new MdxPlugin("remark", "./evil.mjs", pluginOptions.RootElement)],
            ComponentsModule = "./evil.mjs",
        });
        var result = await session.AnalyzeAsync("nonexecution.mdx",
            "---\ntitle: [\n---\nimport Evil from './evil.mjs';\n\n# Page\n\n<Evil />\n");
        await Assert.That(result.Diagnostics.Any(item => item.Id == MarkdownContentDiagnosticIds.InvalidYaml)).IsTrue();
        await Assert.That(result.Imports.Single().Specifier).IsEqualTo("./evil.mjs");
        await Assert.That(File.Exists(sentinel)).IsFalse();
    }

    [Test]
    public async Task CrOnlyFrontMatterKeepsWorkerImportPositionsInOriginalLines()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(new(workspace.Root, FindWorker()));
        const string text = "\uFEFF---\rtitle: Test\r---\r# 日本 😀\r\rimport Counter from './Counter.jsx';\r\r[link](./other.mdx)\r";
        var result = await session.AnalyzeAsync("cr-only.mdx", text);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Headings.Single().Location?.Line).IsEqualTo(4);
        await Assert.That(result.Imports.Single().Location?.Line).IsEqualTo(6);
        await Assert.That(result.Links.Single().Location?.Line).IsEqualTo(8);
        await Assert.That(File.Exists(Path.Combine(workspace.Root, "Counter.jsx"))).IsFalse();
    }

    [Test]
    public async Task UnterminatedPresentFrontMatterRetainsExistingCoreDiagnostic()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(new(workspace.Root, FindWorker()));
        const string text = "---\rtitle: Broken\r# Body\r";
        var expected = MarkdownContentCollectionLoader<object>.MarkdownSourceDocument.Parse(text, "unterminated.mdx", default);
        await Assert.That(expected.IsSuccess).IsFalse();
        var result = await session.AnalyzeAsync("unterminated.mdx", text);
        var diagnostic = result.Diagnostics.Single(item => item.Id == MarkdownContentDiagnosticIds.UnterminatedFrontMatter);
        var original = expected.Diagnostics.Single();
        await Assert.That(diagnostic.Message).IsEqualTo(original.Message);
        await Assert.That(diagnostic.Severity).IsEqualTo(original.Severity);
        await Assert.That(diagnostic.Location?.Line).IsEqualTo(original.Location?.Line);
        await Assert.That(diagnostic.Location?.Column).IsEqualTo(original.Location?.Column);
    }
}
