using System.Text.Json;
using LithoSharp.Documentation;
using LithoSharp.Inspection;

namespace LithoSharp.Tests;

public sealed class DocumentReferenceLspTests
{
    [Test]
    public async Task DefinitionsReferencesAndCompletionUseTheSameParsedCatalogAndCurrentBuffers()
    {
        await using var fixture = await ReferenceFixture.CreateAsync(versioned: false);
        var capabilities = fixture.Initialize.GetProperty("result").GetProperty("capabilities");
        await Assert.That(capabilities.GetProperty("definitionProvider").GetBoolean()).IsTrue();
        await Assert.That(capabilities.GetProperty("renameProvider").GetBoolean()).IsFalse();
        var linkOffset = fixture.Text("source.md").IndexOf("target.md", StringComparison.Ordinal) + 3;
        var definition = (await fixture.Request("textDocument/definition", "source.md", linkOffset)).GetProperty("result");
        await Assert.That(definition.GetArrayLength()).IsEqualTo(1);
        await Assert.That(definition[0].GetProperty("uri").GetString()).IsEqualTo(fixture.Uri("target.md"));
        await Assert.That(definition[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()).IsEqualTo(3);
        var references = (await fixture.Request("textDocument/references", "target.md", fixture.HeadingOffset)).GetProperty("result");
        await Assert.That(references.GetArrayLength()).IsGreaterThanOrEqualTo(2);
        await Assert.That(references.EnumerateArray().All(location => location.GetProperty("uri").GetString() == fixture.Uri("source.md"))).IsTrue();
        await Assert.That(references.EnumerateArray().Any(location =>
            location.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == 8)).IsFalse();
        var completionOffset = fixture.Text("source.md").IndexOf("target.md#intro", StringComparison.Ordinal) + "target.md#intro".Length;
        var completion = (await fixture.Request("textDocument/completion", "source.md", completionOffset)).GetProperty("result");
        await Assert.That(completion.EnumerateArray().Any(item => item.GetProperty("textEdit").GetProperty("newText").GetString() == "target.md#intro")).IsTrue();
        await fixture.Change("target.md", fixture.Text("target.md").Replace("# Intro", "# Elsewhere", StringComparison.Ordinal));
        var changed = (await fixture.Request("textDocument/definition", "source.md", linkOffset)).GetProperty("result");
        await Assert.That(changed.GetArrayLength()).IsEqualTo(0);
        var refused = await fixture.Request("textDocument/rename", "target.md", fixture.Text("target.md").IndexOf("Elsewhere", StringComparison.Ordinal), "Next");
        await Assert.That(refused.GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32602);
    }

    [Test]
    public async Task RenameReturnsVersionedConfirmedEditsAndRejectsAnObsoleteExpectedVersion()
    {
        await using var fixture = await ReferenceFixture.CreateAsync(versioned: true);
        var preview = (await fixture.Request("lithosharp/previewRename", "target.md", fixture.HeadingOffset, "Renamed title")).GetProperty("result");
        await Assert.That(preview.GetProperty("isComplete").GetBoolean()).IsFalse();
        await Assert.That(preview.GetProperty("manualScopes").EnumerateArray().Any(scope => scope.GetString()!.Contains("C#", StringComparison.Ordinal))).IsTrue();
        var renamed = (await fixture.Request("textDocument/rename", "target.md", fixture.HeadingOffset, "Renamed title")).GetProperty("result");
        await Assert.That(renamed.TryGetProperty("changes", out _)).IsFalse();
        await Assert.That(renamed.GetProperty("documentChanges").GetArrayLength()).IsEqualTo(2);
        await Assert.That(renamed.GetProperty("changeAnnotations").GetProperty("lithosharp-static-markdown")
            .GetProperty("needsConfirmation").GetBoolean()).IsTrue();
        await fixture.Apply(renamed);
        await Assert.That(fixture.Text("source.md")).Contains("[Shared][go]");
        await Assert.That(fixture.Text("source.md")).Contains("[go]: target.md#renamed-title");
        await Assert.That(fixture.Text("source.md")).Contains("`[Code](target.md#intro)`");
        await Assert.That(fixture.Text("target.md")).Contains("# Renamed title");
        var stale = await fixture.Request("lithosharp/previewRename", "target.md", fixture.HeadingOffset, "Again", expectedVersion: 1);
        await Assert.That(stale.GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32801);
    }

    [Test]
    public async Task MissingConfirmationOrAtomicEditSupportDoesNotFallBackToBlindChanges()
    {
        await using (var fixture = await ReferenceFixture.CreateAsync(versioned: true, annotations: false))
        {
            await Assert.That(fixture.Initialize.GetProperty("result").GetProperty("capabilities").GetProperty("renameProvider").GetBoolean()).IsFalse();
            var refused = await fixture.Request("textDocument/rename", "target.md", fixture.HeadingOffset, "Unsafe batch");
            await Assert.That(refused.GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32602);
            await Assert.That(refused.TryGetProperty("result", out _)).IsFalse();
        }
        await using (var fixture = await ReferenceFixture.CreateAsync(versioned: true, atomic: false))
        {
            var refused = await fixture.Request("textDocument/rename", "target.md", fixture.HeadingOffset, "Unsafe batch");
            await Assert.That(refused.GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32602);
            await Assert.That(refused.TryGetProperty("result", out _)).IsFalse();
        }
    }

    [Test]
    public async Task MovePreviewAndWillRenameKeepReferenceLabelsAndVersionedSourceEdits()
    {
        await using var fixture = await ReferenceFixture.CreateAsync(versioned: true);
        var preview = (await fixture.RequestMove("lithosharp/previewRename", "target.md", "nested/renamed.md")).GetProperty("result");
        await Assert.That(preview.GetProperty("moves").GetArrayLength()).IsEqualTo(1);
        await Assert.That(preview.GetProperty("isComplete").GetBoolean()).IsFalse();
        var edit = (await fixture.RequestMove("workspace/willRenameFiles", "target.md", "nested/renamed.md")).GetProperty("result");
        await Assert.That(edit.TryGetProperty("changes", out _)).IsFalse();
        await Assert.That(edit.GetProperty("documentChanges").GetArrayLength()).IsEqualTo(1);
        await Assert.That(edit.GetProperty("documentChanges")[0].GetProperty("textDocument").GetProperty("version").GetInt64()).IsEqualTo(1);
        await fixture.Apply(edit);
        await Assert.That(fixture.Text("source.md")).Contains("[Target](nested/renamed.md#intro)");
        await Assert.That(fixture.Text("source.md")).Contains("[Shared][go]");
        await Assert.That(fixture.Text("source.md")).Contains("[go]: nested/renamed.md#intro");
        await Assert.That(fixture.Text("source.md")).Contains("`[Code](target.md#intro)`");
    }

    [Test]
    public async Task AggregateProjectContextSelectsOnlyAnExplicitOrUnambiguousSourceVariant()
    {
        await using var fixture = await ReferenceFixture.CreateAsync(versioned: false, variants: true);
        var offset = fixture.Text("source.md").IndexOf("target.md", StringComparison.Ordinal);
        var scopes = (await fixture.Request("lithosharp/referenceScopes", "source.md", offset)).GetProperty("result");
        await Assert.That(scopes.GetProperty("scopes").GetArrayLength()).IsEqualTo(2);
        await Assert.That(scopes.GetProperty("sourceVersion").GetInt64()).IsEqualTo(1);
        var ambiguous = await fixture.Request("textDocument/definition", "source.md", offset);
        await Assert.That(ambiguous.GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32602);
        await Assert.That(ambiguous.GetProperty("error").GetProperty("message").GetString()!).Contains("referenceScope");
        var selected = (await fixture.Request("textDocument/definition", "source.md", offset,
            referenceScope: new { collection = "guides", version = "v2", locale = "ja" })).GetProperty("result");
        await Assert.That(selected.GetArrayLength()).IsEqualTo(1);
        await Assert.That(selected[0].GetProperty("uri").GetString()).IsEqualTo(fixture.Uri("target.md"));
    }

    private sealed class ReferenceFixture : IAsyncDisposable
    {
        private readonly LspTestClient client = LspTestClient.Start();
        private readonly string root = Path.GetFullPath(Path.Combine("reference-editor", Guid.NewGuid().ToString("N")));
        private readonly Dictionary<string, string> texts = new(StringComparer.Ordinal)
        {
            ["source.md"] = "---\ntitle: Source\n---\n[Target](target.md#intro)\n\n[Shared][go]\n[go]: target.md#intro\n\n`[Code](target.md#intro)`\n",
            ["target.md"] = "---\ntitle: Target\n---\n# Intro\n\nText.\n",
        };
        private readonly Dictionary<string, long> versions = new() { ["source.md"] = 1, ["target.md"] = 1 };
        internal JsonElement Initialize { get; private set; }
        internal int HeadingOffset => texts["target.md"].IndexOf("# ", StringComparison.Ordinal) + 2;
        internal string Uri(string file) => new System.Uri(Path.Combine(root, file)).AbsoluteUri;
        internal string Text(string file) => texts[file];

        internal static async Task<ReferenceFixture> CreateAsync(bool versioned, bool annotations = true, bool atomic = true, bool variants = false)
        {
            var fixture = new ReferenceFixture();
            var id = fixture.client.NextId();
            fixture.client.SendRequest(id, "initialize", new
            {
                capabilities = versioned ? (object)new
                {
                    workspace = new
                    {
                        workspaceEdit = new { documentChanges = true, failureHandling = atomic ? "textOnlyTransactional" : "abort", changeAnnotationSupport = new { groupsOnLabel = true } },
                        fileOperations = new { willRename = true },
                    },
                    textDocument = new { rename = new { prepareSupport = true, honorsChangeAnnotations = annotations } },
                } : new { },
            });
            fixture.Initialize = await fixture.Response(id);
            fixture.client.SendNotification("initialized", new { });
            var routes = fixture.texts.Keys.Select(file => new ProjectRouteCandidate(file, "/docs/" + Path.GetFileNameWithoutExtension(file) + "/",
                "reference-project", "guides", "current", "en", DocumentPublicationState.Published));
            if (variants)
                routes = routes.Concat(fixture.texts.Keys.Select(file => new ProjectRouteCandidate(file, "/docs/ja/v2/" + Path.GetFileNameWithoutExtension(file) + "/",
                    "reference-project", "guides", "v2", "ja", DocumentPublicationState.Published)));
            var context = ProjectInspectionSnapshot.Create("reference-project", 1, "site", "markdown", "document", "current", "default", routes);
            fixture.client.SendNotification("lithosharp/projectContext", new
            {
                projectId = "reference-project",
                folders = new[] { new System.Uri(fixture.root + Path.DirectorySeparatorChar).AbsoluteUri },
                snapshot = JsonSerializer.Deserialize<JsonElement>(context.ToJson()),
                lithosharpContextGeneration = 1,
            });
            foreach (var (file, text) in fixture.texts)
            {
                fixture.client.SendNotification("textDocument/didOpen", new
                {
                    textDocument = new { uri = fixture.Uri(file), languageId = "markdown", version = 1, text },
                });
                await fixture.Published(file, 1);
            }
            return fixture;
        }

        internal async Task<JsonElement> Request(string method, string file, int offset, string? newName = null, long? expectedVersion = null, object? referenceScope = null)
        {
            var id = client.NextId();
            var document = new Dictionary<string, object?> { ["uri"] = Uri(file) };
            if (expectedVersion.HasValue)
                document["version"] = expectedVersion.Value;
            var position = Position(texts[file], offset);
            var parameters = new Dictionary<string, object?>
            {
                ["textDocument"] = document,
                ["position"] = new { line = position.Line, character = position.Character },
                ["context"] = new { includeDeclaration = false },
            };
            if (newName is not null)
                parameters["newName"] = newName;
            if (referenceScope is not null)
                parameters["referenceScope"] = referenceScope;
            client.SendRequest(id, method, parameters);
            return await Response(id);
        }

        internal async Task<JsonElement> RequestMove(string method, string file, string destination)
        {
            var id = client.NextId();
            object parameters = method == "workspace/willRenameFiles"
                ? new { files = new[] { new { oldUri = Uri(file), newUri = Uri(destination) } } }
                : new { textDocument = new { uri = Uri(file), version = versions[file] }, newUri = Uri(destination) };
            client.SendRequest(id, method, parameters);
            return await Response(id);
        }

        internal async Task Apply(JsonElement workspaceEdit)
        {
            foreach (var document in workspaceEdit.GetProperty("documentChanges").EnumerateArray())
            {
                var identifier = document.GetProperty("textDocument");
                var file = texts.Keys.Single(file => Uri(file) == identifier.GetProperty("uri").GetString());
                if (versions[file] != identifier.GetProperty("version").GetInt64())
                    throw new InvalidOperationException("The fixture rejected an obsolete document edit.");
                var original = texts[file];
                var changed = original;
                foreach (var edit in document.GetProperty("edits").EnumerateArray().OrderByDescending(edit => Offset(original, edit.GetProperty("range").GetProperty("start"))))
                {
                    var range = edit.GetProperty("range");
                    var start = Offset(original, range.GetProperty("start"));
                    var end = Offset(original, range.GetProperty("end"));
                    changed = changed.Remove(start, end - start).Insert(start, edit.GetProperty("newText").GetString()!);
                }
                await Change(file, changed);
            }
        }

        internal async Task Change(string file, string text)
        {
            texts[file] = text;
            var version = ++versions[file];
            client.SendNotification("textDocument/didChange", new { textDocument = new { uri = Uri(file), version }, contentChanges = new[] { new { text } } });
            await Published(file, version);
        }

        private Task<JsonElement> Response(string id) => client.WaitForAsync(message =>
            message.TryGetProperty("id", out var value) && value.GetString() == id);
        private Task<JsonElement> Published(string file, long version) => client.WaitForAsync(message =>
            message.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics"
            && message.GetProperty("params").GetProperty("uri").GetString() == Uri(file)
            && message.GetProperty("params").GetProperty("version").GetInt64() == version);
        private static (int Line, int Character) Position(string text, int offset)
        {
            var line = 0;
            var character = 0;
            foreach (var value in text.AsSpan(0, offset))
            {
                if (value == '\n')
                {
                    line++;
                    character = 0;
                }
                else
                {
                    character++;
                }
            }
            return (line, character);
        }
        private static int Offset(string text, JsonElement position)
        {
            var line = position.GetProperty("line").GetInt32();
            var start = 0;
            for (var index = 0; index < line; index++)
                start = text.IndexOf('\n', start) + 1;
            return start + position.GetProperty("character").GetInt32();
        }
        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}
