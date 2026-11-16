using System.Text.Json;

namespace LithoSharp.Tests;

public sealed class IncrementalLanguageServerTests
{
    [Test]
    public async Task SequentialUtf16EditsReuseBlocksAndKeepDiagnosticPositionsAcrossContextAndReopen()
    {
        await using var client = LspTestClient.Start();
        var initialize = client.NextId();
        client.SendRequest(initialize, "initialize", new { capabilities = new { } });
        await client.WaitForAsync(message => message.TryGetProperty("id", out var value) && value.GetString() == initialize);
        client.SendNotification("initialized", new { });
        const string uri = "file:///proj/incremental.md";
        const string original = "---\ntitle: T\n---\nAlpha😀\n\nBravo\n\nCharlie\n\n# Finish\n";
        const string expected = "---\ntitle: T\n---\nAltered[^x]\n\nBravo\n\nCharlie\n\n# Finish\n";
        Open(1, original, 11);
        await Status(1, 11, 0);

        client.SendNotification("textDocument/didChange", new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[]
            {
                new
                {
                    range = new { start = new { line = 3, character = 0 }, end = new { line = 3, character = 5 } },
                    text = "Altered",
                },
                new
                {
                    // These UTF-16 columns address the intermediate text, after the first change.
                    range = new { start = new { line = 3, character = 7 }, end = new { line = 3, character = 9 } },
                    text = "[^x]",
                },
            },
        });
        var changed = await Diagnostics(2, 11, 0);
        var warning = changed.GetProperty("diagnostics").EnumerateArray().Single(item => item.GetProperty("code").GetString() == "LIT001");
        await Assert.That(warning.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()).IsEqualTo(3);
        await Assert.That(warning.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32()).IsEqualTo(7);
        var reused = (await Status(2, 11, 0)).GetProperty("incremental");
        await Assert.That(reused.GetProperty("mode").GetString()).IsEqualTo("Delta");
        await Assert.That(reused.GetProperty("reusedBlocks").GetInt32()).IsGreaterThan(0);

        client.SendNotification("lithosharp/projectContext", new
        {
            projectId = "unselected",
            folders = Array.Empty<string>(),
            snapshot = (object?)null,
            lithosharpContextGeneration = 1,
        });
        var contextDiagnostics = await Diagnostics(2, 11, 1);
        await Assert.That(contextDiagnostics.GetProperty("diagnostics").GetRawText()).IsEqualTo(changed.GetProperty("diagnostics").GetRawText());
        await Assert.That((await Status(2, 11, 1)).GetProperty("incremental").GetProperty("mode").GetString()).IsEqualTo("Full");

        // A new open boundary permits a reset version, even without a preceding close notification.
        Open(1, expected, 12);
        var reopened = await Diagnostics(1, 12, 1);
        await Assert.That(reopened.GetProperty("diagnostics").GetRawText()).IsEqualTo(changed.GetProperty("diagnostics").GetRawText());
        await Assert.That((await Status(1, 12, 1)).GetProperty("incremental").GetProperty("mode").GetString()).IsEqualTo("Full");

        void Open(long version, string text, long generation) => client.SendNotification("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "markdown", version, text },
            lithosharpOpenGeneration = generation,
        });

        Task<JsonElement> Status(long version, long open, long context) => Notification("lithosharp/analysisStatus", version, open, context);
        Task<JsonElement> Diagnostics(long version, long open, long context) => Notification("textDocument/publishDiagnostics", version, open, context);

        async Task<JsonElement> Notification(string name, long version, long open, long context)
        {
            var message = await client.WaitForAsync(message => message.TryGetProperty("method", out var method) && method.GetString() == name
                && message.GetProperty("params").GetProperty("uri").GetString() == uri
                && message.GetProperty("params").GetProperty("version").GetInt64() == version
                && message.GetProperty("params").GetProperty("lithosharpOpenGeneration").GetInt64() == open
                && message.GetProperty("params").GetProperty("lithosharpContextGeneration").GetInt64() == context);
            return message.GetProperty("params");
        }
    }
}
