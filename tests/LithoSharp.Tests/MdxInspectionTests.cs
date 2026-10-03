using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Inspection;
using LithoSharp.Mdx;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

/// <summary>V110-11: unsaved MDX is diagnosed and structured as MDX without
/// executing anything. Analysis shares the build parser but never bundles,
/// renders, loads plugins, or touches the network.</summary>
public sealed class MdxInspectionTests
{
    private static string FindRepository()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx"))) return path.FullName;
        throw new DirectoryNotFoundException("The MDX inspection fixture requires the repository's restored worker.");
    }

    private static MdxOptions WorkspaceOptions(TemporaryWorkspace workspace) =>
        new(workspace.Root, Path.Combine(FindRepository(), "src/LithoSharp.Mdx/worker"));

    [Test]
    public async Task HeadingsLinksImports_StructureWithoutExecution()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(WorkspaceOptions(workspace));
        const string text = "---\ntitle: Guide\n---\n# Guide\n\nimport Counter from \"./Counter.jsx\";\n\nSee [docs](./other.mdx) and ![alt](./img.png).\n";
        var result = await session.AnalyzeAsync("guide.mdx", text,
            new MdxAnalysisOptions(DocumentVersion: 7, ProjectGeneration: 3));

        await Assert.That(result.Stage).IsEqualTo("syntax");
        await Assert.That(result.DocumentVersion).IsEqualTo(7);
        await Assert.That(result.ProjectGeneration).IsEqualTo(3);
        await Assert.That(result.Title).IsEqualTo("Guide");
        await Assert.That(result.Headings.Count).IsEqualTo(1);
        await Assert.That(result.Headings[0].Text).IsEqualTo("Guide");
        // Front matter occupies lines 1-3; the heading stays on its original line.
        await Assert.That(result.Headings[0].Location?.Line).IsEqualTo(4);
        await Assert.That(result.Links.Count).IsEqualTo(2);
        await Assert.That(result.Assets.Count).IsEqualTo(1);
        var importDeclaration = result.Imports.Single();
        await Assert.That(importDeclaration.Specifier).IsEqualTo("./Counter.jsx");
        await Assert.That(importDeclaration.Names).IsEquivalentTo(["Counter"]);
        await Assert.That(importDeclaration.Location?.Line).IsEqualTo(6);
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        // The import target does not exist and was never resolved.
        await Assert.That(File.Exists(Path.Combine(workspace.Root, "Counter.jsx"))).IsFalse();
    }

    [Test]
    public async Task UnclosedJsx_ReturnsFatalDiagnosticAndKeepsWorker()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(WorkspaceOptions(workspace));
        var broken = await session.AnalyzeAsync("broken.mdx", "# Hi\n\n<Unclosed>\n");

        var diagnostic = broken.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("LSMDX001");
        await Assert.That(diagnostic.Severity).IsEqualTo(SiteDiagnosticSeverity.Error);
        await Assert.That(broken.Headings.Count).IsEqualTo(0);
        await Assert.That(broken.Imports.Count).IsEqualTo(0);

        // The worker survives fatal syntax: the next document gets fresh symbols,
        // never the previous failure (and never older success) as "latest".
        var next = await session.AnalyzeAsync("next.mdx", "# Next\n");
        await Assert.That(next.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(next.Title).IsEqualTo("Next");
    }

    [Test]
    public async Task TransformFailureWithoutSourceLocationDoesNotInventOne()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(WorkspaceOptions(workspace));
        var result = await session.AnalyzeAsync("reserved.mdx", "export const frontMatter = {}\n");

        var diagnostic = result.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("LSMDX001");
        await Assert.That(diagnostic.Location?.Line).IsNull();
        await Assert.That(diagnostic.Location?.Column).IsNull();
    }

    [Test]
    public async Task BuildAndAnalysis_AgreeOnSyntaxFailure()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(source);
        const string text = "---\ntitle: Broken\n---\n# Broken\n\n<Unclosed>\n";
        await File.WriteAllTextAsync(Path.Combine(source, "broken.mdx"), text);

        SiteBuildExtensionException? buildFailure = null;
        await using (var mdx = new MdxSite(new(workspace.Root, Path.Combine(FindRepository(), "src/LithoSharp.Mdx/worker"))))
        {
            mdx.AddCollection(new MdxContentCollectionLoader<MdxIntegrationTests.FrontMatter>(new("mdx"), source,
                entry => SiteRoute.ForDirectoryIndex(Path.ChangeExtension(entry.Id.Value, null)), entry => new(entry.FrontMatter.Title)));
            try
            {
                await new SiteGenerator().GenerateWithOptionsAsync(
                    new LithoSharp.Configuration.SiteSettings(), [], Path.Combine(workspace.Root, "out"), true, null,
                    new() { Extensions = [mdx], BuildTimestamp = DateTimeOffset.UnixEpoch }, default);
            }
            catch (SiteBuildExtensionException exception)
            {
                buildFailure = exception;
            }
        }

        await Assert.That(buildFailure).IsNotNull();
        var buildDiagnostic = buildFailure!.Diagnostics.First(item => item.Id == "LSMDX001");
        await using (var session = new MdxInspectionSession(WorkspaceOptions(workspace)))
        {
            var analyzed = await session.AnalyzeAsync("broken.mdx", text);
            var diagnostic = analyzed.Diagnostics.Single(item => item.Id == "LSMDX001");
            await Assert.That(diagnostic.Severity).IsEqualTo(buildDiagnostic.Severity);
            await Assert.That(diagnostic.Message).IsEqualTo(buildDiagnostic.Message);
            await Assert.That(diagnostic.Location?.Line).IsEqualTo(6);
            await Assert.That(diagnostic.Location?.Line).IsEqualTo(buildDiagnostic.Location?.Line);
            await Assert.That(diagnostic.Location?.Column).IsEqualTo(buildDiagnostic.Location?.Column);
        }
    }

    [Test]
    public async Task CodeFenceAndMdxCodeBlock_DoNotExecute()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(WorkspaceOptions(workspace));
        const string text = "# After\n\n```jsx\n<Unclosed>\n```\n\n```mdx-code-block\nimport Live from \"./Live.jsx\";\n```\n\n<Live />\n";
        var result = await session.AnalyzeAsync("live.mdx", text);

        // Fenced JSX stays display code (no fatal diagnostic); the unwrapped
        // block contributes a real import without executing it.
        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Imports.Any(item => item.Specifier == "./Live.jsx")).IsTrue();
        await Assert.That(File.Exists(Path.Combine(workspace.Root, "Live.jsx"))).IsFalse();
    }

    [Test]
    public async Task TabsBrowserOnlyAndIsland_ListWithoutExecution()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(WorkspaceOptions(workspace));
        const string text = "import Tabs from \"@docusaurus/Tabs\";\nimport BrowserOnly from \"@docusaurus/BrowserOnly\";\nimport Counter from \"./Counter.jsx\";\n\n<Tabs><div>tab</div></Tabs>\n\n<BrowserOnly><span>client</span></BrowserOnly>\n\n<Island component={Counter} />\n";
        var result = await session.AnalyzeAsync("page.mdx", text);

        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Imports.Count).IsEqualTo(3);
        await Assert.That(result.Components.Single().Name).IsEqualTo("./Counter.jsx#default");
    }

    [Test]
    public async Task FrontMatterCrlfAndUnicode_MapToOriginalLines()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(WorkspaceOptions(workspace));
        var text = "---\r\ntitle: Test\r\n---\r\n# \u65e5\u672c\u8a9e\u898b\u51fa\u3057 \U0001F600\r\n\r\n[link](./a.mdx)\r\n";
        var result = await session.AnalyzeAsync("doc.mdx", text);

        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Headings.Single().Text.Contains("日本語見出し")).IsTrue();
        await Assert.That(result.Headings.Single().Location?.Line).IsEqualTo(4);
        await Assert.That(result.Links.Single().Url).IsEqualTo("./a.mdx");
    }

    [Test]
    public async Task ImportCycle_CompletesWithoutResolution()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(WorkspaceOptions(workspace));
        var result = await session.AnalyzeAsync("a.mdx",
            "import B from \"./b.mdx\";\n\n# A\n");

        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Imports.Single().Specifier).IsEqualTo("./b.mdx");
        await Assert.That(result.Title).IsEqualTo("A");
    }

    [Test]
    public async Task HugeInput_Completes()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(new MdxOptions(workspace.Root,
            Path.Combine(FindRepository(), "src/LithoSharp.Mdx/worker")) { Timeout = TimeSpan.FromMinutes(5) });
        var body = new System.Text.StringBuilder("# Big\n\n");
        for (var index = 0; index < 5000; index++)
        {
            body.Append("Paragraph ").Append(index).Append(" with stable text.\n\n");
        }

        var result = await session.AnalyzeAsync("big.mdx", body.ToString());

        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Title).IsEqualTo("Big");
    }

    [Test]
    public async Task CancelledAnalysis_DoesNotKillLaterWork()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(WorkspaceOptions(workspace));
        var canceled = false;
        try
        {
            await session.AnalyzeAsync("a.mdx", "# A\n", cancellationToken: new CancellationToken(true));
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        await Assert.That(canceled).IsTrue();
        var next = await session.AnalyzeAsync("b.mdx", "# B\n");
        await Assert.That(next.Title).IsEqualTo("B");
        await Assert.That(next.Diagnostics.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MissingNode_ReportsDependencyDiagnostic()
    {
        using var workspace = new TemporaryWorkspace();
        await using var session = new MdxInspectionSession(
            new MdxOptions(workspace.Root, Path.Combine(FindRepository(), "src/LithoSharp.Mdx/worker"))
            { NodeExecutable = "no-such-node-executable" });

        SiteBuildExtensionException? failure = null;
        try
        {
            await session.AnalyzeAsync("a.mdx", "# A\n");
        }
        catch (SiteBuildExtensionException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Diagnostics.Any(item => item.Id == "LSMDX002")).IsTrue();
    }

    [Test]
    public async Task UserPluginsAndModules_AreNeverLoaded()
    {
        using var workspace = new TemporaryWorkspace();
        var sentinel = Path.Combine(workspace.Root, "evil-ran.txt");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "evil.mjs"),
            "import {writeFileSync} from 'node:fs';\nwriteFileSync(" + System.Text.Json.JsonSerializer.Serialize(sentinel) + ", 'x');\nexport default () => null;\n");
        var options = new MdxOptions(workspace.Root, Path.Combine(FindRepository(), "src/LithoSharp.Mdx/worker"))
        {
            Plugins = [new MdxPlugin("remark", "./evil.mjs", System.Text.Json.JsonDocument.Parse("{}").RootElement)],
            ComponentsModule = "./evil.mjs",
        };
        await using var session = new MdxInspectionSession(options);
        var result = await session.AnalyzeAsync("page.mdx",
            "import Evil from \"./evil.mjs\";\n\n# Page\n\n<Evil />\n");

        await Assert.That(result.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Imports.Single().Specifier).IsEqualTo("./evil.mjs");
        await Assert.That(File.Exists(sentinel)).IsFalse();
    }

    [Test]
    public async Task MdxIsNotMarkdownFallback()
    {
        using var workspace = new TemporaryWorkspace();
        const string valid = "import C from \"./C.jsx\";\n\n<C />\n";
        var markdown = DocumentInspection.Inspect("page.mdx", "---\ntitle: T\n---\n" + valid);

        // Markdown keeps JSX as paragraphs: no MDX imports, no MDX fatal diagnostic.
        await Assert.That(markdown.Diagnostics.Any(item => item.Id == "LSMDX001")).IsFalse();

        await using var session = new MdxInspectionSession(WorkspaceOptions(workspace));
        var mdx = await session.AnalyzeAsync("page.mdx", valid);
        await Assert.That(mdx.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(mdx.Imports.Single().Specifier).IsEqualTo("./C.jsx");

        // Unclosed JSX stays a silent paragraph in Markdown but is fatal in MDX.
        const string broken = "<Unclosed>\n";
        var markdownBroken = DocumentInspection.Inspect("page.mdx", "---\ntitle: T\n---\n" + broken);
        await Assert.That(markdownBroken.Diagnostics.Any(item => item.Id == "LSMDX001")).IsFalse();
        var mdxBroken = await session.AnalyzeAsync("broken.mdx", broken);
        await Assert.That(mdxBroken.Diagnostics.Any(item => item.Id == "LSMDX001")).IsTrue();
    }

    [Test]
    public async Task Analysis_WritesNoOutputs()
    {
        using var workspace = new TemporaryWorkspace();
        var before = Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories).Length;
        await using var session = new MdxInspectionSession(WorkspaceOptions(workspace));
        await session.AnalyzeAsync("a.mdx", "---\ntitle: A\n---\n# A\n");

        await Assert.That(Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories).Length).IsEqualTo(before);
    }
}
