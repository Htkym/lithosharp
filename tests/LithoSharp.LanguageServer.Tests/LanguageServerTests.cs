using System.Text;
using System.Text.Json;

namespace LithoSharp.Tests;

public sealed class LanguageServerTests
{
    private static string WorkerDirectory()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            var candidate = Path.Combine(path.FullName, "src", "LithoSharp.Mdx", "worker");
            if (File.Exists(Path.Combine(candidate, "worker.mjs")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The language server tests require the restored MDX worker.");
    }

    private static async Task<JsonElement> InitializeAsync(LspTestClient client, object? initializationOptions = null)
    {
        var id = client.NextId();
        client.SendRequest(id, "initialize", new
        {
            processId = (object?)null,
            rootUri = "file:///proj",
            capabilities = new { },
            initializationOptions,
        });
        var result = await client.WaitForAsync(message =>
            message.TryGetProperty("id", out var responseId) && responseId.GetString() == id);
        client.SendNotification("initialized", new { });
        return result.GetProperty("result");
    }

    private static void Open(LspTestClient client, string uri, string language, long version, string text) =>
        client.SendNotification("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = language, version, text },
        });

    private static async Task<JsonElement> WaitDiagnosticsAsync(
        LspTestClient client, string uri, long? version = null, TimeSpan? timeout = null) =>
        await client.WaitForAsync(message =>
            message.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics"
            && message.GetProperty("params").GetProperty("uri").GetString() == uri
            && (version is null || (message.GetProperty("params").TryGetProperty("version", out var v) && v.GetInt64() == version)),
            timeout);

    [Test]
    public async Task Initialize_AdvertisesOnlyImplementedCapabilities()
    {
        await using var client = LspTestClient.Start();
        var result = await InitializeAsync(client);

        await Assert.That(result.GetProperty("capabilities").GetProperty("positionEncoding").GetString()).IsEqualTo("utf-16");
        await Assert.That(result.GetProperty("capabilities").GetProperty("textDocumentSync").GetProperty("change").GetInt32()).IsEqualTo(2);
        await Assert.That(result.GetProperty("capabilities").GetProperty("documentSymbolProvider").GetBoolean()).IsTrue();
        await Assert.That(result.GetProperty("serverInfo").GetProperty("name").GetString()).IsEqualTo("lithosharp");
        // No unimplemented capabilities are advertised.
        await Assert.That(result.GetProperty("capabilities").TryGetProperty("hoverProvider", out _)).IsFalse();
        await Assert.That(result.GetProperty("capabilities").TryGetProperty("definitionProvider", out _)).IsFalse();
        await Assert.That(result.GetProperty("capabilities").TryGetProperty("referencesProvider", out _)).IsFalse();
    }

    [Test]
    public async Task MarkdownDiagnostics_FlowThroughVersions()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        const string uri = "file:///proj/a.md";
        Open(client, uri, "markdown", 1, "---\ntitle: T\n---\nSee [^a] here.\n");

        var first = await WaitDiagnosticsAsync(client, uri, 1);
        var diagnostics = first.GetProperty("params").GetProperty("diagnostics").EnumerateArray().ToArray();
        var footnote = diagnostics.Single(item => item.GetProperty("code").GetString() == "LIT001");
        await Assert.That(footnote.GetProperty("severity").GetInt32()).IsEqualTo(2);
        await Assert.That(footnote.GetProperty("source").GetString()).IsEqualTo("lithosharp");
        await Assert.That(footnote.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()).IsEqualTo(3);
        await Assert.That(footnote.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32()).IsEqualTo(4);

        // Incremental edit removes the footnote syntax.
        client.SendNotification("textDocument/didChange", new
        {
            textDocument = new { uri, version = (long)2 },
            contentChanges = new[]
            {
                new
                {
                    range = new { start = new { line = 3, character = 4 }, end = new { line = 3, character = 8 } },
                    text = "link",
                },
            },
        });
        var second = await WaitDiagnosticsAsync(client, uri, 2);
        await Assert.That(second.GetProperty("params").GetProperty("diagnostics").EnumerateArray().Count(item => item.GetProperty("code").GetString() == "LIT001")).IsEqualTo(0);

        client.SendNotification("textDocument/didSave", new { textDocument = new { uri } });
        await WaitDiagnosticsAsync(client, uri, 2);

        client.SendNotification("textDocument/didClose", new { textDocument = new { uri } });
        var cleared = await WaitDiagnosticsAsync(client, uri);
        await Assert.That(cleared.GetProperty("params").GetProperty("diagnostics").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Framing_SplitAndConcatenatedMessages()
    {
        await using var client = LspTestClient.Start();
        var id = client.NextId();
        var init = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "initialize",
            @params = new { processId = (object?)null, rootUri = "file:///proj", capabilities = new { } },
        });
        var framed = LspTestClient.Frame(init);
        // One-character fragments followed by two concatenated messages.
        foreach (var character in framed)
        {
            client.SendRaw(character.ToString());
        }

        var result = await client.WaitForAsync(message =>
            message.TryGetProperty("id", out var responseId) && responseId.GetString() == id);
        await Assert.That(result.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()).IsEqualTo("lithosharp");

