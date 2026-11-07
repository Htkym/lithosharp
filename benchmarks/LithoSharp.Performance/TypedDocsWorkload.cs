#if TYPED_DOCS_BENCHMARK
using System.Diagnostics;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Performance;

// Candidate-only: the adapter does not exist in 2.1.3. Keep its baseline
// standalone rather than comparing it to a different legacy/Blog pipeline.
internal static class TypedDocsWorkload
{
    internal static async Task RunAsync(BenchmarkArguments arguments, string[] rawArguments)
    {
        if (arguments.Mode != "site") throw new ArgumentException("The typed Docs profile measures site mode only.");
        var size = arguments.PageCount ?? 100;
        if (size is not (100 or 1000 or 10000)) throw new ArgumentException("Unsupported page count.");
        var output = Path.GetFullPath(arguments.OutputPath ?? ".local/performance/typed-docs/result.json");
        var root = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(root);
        var corpus = Path.Combine(root, $"corpus-{size}");
        var siteOutput = Path.Combine(root, $"site-{size}");
        if (Directory.Exists(corpus) || Directory.Exists(siteOutput)) throw new IOException("Use a new result directory.");
        Directory.CreateDirectory(corpus);
        for (var index = 1; index <= size; index++)
        {
            var folder = Path.Combine(corpus, $"section-{(index - 1) / 100:D3}");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, $"page-{index:D5}.md"),
                $"---\ntitle: Benchmark page {index:D5}\nsummary: Deterministic benchmark page {index:D5}.\n---\n\n# Benchmark page {index:D5}\n\nThis is deterministic Markdown content.\n\n- Stable list item A\n- Stable list item B\n");
        }
        var site = new SiteSettings { Title = "Typed Docs benchmark", Description = "Deterministic typed Docs and Quality.", BaseUrl = "https://example.test/", Language = "en", TimeZone = "UTC" };
        var customization = new SiteCustomization { Template = new DocsSiteTemplate(), GenerateLlmsTxt = true };
        var workloads = new List<object>();
        SiteBuildPlan? previous = null;
        async Task Measure(string name, bool clean = false)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var process = Process.GetCurrentProcess();
            process.Refresh(); var startRss = process.WorkingSet64;
            using var monitor = new WorkingSetMonitor(process, TimeSpan.FromMilliseconds(10));
            var allocated = GC.GetTotalAllocatedBytes(true);
            var total = Stopwatch.StartNew(); monitor.Start();
            var read = Stopwatch.StartNew();
            var loaded = await new MarkdownContentCollectionLoader<Front>(new ContentCollectionId("benchmark-docs"), corpus,
                entry => SiteRoute.ForFile("guide/" + Path.ChangeExtension(entry.Id.Value, ".html").Replace('\\', '/')),
                entry => new PageMetadata(entry.FrontMatter.Title, entry.FrontMatter.Summary),
                transformationId: new ContentTransformationId("benchmark-docs:v1"), isCacheable: true).LoadAsync();
            read.Stop();
            if (!loaded.IsSuccess || loaded.Collection!.Entries.Count != size)
                throw new InvalidOperationException("Typed corpus load failed: " + string.Join("; ", loaded.Diagnostics.Select(item => $"{item.Id}: {item.Message}")));
            var rendering = loaded.Collection.RenderDocs(entry => new DocsPagePresentation
                { Title = entry.FrontMatter.Title, Description = entry.FrontMatter.Summary },
                (entry, context) => context.RenderMarkdown(entry.Body), rendererFingerprint: "benchmark-docs:v1", isThreadSafe: true);
            var options = new SiteGenerationOptions { BuildTimestamp = CorpusGenerator.BuildTimestamp, CollectTimings = true,
                PreviousBuildPlan = previous, Quality = new SiteQualityOptions(), ContentCollections = [rendering] };
            var generate = Stopwatch.StartNew();
            SiteGenerationResult result;
            try { result = await new SiteGenerator().GenerateWithOptionsAsync(site, [], siteOutput, clean, customization, options, default); }
            finally { generate.Stop(); total.Stop(); await monitor.StopAsync(); }
            var bytes = GC.GetTotalAllocatedBytes(true) - allocated; process.Refresh(); var endRss = process.WorkingSet64;
            if (name == "no-op" && (result.BuildReport.CacheMissCount != 0 || result.BuildReport.Invalidations.Count != 0))
                throw new InvalidOperationException("Unchanged typed Docs must reuse cacheable outputs.");
            previous = result.BuildPlan;
            workloads.Add(new { Name = name, ElapsedMilliseconds = generate.Elapsed.TotalMilliseconds,
                ReadAndGenerateElapsedMilliseconds = total.Elapsed.TotalMilliseconds, InputLoadMilliseconds = read.Elapsed.TotalMilliseconds,
                AllocatedBytes = bytes, AllocationScope = "input-load-and-generation", StartWorkingSetBytes = startRss, EndWorkingSetBytes = endRss,
                PeakWorkingSetBytes = Math.Max(monitor.PeakWorkingSetBytes, Math.Max(startRss, endRss)), WorkingSetScope = "input-load-and-generation-sampled-10ms",
                GeneratedNodeCount = result.BuildReport.CacheMissCount, InvalidatedNodeCount = result.BuildReport.Invalidations.Count,
                ArtifactCount = result.GeneratedFiles.Count, Timings = result.Timings });
        }
        await Measure("clean", true);
        foreach (var name in new[] { "no-op", "single-page-change", "layout-change" })
        {
            if (arguments.Scenario != "all" && arguments.Scenario != name) continue;
            if (name == "single-page-change") await File.AppendAllTextAsync(Path.Combine(corpus, "section-000/page-00001.md"), "\nChanged benchmark body.\n");
            if (name == "layout-change") customization = customization with { Theme = new SiteThemeOptions { ThemeColor = "#7c3aed" } };
            await Measure(name);
        }
        if (arguments.Scenario != "all") workloads = workloads.Where(work => (string)work.GetType().GetProperty("Name")!.GetValue(work)! == arguments.Scenario).ToList();
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { SchemaVersion = 4, Benchmark = "typed-docs-quality",
            Comparison = "standalone-new-feature-baseline", Status = "completed", Profile = new { Template = "Docs", Quality = "Error threshold, orphan checks enabled, external HTTP disabled", Binder = "built-in reflection binder; compiler/Generator measured separately" },
            Corpus = new { PageCount = size, BuildTimestamp = CorpusGenerator.BuildTimestamp },
            Identity = BenchmarkIdentity.Capture("typed-docs-quality"), Provenance = ProvenanceMetadata.Create(rawArguments, 0, 1, corpus),
            Scope = "Generation elapsed excludes input load; ReadAndGenerate includes both. Allocation and working set cover both. Verification overlaps execution.", Workloads = workloads }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Wrote typed Docs+Quality baseline: {output}");
    }

    public sealed class Front
    {
        public required string Title { get; init; }
        public string? Summary { get; init; }
    }
}
#endif
