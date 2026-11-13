using System.Collections;
using System.Reflection;
using System.Text.Json;
using LithoSharp.Content;
using LithoSharp.Inspection;

namespace LithoSharp.Tests;

public sealed class IncrementalInspectionTests
{
    private const string Original = "\uFEFF---\r\ntitle: Alpha\r\n---\r\nAlpha😀\r\n\r\nBravo\r\n\r\nCharlie\r\n";

    [Test]
    public async Task SequentialUtf16ChangesReuseBlocksAndKeepFreshInspectionLocations()
    {
        await using var workspace = new DocumentWorkspace();
        var first = await workspace.InspectVersionedAsync("guide.md", Original, 1, 1);
        var start = Original.IndexOf("Alpha😀", StringComparison.Ordinal);
        var intermediate = Original[..start] + "Altered" + Original[(start + 5)..];
        var updated = intermediate[..(start + 7)] + "Omega" + intermediate[(start + 9)..];
        var changes = new DocumentChangeSet(1, Original,
            [new(start, 5, "Altered"), new(start + 7, 2, "Omega")]);

        var current = await workspace.InspectVersionedEditsAsync("guide.md", updated, 2, 1, changes);

        await Assert.That(current.IncrementalAnalysis!.Mode).IsEqualTo("Delta");
        await Assert.That(current.IncrementalAnalysis.ReusedBlocks).IsGreaterThan(0);
        await Assert.That(current.IncrementalAnalysis.Fallback).IsEqualTo("None");
        await Assert.That(Snapshot(current)).IsEqualTo(Snapshot(DocumentInspection.Inspect("guide.md", updated)));
        await Assert.That(first.MarkdownSyntax!.RawText).Contains("Alpha😀");
        await Assert.That(current.MarkdownSyntax!.RawText).Contains("AlteredOmega");
        await Assert.That(current.MarkdownSyntax.SourceVersion).IsNull();
        await Assert.That((int)current.MarkdownSyntax.RequestedParts).IsEqualTo(159);
    }

    [Test]
    public async Task HeaderProfileAndStaleRevisionChangesFallBackWithoutLosingDiagnostics()
    {
        await using var workspace = new DocumentWorkspace();
        await workspace.InspectVersionedAsync("guide.md", Original, 1, 1);
        var headerStart = Original.IndexOf("title: Alpha", StringComparison.Ordinal) + "title: ".Length;
        var changedHeader = Original[..headerStart] + "Bravo" + Original[(headerStart + 5)..];
        var header = await workspace.InspectVersionedEditsAsync("guide.md", changedHeader, 2, 1,
            new DocumentChangeSet(1, Original, [new(headerStart, 5, "Bravo")]));
        await Assert.That(header.IncrementalAnalysis!.Mode).IsEqualTo("Full");
        await Assert.That(header.IncrementalAnalysis.Fallback).IsEqualTo("HeaderChanged");
        await Assert.That(Snapshot(header)).IsEqualTo(Snapshot(DocumentInspection.Inspect("guide.md", changedHeader)));

        var staleText = changedHeader + "\n# Final\n";
        var stale = await workspace.InspectVersionedEditsAsync("guide.md", staleText, 3, 1,
            new DocumentChangeSet(1, Original, [new(Original.Length, 0, "\n# Final\n")]));
        await Assert.That(stale.IncrementalAnalysis!.Mode).IsEqualTo("Full");
        await Assert.That(stale.IncrementalAnalysis.Fallback).IsEqualTo("PreviousRevisionUnavailable");
        await Assert.That(Snapshot(stale)).IsEqualTo(Snapshot(DocumentInspection.Inspect("guide.md", staleText)));

        var options = new DocumentInspectionOptions { Markdown = new() { ProfileId = MarkdownOptions.DocumentationProfile } };
        var footnote = staleText + "\nNote[^unknown].\n";
        var profile = await workspace.InspectVersionedAsync("guide.md", footnote, 4, 2, options);
        await Assert.That(profile.IncrementalAnalysis!.Mode).IsEqualTo("Full");
        await Assert.That(Snapshot(profile)).IsEqualTo(Snapshot(DocumentInspection.Inspect("guide.md", footnote, options)));
        await Assert.That(profile.MarkdownSyntax!.UnsupportedSyntax).IsNotNull();
        await Assert.That(profile.MarkdownSyntax.RequestedParts.HasFlag(Syntamark.Compilation.MdAnalysisParts.Syntax)).IsTrue();
        await Assert.That(profile.MarkdownSyntax.Status.ToString()).IsEqualTo("Partial");
        await Assert.That(SessionCount(workspace)).IsEqualTo(0);
        var invalid = await workspace.InspectVersionedAsync("guide.md", footnote + "\uD800", 5, 2, options);
        await Assert.That(invalid.MarkdownSyntax!.Status.ToString()).IsEqualTo("Failed");
        await Assert.That(invalid.Diagnostics.Select(diagnostic => diagnostic.Id)).Contains("LSM013");
        await Assert.That(SessionCount(workspace)).IsEqualTo(0);
    }

    [Test]
    public async Task CanceledPublicationCannotRetainAnObsoleteSession()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var publications = 0;
        await using var workspace = new DocumentWorkspace(() =>
        {
            if (Interlocked.Increment(ref publications) == 1)
            {
                ready.SetResult();
                release.Wait();
            }
        });
        var old = workspace.InspectVersionedAsync("guide.md", Original, 1, 1);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var updated = Original.Replace("Alpha😀", "New text", StringComparison.Ordinal);
            var current = await workspace.InspectVersionedAsync("guide.md", updated, 2, 2);
            await Assert.That(current.IncrementalAnalysis!.Mode).IsEqualTo("Full");
            await Assert.That(Snapshot(current)).IsEqualTo(Snapshot(DocumentInspection.Inspect("guide.md", updated)));
            release.Set();
            await Assert.That(async () => await old).Throws<OperationCanceledException>();
            await Assert.That(workspace.TryGet("guide.md", out var latest)).IsTrue();
            await Assert.That(ReferenceEquals(latest, current)).IsTrue();
            await Assert.That(SessionCount(workspace)).IsEqualTo(1);
        }
        finally
        {
            release.Set();
        }
        workspace.Remove("guide.md");
        await Assert.That(SessionCount(workspace)).IsEqualTo(0);
    }

    [Test]
    public async Task SessionRetentionIsBoundedAndCloseReleasesIt()
    {
        await using var workspace = new DocumentWorkspace();
        for (var index = 0; index < 70; index++)
            await workspace.InspectVersionedAsync($"guide-{index}.md", Original, 1, 1);
        await Assert.That(SessionCount(workspace)).IsEqualTo(64);
        for (var index = 0; index < 70; index++)
            workspace.Remove($"guide-{index}.md");
        await Assert.That(SessionCount(workspace)).IsEqualTo(0);
    }

    private static string Snapshot(DocumentInfo info) => JsonSerializer.Serialize(new
    {
        info.Title,
        info.Headings,
        info.Links,
        info.Assets,
        info.Components,
        info.Diagnostics,
        info.FrontMatter,
        info.ProjectStatus,
        info.SchemaInspectionScope,
    });

    private static int SessionCount(DocumentWorkspace workspace) =>
        ((IDictionary)typeof(DocumentWorkspace).GetField("_inspectionSessions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(workspace)!).Count;
}
