#if MAJOR_ACCEPTANCE_BENCHMARK
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.AgentReady;
using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;

namespace LithoSharp.Performance;

// A bounded feature-cost/incremental acceptance, not a statistical speed comparison.
internal static class AgentReadyWorkload
{
    public static async Task RunAsync(BenchmarkArguments arguments, string[] rawArguments)
    {
        const int count = 100;
        const int linksPerPage = 12;
        if (arguments.PageCount is not (null or count) || arguments.Scenario != "all")
            throw new ArgumentException("Agent Ready acceptance requires --size 100 --scenario all.");
        var output = Path.GetFullPath(arguments.OutputPath ?? ".local/performance/agent-ready.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        // A fresh owned directory avoids deleting or accidentally warming an earlier run.
        var root = Path.Combine(Path.GetDirectoryName(output)!, "agent-ready-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var rows = new List<object>();
        string? sharedCorpusHash = null;
        foreach (var enabled in new[] { false, true })
        {
            var variantRoot = Path.Combine(root, enabled ? "on" : "off");
            var corpus = Path.Combine(variantRoot, "content");
            var siteOutput = Path.Combine(variantRoot, "site");
            Directory.CreateDirectory(corpus);
            var posts = new List<MarkdownPost>();
            for (var i = 0; i < count; i++)
            {
                var body = $"# Page {i:D5}\n\nDeterministic body {i:D5}.\n\n## Details\n\n"
                    + string.Join("\n", Enumerable.Range(1, linksPerPage).Select(offset =>
                        $"- [Page {(i + offset) % count:D5}](page-{(i + offset) % count:D5}.md#details)")) + "\n";
                var file = Path.Combine(corpus, $"page-{i:D5}.md");
                await File.WriteAllTextAsync(file, body, new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(file, CorpusGenerator.BuildTimestamp.UtcDateTime);
                posts.Add(new(file, $"page-{i:D5}", new() { Title = $"Page {i:D5}", Summary = $"Summary {i:D5}", Date = CorpusGenerator.BuildTimestamp }, body, $"page-{i:D5}.html"));
            }
            var corpusHash = Hash(string.Join("\n", posts.Select(post => post.MarkdownBody)));
            if (sharedCorpusHash is not null && sharedCorpusHash != corpusHash)
                throw new InvalidOperationException("Off/on corpus differs.");
            sharedCorpusHash = corpusHash;
            var customization = new SiteCustomization { Template = new BlogSiteTemplate(), GenerateLlmsTxt = true,
                FaviconSourceDirectory = Path.Combine(variantRoot, "no-favicon"),
                AgentReady = new() { Enabled = enabled, StructuredData = new() { Mode = AgentReadyStructuredDataMode.Automatic } } };
            var site = new SiteSettings { Title = "Agent Ready acceptance", Description = "Deterministic linked corpus", BaseUrl = "https://example.test/product/", Language = "en", TimeZone = "UTC" };
            SiteBuildPlan? previous = null;
            Dictionary<string, string>? baseline = null;
            foreach (var scenario in new[] { "cold", "warm-cache", "no-op", "single-page-change" })
            {
                if (scenario == "warm-cache") previous = null;
                if (scenario == "single-page-change")
                {
                    posts[0] = posts[0] with { MarkdownBody = posts[0].MarkdownBody + "\nChanged acceptance body.\n" };
                    await File.WriteAllTextAsync(posts[0].FilePath, posts[0].MarkdownBody, new UTF8Encoding(false));
                    File.SetLastWriteTimeUtc(posts[0].FilePath, CorpusGenerator.BuildTimestamp.UtcDateTime);
                }
                // A new compiler per stage proves persistent reuse rather than an in-memory compiler hit.
                var compiler = new LithoMarkdownCompiler(MarkdownCompilerOptions.Default with { RetainAgentReadyFacts = enabled });
                var generator = new SiteGenerator(compiler);
                var options = new SiteGenerationOptions { BuildTimestamp = CorpusGenerator.BuildTimestamp,
                    BuildCacheDirectory = Path.Combine(variantRoot, "cache"), PreviousBuildPlan = previous,
                    Markdown = compiler.Options.Markdown, MaxDegreeOfParallelism = 1, CollectTimings = true };
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var allocated = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                var result = await generator.GenerateWithOptionsAsync(site, posts, siteOutput, scenario == "cold", customization, options, default);
                timer.Stop();
                var allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
                previous = result.BuildPlan;
                var artifacts = result.GeneratedFiles.Order(StringComparer.Ordinal).ToDictionary(
                    path => Path.GetRelativePath(siteOutput, path).Replace('\\', '/'),
                    path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);
                var changed = baseline is null ? artifacts.Keys.ToArray() : artifacts.Where(pair => !baseline.TryGetValue(pair.Key, out var old) || old != pair.Value).Select(pair => pair.Key).ToArray();
                var pageNodes = result.BuildReport.Nodes.Where(node => node.OwnedArtifacts.Any(path => path.StartsWith("page-", StringComparison.Ordinal) && path.EndsWith(".html", StringComparison.Ordinal))).ToArray();
                var markdownNodes = result.BuildReport.Nodes.Where(node => node.NodeId.StartsWith("agent-ready:page:", StringComparison.Ordinal)).ToArray();
                if (compiler.ParseCount > (scenario == "cold" ? count : scenario == "single-page-change" ? 1 : 0))
                    throw new InvalidOperationException($"Unexpected repeated Markdown parsing: {enabled}/{scenario}: {compiler.ParseCount}.");
                if (enabled && scenario is ("warm-cache" or "no-op")
                    && (result.AgentReadyValidationFacts.DirectParseCount != 0 || result.AgentReadyValidationFacts.CacheParseCount != 0
                        || result.AgentReadyValidationFacts.CacheHitCount != count))
                    throw new InvalidOperationException("Unchanged validation must reuse the existing content-hashed facts for every page.");
                if (scenario is "warm-cache" or "no-op")
                {
                    if (result.BuildReport.CacheMissCount != 0 || changed.Length != 0 || result.BuildReport.Invalidations.Count != 0)
                        throw new InvalidOperationException($"Unchanged {scenario} regenerated or changed output.");
                }
                if (scenario == "single-page-change" && (pageNodes.Count(node => !node.CacheHit) != 1 || markdownNodes.Count(node => !node.CacheHit) != (enabled ? 1 : 0)))
                    throw new InvalidOperationException($"Single-page edit regenerated unexpected page nodes: enabled={enabled}, html={pageNodes.Count(node => !node.CacheHit)}, markdown={markdownNodes.Count(node => !node.CacheHit)}.");
                if (enabled && (result.AgentReadyPages.Count != count || result.AgentReadyPages.Any(page => page.Status != PublicMarkdownStatus.Automatic)))
                    throw new InvalidOperationException("Expected complete automatic Markdown for all linked pages.");
                if (!enabled && (result.AgentReadyPages.Count != 0 || artifacts.Keys.Any(path => path.EndsWith(".md", StringComparison.Ordinal))))
                    throw new InvalidOperationException("Disabled Agent Ready published Markdown.");
                rows.Add(new { Enabled = enabled, Scenario = scenario, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds, AllocatedBytes = allocatedBytes,
                    MarkdownParseCount = compiler.ParseCount, MarkdownParseCountScope = "LithoMarkdownCompiler.ParseCount only; excludes direct Syntamark post-edit analysis, not a count of every parser invocation", SharedHtmlParseCount = result.HtmlFactsSharing.ParseCount, SharedHtmlParseCountScope = "BuildHtmlFacts shared owner only; direct validation parses are separate",
                    AgentValidationDirectParseCount = result.AgentReadyValidationFacts.DirectParseCount,
                    AgentValidationCacheParseCount = result.AgentReadyValidationFacts.CacheParseCount,
                    AgentValidationCacheHitCount = result.AgentReadyValidationFacts.CacheHitCount,
                    result.BuildReport.CacheHitCount, RenderedNodeCount = result.BuildReport.CacheMissCount,
                    InvalidatedNodeCount = result.BuildReport.Invalidations.Count, RenderedPageNodeCount = pageNodes.Count(node => !node.CacheHit),
                    RenderedMarkdownNodeCount = markdownNodes.Count(node => !node.CacheHit), ArtifactCount = artifacts.Count,
                    OutputBytes = result.GeneratedFiles.Sum(path => new FileInfo(path).Length), ChangedArtifacts = changed,
                    OutputSha256 = Hash(string.Join("\n", artifacts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":" + pair.Value))),
                    ArtifactSha256 = artifacts, result.Timings });
                baseline = artifacts;
            }
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { SchemaVersion = 1, Benchmark = "agent-ready-incremental-acceptance",
            Status = "completed", PageCount = count, LinksPerPage = linksPerPage, CorpusSha256 = sharedCorpusHash,
            BuildTimestamp = CorpusGenerator.BuildTimestamp, Workspace = root,
            Scope = "Eight stages in one process, off before on; no statistical speed claim. Time/allocation cover generation only, excluding corpus preparation and verification. Direct MarkdownPost construction excludes reader parsing. RenderedNodeCount means build-cache misses, including aggregate nodes. Fresh compiler each stage; fixed timestamp; Quality disabled to isolate Agent Ready.",
            Identity = BenchmarkIdentity.Capture("agent-ready"), Environment = EnvironmentMetadata.Create(),
            Provenance = ProvenanceMetadata.Create(rawArguments, 0, 1, Path.Combine(root, "off", "content")), Workloads = rows }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Wrote Agent Ready acceptance: {output}");
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
#endif
