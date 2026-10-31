using LithoSharp.Documentation;
using LithoSharp.Inspection;

namespace LithoSharp.Tests;

/// <summary>V110-12: concurrent requests publish only the latest buffer result.
/// Stale snapshots are never published, cancelled results never saved, and
/// workspaces never mix. Analysis runs outside the lifetime lock, so state
/// queries and stop stay responsive.</summary>
public sealed class DocumentWorkspaceGenerationTests
{
    private const string SampleA =
        "---\n" +
        "title: Alpha\n" +
        "---\n" +
        "# Alpha\n";

    private static string EditText(int index) =>
        "---\n" +
        $"title: Edit {index}\n" +
        "---\n" +
        $"# Heading {index}\n" +
        "\n" +
        $"Body revision {index}.\n";

    private static string BigText(int paragraphs)
    {
        var builder = new System.Text.StringBuilder("---\ntitle: Big\n---\n\n# Big\n\n");
        for (var index = 0; index < paragraphs; index++)
        {
            builder.Append("Paragraph ").Append(index).AppendLine(" with stable cancellable text.");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static ProjectInspectionSnapshot Project(string projectId, string route, string sourcePath = "doc.md") =>
        ProjectInspectionSnapshot.Create(
            projectId, 1, "docs", ToolingCapabilities.LanguageMarkdown, "document", "v1", "en",
            [new ProjectRouteCandidate(sourcePath, route, projectId, "docs", "v1", "en", DocumentPublicationState.Published)]);

    [Test]
    public async Task SlowOldRequestNeverOverwritesNewerResult()
    {
        await using var workspace = new DocumentWorkspace();
        var slow = workspace.InspectVersionedAsync("docs/slow.md", BigText(20000), documentVersion: 1, projectGeneration: 1);
        // Let the slow analysis start before the newer revision arrives.
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        var fast = await workspace.InspectVersionedAsync("docs/slow.md", SampleA, documentVersion: 2, projectGeneration: 1);
        await Assert.That(fast.Title).IsEqualTo("Alpha");
        try
        {
            await slow;
        }
        catch (OperationCanceledException)
        {
        }

        var found = workspace.TryGet("docs/slow.md", out var latest);
        await Assert.That(found).IsTrue();
        await Assert.That(latest!.Title).IsEqualTo("Alpha");
    }

    [Test]
    public async Task HundredOutOfOrderRequestsConvergeToLatest()
    {
        await using var workspace = new DocumentWorkspace();
        // Hold the existing parser admission slots so every revision is reserved
        // before any analysis can complete. This detects a lazy sequential loop.
        var slots = (SemaphoreSlim)typeof(DocumentWorkspace)
            .GetField("_analysisSlots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(workspace)!;
        await slots.WaitAsync();
        await slots.WaitAsync();
        Task<DocumentInfo>[] tasks;
        try
        {
            tasks = Enumerable.Range(1, 100).Select(index =>
                workspace.InspectVersionedAsync("docs/race.md", EditText(index), documentVersion: index, projectGeneration: 1)).ToArray();
            await Assert.That(tasks.Length).IsEqualTo(100);
            await Assert.That(tasks[^1].IsCompleted).IsFalse();
        }
        finally
        {
            slots.Release(2);
        }
        var successes = 0;
        foreach (var task in tasks)
        {
            try
            {
                await task;
                successes++;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await Assert.That(successes).IsGreaterThanOrEqualTo(1);
        var found = workspace.TryGet("docs/race.md", out var latest);
        await Assert.That(found).IsTrue();
        await Assert.That(latest!.Title).IsEqualTo("Heading 100");
    }

    [Test]
    public async Task CancelledVersionedInspectKeepsPriorSnapshot()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = 0;
        var gateCount = 0;
        var gateTimedOut = 0;
        long gateEnteredAt = 0, gateReleasedAt = 0, tokenObservedAt = 0;
        long waitStartedAt = 0, cancelStartedAt = 0, cancelReturnedAt = 0, outcomeAt = 0;
        var incompleteAtGate = false;
        var priorFoundAtGate = false;
        var samePriorAtGate = false;
        var requestedBeforeRelease = false;
        var canceled = false;
        var tokenAtOutcome = false;
        var exceptionTokenIsCaller = false;
        var snapshotFound = false;
        var samePriorAfter = false;
        var followupFound = false;
        var sameFollowup = false;
        string? priorTitle = null, snapshotTitle = null, followupTitle = null;
        var slotsBefore = -1;
        var slotsAfterCancel = -1;
        var slotsAfterFollowup = -1;
        var outcome = "not-started";
        var drained = false;
        Exception? failure = null, drainFailure = null;
        var cleanupFailures = new List<string>();
        Task<DocumentInfo>? pending = null;
        DocumentWorkspace? workspace = null;
        CancellationTokenSource? source = null;
        var callerToken = CancellationToken.None;
        CancellationTokenRegistration registration = default;

        static long Stamp() => System.Diagnostics.Stopwatch.GetTimestamp();
        static string? Describe(Exception? error)
        {
            if (error is null)
                return null;
            var text = error.ToString();
            return text.Length <= 2048 ? text : text[..2048];
        }
        async Task DrainAsync()
        {
            var owned = pending;
            if (owned is null || drained)
                return;
            try
            {
                await owned;
                outcome = "completed";
            }
            catch (OperationCanceledException exception)
            {
                canceled = true;
                outcome = "canceled";
                exceptionTokenIsCaller = exception.CancellationToken == callerToken;
            }
            catch (Exception exception)
            {
                outcome = "faulted";
                drainFailure = exception;
                failure ??= exception;
            }
            finally
            {
                outcomeAt = Stamp();
                tokenAtOutcome = callerToken.IsCancellationRequested;
                drained = true;
            }
        }

        try
        {
            source = new CancellationTokenSource();
            callerToken = source.Token;
            registration = callerToken.Register(() =>
            {
                Interlocked.CompareExchange(ref tokenObservedAt, Stamp(), 0);
            });
            workspace = new DocumentWorkspace(() =>
            {
                if (Interlocked.Exchange(ref armed, 0) == 0)
                    return;
                Interlocked.Increment(ref gateCount);
                Interlocked.Exchange(ref gateEnteredAt, Stamp());
                ready.TrySetResult();
                if (!release.Task.Wait(TimeSpan.FromSeconds(5)))
                {
                    Interlocked.Exchange(ref gateTimedOut, 1);
                    throw new TimeoutException("The prepublication fixture gate was not released.");
                }
                Interlocked.Exchange(ref gateReleasedAt, Stamp());
            });
            var prior = await workspace.InspectVersionedAsync("docs/c.md", SampleA, documentVersion: 1, projectGeneration: 1);
            priorTitle = prior.Title;
            var slots = (SemaphoreSlim)typeof(DocumentWorkspace)
                .GetField("_analysisSlots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(workspace)!;
            slotsBefore = slots.CurrentCount;
            Interlocked.Exchange(ref armed, 1);
            pending = Task.Run(() => workspace.InspectVersionedAsync("docs/c.md", EditText(2),
                documentVersion: 2, projectGeneration: 1, cancellationToken: callerToken), CancellationToken.None);
            try
            {
                waitStartedAt = Stamp();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                incompleteAtGate = !pending.IsCompleted;
                priorFoundAtGate = workspace.TryGet("docs/c.md", out var atGate);
                samePriorAtGate = ReferenceEquals(prior, atGate);
                cancelStartedAt = Stamp();
                source.Cancel();
                cancelReturnedAt = Stamp();
                requestedBeforeRelease = callerToken.IsCancellationRequested;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                release.TrySetResult();
                await DrainAsync();
            }

            snapshotFound = workspace.TryGet("docs/c.md", out var kept);
            snapshotTitle = kept?.Title;
            samePriorAfter = ReferenceEquals(prior, kept);
            slotsAfterCancel = slots.CurrentCount;
            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            await Assert.That(incompleteAtGate).IsTrue();
            await Assert.That(priorFoundAtGate).IsTrue();
            await Assert.That(samePriorAtGate).IsTrue();
            await Assert.That(requestedBeforeRelease).IsTrue();
            await Assert.That(gateCount).IsEqualTo(1);
            await Assert.That(canceled).IsTrue();
            await Assert.That(snapshotFound).IsTrue();
            await Assert.That(kept!.Title).IsEqualTo("Alpha");
            await Assert.That(samePriorAfter).IsTrue();
            await Assert.That(slotsBefore).IsEqualTo(2);
            await Assert.That(slotsAfterCancel).IsEqualTo(slotsBefore);
            var next = await workspace.InspectVersionedAsync("docs/c.md", EditText(3), documentVersion: 3, projectGeneration: 1);
            followupTitle = next.Title;
            slotsAfterFollowup = slots.CurrentCount;
            followupFound = workspace.TryGet("docs/c.md", out var latest);
            sameFollowup = ReferenceEquals(next, latest);
            await Assert.That(next.Title).IsEqualTo("Heading 3");
            await Assert.That(followupFound).IsTrue();
            await Assert.That(sameFollowup).IsTrue();
            await Assert.That(slotsAfterFollowup).IsEqualTo(slotsBefore);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        finally
        {
            release.TrySetResult();
            await DrainAsync();
            try
            {
                if (workspace is not null)
                    await workspace.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(Describe(exception)!);
                failure ??= exception;
            }
            try
            {
                registration.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(Describe(exception)!);
                failure ??= exception;
            }
            try
            {
                source?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(Describe(exception)!);
                failure ??= exception;
            }
            try
            {
                var diagnostic = new
                {
                    schema = "workspace-cancellation/1",
                    test = nameof(CancelledVersionedInspectKeepsPriorSnapshot),
                    processId = Environment.ProcessId,
                    frequency = System.Diagnostics.Stopwatch.Frequency,
                    gateCount,
                    gateTimedOut,
                    gateEnteredAt = Volatile.Read(ref gateEnteredAt),
                    gateReleasedAt = Volatile.Read(ref gateReleasedAt),
                    waitStartedAt,
                    readyStatus = ready.Task.Status.ToString(),
                    releaseStatus = release.Task.Status.ToString(),
                    incompleteAtGate,
                    priorFoundAtGate,
                    samePriorAtGate,
                    cancelStartedAt,
                    cancelReturnedAt,
                    tokenObservedAt = Volatile.Read(ref tokenObservedAt),
                    requestedBeforeRelease,
                    outcome,
                    outcomeAt,
                    tokenAtOutcome,
                    exceptionTokenIsCaller,
                    drained,
                    pendingStatus = pending?.Status.ToString(),
                    snapshotFound,
                    priorTitle,
                    snapshotTitle,
                    samePriorAfter,
                    followupTitle,
                    followupFound,
                    sameFollowup,
                    slotsBefore,
                    slotsAfterCancel,
                    slotsAfterFollowup,
                    failure = Describe(failure),
                    drainFailure = Describe(drainFailure),
                    cleanupFailures
                };
                var json = System.Text.Json.JsonSerializer.Serialize(diagnostic);
                Console.WriteLine(json);
                var output = Environment.GetEnvironmentVariable("LITHOSHARP_WORKSPACE_CANCELLATION_OUTPUT");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    output = Path.GetFullPath(output);
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false));
                    writer.WriteLine(json);
                }
            }
            catch (Exception exception)
            {
                try
                {
                    Console.Error.WriteLine("Cancellation observation incomplete: " + exception.Message);
                }
                catch (Exception) { }
            }
        }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Test]
    public async Task SameUriAcrossProjectsDoesNotMix()
    {
        await using var workspace = new DocumentWorkspace();
        await workspace.InspectVersionedAsync("shared.md", SampleA, documentVersion: 1, projectGeneration: 1,
            new DocumentInspectionOptions { DocumentId = "shared", Project = Project("docs", "/docs/shared/", "shared.md") });
        await workspace.InspectVersionedAsync("shared.md", SampleA, documentVersion: 1, projectGeneration: 1,
            new DocumentInspectionOptions { DocumentId = "shared", Project = Project("blog", "/blog/shared/", "shared.md") });

        var foundDocs = workspace.TryGet("docs", "shared", out var docs);
        var foundBlog = workspace.TryGet("blog", "shared", out var blog);
        await Assert.That(foundDocs).IsTrue();
        await Assert.That(foundBlog).IsTrue();
        await Assert.That(docs!.Route).IsEqualTo("/docs/shared/");
        await Assert.That(blog!.Route).IsEqualTo("/blog/shared/");
        await Assert.That(workspace.TryGet("docs", "missing", out _)).IsFalse();
    }

    [Test]
    public async Task SourcePathIsNeverLowercased()
    {
        await using var workspace = new DocumentWorkspace();
        var info = await workspace.InspectVersionedAsync("Docs/MixedCase.MD", SampleA, documentVersion: 1, projectGeneration: 1);

        await Assert.That(info.SourcePath).IsEqualTo("Docs/MixedCase.MD");
        await Assert.That(info.DocumentId).IsEqualTo("Docs/MixedCase.MD");
        await Assert.That(workspace.TryGet("Docs/MixedCase.MD", out _)).IsTrue();
        await Assert.That(workspace.TryGet("docs/mixedcase.md", out _)).IsFalse();
    }

    [Test]
    public async Task RemovePreventsResurrectionAfterLateCompletion()
    {
        await using var workspace = new DocumentWorkspace();
        var slow = workspace.InspectVersionedAsync("docs/gone.md", BigText(20000), documentVersion: 1, projectGeneration: 1);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        // Whether the slow analysis already committed or is still in flight,
        // removal wins and its late completion must not resurrect the document.
        workspace.Remove("docs/gone.md");
        try
        {
            await slow;
        }
        catch (OperationCanceledException)
        {
        }

        await Assert.That(workspace.TryGet("docs/gone.md", out _)).IsFalse();

        // Reopen converges to the latest text.
        var reopened = await workspace.InspectVersionedAsync("docs/gone.md", SampleA, documentVersion: 2, projectGeneration: 1);
        await Assert.That(reopened.Title).IsEqualTo("Alpha");
        await Assert.That(workspace.TryGet("docs/gone.md", out var latest)).IsTrue();
        await Assert.That(latest!.Title).IsEqualTo("Alpha");
    }

    [Test]
    public async Task RenameDuringResponseMovesContentOnce()
    {
        await using var workspace = new DocumentWorkspace();
        var inFlight = workspace.InspectVersionedAsync("docs/old.md", BigText(20000), documentVersion: 1, projectGeneration: 1);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        workspace.Remove("docs/old.md");
        await workspace.InspectVersionedAsync("docs/new.md", SampleA, documentVersion: 1, projectGeneration: 1);
        try
        {
            await inFlight;
        }
        catch (OperationCanceledException)
        {
        }

        await Assert.That(workspace.TryGet("docs/old.md", out _)).IsFalse();
        var found = workspace.TryGet("docs/new.md", out var renamed);
        await Assert.That(found).IsTrue();
        await Assert.That(renamed!.Title).IsEqualTo("Alpha");
    }

    [Test]
    public async Task ContextSwitchMovesToNewGenerationRoutes()
    {
        await using var workspace = new DocumentWorkspace();
        var first = ProjectInspectionSnapshot.Create(
            "docs", 1, "docs", ToolingCapabilities.LanguageMarkdown, "document", "v1", "en",
            [new ProjectRouteCandidate("doc.md", "/v1/doc/", "docs", "docs", "v1", "en", DocumentPublicationState.Published)]);
        var second = ProjectInspectionSnapshot.Create(
            "docs", 2, "docs", ToolingCapabilities.LanguageMarkdown, "document", "v2", "en",
            [new ProjectRouteCandidate("doc.md", "/v2/doc/", "docs", "docs", "v2", "en", DocumentPublicationState.Published)]);

        var one = await workspace.InspectVersionedAsync("doc.md", SampleA, documentVersion: 1, projectGeneration: 1,
            new DocumentInspectionOptions { Project = first, Version = "v1", Locale = "en" });
        var two = await workspace.InspectVersionedAsync("doc.md", SampleA, documentVersion: 1, projectGeneration: 2,
            new DocumentInspectionOptions { Project = second, Version = "v2", Locale = "en" });

        await Assert.That(one.Route).IsEqualTo("/v1/doc/");
        await Assert.That(two.Route).IsEqualTo("/v2/doc/");
    }

    [Test]
    public async Task DisposeDuringAnalysisLeavesNoResidue()
    {
        var workspace = new DocumentWorkspace();
        var inFlight = workspace.InspectVersionedAsync("docs/x.md", BigText(20000), documentVersion: 1, projectGeneration: 1);
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        await workspace.DisposeAsync();
        try
        {
            await inFlight;
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
        }

        await Assert.That(workspace.TryGet("docs/x.md", out _)).IsFalse();
        await Assert.That(async () => await workspace.InspectVersionedAsync("docs/y.md", SampleA, documentVersion: 1, projectGeneration: 1))
            .Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task SaturatedQueueKeepsOnlyLatestPerDocument()
    {
        await using var workspace = new DocumentWorkspace();
        // Hold the existing parser admission slots so every revision is reserved
        // before any analysis can complete. This detects a lazy sequential loop.
        var slots = (SemaphoreSlim)typeof(DocumentWorkspace)
            .GetField("_analysisSlots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(workspace)!;
        await slots.WaitAsync();
        await slots.WaitAsync();
        Task<DocumentInfo>[] tasks;
        try
        {
            tasks = Enumerable.Range(1, 50).Select(index =>
                workspace.InspectVersionedAsync("docs/sat.md", EditText(index), documentVersion: index, projectGeneration: 1)).ToArray();
            await Assert.That(tasks.Length).IsEqualTo(50);
            await Assert.That(tasks[^1].IsCompleted).IsFalse();
        }
        finally
        {
            slots.Release(2);
        }
        foreach (var task in tasks)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        var found = workspace.TryGet("docs/sat.md", out var latest);
        await Assert.That(found).IsTrue();
        await Assert.That(latest!.Title).IsEqualTo("Heading 50");
    }

    [Test]
    public async Task SupersededRequestWaitingForAnalysisSlotCancelsImmediately()
    {
        await using var workspace = new DocumentWorkspace();
        var firstActive = workspace.InspectVersionedAsync("docs/active-a.md", BigText(100000), 1, 1);
        var secondActive = workspace.InspectVersionedAsync("docs/active-b.md", BigText(100000), 1, 1);
        var stale = workspace.InspectVersionedAsync("docs/queued.md", EditText(1), 1, 1);
        var latest = workspace.InspectVersionedAsync("docs/queued.md", SampleA, 2, 1);

        var staleCompletion = await Task.WhenAny(stale, Task.Delay(TimeSpan.FromSeconds(2)));
        await Assert.That(ReferenceEquals(staleCompletion, stale)).IsTrue();
        await Assert.That(async () => await stale).Throws<OperationCanceledException>();

        workspace.Remove("docs/active-a.md");
        workspace.Remove("docs/active-b.md");
        try
        {
            await firstActive;
        }
        catch (OperationCanceledException) { }
        try
        {
            await secondActive;
        }
        catch (OperationCanceledException) { }
        var result = await latest;
        await Assert.That(result.Title).IsEqualTo("Alpha");
    }

    [Test]
    [Arguments(2L, 1L, 1L, 1L, false)]
    [Arguments(1L, 2L, 99L, 1L, false)]
    [Arguments(2L, 1L, 1L, 1L, true)]
    [Arguments(1L, 2L, 99L, 1L, true)]
    public async Task OlderReservationCannotCancelNewerPendingInspection(long currentVersion, long currentGeneration, long olderVersion, long olderGeneration, bool primeSnapshot)
    {
        await using var workspace = new DocumentWorkspace();
        if (primeSnapshot)
        {
            await workspace.InspectVersionedAsync("docs/reservation.md", SampleA, olderVersion, olderGeneration);
        }

        var current = workspace.InspectVersionedAsync("docs/reservation.md", BigText(20000), currentVersion, currentGeneration);
        var older = workspace.InspectVersionedAsync("docs/reservation.md", SampleA, olderVersion, olderGeneration);

        await Assert.That(async () => await older).Throws<OperationCanceledException>();
        var result = await current;
        await Assert.That(result.Title).IsEqualTo("Big");
        await Assert.That(workspace.TryGet("docs/reservation.md", out var latest)).IsTrue();
        await Assert.That(ReferenceEquals(result, latest)).IsTrue();

        workspace.Remove("docs/reservation.md");
        var reopened = await workspace.InspectVersionedAsync("docs/reservation.md", SampleA, olderVersion, olderGeneration);
        await Assert.That(reopened.Title).IsEqualTo("Alpha");
    }

    [Test]
    public async Task DuplicateSaveAndWatchNotificationAnalyzesOnce()
    {
        await using var workspace = new DocumentWorkspace();
        var first = await workspace.InspectVersionedAsync("docs/dup.md", SampleA, documentVersion: 3, projectGeneration: 1);
        var second = await workspace.InspectVersionedAsync("docs/dup.md", SampleA, documentVersion: 3, projectGeneration: 1);

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task StateQueryStaysResponsiveDuringAnalysis()
    {
        await using var workspace = new DocumentWorkspace();
        await workspace.InspectVersionedAsync("docs/prior.md", SampleA, documentVersion: 1, projectGeneration: 1);
        var slow = workspace.InspectVersionedAsync("docs/busy.md", BigText(100000), documentVersion: 1, projectGeneration: 1);
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        // Queries and unrelated writes do not block behind the running analysis.
        await Assert.That(workspace.TryGet("docs/prior.md", out var prior)).IsTrue();
        await Assert.That(prior!.Title).IsEqualTo("Alpha");
        var other = await workspace.InspectVersionedAsync("docs/other.md", SampleA, documentVersion: 1, projectGeneration: 1);
        await Assert.That(other.Title).IsEqualTo("Alpha");
        await Assert.That(workspace.Remove("docs/prior.md")).IsTrue();

        var done = await slow;
        await Assert.That(done.Title).IsEqualTo("Big");
    }
}
