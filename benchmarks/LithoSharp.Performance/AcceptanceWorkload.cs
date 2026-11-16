#if MAJOR_ACCEPTANCE_BENCHMARK
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Images;
using LithoSharp.Inspection;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;
using Syntamark;

namespace LithoSharp.Performance;

// New contracts are a candidate-only cost baseline, never a ratio against another rendering path.
internal static class AcceptanceWorkload
{
    private sealed record Sample(string Name, int Iteration, double ElapsedMilliseconds, long AllocatedBytes,
        long ManagedBytesBefore, long ManagedBytesAfterCollection, long WorkingSetBefore, long WorkingSetAfter,
        long SampledPeakWorkingSetBytes, string InputSha256, object Counters);
    private static readonly List<Sample> Samples = [];

    internal static async Task RunAsync(BenchmarkArguments arguments, string[] rawArguments)
    {
        var count = arguments.PageCount ?? 100;
        if (count is not (100 or 1000 or 10000)) throw new ArgumentException("Unsupported corpus size.");
        var output = Path.GetFullPath(arguments.OutputPath ?? ".local/performance/acceptance.json");
        if (File.Exists(output)) throw new IOException("Existing acceptance results are never overwritten.");
        var root = Path.Combine(Path.GetDirectoryName(output)!, "inputs");
        if (Directory.Exists(root)) throw new IOException("Use a fresh acceptance result directory.");
        Directory.CreateDirectory(root);
        for (var iteration = -arguments.Warmup; iteration < arguments.Iterations; iteration++)
        {
            var iterationRoot = Path.Combine(root, "iteration-" + iteration);
            Directory.CreateDirectory(iterationRoot);
            switch (arguments.AcceptanceFamily)
            {
                case "editor": await Editor(arguments, count, iteration); break;
                case "yaml": await Yaml(arguments, count, iteration); break;
                case "images": await Images(iterationRoot, iteration); break;
                case "site": await Site(iterationRoot, count, iteration); break;
                case "factory": await Factory(arguments, iterationRoot, iteration); break;
            }
        }
        var evidence = new
        {
            schemaVersion = 1, status = "completed", acceptance = "not-evaluated", comparison = "candidate-only-feature-cost",
            family = arguments.AcceptanceFamily, count, arguments.Warmup, arguments.Iterations,
            identity = BenchmarkIdentity.Capture("acceptance-" + arguments.AcceptanceFamily),
            images = BenchmarkIdentity.AssemblyIdentity(typeof(PngCodec).Assembly),
            provenance = ProvenanceMetadata.Create(rawArguments, arguments.Warmup, arguments.Iterations, root),
            scope = "Raw operation intervals; fixture construction, forced collection and result serialization are excluded. Process-wide allocations include concurrent instrumentation. Post-collection managed bytes are retained heap observations, not allocation or peak heap. Editor timings end at DocumentWorkspace completion, excluding LSP transport and VS Code.",
            cancellation = "Cancellation delay schedules a request, not an acceptance threshold. completed-before-request and completed-without-cancellation are not evidence of observed cancellation.",
            samples = Samples,
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static async Task Measure(string name, int iteration, string input, Func<Task<object>> operation)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var retainedBefore = GC.GetTotalMemory(true);
        using var process = Process.GetCurrentProcess();
        process.Refresh(); var rssBefore = process.WorkingSet64;
        using var monitor = new WorkingSetMonitor(process, TimeSpan.FromMilliseconds(10));
        var allocated = GC.GetTotalAllocatedBytes(true);
        monitor.Start(); var clock = Stopwatch.StartNew();
        object counters;
        try { counters = await operation(); }
        finally { clock.Stop(); await monitor.StopAsync(); }
        var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        process.Refresh(); var rssAfter = process.WorkingSet64;
        var retainedAfter = GC.GetTotalMemory(true);
        if (iteration >= 0)
        {
            var sample = new Sample(name, iteration, clock.Elapsed.TotalMilliseconds, bytes, retainedBefore, retainedAfter,
                rssBefore, rssAfter, Math.Max(monitor.PeakWorkingSetBytes, Math.Max(rssBefore, rssAfter)), Hash(input), counters);
            Samples.Add(sample);
            Console.WriteLine(JsonSerializer.Serialize(sample));
        }
    }

    private static string Hash(string input) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    private static object EditorCounters(DocumentInfo info, DocumentInfo? previous = null) => new
    {
        reusedSnapshot = ReferenceEquals(info, previous),
        analysis = ReferenceEquals(info, previous) ? null : info.IncrementalAnalysis,
        status = info.MarkdownSyntax?.Status.ToString(), diagnostics = info.Diagnostics.Count,
        headings = info.Headings.Count, links = info.Links.Count,
    };

    private static string InspectionValues(DocumentInfo info) => JsonSerializer.Serialize(new
    { info.Headings, info.Links, info.Diagnostics, info.FrontMatter, info.SchemaInspectionScope });

    private static async Task Editor(BenchmarkArguments arguments, int count, int iteration)
    {
        const string path = "editor.md";
        var body = string.Concat(Enumerable.Range(0, count).Select(index => $"## Section {index}\n\nParagraph {index} 😀 [shared][ref].\n\n"));
        var original = "---\ntitle: Editor\n---\n" + body + "[ref]: target.md\n";
        await using var workspace = new DocumentWorkspace();
        long version = 1;
        DocumentInfo? current = null;
        await Measure("editor-first", iteration, original, async () => EditorCounters(current = await workspace.InspectVersionedAsync(path, original, version, 1)));
        await Measure("editor-no-change", iteration, original, async () => EditorCounters(await workspace.InspectVersionedAsync(path, original, version, 1), current));
        await Measure("editor-warm", iteration, original, async () => EditorCounters(current = await workspace.InspectVersionedAsync(path, original, ++version, 1)));
        var text = original;
        foreach (var (name, updated) in new[]
        {
            ("editor-body", original.Replace("Paragraph 0", "Changed paragraph 0", StringComparison.Ordinal)),
            ("editor-heading", original.Replace("## Section 0", "## Revised section", StringComparison.Ordinal)),
            ("editor-reference-definition", original.Replace("[ref]: target.md", "[ref]: other.md", StringComparison.Ordinal)),
            ("editor-large-paste", original + string.Concat(Enumerable.Repeat("Pasted paragraph 😀.\n\n", count))),
        })
        {
            var change = Difference(text, updated);
            await Measure(name, iteration, updated, async () => EditorCounters(current = await workspace.InspectVersionedEditsAsync(path,
                updated, ++version, 1, new DocumentChangeSet(version - 1, text, [change]))));
            if (InspectionValues(current!) != InspectionValues(DocumentInspection.Inspect(path, updated)))
                throw new InvalidOperationException("Incremental/fresh inspection differs: " + name);
            text = updated;
        }
        workspace.Remove(path);
        current = null;
        await Measure("editor-after-close", iteration, text, () => Task.FromResult<object>(new
        { publishedSnapshot = workspace.TryGet(path, out _), retainedScope = "after removal; caller corpus strings and workspace remain alive, DocumentInfo released" }));
        foreach (var (name, raw) in new[]
        {
            ("cancel-unclosed", "---\ntitle: Large\n---\n```\n" + new string('x', 4 * 1024 * 1024)),
            ("cancel-deep", "---\ntitle: Deep\n---\n" + string.Concat(Enumerable.Repeat(new string('>', 512) + " text\n", 2048))),
        })
        {
            await using var canceledWorkspace = new DocumentWorkspace();
            await Measure(name, iteration, raw, async () =>
            {
                using var cancellation = new CancellationTokenSource();
                var task = canceledWorkspace.InspectVersionedAsync(path, raw, 1, 1, cancellationToken: cancellation.Token);
                await Task.Delay(arguments.CancellationDelayMilliseconds);
                if (task.IsCompleted)
                {
                    await task;
                    return new { outcome = "completed-before-request", cancellationObserved = false, cancellationMilliseconds = (double?)null };
                }
                var requested = Stopwatch.GetTimestamp();
                cancellation.Cancel();
                var observed = false;
                try { await task; } catch (OperationCanceledException) { observed = true; }
                var latency = Stopwatch.GetElapsedTime(requested).TotalMilliseconds;
                var published = canceledWorkspace.TryGet(path, out _);
                if (observed && published) throw new InvalidOperationException("Canceled inspection published a snapshot.");
                return new { outcome = observed ? "canceled" : "completed-without-cancellation", cancellationObserved = observed,
                    cancellationMilliseconds = (double?)latency, publishedSnapshot = published };
            });
        }
    }

    private static DocumentTextChange Difference(string before, string after)
    {
        var start = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start]) start++;
        var suffix = 0;
        while (suffix < before.Length - start && suffix < after.Length - start && before[before.Length - suffix - 1] == after[after.Length - suffix - 1]) suffix++;
        return new(start, before.Length - start - suffix, after.Substring(start, after.Length - start - suffix));
    }

    private static async Task Yaml(BenchmarkArguments arguments, int count, int iteration)
    {
        const string small = "title: Page\ntags: [one, two]\nverified: {by: human:reviewer, at: 2026-11-13T00:00:00Z}\n";
        foreach (var (name, raw, repeats, complete) in new[]
        {
            ("yaml-small-many", small, count, true),
            ("yaml-long-scalar", "text: |\n  " + new string('x', count * 64) + "\n", 1, true),
            ("yaml-deep", "nested: " + new string('[', 256) + "null" + new string(']', 256), 1, false),
            ("yaml-unknown-metadata", "unknown: {nested: [true, null, 99999999999999999999999999999, {future: value}]}\n", count, true),
        })
            await Measure(name, iteration, raw, () =>
            {
                YamlDocument? result = null;
                for (var index = 0; index < repeats; index++) result = YamlParser.Parse(raw);
                if (complete && result!.Status != MarkdownParseStatus.Complete) throw new InvalidOperationException("YAML fixture was incomplete.");
                return Task.FromResult<object>(new { repeats, status = result!.Status.ToString(), result.ProfileId, result.OptionsHash,
                    diagnostics = result.Diagnostics.Select(item => new { kind = item.Kind.ToString(), item.Reason, item.RawSpan }).ToArray() });
            });
        const string typed = "title: Page\ndate: 2026-01-01T00:00:00Z\nsummary: Typed metadata\ntags: [one, two]\n";
        await Measure("yaml-typed-bind-many", iteration, typed, () =>
        {
            for (var index = 0; index < count; index++)
                if (MarkdownFrontMatterYaml.Deserialize(typed)?.Title != "Page") throw new InvalidOperationException("Typed binding changed.");
            return Task.FromResult<object>(new { repeats = count, scope = "native YAML and legacy PostFrontMatter projection" });
        });
        if (arguments.GeneratorAssembly is null) throw new ArgumentException("YAML acceptance requires --generator-assembly for the actual embedded Source consumer.");
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(arguments.GeneratorAssembly));
        var parser = assembly.GetType("Syntamark.Compilation.MdYamlParser", true)!;
        var parse = parser.GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(method => method.Name == "Parse"
            && method.GetParameters().Length == 3 && method.GetParameters()[0].ParameterType == typeof(string));
        var sourceFixture = parse.Invoke(null, [small, null, CancellationToken.None])!;
        if (YamlContract(sourceFixture) != YamlContract(YamlParser.Parse(small)))
            throw new InvalidOperationException("Runtime and embedded Source YAML fixture values, status, diagnostics or spans differ.");
        await Measure("yaml-source-small-many", iteration, small, () =>
        {
            object? result = null;
            for (var index = 0; index < count; index++) result = parse.Invoke(null, [small, null, CancellationToken.None]);
            var status = result!.GetType().GetProperty("Status", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(result)?.ToString();
            if (status != "Complete") throw new InvalidOperationException("Source YAML fixture was incomplete.");
            return Task.FromResult<object>(new { repeats = count, status, source = BenchmarkIdentity.AssemblyIdentity(assembly),
                sourceRuntimeFixtureParity = "matched-values-status-diagnostics-spans",
                scope = "actual embedded Source YAML parse including reflection invocation; not full Generator or C# compilation" });
        });
    }

    private static object? Property(object value, string name) => value.GetType().GetProperty(name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value)
        ?? value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
    private static object? YamlNodeContract(object? node) => node is null ? null : new
    {
        kind = Property(node, "Kind")?.ToString(), scalarKind = Property(node, "ScalarKind")?.ToString(), value = Property(node, "Value"),
        span = RangeContract(Property(node, "RawSpan")),
        children = ((System.Collections.IEnumerable?)Property(node, "Children"))?.Cast<object>().Select(YamlNodeContract).ToArray(),
        entries = ((System.Collections.IEnumerable?)Property(node, "Entries"))?.Cast<object>().Select(entry => new
        { key = YamlNodeContract(Property(entry, "Key")), value = YamlNodeContract(Property(entry, "Value")) }).ToArray(),
    };
    private static object? RangeContract(object? span) => span is null ? null : new { start = Property(span, "Start"), length = Property(span, "Length") };
    private static string YamlContract(object document) => JsonSerializer.Serialize(new
    {
        status = Property(document, "Status")?.ToString(), profile = Property(document, "ProfileId"),
        root = YamlNodeContract(Property(document, "Root")),
        diagnostics = ((System.Collections.IEnumerable?)Property(document, "Diagnostics"))?.Cast<object>().Select(item => new
        { kind = Property(item, "Kind")?.ToString(), reason = Property(item, "Reason"), message = Property(item, "Message"), span = RangeContract(Property(item, "RawSpan")) }).ToArray(),
    });

    private static async Task Images(string root, int iteration)
    {
        var canvas = RgbaImage.Create(1920, 1080, 40, 80, 120);
        var logo = RgbaImage.Create(512, 256, 255, 32, 16, 128);
        var png = PngCodec.Encode(canvas);
        var identity = Convert.ToHexStringLower(SHA256.HashData(png));
        await Measure("png-decode", iteration, identity, () =>
        {
            var decoded = PngCodec.Decode(png);
            if (decoded.Width != 1920 || decoded.Height != 1080) throw new InvalidOperationException("PNG dimensions changed.");
            return Task.FromResult<object>(new { inputBytes = png.Length, decoded.Width, decoded.Height, profile = PngCodec.Profile });
        });
        await Measure("png-encode", iteration, identity, () => Task.FromResult<object>(new { outputBytes = PngCodec.Encode(canvas).Length, profile = PngCodec.Profile }));
        await Measure("image-resize", iteration, identity, () =>
        {
            var resized = canvas.Resize(640);
            if (resized.Height != 360) throw new InvalidOperationException("Resize aspect ratio changed.");
            return Task.FromResult<object>(new { resized.Width, resized.Height, profile = RgbaImage.ProcessingProfile });
        });
        await Measure("image-composite", iteration, identity, () =>
        {
            var target = new RgbaImage(canvas.Width, canvas.Height, canvas.Pixels.Span);
            target.Composite(logo, 128, 128);
            return Task.FromResult<object>(new { target.Width, target.Height, profile = RgbaImage.ProcessingProfile, scope = "canvas copy and source-over composition" });
        });
        await File.WriteAllBytesAsync(Path.Combine(root, "source.png"), png);
        await File.WriteAllTextAsync(Path.Combine(root, "opaque.webp"), "opaque pass-through fixture");
        var source = new SiteAsset("source", root, "source.png", "images/original.png");
        var copy = new SiteAsset("copy", root, "opaque.webp", "images/copied.webp");
        var variant = new ImageVariant("small", "images/small.png", 640);
        var image = new ImageAsset(source, [variant], variant);
        var calls = 0;
        var transform = new SiteAssetTransform(image.Transform.Id, image.Transform.ImplementationFingerprint,
            image.Transform.Inputs, image.Transform.Outputs, async (context, token) =>
            { calls++; await image.Transform.Handler(context, token); });
        foreach (var name in new[] { "image-site-none", "image-site-copy", "image-site-png-miss", "image-site-png-hit" })
        {
            var options = new SiteGenerationOptions
            {
                Assets = name == "image-site-none" ? [] : name == "image-site-copy" ? [copy] : [source],
                AssetTransforms = name.StartsWith("image-site-png", StringComparison.Ordinal) ? [transform] : [],
                AssetCacheDirectory = Path.Combine(root, "asset-cache"), BuildCacheDirectory = Path.Combine(root, "build-cache"),
                PublicDirectory = Path.Combine(root, "public"), BuildTimestamp = CorpusGenerator.BuildTimestamp, CollectTimings = true,
            };
            Directory.CreateDirectory(options.PublicDirectory);
            var before = calls;
            await Measure(name, iteration, identity, async () =>
            {
                var result = await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.test/sub/" }, [],
                    Path.Combine(root, "output"), false, new SiteCustomization { GenerateLlmsTxt = false,
                        Template = new AssetTemplate(name.StartsWith("image-site-png", StringComparison.Ordinal) ? image.Descriptor : null) }, options, default);
                var expected = name == "image-site-png-miss" ? 1 : 0;
                if (calls - before != expected) throw new InvalidOperationException("Asset transform cache state differs from its measured scenario.");
                return new { transformCalls = calls - before, result.BuildReport.CacheHitCount, result.BuildReport.CacheMissCount,
                    outputBytes = result.GeneratedFiles.Sum(file => new FileInfo(file).Length), result.Timings };
            });
        }
    }

    private sealed class AssetTemplate(ImageDescriptor? image) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default) => Task.FromResult(
            new SiteTemplateResult([new SiteTemplateFile { RelativePath = "index.html", Content = image is null ? "No decoding requested"
                : ResponsiveImage.Render(context.Assets, image, "Measured PNG").ToHtmlString() }]));
    }

    private static async Task Site(string root, int count, int iteration)
    {
        var corpus = Path.Combine(root, "content"); Directory.CreateDirectory(corpus);
        for (var index = 0; index < count; index++)
            await File.WriteAllTextAsync(Path.Combine(corpus, $"page-{index:D5}.md"),
                $"---\ntitle: Page {index}\nsummary: Present summary\n---\n# Page {index}\n\nBody.\n\n:::component name=\"badge\" text=\"Shared\"\n:::\n");
        var schemaRevision = 1;
        var componentRevision = 1;
        SiteBuildPlan? previous = null;
        var customization = new SiteCustomization { Template = new DocsSiteTemplate(), FaviconSourceDirectory = Path.Combine(root, "none") };
        foreach (var name in new[] { "site-first", "site-warm-cache", "site-no-change", "site-body", "site-layout", "site-shared-schema", "site-shared-component" })
        {
            if (name == "site-warm-cache") previous = null;
            if (name == "site-body") await File.AppendAllTextAsync(Path.Combine(corpus, "page-00000.md"), "\nChanged body.\n");
            if (name == "site-layout") customization = customization with { Theme = new SiteThemeOptions { ThemeColor = "#7c3aed" } };
            if (name == "site-shared-schema") schemaRevision++;
            if (name == "site-shared-component") componentRevision++;
            var corpusIdentity = Hash(string.Join('\n', Directory.EnumerateFiles(corpus).Order(StringComparer.Ordinal)
                .Select(file => Path.GetRelativePath(corpus, file) + ":" + BenchmarkIdentity.Hash(file))));
            var fingerprint = "schema-" + schemaRevision + ";component-" + componentRevision + ";corpus-" + corpusIdentity;
            await Measure(name, iteration, fingerprint + ":" + count, async () =>
            {
                var load = Stopwatch.StartNew();
                var binder = new FrontBinder(schemaRevision);
                var loaded = await new MarkdownContentCollectionLoader<Front>(new("acceptance-docs"), corpus,
                    entry => SiteRoute.ForFile(entry.Id.Value.Replace(".md", ".html", StringComparison.Ordinal)),
                    entry => new PageMetadata(entry.FrontMatter.Title), binder,
                    declaredDependencies: [ContentDependency.FromValue("schema-contract", binder.SchemaJson)],
                    transformationId: new("acceptance-docs/1"), isCacheable: true).LoadAsync();
                load.Stop();
                if (!loaded.IsSuccess || loaded.Collection!.Entries.Count != count) throw new InvalidOperationException("Acceptance Docs input failed.");
                var registry = new MarkdownComponentRegistry([MarkdownComponentRegistration.Create("badge", new Badge(componentRevision),
                    new ReflectionContentFrontMatterBinder<BadgeProps>(), "badge/" + componentRevision)]);
                var options = new SiteGenerationOptions
                {
                    Markdown = new() { ProfileId = MarkdownOptions.DocumentationProfile, Components = registry },
                    BuildTimestamp = CorpusGenerator.BuildTimestamp, PreviousBuildPlan = previous, CollectTimings = true,
                    Quality = new SiteQualityOptions(),
                    ContentCollections = [loaded.Collection.RenderDocs(entry => new DocsPagePresentation { Title = entry.FrontMatter.Title },
                        (entry, context) => context.RenderMarkdown(entry.Body), "acceptance-docs/1", true)],
                };
                var generate = Stopwatch.StartNew();
                var generator = new SiteGenerator(new LithoMarkdownCompiler(
                    MarkdownCompilerOptions.Default with { Markdown = options.Markdown }));
                var result = await generator.GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.test/" }, [],
                    Path.Combine(root, "output"), false, customization, options, default);
                generate.Stop(); previous = result.BuildPlan;
                if (name == "site-no-change" && result.BuildReport.CacheMissCount != 0) throw new InvalidOperationException("No-change cache reuse failed.");
                if (name is "site-shared-schema" or "site-shared-component" && result.BuildReport.CacheMissCount == 0)
                    throw new InvalidOperationException("Changed shared contract did not invalidate any output.");
                if (name == "site-shared-component" && !(await File.ReadAllTextAsync(Path.Combine(root, "output", "page-00000.html"))).Contains("badge-2", StringComparison.Ordinal))
                    throw new InvalidOperationException("Changed component did not update rendered HTML.");
                return new { inputLoadMilliseconds = load.Elapsed.TotalMilliseconds, generationMilliseconds = generate.Elapsed.TotalMilliseconds,
                    scope = "input loading, runtime binding and generation; not a Roslyn Generator measurement", schemaRevision, componentRevision,
                    qualityEnabled = options.Quality is not null,
                    qualityScope = "candidate Quality with admitted Markdown BodySource and automatic raw origin retention; feature cost, not decoded-only baseline",
                    markdownParseCount = generator.MarkdownCompiler.ParseCount,
                    markdownParseCountScope = "SiteGenerator compiler only; input loader parsing is excluded",
                    htmlFactsSharing = new { result.HtmlFactsSharing.ParseCount, result.HtmlFactsSharing.ReuseCount,
                        result.HtmlFactsSharing.RetainedBytes, result.HtmlFactsSharing.EntryCount,
                        retentionScope = "logical retained-facts charge at build completion; not managed heap" },
                    quality = new { result.QualityReport.HtmlParseCount, result.QualityReport.HtmlFactsCacheHitCount },
                    result.BuildReport.CacheHitCount, result.BuildReport.CacheMissCount, invalidatedNodes = result.BuildReport.Invalidations.Count,
                    artifactCount = result.GeneratedFiles.Count, result.Timings };
            });
        }
    }

    private static async Task Factory(BenchmarkArguments arguments, string root, int iteration)
    {
        if (arguments.FactoryAssembly is null || arguments.FactoryDirectory is null)
            throw new ArgumentException("Factory acceptance requires explicit --factory-assembly and --factory-directory.");
        var path = Path.GetFullPath(arguments.FactoryAssembly);
        var directory = Path.GetFullPath(arguments.FactoryDirectory);
        var resolver = new AssemblyDependencyResolver(path);
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var dependency = resolver.ResolveAssemblyToPath(name);
            return dependency is null ? null : context.LoadFromAssemblyPath(dependency);
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        SiteDefinition? definition = null;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            var type = assembly.GetExportedTypes().Single(candidate => candidate is { IsClass: true, IsAbstract: false }
                && typeof(ISiteFactory).IsAssignableFrom(candidate) && candidate.GetConstructor(Type.EmptyTypes) is not null);
            var factory = (ISiteFactory)Activator.CreateInstance(type)!;
            // The official site's existing hook keeps generated API inputs in the owned measurement directory.
            var generated = type.GetProperty("GeneratedInputDirectory", BindingFlags.Instance | BindingFlags.Public);
            if (generated?.PropertyType != typeof(string) || generated.SetMethod is null)
                throw new ArgumentException("This bounded official-site workload requires the factory's GeneratedInputDirectory hook.");
            generated.SetValue(factory, Path.Combine(root, "generated-inputs"));
            await Measure("factory-create", iteration, BenchmarkIdentity.Hash(path)!, async () =>
            {
                definition = await factory.CreateAsync(new SiteFactoryContext(directory));
                return new { factory = BenchmarkIdentity.AssemblyIdentity(assembly), extensions = definition.Options.Extensions.Count,
                    definition.Options.BuildTimestamp, qualityConfigured = definition.Options.Quality is not null,
                    retainedContext = "created site definition and extension declarations remain live" };
            });
            SiteBuildPlan? previous = null;
            foreach (var name in new[] { "factory-site-first", "factory-site-warm-cache", "factory-site-no-change" })
            {
                if (name == "factory-site-warm-cache") previous = null;
                await Measure(name, iteration, BenchmarkIdentity.Hash(path)!, async () =>
                {
                    var options = definition!.Options with
                    {
                        BuildCacheDirectory = Path.Combine(root, "build-cache"), AssetCacheDirectory = Path.Combine(root, "asset-cache"),
                        BuildTimestamp = definition.Options.BuildTimestamp ?? CorpusGenerator.BuildTimestamp,
                        PreviousBuildPlan = previous, CollectTimings = true,
                    };
                    var result = await new SiteGenerator().GenerateWithOptionsAsync(definition.Site, definition.Posts,
                        Path.Combine(root, "site"), false, definition.Customization, options, default);
                    previous = result.BuildPlan;
                    return new { pages = result.Routes.Count, artifactCount = result.GeneratedFiles.Count, result.Timings,
                        result.BuildReport.CacheHitCount, result.BuildReport.CacheMissCount, options.BuildTimestamp,
                        qualityConfigured = options.Quality is not null,
                        qualityScope = "candidate automatic raw Quality facts for admitted Markdown sources; all generated API pages share this build context",
                        retainedContext = "full definition, extension catalogs and previous build plan remain live" };
                });
            }
        }
        finally
        {
            if (definition is not null)
                foreach (var extension in definition.Options.Extensions.Reverse().Distinct())
                    if (extension is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                    else if (extension is IDisposable disposable) disposable.Dispose();
            definition = null;
            AssemblyLoadContext.Default.Resolving -= Resolve;
        }
        await Measure("factory-after-dispose", iteration, BenchmarkIdentity.Hash(path)!, () => Task.FromResult<object>(new
        { retainedContext = "factory extensions disposed and site definition released; default assembly load context and runtime remain live" }));
    }

    public sealed class Front { public required string Title { get; init; } public string? Summary { get; init; } }
    public sealed class BadgeProps { public required string Text { get; init; } }
    private sealed class Badge(int revision) : ISiteComponent<BadgeProps>
    { public IHtmlContent Render(BadgeProps props, ComponentRenderingContext context) => Html.UnsafeRaw("<span class=\"badge-" + revision + "\">" + Html.Encode(props.Text) + "</span>"); }
    private sealed class FrontBinder(int revision) : IContentFrontMatterBinder<Front>, IContentFrontMatterSchemaProvider
    {
        private readonly ReflectionContentFrontMatterBinder<Front> binder = new();
        public string SchemaJson => "{\"x-lithosharp-schemaVersion\":\"1.0\",\"$ref\":\"#/$defs/Front\",\"$defs\":{\"Front\":{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"summary\":{\"type\":\"string\"}},\"required\":[\"title\"" + (revision > 1 ? ",\"summary\"" : "") + "]}}}";
        public ContentParseResult<Front> Bind(IReadOnlyDictionary<string, object?> values, SiteSourceLocation? sourceLocation = null)
        {
            var result = binder.Bind(values, sourceLocation);
            return revision > 1 && result.IsSuccess && string.IsNullOrEmpty(result.Value!.Summary)
                ? ContentParseResult<Front>.Failure([new(ContentFrontMatterDiagnosticIds.MissingRequiredField, SiteDiagnosticSeverity.Error, "Summary is required.", sourceLocation)]) : result;
        }
    }
}
#endif
