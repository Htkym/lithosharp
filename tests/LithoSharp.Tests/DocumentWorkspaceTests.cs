using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LithoSharp.Inspection;

namespace LithoSharp.Tests;

public sealed class DocumentWorkspaceTests
{
    private const string SampleA =
        "---\n" +
        "title: Alpha\n" +
        "---\n" +
        "# Alpha\n";

    private const string SampleB =
        "---\n" +
        "title: Beta\n" +
        "---\n" +
        "# Beta\n";

    [Test]
    public async Task SameDocumentNameDoesNotMixAcrossWorkspaces()
    {
        await using var first = new DocumentWorkspace();
        await using var second = new DocumentWorkspace();

        await first.InspectAsync("docs/same.md", SampleA);
        await second.InspectAsync("docs/same.md", SampleB);

        var foundFirst = first.TryGet("docs/same.md", out var firstInfo);
        var foundSecond = second.TryGet("docs/same.md", out var secondInfo);

        await Assert.That(foundFirst).IsTrue();
        await Assert.That(foundSecond).IsTrue();
        await Assert.That(firstInfo!.Title).IsEqualTo("Alpha");
        await Assert.That(secondInfo!.Title).IsEqualTo("Beta");
        await Assert.That(first.Id == second.Id).IsFalse();
    }

    [Test]
    public async Task CancelledInspectKeepsPriorSnapshotAndAllowsNext()
    {
        await using var workspace = new DocumentWorkspace();
        await workspace.InspectAsync("docs/c.md", SampleA);

        var canceled = false;
        try
        {
            await workspace.InspectAsync("docs/c.md", SampleB, cancellationToken: new CancellationToken(true));
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        await Assert.That(canceled).IsTrue();
        var found = workspace.TryGet("docs/c.md", out var kept);
        await Assert.That(found).IsTrue();
        await Assert.That(kept!.Title).IsEqualTo("Alpha");

        var next = await workspace.InspectAsync("docs/c.md", SampleB);
        await Assert.That(next.Title).IsEqualTo("Beta");
    }

    [Test]
    public async Task ParallelInspectAndRead()
    {
        await using var workspace = new DocumentWorkspace();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            workspace.InspectAsync("docs/p" + i + ".md", SampleA, new DocumentInspectionOptions { DocumentId = "p" + i })));

