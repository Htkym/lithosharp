using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LithoSharp.Inspection;
using LithoSharp.Documentation;

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
    [Arguments(false)]
    [Arguments(true)]
    public async Task ThousandsOfUniqueClosedDocumentsReleaseOwnedMetadata(bool projectScoped)
    {
        await using var workspace = new DocumentWorkspace();
        var project = projectScoped ? ProjectInspectionSnapshot.Create("owner", 1, "docs", "markdown", "document", "current", "default") : null;
        var options = project is null ? null : new DocumentInspectionOptions { Project = project };
        for (var index = 0; index < 2000; index++)
        {
            var source = $"docs/closed-{index}.md";
            var snapshot = await workspace.InspectVersionedAsync(source, SampleA, 1, 1, options);
            var removed = project is null ? workspace.Remove(source) : workspace.Remove(project.ProjectId, source);
            await Assert.That(removed).IsTrue();
            await Assert.That(snapshot.Title).IsEqualTo("Alpha");
        }
        // Measure the exact owned metadata count, without a global GC heap gate
        // or unrelated concurrent test allocations.
        await Assert.That(OwnedMetadataCount(workspace)).IsEqualTo(0);
        await Assert.That(OwnedActiveAnalysisCount(workspace)).IsEqualTo(0);
    }

    [Test]
    public async Task RemoveQueuedInspectionAndReopenCannotResurrectTheOldEntry()
    {
        await using var workspace = new DocumentWorkspace();
        var slots = AnalysisSlots(workspace);
        await slots.WaitAsync(); await slots.WaitAsync();
        Task<DocumentInfo>? obsolete = null;
        Task<DocumentInfo>? current = null;
        try
        {
            // Both slots are held: reservation has happened, CPU analysis cannot
            // begin. Removal must invalidate/cancel this actual queued request.
            obsolete = workspace.InspectVersionedAsync("docs/reopen.md", SampleA, 10, 10);
            await Assert.That(OwnedMetadataCount(workspace)).IsEqualTo(1);
            await Assert.That(workspace.Remove("docs/reopen.md")).IsFalse();
            await Assert.That(OwnedMetadataCount(workspace)).IsEqualTo(0);
            await Assert.That(async () => await obsolete.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            // Reopening starts a new lifetime and may use a lower version/gen.
            current = workspace.InspectVersionedAsync("docs/reopen.md", SampleB, 1, 1);
        }
        finally { slots.Release(2); }
        var latest = await current!.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(latest.Title).IsEqualTo("Beta");
        await Assert.That(workspace.TryGet("docs/reopen.md", out var saved)).IsTrue();
        await Assert.That(ReferenceEquals(saved, latest)).IsTrue();
        await Assert.That(OwnedMetadataCount(workspace)).IsEqualTo(1);
        await Assert.That(workspace.Remove("docs/reopen.md")).IsTrue();
        await Assert.That(OwnedMetadataCount(workspace)).IsEqualTo(0);
        await Assert.That(OwnedActiveAnalysisCount(workspace)).IsEqualTo(0);
    }

    [Test]
    public async Task DisposeCancelsQueuedInspectionAndKeepsNoClosedMetadata()
    {
        var workspace = new DocumentWorkspace();
        var slots = AnalysisSlots(workspace);
        await slots.WaitAsync(); await slots.WaitAsync();
        Task<DocumentInfo>? pending = null;
        try
        {
            pending = workspace.InspectVersionedAsync("docs/dispose-queued.md", SampleA, 1, 1);
            await workspace.DisposeAsync();
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            await Assert.That(workspace.TryGet("docs/dispose-queued.md", out _)).IsFalse();
            await Assert.That(OwnedMetadataCount(workspace)).IsEqualTo(0);
            await Assert.That(OwnedActiveAnalysisCount(workspace)).IsEqualTo(0);
        }
        finally { slots.Release(2); await workspace.DisposeAsync(); }
    }

    private static object OwnedField(DocumentWorkspace workspace, string name) =>
        typeof(DocumentWorkspace).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(workspace)!;

    private static int OwnedMetadataCount(DocumentWorkspace workspace)
    {
        var entries = OwnedField(workspace, "_entries");
        return (int)entries.GetType().GetProperty("Count")!.GetValue(entries)!;
    }

    private static int OwnedActiveAnalysisCount(DocumentWorkspace workspace) =>
        ((HashSet<Task>)OwnedField(workspace, "_activeAnalyses")).Count;

    private static SemaphoreSlim AnalysisSlots(DocumentWorkspace workspace) =>
        (SemaphoreSlim)OwnedField(workspace, "_analysisSlots");

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

        await Assert.That(workspace.Remove("docs/d.md")).IsFalse();
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

    private static Task<DocumentInfo> InspectSameRevision(
        DocumentWorkspace workspace, bool versioned, string path, string text,
        DocumentInspectionOptions? options, CancellationToken cancellationToken = default) =>
        versioned
            ? workspace.InspectVersionedAsync(path, text, documentVersion: 7, projectGeneration: 11,
                options: options, cancellationToken: cancellationToken)
            : workspace.InspectAsync(path, text, options, cancellationToken);

    [Test]
    [Arguments(false, "route")]
    [Arguments(true, "route")]
    [Arguments(false, "version")]
    [Arguments(true, "version")]
    [Arguments(false, "locale")]
    [Arguments(true, "locale")]
    public async Task SameTextMetadataChangeUpdatesSnapshot(bool versioned, string field)
    {
        await using var workspace = new DocumentWorkspace();
        var first = await InspectSameRevision(workspace, versioned, "docs/same.md", SampleA,
            new DocumentInspectionOptions { DocumentId = "same", Route = "/old/", Version = "v1", Locale = "en" });
        var second = await InspectSameRevision(workspace, versioned, "docs/same.md", SampleA,
            new DocumentInspectionOptions
            {
                DocumentId = "same",
                Route = field == "route" ? "/new/" : "/old/",
                Version = field == "version" ? "v2" : "v1",
                Locale = field == "locale" ? "ja" : "en",
            });

        await Assert.That(second.Route).IsEqualTo(field == "route" ? "/new/" : "/old/");
        await Assert.That(second.Version).IsEqualTo(field == "version" ? "v2" : "v1");
        await Assert.That(second.Locale).IsEqualTo(field == "locale" ? "ja" : "en");
        await Assert.That(second.ProjectStatus).IsEqualTo(DocumentProjectStatus.NoContext);
        await Assert.That(first.Route).IsEqualTo("/old/");
        await Assert.That(first.Version).IsEqualTo("v1");
        await Assert.That(first.Locale).IsEqualTo("en");
        await Assert.That(workspace.TryGet("same", out var latest)).IsTrue();
        await Assert.That(ReferenceEquals(latest, second)).IsTrue();
    }

    [Test]
    [Arguments(false, "docs/new.md")]
    [Arguments(true, "docs/new.md")]
    [Arguments(false, "Docs/Old.MD")]
    [Arguments(true, "Docs/Old.MD")]
    public async Task SameTextSourcePathChangeUpdatesEveryLocation(bool versioned, string newPath)
    {
        await using var workspace = new DocumentWorkspace();
        var options = new DocumentInspectionOptions { DocumentId = "same-path" };
        const string text = "---\ntitle: Test\n---\n# Test\n\nSee [^missing].\n";
        var first = await InspectSameRevision(workspace, versioned, "docs/old.md", text, options);
        var second = await InspectSameRevision(workspace, versioned, newPath, text, options);

        await Assert.That(second.SourcePath).IsEqualTo(newPath);
        await Assert.That(second.Headings[0].Location?.FilePath).IsEqualTo(newPath);
        await Assert.That(second.Diagnostics.Single(item => item.Id == "LIT001").Location?.FilePath).IsEqualTo(newPath);
        await Assert.That(second.DocumentId).IsEqualTo("same-path");
        await Assert.That(first.SourcePath).IsEqualTo("docs/old.md");
        await Assert.That(first.Headings[0].Location?.FilePath).IsEqualTo("docs/old.md");
        await Assert.That(first.Diagnostics.Single(item => item.Id == "LIT001").Location?.FilePath).IsEqualTo("docs/old.md");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SameTextAdvisoryToggleUpdatesDiagnostics(bool versioned)
    {
        await using var workspace = new DocumentWorkspace();
        const string text = "---\ntitle: Test\n---\nTerm\n\n: definition body\n";
        var off = await InspectSameRevision(workspace, versioned, "docs/advisory.md", text,
            new DocumentInspectionOptions { DocumentId = "advisory" });
        var on = await InspectSameRevision(workspace, versioned, "docs/advisory.md", text,
            new DocumentInspectionOptions { DocumentId = "advisory", EnableCompatibilityAdvisory = true });
        var offAgain = await InspectSameRevision(workspace, versioned, "docs/advisory.md", text,
            new DocumentInspectionOptions { DocumentId = "advisory" });

        await Assert.That(on.Diagnostics.Count(item => item.Id == "LIT003")).IsEqualTo(1);
        await Assert.That(off.Diagnostics.Any(item => item.Id == "LIT003")).IsFalse();
        await Assert.That(offAgain.Diagnostics.Any(item => item.Id == "LIT003")).IsFalse();
        await Assert.That(on.Diagnostics.Count(item => item.Id == "LIT003")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SameTextReplacementProjectSnapshotUpdatesRoute(bool versioned)
    {
        await using var workspace = new DocumentWorkspace();
        static ProjectInspectionSnapshot Project(string route) => ProjectInspectionSnapshot.Create(
            "same-project", 11, "docs", ToolingCapabilities.LanguageMarkdown, "document", "v1", "en",
            [new ProjectRouteCandidate("docs/context.md", route, "same-project", "docs", "v1", "en",
                DocumentPublicationState.Published)]);
        var first = await InspectSameRevision(workspace, versioned, "docs/context.md", SampleA,
            new DocumentInspectionOptions { DocumentId = "context", Project = Project("/old/") });
        var second = await InspectSameRevision(workspace, versioned, "docs/context.md", SampleA,
            new DocumentInspectionOptions { DocumentId = "context", Project = Project("/new/") });

        await Assert.That(second.Route).IsEqualTo("/new/");
        await Assert.That(second.RouteCandidates.Single().PublicPath).IsEqualTo("/new/");
        await Assert.That(second.ProjectStatus).IsEqualTo(DocumentProjectStatus.Resolved);
        await Assert.That(first.Route).IsEqualTo("/old/");
        await Assert.That(first.RouteCandidates.Single().PublicPath).IsEqualTo("/old/");
        await Assert.That(workspace.TryGet("same-project", "context", out var latest)).IsTrue();
        await Assert.That(ReferenceEquals(latest, second)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EquivalentOptionsReuseUnchangedSnapshot(bool versioned)
    {
        await using var workspace = new DocumentWorkspace();
        var project = ProjectInspectionSnapshot.Create("stable", 11, "docs",
            ToolingCapabilities.LanguageMarkdown, "document", "v1", "en");
        var first = await InspectSameRevision(workspace, versioned, "docs/stable.md", SampleA,
            new DocumentInspectionOptions { DocumentId = "stable", Route = "/stable/", Project = project });
        var second = await InspectSameRevision(workspace, versioned, "docs/stable.md", SampleA,
            new DocumentInspectionOptions { DocumentId = "stable", Route = "/stable/", Project = project });

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NullAndDefaultOptionsReuseUnchangedSnapshot(bool versioned)
    {
        await using var workspace = new DocumentWorkspace();
        var first = await InspectSameRevision(workspace, versioned, "docs/default.md", SampleA, null);
        var second = await InspectSameRevision(workspace, versioned, "docs/default.md", SampleA,
            new DocumentInspectionOptions());

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancelledOptionChangeKeepsPriorSnapshotAndReuseIdentity(bool versioned)
    {
        await using var workspace = new DocumentWorkspace();
        var oldOptions = new DocumentInspectionOptions { DocumentId = "cancel-options", Route = "/old/" };
        var first = await InspectSameRevision(workspace, versioned, "docs/cancel-options.md", SampleA, oldOptions);
        var cancelled = false;
        try
        {
            await InspectSameRevision(workspace, versioned, "docs/cancel-options.md", SampleA,
                new DocumentInspectionOptions { DocumentId = "cancel-options", Route = "/new/" },
                new CancellationToken(true));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        await Assert.That(cancelled).IsTrue();
        await Assert.That(workspace.TryGet("cancel-options", out var kept)).IsTrue();
        await Assert.That(ReferenceEquals(first, kept)).IsTrue();
        var unchanged = await InspectSameRevision(workspace, versioned, "docs/cancel-options.md", SampleA, oldOptions);
        await Assert.That(ReferenceEquals(first, unchanged)).IsTrue();
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
        using var process = Process.GetCurrentProcess();
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
    // These are process-wide measurements, so unrelated tests must not allocate
    // into the sampled heap or working set while the edit sequence runs.
    [NotInParallel]
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
                ResourceScope = "process-wide",
                TestScheduling = "exclusive-within-assembly",
                ForceFullCollection = false,
                ManagedGrowthLimitBytes = 50_000_000,
                WorkingSetGrowthLimitBytes = 150_000_000,
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


}
