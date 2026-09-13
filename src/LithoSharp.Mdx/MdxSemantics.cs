using System.Text.Json;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;

namespace LithoSharp.Mdx;

/// <summary>
/// Maps worker-extracted MDX information (headings, links, text, islands) into the
/// common <see cref="DocumentSemantics"/> model. The worker reports file lines only,
/// so spans cover the corresponding body line (line precision, no false precision).
/// Arbitrarily generated React content is never inferred: only statically extracted
/// information is mapped, and final-DOM validation stays responsible for the rest.
/// </summary>
internal static class MdxSemantics
{
    /// <summary>Builds document semantics from a worker page response.</summary>
    /// <param name="sourcePath">The entry's root-relative source path.</param>
    /// <param name="body">The MDX source document (for line mapping).</param>
    /// <param name="headings">The worker <c>headings</c> array (depth/text/line/id?).</param>
    /// <param name="links">The worker <c>links</c> array (url/line/image?).</param>
    /// <param name="islands">The worker <c>islands</c> array (module/exportName?).</param>
    /// <param name="plainText">The worker-extracted plain text.</param>
    internal static DocumentSemantics FromWorker(
        string sourcePath,
        MdxDocument body,
        JsonElement headings,
        JsonElement links,
        JsonElement islands,
        string? plainText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(body);
        var lines = SplitLines(body.Body);
        var mappedHeadings = new List<DocumentHeading>();
        if (headings.ValueKind == JsonValueKind.Array)
        {
            foreach (var heading in headings.EnumerateArray())
            {
                if (heading.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var depth = GetInt(heading, "depth");
                if (depth is null or < 1 or > 6)
                {
                    continue;
                }

                var text = GetString(heading, "text") ?? string.Empty;
                mappedHeadings.Add(new DocumentHeading(
                    text,
                    GetString(heading, "id"),
                    depth.Value,
                    depth.Value,
                    LineSpan(lines, GetInt(heading, "line"), body.BodyStartLine, body.BodyStartOffset)));
            }
        }

        var mappedLinks = new List<DocumentLink>();
        var assets = new List<DocumentAsset>();
        if (links.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in links.EnumerateArray())
            {
                if (link.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var url = GetString(link, "url") ?? string.Empty;
                var span = LineSpan(lines, GetInt(link, "line"), body.BodyStartLine, body.BodyStartOffset);
                var image = GetBool(link, "image") == true;
                mappedLinks.Add(new DocumentLink(string.Empty, url, null, image, span));
                if (image && url.Length != 0)
                {
                    assets.Add(new DocumentAsset(url, span));
                }
            }
        }

        var components = new List<DocumentComponent>();
        if (islands.ValueKind == JsonValueKind.Array)
        {
            // Selective-hydration islands only: page-level hydration components cannot
            // be inventoried statically and stay with worker inspection instead.
            foreach (var island in islands.EnumerateArray())
            {
                if (island.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var module = GetString(island, "module");
                var exportName = GetString(island, "exportName");
                if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(exportName))
                {
                    continue;
                }

                components.Add(new DocumentComponent(module + "#" + exportName, SourceSpan.Empty));
            }
        }

        return new DocumentSemantics(
            new DocumentSource(sourcePath, 0),
            DocumentSemantics.TitleOf(mappedHeadings),
            plainText ?? string.Empty,
            mappedHeadings,
            mappedLinks,
            assets,
            components,
            []);
    }

    private static SourceSpan LineSpan(List<(int Start, int Length)> lines, int? fileLine, int bodyStartLine, int bodyStartOffset)
    {
        if (fileLine is null)
        {
            return SourceSpan.Empty;
        }

        var index = fileLine.Value - bodyStartLine;
        if (index < 0 || index >= lines.Count)
        {
            return SourceSpan.Empty;
        }

        var (start, length) = lines[index];
        return length == 0 ? SourceSpan.Empty : new SourceSpan(bodyStartOffset + start, length);
    }

    private static List<(int Start, int Length)> SplitLines(string text)
    {
        var lines = new List<(int Start, int Length)>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                lines.Add((start, index - start));
                start = index + 1 < text.Length && text[index + 1] == '\n' ? index + 2 : index + 1;
                index = start - 1;
            }
            else if (text[index] == '\n')
            {
                lines.Add((start, index - start));
                start = index + 1;
            }
        }

        lines.Add((start, text.Length - start));
        return lines;
    }

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;
}
