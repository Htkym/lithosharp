using System.Diagnostics;
using System.Text;
using LithoSharp.Content.Compilation;

namespace LithoSharp.Tests;

/// <summary>
/// C12 deterministic mutational fuzz: bounded robustness properties over both
/// compilers. No agreement assertions here (scope stays human-judged); every
/// case must complete without exceptions, within the watchdog, and
/// deterministically. Violations are shrunk and saved under .local/fuzz/c12
/// for minimization into ordinary regression tests.
/// </summary>
public sealed class LithoFuzzTests
{
    private const int Seed = 20260910;
    private const int Iterations = 300;
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    private static readonly string[] Seeds =
    [
        "# Title\n\nBody text with *emphasis* and **strong**.\n",
        "## Links\n\n[one](https://example.org/1) and [two][r].\n\n[r]: https://example.org/r\n",
        "- a\n- b\n  - c\n\n1. one\n2. two\n",
        "> quote\n>\n> - item\n",
        "| A | B |\n| --- | :-: |\n| 1 | 2 |\n",
        "```csharp\nvar x = 1;\n```\n\nInline `code` here.\n",
        "Text with \\*escape\\* and &copy; entity.\n",
        "## 日本語見出し\n\n本文 with emoji \U0001F600.\n",
        ":::note\nCallout body.\n:::\n",
        "Inline $x^2$ and block:\n\n$$\ny = mx\n$$\n",
        "#nospace\n\nSetext\n======\n",
        "---\ntitle: Doc\n---\n\nFront matter body.\n",
        "a\r\nb\r\nc\n",
        "[![alt](https://example.org/i.png)](https://example.org/p)\n",
    ];

    private static readonly string[] Tokens =
    [
        "*", "_", "`", "[", "]", "(", ")", "#", ">", "-", "|", "\\", "&", ";", "<", ">", "$", ":",
        "\n", "\r\n", "\0", "\ud800", "\U0001F600", "{", "}", "!", "~", " ", "\t", "a",
    ];

    [Test]
    public async Task Mutations_StayBounded()
    {
        var random = new Random(Seed);
        var violations = new List<string>();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var input = Mutate(Seeds[random.Next(Seeds.Length)], random);
            var violation = Check(input);
            if (violation is null) continue;
            var directory = Path.Combine(RepoRoot(), ".local", "fuzz", "c12");
            Directory.CreateDirectory(directory);
            var prefix = Path.Combine(directory, $"iter-{iteration:D4}");
            await File.WriteAllTextAsync(prefix + ".input.txt", input);
            string minimized;
            int probes;
            try
            {
                minimized = Shrink(input, out probes);
                await File.WriteAllTextAsync(prefix + ".min.txt", minimized);
                await File.WriteAllTextAsync(prefix + ".error.txt",
                    $"seed={Seed} iteration={iteration} shrinkProbes={probes}\n{violation}\n");
            }
            catch (Exception shrink)
            {
                await File.WriteAllTextAsync(prefix + ".error.txt",
                    $"seed={Seed} iteration={iteration} shrinkFailed={shrink.Message}\n{violation}\n");
            }

            violations.Add($"iter-{iteration:D4}: {violation.Split('\n')[0]} (see {prefix}.min.txt)");
        }

