using System.Text.Json;
using LithoSharp.Inspection;

namespace LithoSharp.Tests;

public sealed class FrontMatterSchemaLspTests
{
    private const string Schema = """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","x-lithosharp-schemaVersion":"1.0","$ref":"#/$defs/Front","$defs":{"Front":{"type":"object","additionalProperties":false,"properties":{"heading":{"type":"string","x-lithosharp-clrType":"System.String","description":"The custom heading."},"priority":{"anyOf":[{"type":"string"},{"type":"integer"}],"x-lithosharp-enumNames":["Low","High"],"x-lithosharp-enumUnderlyingType":"System.Int32"},"audience":{"type":"string","x-lithosharp-clrType":"System.String"}},"required":["heading"]}}}
        """;

    [Test]
    public async Task CustomFieldsValuesAndHoverFollowTheCurrentAcquiredSchema()
    {
        await using var client = LspTestClient.Start();
        var initialize = client.NextId();
        client.SendRequest(initialize, "initialize", new { capabilities = new { } });
        await Response(initialize);
        client.SendNotification("initialized", new { });
        var path = Path.GetFullPath(Path.Combine("schema-editor", "guide.md"));
        var uri = new Uri(path).AbsoluteUri;
        SendSchema(1, Schema);
        client.SendNotification("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "markdown", version = 1, text = "---\nheading: Hello\npriority: Low\n---\nBody" },
        });
        await Published(1);

        var fields = await Request("textDocument/completion", 1, 2);
        await Assert.That(fields.EnumerateArray().Select(item => item.GetProperty("label").GetString())).Contains("audience");
        var values = await Request("textDocument/completion", 2, 11);
        await Assert.That(values.EnumerateArray().Select(item => item.GetProperty("label").GetString())).IsEquivalentTo(new string?[] { "Low", "High" });
        var hover = await Request("textDocument/hover", 1, 2);
        await Assert.That(hover.GetProperty("contents").GetProperty("value").GetString()).Contains("The custom heading.");

        SendSchema(2, Schema.Replace("High", "Urgent", StringComparison.Ordinal));
        await Published(2);
        var updated = await Request("textDocument/completion", 2, 11);
        await Assert.That(updated.EnumerateArray().Select(item => item.GetProperty("label").GetString())).IsEquivalentTo(new string?[] { "Low", "Urgent" });

        client.SendNotification("lithosharp/projectContext", new
        {
            projectId = "schema-project",
            folders = Array.Empty<string>(),
            snapshot = new { schemaVersion = "unsupported" },
            lithosharpContextGeneration = 3,
        });
        await Published(3);
        await Assert.That((await Request("textDocument/completion", 1, 2)).GetArrayLength()).IsEqualTo(0);

        client.SendNotification("lithosharp/projectContext", new
        {
            projectId = "schema-project",
            folders = Array.Empty<string>(),
            snapshot = (object?)null,
            lithosharpContextGeneration = 4,
        });
        await Published(4);
        await Assert.That((await Request("textDocument/completion", 1, 2)).GetArrayLength()).IsEqualTo(0);

        void SendSchema(long generation, string json)
        {
            var hash = "sha256:" + new string((char)('0' + generation), 64);
            var schema = FrontMatterSchemaSnapshot.Create("schema-project", generation, hash, "guides", json, [path]);
            var snapshot = ProjectInspectionSnapshot.Create("schema-project", generation, "guides", "markdown", "Front", "current", "en")
                .WithSchemas(hash, [schema]);
            client.SendNotification("lithosharp/projectContext", new
            {
                projectId = "schema-project",
                folders = Array.Empty<string>(),
                snapshot = JsonSerializer.Deserialize<JsonElement>(snapshot.ToJson()),
                lithosharpContextGeneration = generation,
            });
        }

        async Task<JsonElement> Request(string method, int line, int character)
        {
            var id = client.NextId();
            client.SendRequest(id, method, new { textDocument = new { uri }, position = new { line, character } });
            return (await Response(id)).GetProperty("result");
        }

        Task<JsonElement> Response(string id) => client.WaitForAsync(message => message.TryGetProperty("id", out var value) && value.GetString() == id);
        Task<JsonElement> Published(long generation) => client.WaitForAsync(message =>
            message.TryGetProperty("method", out var method) && method.GetString() == "textDocument/publishDiagnostics"
            && message.GetProperty("params").GetProperty("uri").GetString() == uri
            && message.GetProperty("params").GetProperty("lithosharpContextGeneration").GetInt64() == generation);
    }
}
