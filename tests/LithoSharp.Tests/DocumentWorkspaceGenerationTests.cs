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
        var tasks = Enumerable.Range(1, 100).Select(index =>
            workspace.InspectVersionedAsync("docs/race.md", EditText(index), documentVersion: index, projectGeneration: 1));
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
        await using var workspace = new DocumentWorkspace();
        await workspace.InspectVersionedAsync("docs/c.md", SampleA, documentVersion: 1, projectGeneration: 1);

        var canceled = false;
        try
        {
            using var source = new CancellationTokenSource();
            var pending = workspace.InspectVersionedAsync("docs/c.md", BigText(200000), documentVersion: 2, projectGeneration: 1, cancellationToken: source.Token);
            source.CancelAfter(TimeSpan.FromMilliseconds(200));
            await pending;
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        await Assert.That(canceled).IsTrue();
        var found = workspace.TryGet("docs/c.md", out var kept);
        await Assert.That(found).IsTrue();
        await Assert.That(kept!.Title).IsEqualTo("Alpha");
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
        await Assert.That(workspace.Remove("docs/gone.md")).IsFalse();
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
        var tasks = Enumerable.Range(1, 50).Select(index =>
            workspace.InspectVersionedAsync("docs/sat.md", EditText(index), documentVersion: index, projectGeneration: 1));
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