        var second = client.NextId();
        var third = client.NextId();
        client.SendRaw(LspTestClient.Frame(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = second, method = "shutdown" }))
            + LspTestClient.Frame(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "exit" })));
        await client.WaitForAsync(message =>
            message.TryGetProperty("id", out var responseId) && responseId.GetString() == second);
        await Assert.That(client.WaitForExit()).IsEqualTo(0);
        _ = third;
    }

    private sealed class SplitHeaderMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool firstRead = true;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (firstRead)
            {
                firstRead = false;
                buffer = buffer[..Math.Min(1, buffer.Length)];
            }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Test]
    public async Task Framing_OversizedPrefetchedBodyPreservesFollowingFrame()
    {
        var transportType = typeof(LithoSharp.LanguageServer.LspServer).Assembly
            .GetType("LithoSharp.LanguageServer.StdioTransport", throwOnError: true)!;
        var following = LspTestClient.Frame("{\"jsonrpc\":\"2.0\",\"id\":\"following\",\"method\":\"initialize\"}");
        var bytes = Encoding.UTF8.GetBytes("Content-Length: 8193\r\n\r\n" + new string('x', 8193) + following);
        foreach (var splitHeader in new[] { false, true })
        {
            var transport = Activator.CreateInstance(transportType, [8192])!;
            using var input = splitHeader ? new SplitHeaderMemoryStream(bytes) : new MemoryStream(bytes);
            // Isolate the existing configurable transport without changing Console
            // streams or adding a production-only test hook.
            transportType.GetField("input", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(transport, input);
            var read = (Task<JsonDocument?>)transportType.GetMethod("ReadMessageAsync")!
                .Invoke(transport, [CancellationToken.None])!;
            using var message = await read;
            await Assert.That(message).IsNotNull();
            await Assert.That(message!.RootElement.GetProperty("id").GetString()).IsEqualTo("following");
        }
    }

    [Test]
    public async Task Utf8Multibyte_FramesAndPositions()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        const string uri = "file:///proj/uni.md";
        Open(client, uri, "markdown", 1, "---\ntitle: T\n---\n# 日本語見出し 🎉\n\nSee [^a] 🎉.\n");
        var published = await WaitDiagnosticsAsync(client, uri, 1);

        var footnote = published.GetProperty("params").GetProperty("diagnostics").EnumerateArray()
            .Single(item => item.GetProperty("code").GetString() == "LIT001");
        // "---/title/---/# head/blank/See" — [^a] is on 0-based line 5.
        await Assert.That(footnote.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()).IsEqualTo(5);
        await Assert.That(footnote.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32()).IsEqualTo(4);

        var symbolId = client.NextId();
        client.SendRequest(symbolId, "textDocument/documentSymbol", new { textDocument = new { uri } });
        var symbols = await client.WaitForAsync(message =>
            message.TryGetProperty("id", out var responseId) && responseId.GetString() == symbolId);
        var heading = symbols.GetProperty("result").EnumerateArray().Single();
        await Assert.That(heading.GetProperty("name").GetString()).IsEqualTo("日本語見出し 🎉");
        await Assert.That(heading.GetProperty("selectionRange").GetProperty("start").GetProperty("line").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task Crlf_PositionsStayAccurate()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        const string uri = "file:///proj/crlf.md";
        Open(client, uri, "markdown", 1, "---\r\ntitle: T\r\n---\r\nSee [^a] here.\r\n");
        var published = await WaitDiagnosticsAsync(client, uri, 1);

        var footnote = published.GetProperty("params").GetProperty("diagnostics").EnumerateArray()
            .Single(item => item.GetProperty("code").GetString() == "LIT001");
        await Assert.That(footnote.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()).IsEqualTo(3);
        await Assert.That(footnote.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32()).IsEqualTo(4);
    }

    [Test]
    public async Task DocumentSymbols_NestByHeadingLevel()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        const string uri = "file:///proj/outline.md";
        Open(client, uri, "markdown", 1, "---\ntitle: T\n---\n# Top\n\n## Child\n\n# Next\n");
        await WaitDiagnosticsAsync(client, uri, 1);

        var symbolId = client.NextId();
        client.SendRequest(symbolId, "textDocument/documentSymbol", new { textDocument = new { uri } });
        var response = await client.WaitForAsync(message =>
            message.TryGetProperty("id", out var responseId) && responseId.GetString() == symbolId);
        var roots = response.GetProperty("result").EnumerateArray().ToArray();
        await Assert.That(roots.Length).IsEqualTo(2);
        await Assert.That(roots[0].GetProperty("name").GetString()).IsEqualTo("Top");
        var child = roots[0].GetProperty("children").EnumerateArray().Single();
        await Assert.That(child.GetProperty("name").GetString()).IsEqualTo("Child");
        // selectionRange is the original heading; range is the owned accurate range.
        await Assert.That(child.GetProperty("selectionRange").GetProperty("start").GetProperty("line").GetInt32())
            .IsEqualTo(child.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        await Assert.That(roots[1].GetProperty("children").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task DocumentSymbols_ImmediateOpenWaitsForCurrentInspection()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        const string uri = "file:///proj/immediate-open.md";
        var text = "---\ntitle: T\n---\n# Current heading\n\n"
            + string.Concat(Enumerable.Repeat("A paragraph with enough content to exercise asynchronous inspection.\n\n", 8192));
        var symbolId = client.NextId();
        // Both frames are available together, with no diagnostics wait or sleep.
        client.SendRaw(LspTestClient.Frame(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", method = "textDocument/didOpen",
            @params = new { textDocument = new { uri, languageId = "markdown", version = 1, text } },
        })) + LspTestClient.Frame(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = symbolId, method = "textDocument/documentSymbol",
            @params = new { textDocument = new { uri } },
        })));

        var response = await client.WaitForAsync(message =>
            message.TryGetProperty("id", out var id) && id.GetString() == symbolId);
        var symbols = response.GetProperty("result").EnumerateArray().ToArray();
        await Assert.That(symbols.Length).IsEqualTo(1);
        await Assert.That(symbols[0].GetProperty("name").GetString()).IsEqualTo("Current heading");
        await WaitDiagnosticsAsync(client, uri, 1);
    }

    [Test]
    public async Task DocumentSymbols_ImmediateChangeNeverReturnsPreviousHeading()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        const string uri = "file:///proj/immediate-change.md";
        Open(client, uri, "markdown", 1, "---\ntitle: T\n---\n# Previous heading\n");
        await WaitDiagnosticsAsync(client, uri, 1);

        var text = "---\ntitle: T\n---\n# Renamed heading\n\n"
            + string.Concat(Enumerable.Repeat("A paragraph with enough content to exercise asynchronous inspection.\n\n", 8192));
        var symbolId = client.NextId();
        client.SendRaw(LspTestClient.Frame(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", method = "textDocument/didChange",
            @params = new { textDocument = new { uri, version = 2 }, contentChanges = new[] { new { text } } },
        })) + LspTestClient.Frame(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = symbolId, method = "textDocument/documentSymbol",
            @params = new { textDocument = new { uri } },
        })));

        var response = await client.WaitForAsync(message =>
            message.TryGetProperty("id", out var id) && id.GetString() == symbolId);
        var symbols = response.GetProperty("result").EnumerateArray().ToArray();
        await Assert.That(symbols.Length).IsEqualTo(1);
        await Assert.That(symbols[0].GetProperty("name").GetString()).IsEqualTo("Renamed heading");
        await WaitDiagnosticsAsync(client, uri, 2);
    }

    [Test]
    public async Task DocumentSymbols_CancelledWaitDoesNotCancelSharedDiagnostics()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        const string uri = "file:///proj/cancelled-symbol-wait.md";
        var text = "---\ntitle: T\n---\n# Current heading\nSee [^missing] here.\n\n"
            + string.Concat(Enumerable.Repeat("A paragraph with enough content to exercise asynchronous inspection.\n\n", 8192));
        var symbolId = client.NextId();
        client.SendRaw(LspTestClient.Frame(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", method = "textDocument/didOpen",
            @params = new { textDocument = new { uri, languageId = "markdown", version = 1, text } },
        })) + LspTestClient.Frame(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = symbolId, method = "textDocument/documentSymbol",
            @params = new { textDocument = new { uri } },
        })) + LspTestClient.Frame(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", method = "$/cancelRequest", @params = new { id = symbolId },
        })));
        await client.WaitForAsync(message => message.TryGetProperty("id", out var id) && id.GetString() == symbolId);
        var diagnostics = await WaitDiagnosticsAsync(client, uri, 1);
        await Assert.That(diagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray()
            .Any(item => item.GetProperty("code").GetString() == "LIT001")).IsTrue();

        var nextId = client.NextId();
        client.SendRequest(nextId, "textDocument/documentSymbol", new { textDocument = new { uri } });
        var next = await client.WaitForAsync(message => message.TryGetProperty("id", out var id) && id.GetString() == nextId);
        await Assert.That(next.GetProperty("result").EnumerateArray().Single().GetProperty("name").GetString())
            .IsEqualTo("Current heading");
    }

    [Test]
    public async Task ProjectContext_AppliesBinderAndRoutesPerFolder()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        var snapshot = new
        {
            schemaVersion = "1.0",
            projectId = "docs",
            projectGeneration = (long)5,
            coreVersion = typeof(LithoSharp.Inspection.DocumentWorkspace).Assembly.GetName().Version?.ToString(3),
            collection = "docs",
            language = "markdown",
            schema = "document",
            version = "v1",
            locale = "en",
            acquiredAt = "2026-09-27T00:00:00Z",
            routes = new[]
            {
                new { sourcePath = "intro.md", publicPath = "/docs/intro/", projectId = "docs", collection = "docs", version = "v1", locale = "en", publication = "Published" },
            },
        };
        client.SendNotification("lithosharp/projectContext", new
        {
            projectId = "docs",
            folders = new[] { "file:///proj" },
            snapshot,
        });

        // Unknown front matter field: silent without context rules, LSC101 with them.
        const string uri = "file:///proj/intro.md";
        Open(client, uri, "markdown", 1, "---\ntitle: Hi\nunknown_field_xyz: 1\n---\n# Hi\n");
        var published = await WaitDiagnosticsAsync(client, uri, 1);
        var codes = published.GetProperty("params").GetProperty("diagnostics").EnumerateArray()
            .Select(item => item.GetProperty("code").GetString()).ToArray();
        await Assert.That(codes.Contains("LSC101")).IsTrue();
    }

    [Test]
    public async Task IncompatibleProjectContext_IsIgnored()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        client.SendNotification("lithosharp/projectContext", new
        {
            projectId = "docs",
            folders = new[] { "file:///proj" },
            snapshot = new
            {
                schemaVersion = "1.0",
                projectId = "docs",
                projectGeneration = (long)1,
                coreVersion = "9999.0.0",
                collection = "docs",
                language = "markdown",
                schema = "document",
                version = "v1",
                locale = "en",
                acquiredAt = "2026-09-27T00:00:00Z",
                routes = Array.Empty<object>(),
            },
        });

        const string uri = "file:///proj/a.md";
        Open(client, uri, "markdown", 1, "---\ntitle: Hi\nunknown_field_xyz: 1\n---\n# Hi\n");
        var published = await WaitDiagnosticsAsync(client, uri, 1);
        var codes = published.GetProperty("params").GetProperty("diagnostics").EnumerateArray()
            .Select(item => item.GetProperty("code").GetString()).ToArray();
        await Assert.That(codes.Contains("LSC101")).IsFalse();
    }

    [Test]
    public async Task InvalidProjectContextUpdateDoesNotEraseTheLastValidSnapshot()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        const string uri = "file:///proj/intro.md";
        client.SendNotification("lithosharp/projectContext", new
        {
            projectId = "docs",
            folders = new[] { "file:///proj" },
            snapshot = new
            {
                schemaVersion = "1.0",
                projectId = "docs",
                projectGeneration = (long)1,
                coreVersion = typeof(LithoSharp.Inspection.DocumentWorkspace).Assembly.GetName().Version?.ToString(3),
                collection = "docs",
                language = "markdown",
                schema = "document",
                version = "v1",
                locale = "en",
                acquiredAt = "2026-09-27T00:00:00Z",
                routes = Array.Empty<object>(),
            },
        });
        Open(client, uri, "markdown", 1, "---\ntitle: Hi\nunknown_field_xyz: 1\n---\n# Hi\n");
        var initial = await WaitDiagnosticsAsync(client, uri, 1);
        await Assert.That(initial.GetProperty("params").GetProperty("diagnostics").EnumerateArray()
            .Any(item => item.GetProperty("code").GetString() == "LSC101")).IsTrue();

        client.SendNotification("lithosharp/projectContext", new
        {
            projectId = "docs",
            folders = new[] { "file:///proj" },
            snapshot = new { schemaVersion = "999.0", projectId = "docs" },
        });
        client.SendNotification("textDocument/didChange", new
        {
            textDocument = new { uri, version = (long)2 },
            contentChanges = new[] { new { text = "---\ntitle: Hi\nunknown_field_xyz: 1\n---\n# Changed\n" } },
        });

        var updated = await WaitDiagnosticsAsync(client, uri, 2);
        await Assert.That(updated.GetProperty("params").GetProperty("diagnostics").EnumerateArray()
            .Any(item => item.GetProperty("code").GetString() == "LSC101")).IsTrue();
    }

    [Test]
    public async Task NestedProjectFolderUsesTheMostSpecificContext()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        var coreVersion = typeof(LithoSharp.Inspection.DocumentWorkspace).Assembly.GetName().Version?.ToString(3);
        object Snapshot(string projectId, string schema) => new
        {
            schemaVersion = "1.0",
            projectId,
            projectGeneration = (long)1,
            coreVersion,
            collection = "docs",
            language = "markdown",
            schema,
            version = "v1",
            locale = "en",
            acquiredAt = "2026-09-27T00:00:00Z",
            routes = Array.Empty<object>(),
        };
        client.SendNotification("lithosharp/projectContext", new
        {
            projectId = "parent",
            folders = new[] { "file:///proj" },
            snapshot = Snapshot("parent", "document"),
        });
        client.SendNotification("lithosharp/projectContext", new
        {
            projectId = "nested",
            folders = new[] { "file:///proj/docs" },
            snapshot = Snapshot("nested", "custom"),
        });

        const string uri = "file:///proj/docs/intro.md";
        Open(client, uri, "markdown", 1, "---\ntitle: Hi\nunknown_field_xyz: 1\n---\n# Hi\n");
        var published = await WaitDiagnosticsAsync(client, uri, 1);
        var codes = published.GetProperty("params").GetProperty("diagnostics").EnumerateArray()
            .Select(item => item.GetProperty("code").GetString()).ToArray();
        await Assert.That(codes.Contains("LSC101")).IsFalse();
    }

    [Test]
    public async Task EqualFolderOwnershipIsSyntaxOnlyInBothArrivalOrders()
    {
        foreach (var reverse in new[] { false, true })
        {
            await using var client = LspTestClient.Start();
            await InitializeAsync(client);
            foreach (var id in reverse ? new[] { "two", "one" } : new[] { "one", "two" })
                SendOwnershipContext(client, id, "document", []);
            const string uri = "file:///proj/intro.md";
            Open(client, uri, "markdown", 1, OwnershipText);
            await WaitOwnershipDiagnostics(client, uri, false);
        }
    }

    [Test]
    public async Task ExactSourceOwnershipOverridesEqualFolderInBothArrivalOrders()
    {
        foreach (var reverse in new[] { false, true })
        {
            await using var client = LspTestClient.Start();
            await InitializeAsync(client);
            foreach (var id in reverse ? new[] { "other", "owner" } : new[] { "owner", "other" })
                SendOwnershipContext(client, id, id == "owner" ? "document" : "custom",
                    [id == "owner" ? "/proj/intro.md" : "/proj/other.md"]);
            const string uri = "file:///proj/intro.md";
            Open(client, uri, "markdown", 1, OwnershipText);
            await WaitOwnershipDiagnostics(client, uri, true);
        }
    }

    [Test]
    public async Task SharedSourceAndRemovalReanalyseOpenBufferWithoutAnEdit()
    {
        foreach (var reverse in new[] { false, true })
        {
            await using var client = LspTestClient.Start();
            await InitializeAsync(client);
            var first = reverse ? "two" : "one";
            var second = reverse ? "one" : "two";
            SendOwnershipContext(client, first, "document", ["/proj/intro.md"]);
            const string uri = "file:///proj/intro.md";
            Open(client, uri, "markdown", 1, OwnershipText);
            await WaitOwnershipDiagnostics(client, uri, true);
            SendOwnershipContext(client, second, "document", ["/proj/intro.md"]);
            await WaitOwnershipDiagnostics(client, uri, false);
            client.SendNotification("lithosharp/projectContext", new { projectId = first, folders = Array.Empty<string>(), snapshot = (object?)null });
            await WaitOwnershipDiagnostics(client, uri, true);
            client.SendNotification("lithosharp/projectContext", new { projectId = second, folders = Array.Empty<string>(), snapshot = (object?)null });
            await WaitOwnershipDiagnostics(client, uri, false);
        }
    }

    [Test]
    public async Task ExactSourceOutsideFolderReacquisitionReanalysesOpenBuffer()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        const string uri = "file:///shared/intro.md";
        Open(client, uri, "markdown", 1, OwnershipText);
        await WaitOwnershipDiagnostics(client, uri, false);
        SendOwnershipContext(client, "owner", "document", ["/shared/intro.md"]);
        await WaitOwnershipDiagnostics(client, uri, true);
        SendOwnershipContext(client, "owner", "document", ["/proj/other.md"]);
        await WaitOwnershipDiagnostics(client, uri, false);
    }

    private const string OwnershipText = "---\ntitle: Hi\nunknown_field_xyz: 1\n---\n# Hi\n";

    private static void SendOwnershipContext(LspTestClient client, string projectId, string schema, string[] sources) =>
        client.SendNotification("lithosharp/projectContext", new
        {
            projectId,
            folders = new[] { "file:///proj" },
            snapshot = new
            {
                schemaVersion = "1.0", projectId, projectGeneration = (long)1,
                coreVersion = typeof(LithoSharp.Inspection.DocumentWorkspace).Assembly.GetName().Version?.ToString(3),
                collection = "docs", language = "markdown", schema, version = "current", locale = "default",
                acquiredAt = "2026-10-05T00:00:00Z",
                routes = sources.Select(sourcePath => new
                {
                    sourcePath, publicPath = "/intro/", projectId, collection = "docs",
                    version = "current", locale = "default", publication = "Published",
                }).ToArray(),
            },
        });

    private static async Task WaitOwnershipDiagnostics(LspTestClient client, string uri, bool hasBinderDiagnostic)
    {
        var message = await client.WaitForAsync(message =>
            message.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics"
            && message.TryGetProperty("params", out var parameters)
            && parameters.GetProperty("uri").GetString() == uri
            && parameters.GetProperty("version").GetInt64() == 1
            && parameters.GetProperty("diagnostics").EnumerateArray().Any(item =>
                item.GetProperty("code").GetString() == "LSC101") == hasBinderDiagnostic);
        await Assert.That(message.GetProperty("params").GetProperty("version").GetInt64()).IsEqualTo(1);
    }

    [Test]
    public async Task RegressedVersionsAndInvalidRanges_DoNotCrash()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        const string uri = "file:///proj/a.md";
        Open(client, uri, "markdown", 3, "---\ntitle: T\n---\nBody.\n");
        await WaitDiagnosticsAsync(client, uri, 3);

        // Regressed version is ignored; diagnostics stay at version 3.
        client.SendNotification("textDocument/didChange", new
        {
            textDocument = new { uri, version = (long)2 },
            contentChanges = new[] { new { text = "stale" } },
        });
        // Invalid range is dropped without crashing.
        client.SendNotification("textDocument/didChange", new
        {
            textDocument = new { uri, version = (long)4 },
            contentChanges = new[] { new { range = new { start = new { line = 99, character = 0 }, end = new { line = 99, character = 5 } }, text = "x" } },
        });
        // Unknown method and garbage are safe.
        client.SendRequest(client.NextId(), "textDocument/hover", new { textDocument = new { uri } });
        client.SendRaw("this is not a header\n\n");

        client.SendNotification("textDocument/didChange", new
        {
            textDocument = new { uri, version = (long)5 },
            contentChanges = new[] { new { text = "---\ntitle: T\n---\nChanged.\n" } },
        });
        var published = await WaitDiagnosticsAsync(client, uri, 5);
        await Assert.That(published.GetProperty("params").GetProperty("diagnostics").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task MdxDiagnosticsAndConcurrentSymbolsShareOneOwnedAnalysis()
    {
        await WithProtocolInspectionWorkerAsync(async (worker, client) =>
        {
            await InitializeAsync(client, new { workerDirectory = worker.Root });
            const string uri = "file:///proj/shared.mdx";
            Open(client, uri, "mdx", 1, "# Shared\n");
            await worker.WaitForRequestAsync(1);
            var first = client.NextId(); var second = client.NextId();
            client.SendRequest(first, "textDocument/documentSymbol", new { textDocument = new { uri } });
            client.SendRequest(second, "textDocument/documentSymbol", new { textDocument = new { uri } });
            // Release possible extra requests too: the old implementation must fail
            // by its actual duplicate count, rather than an unreleased-fixture timeout.
            worker.Release(2); worker.Release(3); worker.Release(1);
            await WaitProtocolDiagnosticsAsync(client, uri, 1, 1);
            await AssertProtocolSymbolAsync(client, first, "Shared");
            await AssertProtocolSymbolAsync(client, second, "Shared");
            await Assert.That(worker.RequestCount).IsEqualTo(1);
            await Assert.That(worker.WorkerStartCount).IsEqualTo(1);
        });
    }

    [Test]
    public async Task CancellingOneMdxSymbolWaiterDoesNotStopSharedDiagnosticsOrOtherWaiters()
    {
        await WithProtocolInspectionWorkerAsync(async (worker, client) =>
        {
            await InitializeAsync(client, new { workerDirectory = worker.Root });
            const string uri = "file:///proj/cancel-shared.mdx";
            Open(client, uri, "mdx", 1, "# Survives\n");
            await worker.WaitForRequestAsync(1);
            var cancelled = client.NextId(); var surviving = client.NextId();
            client.SendRequest(cancelled, "textDocument/documentSymbol", new { textDocument = new { uri } });
            client.SendRequest(surviving, "textDocument/documentSymbol", new { textDocument = new { uri } });
            client.SendNotification("$/cancelRequest", new { id = cancelled });
            var response = await WaitProtocolResponseAsync(client, cancelled);
            await Assert.That(response.GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32800);
            worker.Release(2); worker.Release(3); worker.Release(1);
            await WaitProtocolDiagnosticsAsync(client, uri, 1, 1);
            await AssertProtocolSymbolAsync(client, surviving, "Survives");
            await Assert.That(worker.RequestCount).IsEqualTo(1);
            await Assert.That(worker.WorkerStartCount).IsEqualTo(1);
        });
    }

    [Test]
    [Arguments("edit")]
    [Arguments("reopen")]
    [Arguments("context")]
    public async Task SupersededMdxAnalysisCannotSupplyOldSymbolsOrDiagnostics(string change)
    {
        await WithProtocolInspectionWorkerAsync(async (worker, client) =>
        {
            await InitializeAsync(client, new { workerDirectory = worker.Root });
            const string uri = "file:///proj/revision.mdx";
            if (change == "context") SendOwnershipContext(client, "owner", "custom", ["/proj/revision.mdx"]);
            Open(client, uri, "mdx", 1, "# Original\n");
            await worker.WaitForRequestAsync(1);
            var obsolete = client.NextId();
            client.SendRequest(obsolete, "textDocument/documentSymbol", new { textDocument = new { uri } });
            var version = change == "edit" ? 2 : 1;
            if (change == "edit")
                client.SendNotification("textDocument/didChange", new { textDocument = new { uri, version }, contentChanges = new[] { new { text = "# Current\n" } } });
            else if (change == "reopen")
            {
                client.SendNotification("textDocument/didClose", new { textDocument = new { uri } });
                Open(client, uri, "mdx", 1, "# Current\n");
            }
            else
                SendOwnershipContext(client, "owner", "custom", ["/proj/revision.mdx"]);
            await worker.WaitForRequestAsync(2);
            var current = client.NextId();
            client.SendRequest(current, "textDocument/documentSymbol", new { textDocument = new { uri } });
            worker.Release(1); worker.Release(2); worker.Release(3); worker.Release(4);
            var rejected = await WaitProtocolResponseAsync(client, obsolete);
            await Assert.That(rejected.GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32800);
            await AssertProtocolSymbolAsync(client, current, change == "context" ? "Original" : "Current");
            await Assert.That(worker.RequestCount).IsEqualTo(2);
            await WaitProtocolDiagnosticsAsync(client, uri, version, 2);
            await Assert.That(client.Received.Any(message =>
                message.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics"
                && message.GetProperty("params").GetProperty("diagnostics").EnumerateArray().Any(diagnostic =>
                    diagnostic.GetProperty("message").GetString() == "analysis-1"))).IsFalse();
        });
    }

    [Test]
    public async Task UnavailableMdxResultIsSharedUntilAnExplicitSaveRetriesIt()
    {
        await WithProtocolInspectionWorkerAsync(async (worker, client) =>
        {
            await File.WriteAllTextAsync(Path.Combine(worker.Root, "unavailable"), "controlled");
            await InitializeAsync(client, new { workerDirectory = worker.Root });
            const string uri = "file:///proj/unavailable.mdx";
            Open(client, uri, "mdx", 1, "# Restored\n");
            await worker.WaitForRequestAsync(1); worker.Release(1);
            var unavailable = await WaitDiagnosticsAsync(client, uri, 1);
            await Assert.That(unavailable.GetProperty("params").GetProperty("diagnostics").GetArrayLength()).IsEqualTo(0);
            worker.Release(2);
            var first = client.NextId();
            client.SendRequest(first, "textDocument/documentSymbol", new { textDocument = new { uri } });
            var empty = await WaitProtocolResponseAsync(client, first);
            await Assert.That(empty.GetProperty("result").GetArrayLength()).IsEqualTo(0);
            await Assert.That(worker.RequestCount).IsEqualTo(1);
            File.Delete(Path.Combine(worker.Root, "unavailable"));
            client.SendNotification("textDocument/didSave", new { textDocument = new { uri } });
            await worker.WaitForRequestAsync(2); worker.Release(2);
            await WaitProtocolDiagnosticsAsync(client, uri, 1, 2);
            var restored = client.NextId();
            client.SendRequest(restored, "textDocument/documentSymbol", new { textDocument = new { uri } });
            await AssertProtocolSymbolAsync(client, restored, "Restored");
            await Assert.That(worker.RequestCount).IsEqualTo(2);
        });
    }

    private static async Task WithProtocolInspectionWorkerAsync(Func<ProtocolInspectionWorker, LspTestClient, Task> run)
    {
        var worker = await ProtocolInspectionWorker.CreateAsync();
        LspTestClient? client = null;
        Exception? failure = null;
        List<Exception> cleanupFailures = [];
        try
        {
            client = LspTestClient.Start();
            await run(worker, client);
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            if (client is not null)
            {
                try { await client.DisposeAsync(); }
                catch (Exception error) { cleanupFailures.Add(error); }
            }
            try { worker.Dispose(); }
            catch (Exception error) { cleanupFailures.Add(error); }
        }

        if (failure is not null)
        {
            if (cleanupFailures.Count > 0)
                failure.Data["OwnedProtocolFixtureCleanupFailures"] = new AggregateException(cleanupFailures);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        if (cleanupFailures.Count > 0)
            throw new AggregateException("Owned protocol fixture cleanup did not complete cleanly.", cleanupFailures);
    }

    private static Task<JsonElement> WaitProtocolResponseAsync(LspTestClient client, string id) =>
        client.WaitForAsync(message => message.TryGetProperty("id", out var value) && value.GetString() == id);

    private static async Task AssertProtocolSymbolAsync(LspTestClient client, string id, string heading)
    {
        var message = await WaitProtocolResponseAsync(client, id);
        await Assert.That(message.GetProperty("result").EnumerateArray().Single().GetProperty("name").GetString()).IsEqualTo(heading);
    }

    private static Task<JsonElement> WaitProtocolDiagnosticsAsync(LspTestClient client, string uri, long version, int request) =>
        client.WaitForAsync(message => IsProtocolDiagnostics(message, uri, version, request));

    private static bool IsProtocolDiagnostics(JsonElement message, string uri, long version, int request) =>
        message.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics"
        && message.GetProperty("params").GetProperty("uri").GetString() == uri
        // didClose legitimately publishes an empty, versionless diagnostic clear.
        && message.GetProperty("params").TryGetProperty("version", out var publishedVersion)
        && publishedVersion.ValueKind == JsonValueKind.Number && publishedVersion.TryGetInt64(out var value) && value == version
        && message.GetProperty("params").GetProperty("diagnostics").EnumerateArray().Any(diagnostic =>
            diagnostic.GetProperty("message").GetString() == "analysis-" + request);

    [Test]
    public async Task ProtocolDiagnosticsMatcherSkipsCloseClearAndObsoleteAnalysis()
    {
        const string uri = "file:///proj/revision.mdx";
        using var messages = JsonDocument.Parse("""
            [
              {"method":"textDocument/publishDiagnostics","params":{"uri":"file:///proj/revision.mdx","diagnostics":[]}},
              {"method":"textDocument/publishDiagnostics","params":{"uri":"file:///proj/revision.mdx","version":1,"diagnostics":[{"message":"analysis-1"}]}},
              {"method":"textDocument/publishDiagnostics","params":{"uri":"file:///proj/revision.mdx","version":1,"diagnostics":[{"message":"analysis-2"}]}}
            ]
            """);
        var rows = messages.RootElement.EnumerateArray().ToArray();
        await Assert.That(IsProtocolDiagnostics(rows[0], uri, 1, 2)).IsFalse();
        await Assert.That(IsProtocolDiagnostics(rows[1], uri, 1, 2)).IsFalse();
        await Assert.That(IsProtocolDiagnostics(rows[2], uri, 1, 2)).IsTrue();
        await Assert.That(IsProtocolDiagnostics(rows[2], uri, 2, 2)).IsFalse();
        await Assert.That(IsProtocolDiagnostics(rows[2], "file:///proj/other.mdx", 1, 2)).IsFalse();
    }

    // Real process + actual MdxInspectionSession/MdxWorker JSONL protocol. This
    // fixture controls only worker replies, not the MDX compiler semantics.
    private sealed class ProtocolInspectionWorker : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "lithosharp-lsp-shared-mdx-" + Guid.NewGuid().ToString("N"));
        public int RequestCount => Directory.EnumerateFiles(Root, "request-*.json").Count();
        public int WorkerStartCount => File.Exists(Path.Combine(Root, "starts.jsonl")) ? File.ReadAllLines(Path.Combine(Root, "starts.jsonl")).Length : 0;

        public static async Task<ProtocolInspectionWorker> CreateAsync()
        {
            var result = new ProtocolInspectionWorker();
            Directory.CreateDirectory(result.Root);
            await File.WriteAllTextAsync(Path.Combine(result.Root, "worker.mjs"), WorkerSource);
            return result;
        }

        public void Release(int request) => File.WriteAllText(Path.Combine(Root, "release-" + request), "released");

        public async Task WaitForRequestAsync(int request)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(Root, "request-" + request + ".json")))
                await Task.Delay(10, limit.Token);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private const string WorkerSource = """
            import fs from 'node:fs';
            import path from 'node:path';
            import readline from 'node:readline';
            import { fileURLToPath } from 'node:url';
            const root = path.dirname(fileURLToPath(import.meta.url));
            fs.appendFileSync(path.join(root, 'starts.jsonl'), JSON.stringify({pid:process.pid})+'\n');
            const emit = value => process.stdout.write(JSON.stringify(value)+'\n');
            emit({protocol:1,type:'ready',node:process.versions.node,mdx:'3.1.1',react:'19.2.4',esbuild:'0.28.2'});
            for await (const line of readline.createInterface({input:process.stdin,crlfDelay:Infinity})) {
              const request = JSON.parse(line);
              if (request.type !== 'analyze') throw new Error('Unexpected control request');
              const number = fs.readdirSync(root).filter(name=>/^request-\d+\.json$/.test(name)).length+1;
              const final = path.join(root, `request-${number}.json`);
              fs.writeFileSync(final+'.pending', JSON.stringify({type:request.type,text:request.text}));
              fs.renameSync(final+'.pending',final);
              while(!fs.existsSync(path.join(root,`release-${number}`))) await new Promise(resolve=>setTimeout(resolve,10));
              if(fs.existsSync(path.join(root,'unavailable'))) {
                emit({protocol:1,requestId:request.requestId,success:false,diagnostics:[{id:'LSMDX002',message:'controlled unavailable'}]});
                continue;
              }
              const heading = request.text.split('\n').find(line=>line.startsWith('# '))?.slice(2) ?? 'Controlled';
              emit({protocol:1,requestId:request.requestId,success:true,result:{
                headings:[{depth:1,text:heading,line:1}],links:[],islands:[],text:heading,imports:[],
                diagnostics:[{message:`analysis-${number}`,line:1,column:0}]
              }});
            }
            """;
    }

    [Test]
    public async Task MdxDiagnosticsAndSymbols_WithoutVsCode()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client, new { workerDirectory = WorkerDirectory() });
        const string uri = "file:///proj/page.mdx";
        Open(client, uri, "mdx", 1, "---\ntitle: Guide\n---\n# Guide\n\nimport Counter from \"./Counter.jsx\";\n\n<Counter />\n");
        // MDX analysis starts a Node worker; allow headroom under parallel load.
        var published = await WaitDiagnosticsAsync(client, uri, 1, TimeSpan.FromSeconds(60));
        await Assert.That(published.GetProperty("params").GetProperty("diagnostics").GetArrayLength()).IsEqualTo(0);

        var symbolId = client.NextId();
        client.SendRequest(symbolId, "textDocument/documentSymbol", new { textDocument = new { uri } });
        var symbols = await client.WaitForAsync(message =>
            message.TryGetProperty("id", out var responseId) && responseId.GetString() == symbolId);
        await Assert.That(symbols.GetProperty("result").EnumerateArray().Single().GetProperty("name").GetString()).IsEqualTo("Guide");

        const string broken = "file:///proj/broken.mdx";
        Open(client, broken, "mdx", 1, "# Hi\n\n<Unclosed>\n");
        var fatal = await WaitDiagnosticsAsync(client, broken, 1, TimeSpan.FromSeconds(60));
        var diagnostic = fatal.GetProperty("params").GetProperty("diagnostics").EnumerateArray().Single();
        await Assert.That(diagnostic.GetProperty("code").GetString()).IsEqualTo("LSMDX001");

        var brokenSymbols = client.NextId();
        client.SendRequest(brokenSymbols, "textDocument/documentSymbol", new { textDocument = new { uri = broken } });
        var empty = await client.WaitForAsync(message =>
            message.TryGetProperty("id", out var responseId) && responseId.GetString() == brokenSymbols);
        await Assert.That(empty.GetProperty("result").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task MarkdownKeepsWorking_WithoutNode()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client, new { workerDirectory = WorkerDirectory(), nodeExecutable = "no-such-node-executable" });
        const string uri = "file:///proj/a.md";
        Open(client, uri, "markdown", 1, "---\ntitle: T\n---\n# Title\n");
        var published = await WaitDiagnosticsAsync(client, uri, 1);
        await Assert.That(published.GetProperty("params").GetProperty("diagnostics").GetArrayLength()).IsEqualTo(0);

        // MDX reports nothing fabricated; the explanation goes to stderr.
        const string mdx = "file:///proj/a.mdx";
        Open(client, mdx, "mdx", 1, "# Hi\n");
        var empty = await WaitDiagnosticsAsync(client, mdx, 1);
        await Assert.That(empty.GetProperty("params").GetProperty("diagnostics").GetArrayLength()).IsEqualTo(0);
        await client.WaitStderrAsync(line => line.Contains("MDX analysis unavailable"));
    }

    [Test]
    public async Task MultiRoot_SameNameStaysSeparate()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        Open(client, "file:///one/intro.md", "markdown", 1, "---\ntitle: One\n---\n# One\n");
        Open(client, "file:///two/intro.md", "markdown", 1, "---\ntitle: Two\n---\n# Two\n");
        var first = await WaitDiagnosticsAsync(client, "file:///one/intro.md", 1);
        var second = await WaitDiagnosticsAsync(client, "file:///two/intro.md", 1);
        await Assert.That(first.GetProperty("params").GetProperty("diagnostics").GetArrayLength()).IsEqualTo(0);
        await Assert.That(second.GetProperty("params").GetProperty("diagnostics").GetArrayLength()).IsEqualTo(0);

        var symbolId = client.NextId();
        client.SendRequest(symbolId, "textDocument/documentSymbol", new { textDocument = new { uri = "file:///two/intro.md" } });
        var symbols = await client.WaitForAsync(message =>
            message.TryGetProperty("id", out var responseId) && responseId.GetString() == symbolId);
        await Assert.That(symbols.GetProperty("result").EnumerateArray().Single().GetProperty("name").GetString()).IsEqualTo("Two");
    }

    [Test]
    public async Task Cancel_UnknownIdSurvivesAndServerLives()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        client.SendNotification("$/cancelRequest", new { id = "no-such-request" });

        const string uri = "file:///proj/a.md";
        Open(client, uri, "markdown", 1, "---\ntitle: T\n---\n# T\n");
        await WaitDiagnosticsAsync(client, uri, 1);

        var symbolId = client.NextId();
        client.SendRequest(symbolId, "textDocument/documentSymbol", new { textDocument = new { uri } });
        client.SendNotification("$/cancelRequest", new { id = symbolId });
        var response = await client.WaitForAsync(message =>
            message.TryGetProperty("id", out var responseId) && responseId.GetString() == symbolId,
            TimeSpan.FromSeconds(20));
        // Either a normal result or a cancelled error; the server must stay alive either way.
        var alive = client.NextId();
        client.SendRequest(alive, "textDocument/documentSymbol", new { textDocument = new { uri } });
        await client.WaitForAsync(message =>
            message.TryGetProperty("id", out var responseId) && responseId.GetString() == alive,
            TimeSpan.FromSeconds(20));
        await Assert.That(response.ValueKind).IsEqualTo(JsonValueKind.Object);
    }

    [Test]
    public async Task Stdout_StaysPureFrames()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        Open(client, "file:///proj/a.md", "markdown", 1, "not json at all\n");
        await WaitDiagnosticsAsync(client, "file:///proj/a.md", 1);
        client.SendNotification("unknown/notification", new { });
        client.SendRequest(client.NextId(), "shutdown", null);
        await client.WaitForAsync(message =>
            message.TryGetProperty("id", out _) && message.TryGetProperty("result", out _));
        client.SendNotification("exit", null);
        await Assert.That(client.WaitForExit()).IsEqualTo(0);

        // Every stdout byte belonged to a frame; logs stayed on stderr.
        foreach (var message in client.Received)
        {
            await Assert.That(message.ValueKind).IsEqualTo(JsonValueKind.Object);
        }

        await Assert.That(client.ReadStderr().Any(line => line.Contains("Content-Length"))).IsFalse();
    }

    [Test]
    public async Task ShutdownAndExit_Codes()
    {
        await using var clean = LspTestClient.Start();
        await InitializeAsync(clean);
        var shutdown = clean.NextId();
        clean.SendRequest(shutdown, "shutdown", null);
        await clean.WaitForAsync(message =>
            message.TryGetProperty("id", out var responseId) && responseId.GetString() == shutdown);
        clean.SendNotification("exit", null);
        await Assert.That(clean.WaitForExit()).IsEqualTo(0);

        await using var abrupt = LspTestClient.Start();
        await InitializeAsync(abrupt);
        abrupt.SendNotification("exit", null);
        await Assert.That(abrupt.WaitForExit()).IsEqualTo(1);
    }

    [Test]
    public async Task ClientDisconnect_ExitsCleanly()
    {
        await using var client = LspTestClient.Start();
        await InitializeAsync(client);
        Open(client, "file:///proj/a.md", "markdown", 1, "---\ntitle: T\n---\n# T\n");
        await WaitDiagnosticsAsync(client, "file:///proj/a.md", 1);
        client.CloseStdin();
        await Assert.That(client.WaitForExit()).IsEqualTo(0);
    }

    [Test]
    public async Task ServerRestart_AcceptsNewSession()
    {
        await using var first = LspTestClient.Start();
        await InitializeAsync(first);
        Open(first, "file:///proj/a.md", "markdown", 1, "---\ntitle: T\n---\n# T\n");
        await WaitDiagnosticsAsync(first, "file:///proj/a.md", 1);
        var shutdown = first.NextId();
        first.SendRequest(shutdown, "shutdown", null);
        await first.WaitForAsync(message =>
            message.TryGetProperty("id", out var responseId) && responseId.GetString() == shutdown);
        first.SendNotification("exit", null);
        await Assert.That(first.WaitForExit()).IsEqualTo(0);

        await using var second = LspTestClient.Start();
        await InitializeAsync(second);
        Open(second, "file:///proj/b.md", "markdown", 1, "---\ntitle: U\n---\n# U\n");
        await WaitDiagnosticsAsync(second, "file:///proj/b.md", 1);
    }
}