        await Assert.That(violations).IsEmpty();
    }

    private static string? Check(string input)
    {
        MarkdownCompilationResult first;
        var elapsed = Stopwatch.StartNew();
        try
        {
            first = new LithoMarkdownCompiler().Analyze(input);
        }
        catch (Exception exception)
        {
            return $"Litho threw {exception.GetType().Name}: {exception.Message}";
        }

        try
        {
            _ = new ReferenceMarkdigCompiler().Analyze(input);
        }
        catch (ArgumentException) when (HasUnpairedSurrogate(input))
        {
            // Independently pinned reference boundary: Markdig normalization
            // throws on lone surrogates where Litho degrades. Not a violation.
        }
        catch (Exception exception)
        {
            return $"Reference threw {exception.GetType().Name}: {exception.Message}";
        }

        string second;
        try
        {
            second = new LithoMarkdownCompiler().Analyze(input).Html;
        }
        catch (Exception exception)
        {
            return $"Litho rerun threw {exception.GetType().Name}: {exception.Message}";
        }

        elapsed.Stop();
        if (first.Html != second) return "Litho output is not deterministic.";
        if (elapsed.Elapsed > Watchdog) return $"Analysis took {elapsed.Elapsed} (watchdog {Watchdog}).";
        // Mirror the robustness bound: NUL counts as one unit inside Litho.
        var bound = input.Replace("\0", "\uFFFD", StringComparison.Ordinal).Length;
        if (first.Syntax is null || first.Semantics is null) return "Litho returned no syntax or semantics.";
        if (first.Syntax.Blocks.Any(block => block.Span.End > bound)) return "Litho block span escapes the input.";
        if (first.Semantics.Headings.Any(heading => heading.Span.End > bound)) return "Litho heading span escapes the input.";
        return null;
    }

    private static string Mutate(string seed, Random random)
    {
        var builder = new StringBuilder(seed);
        switch (random.Next(8))
        {
            case 0:
                builder.Insert(random.Next(builder.Length + 1), Tokens[random.Next(Tokens.Length)]);
                break;
            case 1:
                if (builder.Length > 0)
                {
                    var start = random.Next(builder.Length);
                    builder.Remove(start, Math.Min(random.Next(1, 33), builder.Length - start));
                }

                break;
            case 2:
            {
                var lines = builder.ToString().Split('\n');
                builder.Append(lines[random.Next(lines.Length)]).Append('\n');
                break;
            }
            case 3:
                builder.Length = random.Next(builder.Length + 1);
                break;
            case 4:
                builder.Insert(random.Next(builder.Length + 1), Seeds[random.Next(Seeds.Length)]);
                break;
            case 5:
            {
                var lines = builder.ToString().Split('\n');
                var line = lines[random.Next(lines.Length)];
                for (var i = 0; i < random.Next(1, 51); i++) builder.Append(line).Append('\n');
                break;
            }
            case 6:
            {
                var lines = builder.ToString().Split('\n');
                if (lines.Length > 2)
                {
                    var at = random.Next(lines.Length - 1);
                    (lines[at], lines[at + 1]) = (lines[at + 1], lines[at]);
                    builder = new StringBuilder(string.Join('\n', lines));
                }

                break;
            }
            default:
            {
                if (builder.Length > 0)
                    builder[random.Next(builder.Length)] = Tokens[random.Next(Tokens.Length)][0];
                break;
            }
        }

        return builder.ToString();
    }

    private static string Shrink(string input, out int probes)
    {
        var count = 0;
        bool Reproduces(string candidate)
        {
            if (++count > 60) return false;
            try
            {
                var first = new LithoMarkdownCompiler().Analyze(candidate).Html;
                return first != new LithoMarkdownCompiler().Analyze(candidate).Html;
            }
            catch
            {
                return true;
            }
        }

        var best = input;
        var size = Math.Max(8, best.Length / 2);
        while (size >= 8 && best.Length > 1)
        {
            var improved = false;
            for (var start = 0; start < best.Length; start += size)
            {
                var candidate = best.Remove(start, Math.Min(size, best.Length - start));
                if (candidate.Length < best.Length && Reproduces(candidate))
                {
                    best = candidate;
                    improved = true;
                    break;
                }
            }

            size = improved ? Math.Max(8, best.Length / 2) : size / 2;
        }

        while (best.Length > 1 && count <= 60 && Reproduces(best[1..])) best = best[1..];
        while (best.Length > 1 && count <= 60 && Reproduces(best[..^1])) best = best[..^1];
        probes = count;
        return best;
    }

    private static bool HasUnpairedSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))) return true;
            if (char.IsLowSurrogate(text[i]) && (i == 0 || !char.IsHighSurrogate(text[i - 1]))) return true;
        }

        return false;
    }

    private static string RepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
            {
                return path.FullName;
            }
        }

        throw new DirectoryNotFoundException("The fuzz output requires the repository root.");
    }
}
