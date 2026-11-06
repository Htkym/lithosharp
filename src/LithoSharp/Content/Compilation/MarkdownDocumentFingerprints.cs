using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LithoSharp.Content.Compilation;

/// <summary>
/// Document fingerprints with separated concerns for incremental compilation:
/// source (raw bytes), syntax (compiler fingerprint on <see cref="IMarkdownCompiler"/>),
/// semantic (resolved document information), and render (layout/theme inputs on the caller).
/// </summary>
internal static class MarkdownDocumentFingerprints
{
    private static readonly UTF8Encoding StrictCacheUtf8 = new(false, true);

    /// <summary>
    /// Keeps valid UTF-8 cache hashes unchanged while distinguishing malformed UTF-16
    /// from literal replacement characters before any successful cache can be reused.
    /// </summary>
    internal static string ExactBodyCacheHash(string markdownBody)
    {
        ArgumentNullException.ThrowIfNull(markdownBody);
        byte[] bytes;
        try
        {
            bytes = StrictCacheUtf8.GetBytes(markdownBody);
        }
        catch (EncoderFallbackException)
        {
            // FF cannot begin valid UTF-8, so this exact little-endian code-unit
            // domain cannot share an encoded input with a valid body.
            bytes = new byte[checked(markdownBody.Length * 2 + 1)];
            bytes[0] = 0xff;
            for (var index = 0; index < markdownBody.Length; index++)
            {
                bytes[index * 2 + 1] = (byte)markdownBody[index];
                bytes[index * 2 + 2] = (byte)(markdownBody[index] >> 8);
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Source fingerprint: SHA-256 over the UTF-8 Markdown body.</summary>
    public static string SourceHash(string markdownBody)
    {
        ArgumentNullException.ThrowIfNull(markdownBody);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(markdownBody)));
    }

    /// <summary>
    /// Semantic fingerprint: SHA-256 over resolved headings, links, assets, and plain text.
    /// Source spans and file identity are excluded: identical meaning hashes identically
    /// even when positions differ. Emphasis-marker variants (for example, `*a*` versus
    /// `_a_`) resolve to the same tree and share a hash.
    /// </summary>
    public static string SemanticHash(DocumentSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        var canonical = JsonSerializer.Serialize(new
        {
            PlainText = semantics.PlainText,
            Headings = semantics.Headings.Select(heading => new
            {
                heading.Text,
                heading.Id,
                heading.RawLevel,
                heading.OutputLevel,
            }).ToArray(),
            Links = semantics.Links.Select(link => new
            {
                link.RawText,
                link.Url,
                link.Title,
                link.IsImage,
            }).ToArray(),
            Assets = semantics.Assets.Select(asset => asset.Url).ToArray(),
        });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
