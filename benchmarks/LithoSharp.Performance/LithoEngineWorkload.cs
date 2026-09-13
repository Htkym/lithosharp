using System.Security.Cryptography;
using System.Text;
using LithoSharp.Content.Compilation;
using static LithoSharp.Performance.EngineWorkload;

namespace LithoSharp.Performance;

/// <summary>
/// C05 Litho frontend measurements over the fixed corpora with iteration
/// statistics. Since C13 this is the only engine workload; Markdig reference
/// columns were removed (last compared in c12-engine.json at the old commit).
/// </summary>
internal static class LithoEngineWorkload
{
    public sealed record LithoCorpusMeasurement
    {
        public string Name { get; init; } = string.Empty;
        public int InputBytes { get; init; }
        public string InputSha256 { get; init; } = string.Empty;
        public IterationStats ParseOnly { get; init; } = new();
        public IterationStats RenderOnly { get; init; } = new();
        public IterationStats ParseAndRender { get; init; } = new();
        public IterationStats Analyze { get; init; } = new();
        public string RenderedSha256 { get; init; } = string.Empty;
        public int HeadingCount { get; init; }
        public int PlainTextLength { get; init; }
    }

    public static IReadOnlyList<LithoCorpusMeasurement> Measure(int warmup, int iterations)
    {
        var compiler = new LithoMarkdownCompiler();
        var results = new List<LithoCorpusMeasurement>(EngineWorkload.FixedCorpora().Count);
        foreach (var corpus in EngineWorkload.FixedCorpora())
        {
            results.Add(MeasureCorpus(compiler, corpus, warmup, iterations));
        }

        return results;
    }

    private static LithoCorpusMeasurement MeasureCorpus(
        LithoMarkdownCompiler compiler, Corpus corpus, int warmup, int iterations)
    {
        for (var i = 0; i < warmup; i++)
        {
            _ = compiler.Compile(corpus.Markdown);
            _ = compiler.Analyze(corpus.Markdown);
        }

        var parseStats = TimeShared(warmup, iterations, () =>
        {
            _ = compiler.Parse(corpus.Markdown);
        });

        var preParsed = new (IReadOnlyList<LithoBlock> Tree, IReadOnlyList<string> HeadingIds)[iterations];
        for (var i = 0; i < iterations; i++)
        {
            preParsed[i] = compiler.PrepareRender(corpus.Markdown);
        }

        var slot = 0;
        var renderStats = TimeShared(warmup, iterations, () =>
        {
            // Cycled deterministically; no parsing inside the timer.
            var current = preParsed[slot % preParsed.Length];
            slot++;
            _ = LithoMarkdownCompiler.RenderTree(current.Tree, current.HeadingIds);
        });

        var parseRenderStats = TimeShared(warmup, iterations, () =>
        {
            _ = compiler.Compile(corpus.Markdown);
        });

        var analyzeStats = TimeShared(warmup, iterations, () =>
        {
            _ = compiler.Analyze(corpus.Markdown);
        });

        var analyzed = compiler.Analyze(corpus.Markdown);
        return new LithoCorpusMeasurement
        {
            Name = corpus.Name,
            InputBytes = corpus.InputBytes,
            InputSha256 = corpus.InputSha256,
            ParseOnly = parseStats,
            RenderOnly = renderStats,
            ParseAndRender = parseRenderStats,
            Analyze = analyzeStats,
            RenderedSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(analyzed.Html))),
            HeadingCount = analyzed.Semantics!.Headings.Count,
            PlainTextLength = analyzed.Semantics.PlainText.Length,
        };
    }
}