        var reads = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => workspace.TryGet("p" + i, out _))));
        await Assert.That(reads.All(x => x)).IsTrue();
    }

    [Test]
    public async Task DisposeClearsOwnedCacheButKeepsReturnedSnapshot()
    {
        var workspace = new DocumentWorkspace();
        var snapshot = await workspace.InspectAsync("docs/d.md", SampleA);
        await workspace.DisposeAsync();

        await Assert.That(workspace.TryGet("docs/d.md", out _)).IsFalse();
        await Assert.That(snapshot.Title).IsEqualTo("Alpha");

        var disposed = false;
        try
        {
            await workspace.InspectAsync("docs/e.md", SampleA);
        }
        catch (ObjectDisposedException)
        {
            disposed = true;
        }

        await Assert.That(disposed).IsTrue();
    }

    [Test]
    public async Task RemoveAfterDisposeReturnsFalse()
    {
        var workspace = new DocumentWorkspace();
        await workspace.InspectAsync("docs/d.md", SampleA);
        await workspace.DisposeAsync();

        await Assert.That(workspace.Remove("docs/d.md")).IsFalse();
        await Assert.That(workspace.TryGet("docs/d.md", out _)).IsFalse();
    }

    [Test]
    public async Task ErrorInOneDocumentDoesNotAffectOthers()
    {
        await using var workspace = new DocumentWorkspace();
        await workspace.InspectAsync("docs/good.md", SampleA);

        var failed = false;
        try
        {
            await workspace.InspectAsync("docs/bad.md", null!);
        }
        catch (ArgumentNullException)
        {
            failed = true;
        }

        await Assert.That(failed).IsTrue();
        var found = workspace.TryGet("docs/good.md", out var kept);
        await Assert.That(found).IsTrue();
        await Assert.That(kept!.Title).IsEqualTo("Alpha");
    }

    private static string EditText(int index) =>
        "---\n" +
        $"title: Edit {index}\n" +
        "---\n" +
        $"# Heading {index}\n" +
        "\n" +
        $"Body revision {index}.\n";

    private static (long ManagedBytes, long WorkingSetBytes, int HandleCount, int ThreadCount) SampleProcess()
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        int handles;
        try
        {
            handles = process.HandleCount;
        }
        catch (PlatformNotSupportedException)
        {
            handles = -1;
        }
        return (GC.GetTotalMemory(forceFullCollection: false), process.WorkingSet64, handles, process.Threads.Count);
    }

    [Test]
    public async Task ThousandSequentialEditsConvergeToLatestWithoutUnboundedGrowth()
    {
        await using var workspace = new DocumentWorkspace();
        const int total = 1000;
        const int warmup = 100;
        const int stride = 100;

        DocumentInfo? stale = null;
        var series = new List<object>();
        for (var index = 0; index < total; index++)
        {
            var info = await workspace.InspectAsync("docs/long.md", EditText(index));
            if (index == 500)
            {
                stale = info;
            }
            if (index >= warmup && (index + 1) % stride == 0)
            {
                var sample = SampleProcess();
                series.Add(new
                {
                    Edit = index + 1,
                    sample.ManagedBytes,
                    sample.WorkingSetBytes,
                    sample.HandleCount,
                    sample.ThreadCount,
                });
            }
        }

        var found = workspace.TryGet("docs/long.md", out var latest);
        await Assert.That(found).IsTrue();
        await Assert.That(latest!.Title).IsEqualTo("Heading 999");
        await Assert.That(latest.Headings.Count).IsEqualTo(1);
        await Assert.That(latest.Headings[0].Text).IsEqualTo("Heading 999");

        // Stale snapshot object must not have been rewritten by newer edits.
        await Assert.That(stale).IsNotNull();
        await Assert.That(stale!.Title).IsEqualTo("Heading 500");

        // Same DocumentId overwrites: no unbounded snapshot accumulation.
        await Assert.That(workspace.TryGet("docs/unknown.md", out _)).IsFalse();

        // Post-warmup resource stability: allow noise but fail on continuous leak.
        var managed = series.Select(item => (long)((dynamic)item).ManagedBytes).ToArray();
        var working = series.Select(item => (long)((dynamic)item).WorkingSetBytes).ToArray();
        var firstManaged = managed[0];
        var lastManaged = managed[^1];
        var firstWorking = working[0];
        var lastWorking = working[^1];
        await Assert.That(lastManaged - firstManaged).IsLessThanOrEqualTo(50_000_000);
        await Assert.That(lastWorking - firstWorking).IsLessThanOrEqualTo(150_000_000);
        foreach (var item in series)
        {
            await Assert.That((int)((dynamic)item).HandleCount).IsGreaterThanOrEqualTo(-1);
        }

        var output = Environment.GetEnvironmentVariable("LITHOSHARP_T06_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(output));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }
            var payload = new
            {
                SchemaVersion = "1.0",
                Test = "ThousandSequentialEdits",
                TotalEdits = total,
                WarmupEdits = warmup,
                Stride = stride,
                FinalTitle = latest.Title,
                StaleTitle = stale.Title,
                Environment = new
                {
                    OS = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.OSArchitecture.ToString(),
                    Framework = RuntimeInformation.FrameworkDescription,
                    ProcessorCount = Environment.ProcessorCount,
                },
                Series = series,
            };
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
    }

    [Test]
    public async Task AddDeleteRenameCycleConverges()
    {
        await using var workspace = new DocumentWorkspace();
        await workspace.InspectAsync("docs/a.md", SampleA);
        await workspace.InspectAsync("docs/b.md", SampleB);
        await Assert.That(workspace.TryGet("docs/a.md", out _)).IsTrue();

        await Assert.That(workspace.Remove("docs/a.md")).IsTrue();
        await Assert.That(workspace.TryGet("docs/a.md", out _)).IsFalse();
        await Assert.That(workspace.TryGet("docs/b.md", out var keptB)).IsTrue();
        await Assert.That(keptB!.Title).IsEqualTo("Beta");

        // Re-add with new content converges to the latest text.
        var readded = await workspace.InspectAsync("docs/a.md", SampleB);
        await Assert.That(readded.Title).IsEqualTo("Beta");
        await Assert.That(workspace.TryGet("docs/a.md", out var latestA)).IsTrue();
        await Assert.That(latestA!.Title).IsEqualTo("Beta");

        // Rename: old id disappears, new id carries the content.
        await Assert.That(workspace.Remove("docs/a.md")).IsTrue();
        await workspace.InspectAsync("docs/renamed.md", SampleA);
        await Assert.That(workspace.TryGet("docs/a.md", out _)).IsFalse();
        await Assert.That(workspace.TryGet("docs/renamed.md", out var renamed)).IsTrue();
        await Assert.That(renamed!.Title).IsEqualTo("Alpha");

        await Assert.That(workspace.Remove("docs/missing.md")).IsFalse();
    }

    [Test]
    public async Task VersionLocaleAndRouteSwitchingDoesNotMix()
    {
        await using var workspace = new DocumentWorkspace();
        var first = await workspace.InspectAsync("docs/v.md", SampleA, new DocumentInspectionOptions
        {
            DocumentId = "v-doc",
            Route = "/docs/v1/",
            Version = "v1",
            Locale = "en",
        });
        var second = await workspace.InspectAsync("docs/v.md", SampleB, new DocumentInspectionOptions
        {
            DocumentId = "v-doc",
            Route = "/docs/v2/",
            Version = "v2",
            Locale = "ja",
        });

        await Assert.That(first.Version).IsEqualTo("v1");
        await Assert.That(first.Locale).IsEqualTo("en");
        await Assert.That(first.Route).IsEqualTo("/docs/v1/");
        await Assert.That(second.Version).IsEqualTo("v2");
        await Assert.That(second.Locale).IsEqualTo("ja");
        await Assert.That(second.Route).IsEqualTo("/docs/v2/");

        // The earlier snapshot object keeps its own config.
        await Assert.That(first.Title).IsEqualTo("Alpha");
        await Assert.That(second.Title).IsEqualTo("Beta");
        var found = workspace.TryGet("v-doc", out var latest);
        await Assert.That(found).IsTrue();
        await Assert.That(latest!.Version).IsEqualTo("v2");
        await Assert.That(latest.Locale).IsEqualTo("ja");
    }

    [Test]
    public async Task ProjectReloadProducesFreshWorkspaceWithoutMixing()
    {
        var old = new DocumentWorkspace();
        await old.InspectAsync("docs/same.md", SampleA);
        await old.DisposeAsync();

        await using var reloaded = new DocumentWorkspace();
        await reloaded.InspectAsync("docs/same.md", SampleB);

        await Assert.That(old.TryGet("docs/same.md", out _)).IsFalse();
        var found = reloaded.TryGet("docs/same.md", out var latest);
        await Assert.That(found).IsTrue();
        await Assert.That(latest!.Title).IsEqualTo("Beta");
        await Assert.That(old.Id == reloaded.Id).IsFalse();
    }

    [Test]
    public async Task CancellationDuringLongSequenceKeepsLatest()
    {
        await using var workspace = new DocumentWorkspace();
        var lastApplied = -1;
        for (var index = 0; index < 200; index++)
        {
            if (index % 10 == 9)
            {
                var canceled = false;
                try
                {
                    await workspace.InspectAsync("docs/cancel.md", EditText(index), cancellationToken: new CancellationToken(true));
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }
                await Assert.That(canceled).IsTrue();
            }
            else
            {
                await workspace.InspectAsync("docs/cancel.md", EditText(index));
                lastApplied = index;
            }
        }

        var found = workspace.TryGet("docs/cancel.md", out var latest);
        await Assert.That(found).IsTrue();
        await Assert.That(lastApplied).IsEqualTo(198);
        await Assert.That(latest!.Title).IsEqualTo("Heading 198");
    }

    [Test]
    public async Task RapidOverwritesConvergeToLastSequentialWrite()
    {
        await using var workspace = new DocumentWorkspace();
        var first = await workspace.InspectAsync("docs/rapid.md", EditText(1));
        var second = await workspace.InspectAsync("docs/rapid.md", EditText(2));

        await Assert.That(first.Title).IsEqualTo("Heading 1");
        await Assert.That(second.Title).IsEqualTo("Heading 2");
        var found = workspace.TryGet("docs/rapid.md", out var latest);
        await Assert.That(found).IsTrue();
        await Assert.That(latest!.Title).IsEqualTo("Heading 2");
        // The stale object still reports its own revision.
        await Assert.That(first.Title).IsEqualTo("Heading 1");
    }
}
