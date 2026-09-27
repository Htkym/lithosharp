using System.Text.Json;
using LithoSharp.Content.Compilation;
using LithoSharp.Inspection;

namespace LithoSharp.Tests;

/// <summary>V110-09 ledger: the correspondence table and the execution results
/// correspond mechanically; unexecuted official examples stay visible.</summary>
public sealed class MarkdownCompatLedgerTests
{
    private static string RepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
            {
                return path.FullName;
            }
        }

        throw new DirectoryNotFoundException("The ledger requires the repository root.");
    }

    private static JsonDocument LoadLedger()
    {
        var path = Path.Combine(RepoRoot(), "eng", "verification", "1.1.0", "markdown-compat.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Test]
    public async Task Ledger_SourcesAndDenominatorsAreExplicit()
    {
        using var ledger = LoadLedger();
        var root = ledger.RootElement;

        await Assert.That(root.GetProperty("schemaVersion").GetString()).IsEqualTo("1.0");
        await Assert.That(root.GetProperty("taskId").GetString()).IsEqualTo("V110-09");

        var sources = root.GetProperty("sources").EnumerateArray().ToArray();
        var commonmark = sources.Single(item => item.GetProperty("name").GetString() == "CommonMark spec");
        await Assert.That(commonmark.GetProperty("version").GetString()).IsEqualTo("0.31.2");
        await Assert.That(commonmark.GetProperty("vendored").GetBoolean()).IsFalse();
        await Assert.That(commonmark.GetProperty("status").GetString()).IsEqualTo("not-run");

        var classifications = root.GetProperty("classifications").EnumerateArray().ToArray();
        var unsupported = classifications.Single(item => item.GetProperty("name").GetString() == "unsupported");
        await Assert.That(unsupported.GetProperty("count").GetInt32()).IsEqualTo(LithoLimits.UnsupportedIds.Count);

        var totals = root.GetProperty("totals");
        await Assert.That(totals.GetProperty("fullComplianceClaim").GetBoolean()).IsFalse();
        await Assert.That(totals.GetProperty("totalDenominator").GetString()!.Contains("not-run")).IsTrue();
    }

    [Test]
    public async Task Ledger_CasesExecuteAsClassified()
    {
        using var ledger = LoadLedger();
        var compiler = new LithoMarkdownCompiler();
        var executed = 0;
        foreach (var entry in ledger.RootElement.GetProperty("cases").EnumerateArray())
        {
            var markdown = entry.GetProperty("markdown").GetString()!;
            var classification = entry.GetProperty("classification").GetString()!;
            var html = compiler.Compile(markdown).Html;

            if (entry.TryGetProperty("expectedHtml", out var expected))
            {
                await Assert.That(html).IsEqualTo(expected.GetString());
            }

            if (entry.TryGetProperty("expectedContains", out var contains))
            {
                await Assert.That(html.Contains(contains.GetString()!)).IsTrue();
            }

            if (entry.TryGetProperty("diagnostic", out var diagnostic))
            {
                var analyzed = compiler.Analyze(markdown);
                await Assert.That(analyzed.Semantics!.Diagnostics.Any(item => item.Id == diagnostic.GetString())).IsTrue();
            }

            if (entry.TryGetProperty("advisory", out var advisory))
            {
                var off = DocumentInspection.Inspect("a.md", $"---\ntitle: T\n---\n{markdown}");
                await Assert.That(off.Diagnostics.Any(item => item.Id == advisory.GetString())).IsFalse();
                var on = DocumentInspection.Inspect("a.md", $"---\ntitle: T\n---\n{markdown}",
                    new DocumentInspectionOptions { EnableCompatibilityAdvisory = true });
                await Assert.That(on.Diagnostics.Any(item => item.Id == advisory.GetString())).IsTrue();
            }

            if (classification is "supported-pass" or "intentional-divergence" or "unsupported")
            {
                executed++;
            }
        }

        await Assert.That(executed).IsGreaterThanOrEqualTo(11);
    }

    [Test]
    public async Task Ledger_FixturePathsExist()
    {
        using var ledger = LoadLedger();
        var root = RepoRoot();
        var litho = ledger.RootElement.GetProperty("sources").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "Litho fixtures");
        foreach (var relative in litho.GetProperty("paths").EnumerateArray())
        {
            var path = Path.Combine(root, relative.GetString()!.Replace('/', Path.DirectorySeparatorChar));
            await Assert.That(Directory.Exists(path) || File.Exists(path)).IsTrue();
        }
    }
}
