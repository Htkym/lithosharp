using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LithoSharp.Mdx;

internal enum MdxPublicMarkdownStatus { Complete, Unsupported, Unavailable }

// This is a verified worker result, not a caller declaration that arbitrary components are static.
internal sealed record MdxPublicMarkdownCandidate(MdxPublicMarkdownStatus Status, string? Markdown, string? Reason)
{
    internal const string CurrentImplementationId = "mdx-static-markdown/1";
    internal string ImplementationId => CurrentImplementationId;
    internal bool IsComplete => Status == MdxPublicMarkdownStatus.Complete;

    internal static MdxPublicMarkdownCandidate FromWorker(JsonElement page, MdxDocument source)
    {
        if (!source.HasOriginalBodySnapshot)
            return Unavailable("The MDX body has no verified single original source.");
        if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("publicMarkdown", out var result) || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1
            || !String(result, "implementationId", out var implementation) || implementation != CurrentImplementationId
            || !String(result, "sourceHash", out var sourceHash)
            || sourceHash != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.CompilerSource)))
            || !String(result, "status", out var status))
            return Unavailable("The worker public Markdown result does not match this source and contract.");
        if (status == "unsupported")
            return new(MdxPublicMarkdownStatus.Unsupported, null, "The MDX body contains content without a complete static Markdown representation.");
        if (status != "complete" || !String(result, "markdown", out var markdown))
            return Unavailable("The worker did not provide a complete public Markdown body.");
        return new(MdxPublicMarkdownStatus.Complete, markdown, null);
    }

    private static bool String(JsonElement value, string name, out string? text)
    {
        text = value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
        return text is not null;
    }

    private static MdxPublicMarkdownCandidate Unavailable(string reason) => new(MdxPublicMarkdownStatus.Unavailable, null, reason);
}
