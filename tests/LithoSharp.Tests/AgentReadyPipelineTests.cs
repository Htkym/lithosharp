using LithoSharp.AgentReady;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Quality;

namespace LithoSharp.Tests;

[NotInParallel]
public sealed class AgentReadyPipelineTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
    private static SiteSettings Site(string baseUrl = "https://example.test/product/") => new()
    { Title = "Public site", Description = "Public description", BaseUrl = baseUrl, Language = "en", TimeZone = "UTC" };
    private static SiteCustomization Custom(bool enabled = true) => new()
    {
        GenerateLlmsTxt = true,
        AgentReady = new() { Enabled = enabled, StructuredData = new() { Mode = AgentReadyStructuredDataMode.Automatic } },
    };
    private static SiteGenerationOptions Options => new() { BuildTimestamp = Timestamp, MaxDegreeOfParallelism = 1 };
    private static MarkdownPost Post(AgentReadyWorkspace workspace, string body = "Public body") => new(
        Path.Combine(workspace.Root, "guide.md"), "guide", new() { Title = "Guide", Summary = "Guide summary", Date = Timestamp }, body, "guide.html");
    private static string Output(AgentReadyWorkspace workspace) => Path.Combine(workspace.Root, "out");

    [Test]
    public async Task PublishesOwnedMarkdownDiscoveryJsonLdAndIndexFromOnePlan()
    {
        using var workspace = new AgentReadyWorkspace();
        var result = await new SiteGenerator().GenerateWithOptionsAsync(Site(), [Post(workspace, "---\n\nPublic [same](#part)\n\n## Part\nBody")], Output(workspace), true, Custom(), Options, default);
        var html = await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.html"));
        var markdown = await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.md"));
        var llms = await File.ReadAllTextAsync(Path.Combine(Output(workspace), "llms.txt"));
        await Assert.That(html).Contains("type=\"text/markdown\"");
        await Assert.That(html).Contains("application/ld+json");
        await Assert.That(markdown).Contains("canonical: \"https://example.test/product/guide.html\"");
        await Assert.That(markdown).Contains("---\n\n---\n\nPublic [same](https://example.test/product/guide.html#part)");
        await Assert.That(llms).Contains("https://example.test/product/guide.md");
        await Assert.That(result.BuildPlan!.Artifacts.Any(artifact => artifact.RelativeOutputPath == "guide.md")).IsTrue();
    }

    [Test]
    public async Task OffKeepsHtmlAndLegacyLlmsWithoutMarkdown()
    {
        using var workspace = new AgentReadyWorkspace();
        var result = await new SiteGenerator().GenerateWithOptionsAsync(Site(), [Post(workspace)], Output(workspace), true, Custom(false), Options, default);
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.md"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.html"))).DoesNotContain("text/markdown");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "llms.txt"))).Contains("guide.html");
        await Assert.That(result.BuildPlan!.Nodes.Any(node => node.Id.Value.StartsWith("agent-ready:", StringComparison.Ordinal))).IsFalse();
        await Assert.That(result.AgentReadyPages.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WarmOutcomesReuseBodyAnalysisAndReplayAvailability()
    {
        using var workspace = new AgentReadyWorkspace();
        var compiler = new LithoMarkdownCompiler(MarkdownCompilerOptions.Default with { RetainAgentReadyFacts = true });
        var generator = new SiteGenerator(compiler);
        var options = Options with { Markdown = compiler.Options.Markdown };
        var post = Post(workspace);
        await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), true, Custom(), options, default);
        var parses = compiler.ParseCount;
        var second = await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), false, Custom(), options, default);
        await Assert.That(parses).IsEqualTo(1);
        await Assert.That(compiler.ParseCount).IsEqualTo(parses);
        await Assert.That(second.BuildReport.Nodes.Single(node => node.NodeId == "agent-ready:page:guide.html").CacheHit).IsTrue();
    }

    [Test]
    public async Task UnsupportedAutoKeepsHtmlOnlyAndWarmWarning()
    {
        using var workspace = new AgentReadyWorkspace();
        var generator = new SiteGenerator();
        var post = Post(workspace, "Visible <Unknown /> body");
        await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), true, Custom(), Options, default);
        var result = await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), false, Custom(), Options, default);
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.md"))).IsFalse();
        await Assert.That(result.BuildReport.Diagnostics.Any(item => item.Id == "LSQ012")).IsTrue();
        var outcome = result.AgentReadyPages.Single();
        await Assert.That(outcome.Status).IsEqualTo(PublicMarkdownStatus.Unsupported);
        await Assert.That(outcome.MarkdownPath).IsNull();
        await Assert.That(outcome.Reason).IsNotNull();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.html"))).DoesNotContain("text/markdown");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "llms.txt"))).Contains("guide.html");
    }

    [Test]
    public async Task RequiredFailureCannotBeSuppressedByMissingQualityOptions()
    {
        using var workspace = new AgentReadyWorkspace();
        Directory.CreateDirectory(Output(workspace));
        var marker = Path.Combine(Output(workspace), "keep.txt");
        await File.WriteAllTextAsync(marker, "unchanged");
        var post = Post(workspace, "<Unknown />") with { AgentReady = new() { MarkdownMode = PublicMarkdownMode.Required } };
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(Site(), [post], Output(workspace), false, Custom(), Options, default))
            .Throws<SiteQualityValidationException>();
        await Assert.That(await File.ReadAllTextAsync(marker)).IsEqualTo("unchanged");
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.html"))).IsFalse();
    }

    [Test]
    public async Task ExplicitEmptyReplacementIsCompleteAndDisabledWins()
    {
        using var workspace = new AgentReadyWorkspace();
        var post = Post(workspace, "<Unknown />") with { AgentReady = new() { MarkdownMode = PublicMarkdownMode.Required, MarkdownReplacement = string.Empty } };
        var generator = new SiteGenerator();
        await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), true, Custom(), Options, default);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.md"))).EndsWith("---\n\n");
        await generator.GenerateWithOptionsAsync(Site(), [post with { AgentReady = post.AgentReady with { MarkdownMode = PublicMarkdownMode.Disabled } }], Output(workspace), false, Custom(), Options, default);
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.md"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.html"))).DoesNotContain("text/markdown");
    }

    [Test]
    [Arguments("guide.html")]
    [Arguments("guide.md")]
    public async Task ExactSingleArtifactScopeRejectsChangedPairButPairedPathsKeepUnchangedIndex(string selected)
    {
        using var workspace = new AgentReadyWorkspace();
        var generator = new SiteGenerator();
        var post = Post(workspace);
        await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), true, Custom(), Options, default);
        var before = await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.html"));
        var llms = await File.ReadAllTextAsync(Path.Combine(Output(workspace), "llms.txt"));
        var changed = post with { MarkdownBody = "Changed body" };
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(Site(), [changed], Output(workspace), false, Custom(),
            Options with { OutputScope = [selected] }, default)).Throws<ArgumentException>();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.html"))).IsEqualTo(before);
        await generator.GenerateWithOptionsAsync(Site(), [changed], Output(workspace), false, Custom(),
            Options with { OutputScope = ["guide.html", "guide.md"] }, default);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.md"))).Contains("Changed body");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "llms.txt"))).IsEqualTo(llms);
    }

    [Test]
    public async Task ChangedIndexOutsideScopeAndGlobalOffRequireFullBuild()
    {
        using var workspace = new AgentReadyWorkspace();
        var generator = new SiteGenerator();
        var post = Post(workspace);
        await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), true, Custom(), Options, default);
        var renamed = post with { FrontMatter = post.FrontMatter with { Title = "New title" } };
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(Site(), [renamed], Output(workspace), false, Custom(),
            Options with { OutputScope = ["guide.html", "guide.md"] }, default)).Throws<ArgumentException>();
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), false, Custom(false),
            Options with { OutputScope = ["guide.html", "guide.md"] }, default)).Throws<ArgumentException>();
        await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), false, Custom(false), Options, default);
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.md"))).IsFalse();
    }

    [Test]
    public async Task CollidingPublicMarkdownIsRejectedWithoutOverwritingUserOutput()
    {
        using var workspace = new AgentReadyWorkspace();
        var publicRoot = Path.Combine(workspace.Root, "public");
        Directory.CreateDirectory(publicRoot);
        await File.WriteAllTextAsync(Path.Combine(publicRoot, "guide.md"), "manual");
        await Assert.That(async () => await new SiteGenerator().GenerateWithOptionsAsync(Site(), [Post(workspace)], Output(workspace), true, Custom(),
            Options with { PublicDirectory = publicRoot }, default)).ThrowsException();
        await Assert.That(Directory.Exists(Output(workspace))).IsFalse();
    }
    [Test]
    public async Task QualityAndMandatoryAgentValidationCanReadTheSameHtml()
    {
        using var workspace = new AgentReadyWorkspace();
        await new SiteGenerator().GenerateWithOptionsAsync(Site(), [Post(workspace)], Output(workspace), true, Custom(),
            Options with { Quality = new SiteQualityOptions() }, default);
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.md"))).IsTrue();
    }

    [Test]
    public async Task RemovedPageMustRemoveBothOldPublicArtifactsWithinScope()
    {
        using var workspace = new AgentReadyWorkspace();
        var generator = new SiteGenerator();
        await generator.GenerateWithOptionsAsync(Site(), [Post(workspace)], Output(workspace), true, Custom(), Options, default);
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(Site(), [], Output(workspace), false, Custom(),
            Options with { OutputScope = ["guide.html", "llms.txt"] }, default)).Throws<ArgumentException>();
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.html"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.md"))).IsTrue();
        await generator.GenerateWithOptionsAsync(Site(), [], Output(workspace), false, Custom(),
            Options with { OutputScope = ["guide.html", "guide.md", "llms.txt"] }, default);
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.md"))).IsFalse();
    }

    [Test]
    public async Task ActualHomeExtraAndExplicitReplacementRoutesAreResolvable()
    {
        using var workspace = new AgentReadyWorkspace();
        var custom = Custom() with
        {
            ExtraPages = [new SiteExtraPage { Title = "Extra", RelativePath = "extra.html", BodyHtml = "<p>Extra</p>",
                AgentReady = new() { MarkdownReplacement = "[Home](/product/) [self](#part)" } }],
        };
        await new SiteGenerator().GenerateWithOptionsAsync(Site(), [Post(workspace, "[Home](/product/) [extra](extra.html)")],
            Output(workspace), true, custom, Options, default);
        var body = await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.md"));
        await Assert.That(body).Contains("https://example.test/product/index.html");
        await Assert.That(body).Contains("https://example.test/product/extra.html");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "extra.md"))).Contains("https://example.test/product/extra.html#part");
    }

    [Test]
    public async Task DisabledPageDoesNotInvokeStaticPublicProviderDuringHtmlRendering()
    {
        using var workspace = new AgentReadyWorkspace();
        var component = new ContextComponent();
        var registry = new MarkdownComponentRegistry([MarkdownComponentRegistration.Create("context", component, new EmptyBinder(), "context:v1")]);
        var post = Post(workspace, ":::component name=\"context\"\n:::") with { AgentReady = new() { MarkdownMode = PublicMarkdownMode.Disabled } };
        await new SiteGenerator().GenerateWithOptionsAsync(Site(), [post], Output(workspace), true, Custom(),
            Options with { Markdown = new() { ProfileId = MarkdownOptions.DocumentationProfile, Components = registry } }, default);
        await Assert.That(component.HtmlCalls).IsEqualTo(1);
        await Assert.That(component.MarkdownCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task PublicComponentContextChangesInvalidateCachedOutcome(int dimension)
    {
        using var workspace = new AgentReadyWorkspace();
        var component = new ContextComponent();
        var registry = new MarkdownComponentRegistry([MarkdownComponentRegistration.Create("context", component, new EmptyBinder(), "context:v1")]);
        var options = Options with { Markdown = new() { ProfileId = MarkdownOptions.DocumentationProfile, Components = registry } };
        var custom = Custom();
        var post = Post(workspace, ":::component name=\"context\"\n:::");
        var generator = new SiteGenerator();
        await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), true, custom, options, default);
        var before = await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.md"));
        if (dimension == 0)
            options = options with { BuildTimestamp = Timestamp.AddDays(1) };
        else if (dimension == 1)
            custom = custom with { Text = custom.Text with { MenuLabel = "Changed menu" } };
        else
            custom = custom with { Theme = custom.Theme with { BrandPrefix = "Changed brand" } };
        await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), false, custom, options, default);
        var after = await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.md"));
        await Assert.That(after).IsNotEqualTo(before);
        await Assert.That(component.MarkdownCalls).IsEqualTo(2);
    }

    [Test]
    public async Task RepeatedQualityReadKeepsFirstHashAndRejectsChangedBytes()
    {
        using var workspace = new AgentReadyWorkspace();
        var file = Path.Combine(workspace.Root, "page.html");
        await File.WriteAllTextAsync(file, "first");
        var inspected = new Dictionary<string, LithoSharp.Build.CachedBuildArtifact>();
        await SiteGenerator.ReadQualityTextAsync(workspace.Root, "page.html", inspected, default);
        var first = inspected["page.html"];
        await SiteGenerator.ReadQualityTextAsync(workspace.Root, "page.html", inspected, default);
        await File.WriteAllTextAsync(file, "other");
        await Assert.That(async () => await SiteGenerator.ReadQualityTextAsync(workspace.Root, "page.html", inspected, default)).Throws<IOException>();
        await Assert.That(inspected["page.html"]).IsEqualTo(first);
    }

    private sealed record EmptyProps;
    private sealed class EmptyBinder : IContentFrontMatterBinder<EmptyProps>
    {
        public ContentParseResult<EmptyProps> Bind(IReadOnlyDictionary<string, object?> values, LithoSharp.Diagnostics.SiteSourceLocation? sourceLocation = null) =>
            ContentParseResult<EmptyProps>.Success(new());
    }
    private sealed class ContextComponent : ISiteComponent<EmptyProps>, IPublicMarkdownComponent<EmptyProps>
    {
        internal int HtmlCalls;
        internal int MarkdownCalls;
        private static string Value(ComponentRenderingContext context) =>
            context.BuildTimestamp.ToString("O") + " " + context.Text.MenuLabel + " " + context.Theme.BrandPrefix;
        public IHtmlContent Render(EmptyProps props, ComponentRenderingContext context)
        {
            HtmlCalls++;
            return new HtmlText(Value(context));
        }
        public string RenderPublicMarkdown(EmptyProps props, ComponentRenderingContext context)
        {
            MarkdownCalls++;
            return Value(context);
        }
    }

    [Test]
    public async Task AutoUnavailableParticipatesInWarningFailureThreshold()
    {
        using var workspace = new AgentReadyWorkspace();
        SiteQualityValidationException? observed = null;
        try
        {
            await new SiteGenerator().GenerateWithOptionsAsync(Site(), [Post(workspace, "Visible <Unknown /> body")], Output(workspace), true, Custom(),
                Options with { Quality = new SiteQualityOptions(LithoSharp.Diagnostics.SiteDiagnosticSeverity.Warning) }, default);
        }
        catch (SiteQualityValidationException exception)
        {
            observed = exception;
        }
        await Assert.That(observed?.Diagnostics.Any(item => item.Id == "LSQ012") == true).IsTrue();
        await Assert.That(Directory.Exists(Output(workspace))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OptionalJsonLdQualityFollowsQualityEnablement(bool qualityEnabled)
    {
        using var workspace = new AgentReadyWorkspace();
        var custom = Custom() with
        {
            AgentReady = new()
            {
                Enabled = true,
                StructuredData = new() { Mode = AgentReadyStructuredDataMode.UserManaged }
            }
        };
        SiteQualityValidationException? observed = null;
        try
        {
            await new SiteGenerator().GenerateWithOptionsAsync(Site(), [Post(workspace)], Output(workspace), true, custom,
                Options with { Quality = qualityEnabled ? new SiteQualityOptions() : null }, default);
        }
        catch (SiteQualityValidationException exception)
        {
            observed = exception;
        }
        await Assert.That(observed?.Diagnostics.Any(item => item.Id == "LSQ013") == true).IsEqualTo(qualityEnabled);
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.md"))).IsEqualTo(!qualityEnabled);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GeneratedThemeRouteIsAvailableOnlyWhenItsActualRenderIsEnabled(bool externalTheme)
    {
        using var workspace = new AgentReadyWorkspace();
        var custom = Custom() with { Theme = new() { EnableThemeSwitching = true, UseExternalThemeRestoration = externalTheme } };
        var result = await new SiteGenerator().GenerateWithOptionsAsync(Site(), [Post(workspace, "[Theme](assets/theme.js)")], Output(workspace), true,
            custom, Options, default);
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.md"))).IsEqualTo(externalTheme);
        await Assert.That(result.QualityReport.Diagnostics.Any(item => item.Id == "LSQ012")).IsEqualTo(!externalTheme);
    }

    [Test]
    public async Task CommonLlmsArtifactIsAnActualResolvableRoute()
    {
        using var workspace = new AgentReadyWorkspace();
        await new SiteGenerator().GenerateWithOptionsAsync(Site(), [Post(workspace, "[Index](llms.txt)")], Output(workspace), true, Custom(), Options, default);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.md"))).Contains("https://example.test/product/llms.txt");
    }


    [Test]
    [Arguments("target.md", false)]
    [Arguments("/product/target.html", true)]
    public async Task ChangedMappedProviderNeverGuessesRelativeIncludeOrigins(string destination, bool complete)
    {
        using var workspace = new AgentReadyWorkspace();
        var raw = "[Target](" + destination + ")";
        var sourcePath = Path.Combine(workspace.Root, "source.md");
        var included = new Syntamark.Compilation.MdSourceSnapshot(Path.Combine(workspace.Root, "includes", "chapter.md"), "included", raw);
        var expanded = new Syntamark.Compilation.MdSourceSnapshot(sourcePath, "expanded", raw);
        var map = new Syntamark.Compilation.MdSourceMap(expanded,
            [new(new(0, raw.Length), new(included, new(0, raw.Length)), Syntamark.Compilation.MdSegmentKind.Linear)]);
        var entry = new ContentEntry<string, string>(new("source"), "source.md", "source:v1", "Source", raw)
        {
            BodySource = new DocumentSource(sourcePath, 0) { SourceMap = map, ExactBody = raw },
        };
        var target = new ContentEntry<string, string>(new("target"), "target.md", "target:v1", "Target", "Target body");
        var collection = new ContentCollection<string, string>(new("mapped"), workspace.Root, [entry, target],
            item => LithoSharp.Routing.SiteRoute.ForFile(item.Id.Value + ".html"), item => new LithoSharp.Pages.PageMetadata(item.FrontMatter));
        var pages = new SiteContentCollection<string, string>(collection,
            (item, context) => context.RenderDocument(context.RenderMarkdown(item.Body)))
        { MarkdownProvider = (item, _) => item.Body + "\n\nAdditional public text" };
        var result = await new SiteGenerator().GenerateWithOptionsAsync(Site(), [], Output(workspace), true, Custom(),
            Options with { ContentCollections = [pages] }, default);
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "source.md"))).IsEqualTo(complete);
        await Assert.That(result.AgentReadyPages.Single(page => page.HtmlPath == "source.html").Status)
            .IsEqualTo(complete ? PublicMarkdownStatus.Automatic : PublicMarkdownStatus.Unsupported);
    }


    [Test]
    public async Task RootRobotsIsAFormalArtifactAndResolvablePublicTarget()
    {
        using var workspace = new AgentReadyWorkspace();
        var custom = Custom() with { Template = new BlogSiteTemplate(), AgentReady = Custom().AgentReady with { Robots = new() { Mode = AgentReadyRobotsMode.Generated } } };
        var result = await new SiteGenerator().GenerateWithOptionsAsync(Site("https://example.test/"), [Post(workspace, "[Robots](/robots.txt)")],
            Output(workspace), true, custom, Options, default);
        var robots = await File.ReadAllTextAsync(Path.Combine(Output(workspace), "robots.txt"));
        await Assert.That(robots).Contains("Sitemap: https://example.test/sitemap.xml");
        await Assert.That(robots).DoesNotContain("User-agent:");
        await Assert.That(result.BuildPlan.Artifacts.Single(artifact => artifact.RelativeOutputPath == "robots.txt").OwnerNodeId.Value).IsEqualTo("agent-ready:robots");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "guide.md"))).Contains("https://example.test/robots.txt");
    }


    [Test]
    public async Task CancellationPreservesPublishedPairIndexAndUserFiles()
    {
        using var workspace = new AgentReadyWorkspace();
        var generator = new SiteGenerator();
        var post = Post(workspace);
        await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), true, Custom(), Options, default);
        var files = new[] { "guide.html", "guide.md", "llms.txt", ".lithosharp-output-manifest.json" };
        var before = files.ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(Output(workspace), path)));
        var manual = Path.Combine(Output(workspace), "manual.md");
        await File.WriteAllTextAsync(manual, "user-owned");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await generator.GenerateWithOptionsAsync(Site(), [post with { MarkdownBody = "Changed" }],
            Output(workspace), false, Custom(), Options, cancellation.Token)).Throws<OperationCanceledException>();
        foreach (var entry in before)
            await Assert.That((await File.ReadAllBytesAsync(Path.Combine(Output(workspace), entry.Key))).SequenceEqual(entry.Value)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(manual)).IsEqualTo("user-owned");
        await generator.GenerateWithOptionsAsync(Site(), [], Output(workspace), false, Custom(), Options, default);
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.html"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.md"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(manual)).IsEqualTo("user-owned");
    }

    [Test]
    public async Task RouteMoveRemovesOldPairAndUpdatesActualDiscovery()
    {
        using var workspace = new AgentReadyWorkspace();
        var generator = new SiteGenerator();
        var post = Post(workspace);
        await generator.GenerateWithOptionsAsync(Site(), [post], Output(workspace), true, Custom(), Options, default);
        var moved = post with { RelativeOutputPath = "moved/page.html" };
        await generator.GenerateWithOptionsAsync(Site(), [moved], Output(workspace), false, Custom(), Options, default);
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.html"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "guide.md"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(Output(workspace), "moved/page.md"))).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(Output(workspace), "moved/page.html"))).Contains("https://example.test/product/moved/page.md");
        var llms = await File.ReadAllTextAsync(Path.Combine(Output(workspace), "llms.txt"));
        await Assert.That(llms).Contains("https://example.test/product/moved/page.md");
        await Assert.That(llms).DoesNotContain("https://example.test/product/guide.md");
    }
    // Generation owns private transaction directories. Keep integration outputs in the normal
    // temporary workspace, independent of a source checkout's inherited ACL shape.
    private sealed class AgentReadyWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : AppContext.BaseDirectory, "lithosharp-agent-ready-" + Guid.NewGuid().ToString("N"));
        public AgentReadyWorkspace() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
