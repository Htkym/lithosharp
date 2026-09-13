using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace LithoSharp.Performance;

/// <summary>
/// C00 fixed engine corpora and separated parse/render measurements.
/// Corpora inputs are frozen; the Markdig reference columns were removed in
/// C13 (last compared in c12-engine.json at the old commit).
/// </summary>
internal static class EngineWorkload
{
    public sealed record Corpus(string Name, string Markdown)
    {
        public int InputBytes => Encoding.UTF8.GetByteCount(Markdown);
        public string InputSha256 => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Markdown)));
    }

    public sealed record IterationStats
    {
        public int Iterations { get; init; }
        public int Warmup { get; init; }
        public IReadOnlyList<double> RawElapsedMilliseconds { get; init; } = [];
        public IReadOnlyList<long> RawAllocatedBytes { get; init; } = [];
        public double FirstElapsedMilliseconds { get; init; }
        public double MedianElapsedMilliseconds { get; init; }
        public double MeanElapsedMilliseconds { get; init; }
        public double VarianceElapsedMilliseconds { get; init; }
        public double MinElapsedMilliseconds { get; init; }
        public double MaxElapsedMilliseconds { get; init; }
        public long MedianAllocatedBytes { get; init; }
    }

    public static IReadOnlyList<Corpus> FixedCorpora() =>
    [
        new("paragraph-heavy", BuildParagraphHeavy()),
        new("heading-heavy", BuildHeadingHeavy()),
        new("nested-list", BuildNestedList()),
        new("link-heavy", BuildLinkHeavy()),
        new("code-heavy", BuildCodeHeavy()),
        new("gfm-table", BuildGfmTable()),
        new("task-list", BuildTaskList()),
        new("admonition", BuildAdmonition()),
        new("practical-docs", BuildPracticalDocs()),
        new("docusaurus-doc", BuildDocusaurusDoc()),
        new("pathological", BuildPathological()),
    ];

    private static IterationStats Time(
        int warmup,
        int iterations,
        Action action)
    {
        var elapsed = new List<double>(iterations);
        var allocated = new List<long>(iterations);

        for (var i = 0; i < iterations; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var stopwatch = Stopwatch.StartNew();
            action();
            stopwatch.Stop();
            var allocatedAfter = GC.GetTotalAllocatedBytes(true);
            elapsed.Add(stopwatch.Elapsed.TotalMilliseconds);
            allocated.Add(Math.Max(0, allocatedAfter - allocatedBefore));
        }

        return ToStats(warmup, iterations, elapsed, allocated);
    }

    internal static IterationStats TimeShared(
        int warmup,
        int iterations,
        Action action) => Time(warmup, iterations, action);

    private static IterationStats ToStats(
        int warmup,
        int iterations,
        List<double> elapsed,
        List<long> allocated)
    {
        var sorted = elapsed.OrderBy(value => value).ToArray();
        var mean = sorted.Length == 0 ? 0 : sorted.Average();
        var variance = sorted.Length == 0 ? 0 : sorted.Average(value => (value - mean) * (value - mean));
        var sortedAlloc = allocated.OrderBy(value => value).ToArray();

        return new IterationStats
        {
            Iterations = iterations,
            Warmup = warmup,
            RawElapsedMilliseconds = elapsed,
            RawAllocatedBytes = allocated,
            FirstElapsedMilliseconds = elapsed.Count > 0 ? elapsed[0] : 0,
            MedianElapsedMilliseconds = Median(sorted),
            MeanElapsedMilliseconds = mean,
            VarianceElapsedMilliseconds = variance,
            MinElapsedMilliseconds = sorted.Length > 0 ? sorted[0] : 0,
            MaxElapsedMilliseconds = sorted.Length > 0 ? sorted[^1] : 0,
            MedianAllocatedBytes = sortedAlloc.Length == 0 ? 0 : sortedAlloc[sortedAlloc.Length / 2],
        };
    }

    private static double Median(double[] sorted) =>
        sorted.Length == 0 ? 0
        : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
        : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;

    private static string BuildParagraphHeavy()
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Paragraph Heavy");
        for (var i = 1; i <= 200; i++)
        {
            builder.AppendLine($"Paragraph {i:D3} with stable deterministic text for baseline measurement. " +
                "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt.");
        }

        return builder.ToString();
    }

    private static string BuildHeadingHeavy()
    {
        var builder = new StringBuilder();
        for (var i = 1; i <= 100; i++)
        {
            var level = (i % 6) + 1;
            builder.AppendLine($"{new string('#', level)} Heading {i:D3}");
            builder.AppendLine($"Body for heading {i:D3}.");
        }

        return builder.ToString();
    }

    private static string BuildNestedList()
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Nested List");
        for (var i = 1; i <= 60; i++)
        {
            builder.AppendLine($"- Level 1 item {i:D3}");
            builder.AppendLine($"  - Level 2 item {i:D3}");
            builder.AppendLine($"    - Level 3 item {i:D3}");
            builder.AppendLine($"      - Level 4 item {i:D3}");
        }

        builder.AppendLine();
        for (var i = 1; i <= 20; i++)
        {
            builder.AppendLine($"{i}. Ordered {i:D3}");
            builder.AppendLine($"   a. Alpha {i:D3}");
        }

        return builder.ToString();
    }

    private static string BuildLinkHeavy()
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Link Heavy");
        for (var i = 1; i <= 100; i++)
        {
            builder.AppendLine($"- [External {i:D3}](https://example.org/docs/{i:D3}) and [Internal {i:D3}](/posts/{i:D3}.html) and [Anchor](#section-{i:D3})");
        }

        builder.AppendLine();
        builder.AppendLine("Autolinks: https://example.org/auto and www.example.org/short.");
        return builder.ToString();
    }

    private static string BuildCodeHeavy()
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Code Heavy");
        for (var i = 1; i <= 30; i++)
        {
            builder.AppendLine($"```csharp\nvar value{i} = {i};\nConsole.WriteLine(value{i});\n```");
            builder.AppendLine($"Inline `code-{i:D3}` sample.");
        }

        builder.AppendLine("    Indented code block line one.");
        builder.AppendLine("    Indented code block line two.");
        return builder.ToString();
    }

    private static string BuildGfmTable()
    {
        var builder = new StringBuilder();
        builder.AppendLine("## GFM Table");
        builder.AppendLine("| Name | Value | Note |");
        builder.AppendLine("| ---- | ----- | ---- |");
        for (var i = 1; i <= 50; i++)
        {
            builder.AppendLine($"| Row {i:D3} | {i * 7} | Note {i:D3} |");
        }

        return builder.ToString();
    }

    private static string BuildTaskList()
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Task List");
        for (var i = 1; i <= 100; i++)
        {
            builder.AppendLine(i % 2 == 0 ? $"- [x] Done {i:D3}" : $"- [ ] Todo {i:D3}");
        }

        return builder.ToString();
    }

    private static string BuildAdmonition()
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Admonition");
        for (var i = 1; i <= 20; i++)
        {
            builder.AppendLine($"> [!NOTE]\n> Alert body {i:D3}.");
            builder.AppendLine($"::: custom\nContainer body {i:D3}.\n:::");
        }

        return builder.ToString();
    }

    private static string BuildPracticalDocs()
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Getting Started");
        builder.AppendLine("Install the package and configure the site.");
        builder.AppendLine("```powershell\ndotnet tool install lithosharp\n```");
        builder.AppendLine("| Option | Description |");
        builder.AppendLine("| ------ | ----------- |");
        builder.AppendLine("| `--size` | Corpus size |");
        builder.AppendLine("| `--output` | Output path |");
        builder.AppendLine("- [x] Install SDK");
        builder.AppendLine("- [ ] Build site");
        builder.AppendLine("[Docs](https://example.org/docs) and `/local/path`.");
        builder.AppendLine("![Diagram](assets/diagram.png)");
        return builder.ToString();
    }

    private static string BuildDocusaurusDoc()
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Docusaurus Style Doc");
        builder.AppendLine("::: note");
        builder.AppendLine("Docusaurus container style body.");
        builder.AppendLine(":::");
        builder.AppendLine("```jsx\n<Tabs>\n  <TabItem value=\"a\">A</TabItem>\n</Tabs>\n```");
        builder.AppendLine("| Prop | Type |");
        builder.AppendLine("| ---- | ---- |");
        builder.AppendLine("| `value` | `string` |");
        builder.AppendLine("- [ ] Migrate this page");
        builder.AppendLine("[External](https://example.org/docusaurus) and ![Image](assets/docusaurus.png).");
        return builder.ToString();
    }

    private static string BuildPathological()
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Pathological");
        // Deep emphasis nesting (bounded), long delimiter runs, huge link text.
        builder.AppendLine("***Bold italic " + new string('*', 60) + " tail***");
        builder.AppendLine("- " + string.Concat(Enumerable.Repeat("nest ", 40)));
        builder.AppendLine("  - " + string.Concat(Enumerable.Repeat("deep ", 40)));
        builder.AppendLine($"[{'x'.Repeat(500)}](https://example.org/{'y'.Repeat(200)})");
        builder.AppendLine("```\n" + new string('z', 4000) + "\n```");
        builder.AppendLine("Text with \\*escaped\\* and \\[brackets\\] and &amp; entity.");
        return builder.ToString();
    }

    private static string Repeat(this char value, int count) => new(value, count);
}
