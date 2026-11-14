using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Documentation;
using LithoSharp.Inspection;
using LithoSharp.Mdx;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class MarkdownComponentReferenceTests
{
    private const string Body = ":::component name=\"reference-card\" target=\"target\"\n:::";
    private static readonly DocumentKey Source = new("guide", "v1", "en", "source");

    [Test]
    public async Task ReferencePropsUseTheSameCatalogRulesDuringRenderingAndInspection()
    {
        var source = new DocumentSemanticDocument(Source, "source.md", new Dictionary<string, object?>(),
            DocumentSemanticPublication.Published, true);
        var target = Source with { Id = "target" };
        var snapshot = new DocumentSemanticSnapshot(new([Page(Source), Page(target)]), [source], true);
        var standalone = ComponentRenderingContext.Create(new SiteSettings());
        var context = new ComponentRenderingContext(null, standalone.Configuration) { Document = source, DocumentSemantics = snapshot };
        var registry = Registry();
        var compiler = Compiler(registry);
        var valid = compiler.Analyze(Body, new DocumentSource("source.md", 0), CancellationToken.None, null, context);
        await Assert.That(valid.Html).Contains("href=\"/docs/target/\"");
        await Assert.That(valid.Semantics!.Diagnostics.Count).IsEqualTo(0);

        var missing = Body.Replace("target=\"target\"", "target=\"missing\"", StringComparison.Ordinal);
        var failed = compiler.Analyze(missing, new DocumentSource("source.md", 0), CancellationToken.None, null, context);
        var failure = failed.Semantics!.Diagnostics.Single(static item => item.Severity == SiteDiagnosticSeverity.Error);
        await Assert.That(failure.Id).IsEqualTo("LSDOC211");
        await Assert.That(failure.Location!.Column).IsEqualTo(missing.IndexOf("missing", StringComparison.Ordinal) + 1);

        var partial = new DocumentSemanticSnapshot(new([Page(Source)]), [source], false);
        var unavailable = compiler.Analyze(missing, new DocumentSource("source.md", 0), CancellationToken.None, null,
            new ComponentRenderingContext(null, standalone.Configuration) { Document = source, DocumentSemantics = partial });
        await Assert.That(unavailable.Semantics!.Diagnostics.Any(static item => item.Id == "LSDOC211")).IsFalse();
        await Assert.That(unavailable.Semantics.Diagnostics.Any(static item => item.Id == "LSDOC214")).IsTrue();
        await Assert.That(unavailable.Semantics.Diagnostics.Any(static item => item.Id == MarkdownComponentDiagnosticIds.ReferenceContextUnavailable)).IsTrue();

        var projectHash = "sha256:" + new string('a', 64);
        var project = ProjectInspectionSnapshot.Create("test", 1, "guide", "markdown", "custom", "v1", "en")
            .WithMarkdownProfile(MarkdownOptions.DocumentationProfile)
            .WithMarkdownComponents(projectHash, registry.Schemas)
            .WithSemantics(projectHash, snapshot, new Dictionary<string, DocumentSemanticRuleSet>());
        var reloaded = ProjectInspectionSnapshot.ParseJson(project.ToJson());
        var inspected = DocumentInspection.Inspect("source.md", "---\ntitle: Source\n---\n" + missing,
            new DocumentInspectionOptions { Project = reloaded });
        await Assert.That(inspected.Diagnostics.Any(static item => item.Id == "LSDOC211")).IsTrue();
        await Assert.That(inspected.Diagnostics.Any(static item => item.Id == MarkdownComponentDiagnosticIds.ReferenceContextUnavailable)).IsFalse();
        var explicitlyLegacy = DocumentInspection.Inspect("source.md", "---\ntitle: Source\n---\n" + missing,
            new DocumentInspectionOptions { Project = reloaded, Markdown = new MarkdownOptions() });
        await Assert.That(explicitlyLegacy.Components).IsEmpty();
        await Assert.That(explicitlyLegacy.Diagnostics.Any(static item => item.Id == "LSDOC211")).IsFalse();
    }

    [Test]
    public async Task DocumentationBuildCapturesReferencePublicationAndInvalidatesCachedConsumers()
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(input);
        var target = Path.Combine(input, "target.md");
        await File.WriteAllTextAsync(Path.Combine(input, "source.md"), "---\nid: source\ntitle: Source\n---\n" + Body);
        await File.WriteAllTextAsync(target, "---\nid: target\ntitle: Target\n---\n# Target\n");
        await using var docs = new DocumentationSite(new(workspace.Root) { NodeExecutable = "unavailable-node-for-markdown" });
        docs.AddCollection(new("guide", [new("v1", "en", input, "docs")]));
        var output = Path.Combine(workspace.Root, "output");
        var options = new SiteGenerationOptions
        {
            Extensions = [docs],
            Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.DocumentationProfile, Components = Registry() },
            BuildTimestamp = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero),
        };
        var generator = new SiteGenerator();
        async Task Build() => await generator.GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.test/base/" }, [],
            output, false, new() { Template = new DocsSiteTemplate() }, options, CancellationToken.None);
        await Build();
        var path = Path.Combine(output, "docs", "source", "index.html");
        var valid = await File.ReadAllTextAsync(path);
        await Assert.That(valid).Contains("href=\"/base/docs/target/\"");
        await Build();
        await File.WriteAllTextAsync(target, "---\nid: target\ntitle: Target\ndraft: true\n---\n# Target\n");
        await Assert.That(Build).Throws<SiteQualityValidationException>();
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo(valid);
        await File.WriteAllTextAsync(target, "---\nid: target\ntitle: Target updated\n---\n# Target\n");
        await Build();
        await Assert.That(await File.ReadAllTextAsync(path)).Contains("href=\"/base/docs/target/\"");
    }

    [Test]
    public async Task CanceledRenderingDoesNotInvokeComponentCode()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var standalone = ComponentRenderingContext.Create(new SiteSettings());
        var context = new ComponentRenderingContext(null, standalone.Configuration with { Cancellation = canceled.Token });
        await Assert.That(() => Compiler(Registry()).Compile(Body, context)).Throws<OperationCanceledException>();
    }

    private static DocumentPage Page(DocumentKey key) => new(key, key.Id + ".md", new() { Title = key.Id }, SiteRoute.ForDirectoryIndex("docs/" + key.Id));
    private static MarkdownComponentRegistry Registry() => new([
        MarkdownComponentRegistration.Create("reference-card", new ReferenceCard(), new ReflectionContentFrontMatterBinder<ReferenceProps>(),
            "reference-card:v1", semanticRules: new([DocumentSemanticRule.Reference("target")]))
    ]);
    private static LithoMarkdownCompiler Compiler(MarkdownComponentRegistry registry) => new(MarkdownCompilerOptions.Default with
    {
        Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.DocumentationProfile, Components = registry },
    });
    private sealed class ReferenceProps
    {
        public required string Target { get; set; }
    }
    private sealed class ReferenceCard : ISiteComponent<ReferenceProps>
    {
        public IHtmlContent Render(ReferenceProps props, ComponentRenderingContext context)
        {
            var target = context.DocumentSemantics!.Resolve(context.Document!.Key with { Id = props.Target });
            return Html.UnsafeRaw("<a href=\"" + Html.Encode(target.Route!.PublicPath) + "\">Reference target</a>");
        }
    }
}
