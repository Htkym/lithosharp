using LithoSharp.Configuration;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Inspection;
using System.Text.Json;

namespace LithoSharp.Tests;

public sealed class MarkdownComponentTests
{
    private const string Badge = ":::component name=\"version-badge\" version=\"<3&>\" stability=\"Stable\"\n:::";

    [Test]
    public async Task PageContextForwardsCapturedEnvironmentAssetsAndTime()
    {
        var standalone = ComponentRenderingContext.Create(new SiteSettings { BaseUrl = "https://example.test/base/" });
        var assets = AssetRegistry.Empty.WithGenerated([new("probe", "probe.txt", new byte[] { 1 })], standalone.Site.BaseUrl);
        var timestamp = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        var configuration = standalone.Configuration with { EnvironmentName = "Preview", Assets = assets, BuildTimestamp = timestamp };
        var page = new PageRenderingContext(new SiteGenerator(), configuration);
        await Assert.That(page.EnvironmentName).IsEqualTo("Preview");
        await Assert.That(ReferenceEquals(page.Assets, assets)).IsTrue();
        await Assert.That(page.BuildTimestamp).IsEqualTo(timestamp);
        await Assert.That(page.Site.BaseUrl).IsEqualTo("https://example.test/base/");
    }

    [Test]
    public async Task RegisteredComponentsBindTypedPropsAndPreserveGenericContainers()
    {
        var compiler = Compiler(DocsSampleComponents.Registry);
        var result = compiler.Analyze(Badge, new DocumentSource("page.md", 0), CancellationToken.None, null,
            ComponentRenderingContext.Create(new SiteSettings()));
        await Assert.That(result.Html).Contains("<span class=\"version-badge\" data-stability=\"Stable\">Version &lt;3&amp;&gt;</span>");
        await Assert.That(result.Semantics!.Diagnostics.Count).IsEqualTo(0);
        await Assert.That(result.Semantics.Components.Single().Name).IsEqualTo("version-badge");
        await Assert.That(compiler.ParseCount).IsEqualTo(1);
        var generic = compiler.Compile(":::feature\nOrdinary container\n:::", ComponentRenderingContext.Create(new SiteSettings()));
        await Assert.That(generic.Html).Contains("<div class=\"feature\">\n<p>Ordinary container</p>");
        var literal = compiler.Compile(Badge.Replace("<3&>", "{1 + 2}", StringComparison.Ordinal),
            ComponentRenderingContext.Create(new SiteSettings()));
        await Assert.That(literal.Html).Contains("Version {1 + 2}");
        var unregistered = new LithoMarkdownCompiler(MarkdownCompilerOptions.Default with
        {
            Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.DocumentationProfile },
        }).Analyze(Badge, new DocumentSource("page.md", 0), CancellationToken.None, null,
            ComponentRenderingContext.Create(new SiteSettings()));
        await Assert.That(unregistered.Semantics!.Diagnostics.Single().Id).IsEqualTo(MarkdownComponentDiagnosticIds.UnknownComponent);
    }

    [Test]
    public async Task InvalidComponentPropsReportTheirNativeAttributeLocations()
    {
        foreach (var (body, id, token) in new[]
        {
            (Badge.Replace("version-badge", "missing", StringComparison.Ordinal), MarkdownComponentDiagnosticIds.UnknownComponent, "missing"),
            (Badge.Replace("Stable", "Future", StringComparison.Ordinal), ContentFrontMatterDiagnosticIds.InvalidValue, "Future"),
            (Badge.Replace("stability=\"Stable\"", "extra=\"unknown\"", StringComparison.Ordinal), ContentFrontMatterDiagnosticIds.UnknownField, "extra"),
            (Badge.Replace("version=\"<3&>\" ", "", StringComparison.Ordinal), ContentFrontMatterDiagnosticIds.MissingRequiredField, "component"),
            (Badge.Replace("\n:::", "\nChild content\n:::", StringComparison.Ordinal), MarkdownComponentDiagnosticIds.InvalidInvocation, "Child content"),
        })
        {
            var raw = "😀\r\n\r\n" + body.Replace("\n", "\r\n", StringComparison.Ordinal);
            var result = Compiler(DocsSampleComponents.Registry).Analyze(raw,
                new DocumentSource("original.md", 27) { BodyStartLine = 6 }, CancellationToken.None, null,
                ComponentRenderingContext.Create(new SiteSettings()));
            var failure = result.Semantics!.Diagnostics.Single(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error);
            await Assert.That(failure.Id).IsEqualTo(id);
            var prefix = raw[..raw.IndexOf(token, StringComparison.Ordinal)];
            await Assert.That(failure.Location!.FilePath).IsEqualTo("original.md");
            await Assert.That(failure.Location.Line).IsEqualTo(6 + prefix.Count(static character => character == '\n'));
            await Assert.That(failure.Location.Column).IsEqualTo(prefix.Length - prefix.LastIndexOf('\n'));
        }
    }

    [Test]
    public async Task StaticComponentInspectionDoesNotRunBindersOrComponents()
    {
        var registry = new MarkdownComponentRegistry([
            MarkdownComponentRegistration.Create("version-badge", new NeverRender(), new NeverBind(), "static-inspection:v1")
        ]);
        var compiler = Compiler(registry);
        var result = compiler.AnalyzeForInspection(Badge.Replace("Stable", "Future", StringComparison.Ordinal), new DocumentSource("editor.md", 0));
        await Assert.That(result.Html).IsEqualTo(string.Empty);
        await Assert.That(result.Semantics!.Diagnostics.Single().Id).IsEqualTo(ContentFrontMatterDiagnosticIds.InvalidValue);
        await Assert.That(result.Semantics.Components.Single().Name).IsEqualTo("version-badge");
        var raw = "---\ntitle: Editor\n---\n" + Badge;
        var document = DocumentInspection.Inspect("editor.md", raw, new DocumentInspectionOptions
        {
            Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.DocumentationProfile, Components = registry },
        });
        var descriptors = JsonSerializer.Deserialize<MarkdownComponentSchema[]>(JsonSerializer.Serialize(registry.Schemas))!;
        var values = MarkdownComponentEditing.Complete(document, raw.IndexOf("Stable", StringComparison.Ordinal) + 2, descriptors);
        await Assert.That(values.Select(static value => value.Label)).IsEquivalentTo(new[] { "Preview", "Stable" });
        var names = MarkdownComponentEditing.Complete(document, raw.IndexOf("version-badge", StringComparison.Ordinal) + 2, descriptors);
        await Assert.That(names.Single().Label).IsEqualTo("version-badge");
        await Assert.That(MarkdownComponentEditing.Hover(document, raw.IndexOf("stability", StringComparison.Ordinal) + 2, descriptors)!.Contents)
            .Contains("stability: enum");
    }

    [Test]
    public async Task DeclaredComponentInputsInvalidateBuildsWithoutCachingContextBoundHtml()
    {
        using var workspace = new TemporaryWorkspace();
        var file = Path.Combine(workspace.Root, "version.txt");
        await File.WriteAllTextAsync(file, "one");
        var output = Path.Combine(workspace.Root, "output");
        var calls = 0;
        var timestamp = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        var post = new MarkdownPost("page.md", "page", new PostFrontMatter { Title = "Components", Date = timestamp.AddDays(-1) },
            Badge, "posts/page.html");
        var customization = new SiteCustomization { Template = new BlogSiteTemplate(), FaviconSourceDirectory = Path.Combine(workspace.Root, "no-favicon") };
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = timestamp,
            EnvironmentName = "Preview",
            BuildCacheDirectory = Path.Combine(workspace.Root, "cache"),
            Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.DocumentationProfile, Components = Registry("config-one") },
        };
        var generator = new SiteGenerator();
        await generator.GenerateWithOptionsAsync(new SiteSettings(), [post], output, true, customization, options, CancellationToken.None);
        await Assert.That(calls).IsEqualTo(1);
        calls = 0;
        var unchanged = await generator.GenerateWithOptionsAsync(new SiteSettings(), [post], output, false, customization, options, CancellationToken.None);
        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(unchanged.BuildReport.Nodes.Single(node => node.NodeId == "page:markdown:posts/page.html").CacheHit).IsTrue();
        var partition = SiteGenerator.MeasureCache(output, options).CacheDirectory;
        var cache = new SiteBuildCache(Path.GetDirectoryName(partition)!, Path.GetFileName(partition));
        await Assert.That(await cache.ReadPostParseAsync(generator.MarkdownCompiler.Fingerprint,
            MarkdownDocumentFingerprints.ExactBodyCacheHash(post.MarkdownBody), post.MarkdownBody.Length, CancellationToken.None)).IsNull();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, ".lithosharp-output-manifest.json")));
        var nodes = await cache.LoadAsync(manifest.RootElement.GetProperty("buildCache").GetString(), CancellationToken.None);
        var outcome = nodes["page:markdown:posts/page.html"].CollectionDiagnostics;
        await Assert.That(outcome).IsNotNull();
        await Assert.That(outcome!.Diagnostics).IsEmpty();
        await File.WriteAllTextAsync(file, "two");
        var changed = await generator.GenerateWithOptionsAsync(new SiteSettings(), [post], output, false, customization, options, CancellationToken.None);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(changed.BuildReport.Nodes.Single(node => node.NodeId == "page:markdown:posts/page.html").CacheHit).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "page.html"))).Contains("config-one:two:Preview");
        calls = 0;
        options = options with { Markdown = options.Markdown with { Components = Registry("config-two") } };
        await generator.GenerateWithOptionsAsync(new SiteSettings(), [post], output, false, customization, options, CancellationToken.None);
        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "posts", "page.html"))).Contains("config-two:two:Preview");

        MarkdownComponentRegistry Registry(string configuration) => new([
            MarkdownComponentRegistration.Create("version-badge", new FileComponent(file, configuration, () => calls++),
                GeneratedVersionBadges.Binder, configuration, [ContentDependency.FromFile("version.txt")], workspace.Root)
        ]);
    }

    private static LithoMarkdownCompiler Compiler(MarkdownComponentRegistry registry) => new(
        MarkdownCompilerOptions.Default with { Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.DocumentationProfile, Components = registry } });

    private sealed class NeverBind : IContentFrontMatterBinder<VersionBadgeProps>, IContentFrontMatterSchemaProvider
    {
        public string SchemaJson => ((IContentFrontMatterSchemaProvider)GeneratedVersionBadges.Binder).SchemaJson;
        public ContentParseResult<VersionBadgeProps> Bind(IReadOnlyDictionary<string, object?> values, SiteSourceLocation? sourceLocation = null) =>
            throw new InvalidOperationException("The editor must not invoke a component binder.");
    }

    private sealed class NeverRender : ISiteComponent<VersionBadgeProps>
    {
        public IHtmlContent Render(VersionBadgeProps props, ComponentRenderingContext context) =>
            throw new InvalidOperationException("The editor must not invoke component rendering.");
    }

    private sealed class FileComponent(string path, string configuration, Action rendered) : ISiteComponent<VersionBadgeProps>
    {
        public IHtmlContent Render(VersionBadgeProps props, ComponentRenderingContext context)
        {
            rendered();
            return new HtmlText(configuration + ":" + File.ReadAllText(path) + ":" + context.EnvironmentName);
        }
    }
}
