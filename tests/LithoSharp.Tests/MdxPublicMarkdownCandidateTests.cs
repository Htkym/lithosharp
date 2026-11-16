using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Mdx;

namespace LithoSharp.Tests;

public sealed class MdxPublicMarkdownCandidateTests
{
    private static MdxDocument Source() => new("# 😀\r\nBody.", bodyStartLine: 4, bodyStartOffset: 30) { HasOriginalBodySnapshot = true };
    private static JsonElement Page(MdxDocument source, string status = "complete", string? markdown = "# Public\n", string? sourceHash = null) =>
        JsonSerializer.SerializeToElement(new
        {
            publicMarkdown = new
            {
                schemaVersion = 1,
                implementationId = MdxPublicMarkdownCandidate.CurrentImplementationId,
                status,
                markdown,
                sourceHash = sourceHash ?? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.CompilerSource)))
            }
        });

    [Test]
    public async Task CompleteCandidateRequiresExactCompilerSnapshotAndKeepsAnEmptyCompleteBody()
    {
        var source = Source();
        var candidate = MdxPublicMarkdownCandidate.FromWorker(Page(source), source);
        await Assert.That(candidate.IsComplete).IsTrue();
        await Assert.That(candidate.Markdown).IsEqualTo("# Public\n");
        await Assert.That(MdxPublicMarkdownCandidate.FromWorker(Page(source, markdown: ""), source).IsComplete).IsTrue();
        await Assert.That(MdxPublicMarkdownCandidate.FromWorker(Page(source, sourceHash: "wrong"), source).Status).IsEqualTo(MdxPublicMarkdownStatus.Unavailable);
        var changed = new MdxDocument(source.Body + "changed", source.BodyStartLine, source.BodyStartOffset) { HasOriginalBodySnapshot = true };
        await Assert.That(MdxPublicMarkdownCandidate.FromWorker(Page(source), changed).IsComplete).IsFalse();
    }

    [Test]
    public async Task UnknownOriginsAndIncompleteResultsNeverFallBackToPlainText()
    {
        var source = Source();
        var transformed = new MdxDocument(source.Body, source.BodyStartLine) { PlainText = "Not Markdown" };
        await Assert.That(MdxPublicMarkdownCandidate.FromWorker(Page(transformed), transformed).Status).IsEqualTo(MdxPublicMarkdownStatus.Unavailable);
        await Assert.That(MdxPublicMarkdownCandidate.FromWorker(Page(source, status: "unsupported", markdown: "must not leak"), source).Markdown).IsNull();
        await Assert.That(MdxPublicMarkdownCandidate.FromWorker(Page(source, markdown: null), source).IsComplete).IsFalse();
        await Assert.That(MdxPublicMarkdownCandidate.FromWorker(JsonSerializer.SerializeToElement(new { text = "Plain text is not a candidate" }), source).IsComplete).IsFalse();
    }

    [Test]
    public async Task MalformedOrFutureWorkerContractsFailClosed()
    {
        var source = Source();
        foreach (var json in new[] { "null", "[]", "{}", "{\"publicMarkdown\":null}", "{\"publicMarkdown\":{\"schemaVersion\":\"1\"}}", "{\"publicMarkdown\":{\"schemaVersion\":2}}" })
        {
            using var document = JsonDocument.Parse(json);
            await Assert.That(MdxPublicMarkdownCandidate.FromWorker(document.RootElement, source).Status).IsEqualTo(MdxPublicMarkdownStatus.Unavailable);
        }
        await Assert.That(MdxPublicMarkdownCandidate.FromWorker(Page(source, status: "future-state"), source).IsComplete).IsFalse();
    }
}
