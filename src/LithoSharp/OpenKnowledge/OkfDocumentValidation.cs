using System.Globalization;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using Syntamark;

namespace LithoSharp.OpenKnowledge;

internal static class OkfDocumentValidation
{
    internal static OkfMetadata? FrontMatter(ContentInputKind kind, string inputPath, MarkdownFrontMatterState? state, YamlDocument? yaml,
        int yamlOffset, RawSpan? openingSpan, RawSpan? yamlSpan, DateTimeOffset asOf,
        Action<string, SiteDiagnosticSeverity, OkfDiagnosticCategory, string, RawSpan?> report)
    {
        var metadata = yaml is { Status: MarkdownParseStatus.Complete, Root.Kind: MarkdownYamlKind.Mapping }
            ? new OkfMetadata(yaml.Root, asOf, (id, message, node) => report(id, SiteDiagnosticSeverity.Warning,
                OkfDiagnosticCategory.Quality, message,
                node is null ? null : new RawSpan(yamlOffset + node.RawSpan.Start, node.RawSpan.Length))) : null;
        if (kind == ContentInputKind.Concept)
        {
            if (state is null or MarkdownFrontMatterState.Absent or MarkdownFrontMatterState.Empty or MarkdownFrontMatterState.Unterminated
                || yaml is { Status: MarkdownParseStatus.Complete, Root: null }
                || yaml?.Root is { Kind: not MarkdownYamlKind.Mapping })
                report("OKF004", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Structure,
                    "A concept requires a YAML frontmatter mapping.", openingSpan);
            else if (metadata is not null && string.IsNullOrWhiteSpace(metadata.Type))
                report("OKF005", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Structure,
                    "A concept requires a nonempty string type; scalar coercion is not applied.",
                    metadata.GetField("type") is { } type ? new RawSpan(yamlOffset + type.RawSpan.Start, type.RawSpan.Length) : yamlSpan);
        }
        else if (kind == ContentInputKind.Index && state != MarkdownFrontMatterState.Absent)
        {
            if (inputPath != "index.md" || yaml?.Status == MarkdownParseStatus.Complete
                && (metadata is null || string.IsNullOrWhiteSpace(OkfMetadata.String(metadata.GetField("okf_version")))))
                report("OKF006", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Structure,
                    "Only a bundle-root index may carry frontmatter, containing a string okf_version declaration.", openingSpan);
            else if (metadata is not null && OkfMetadata.String(metadata.GetField("okf_version")) != "0.2")
                report("OKF007", SiteDiagnosticSeverity.Warning, OkfDiagnosticCategory.Unsupported,
                    "The declared OKF version is not validated by this v0.2 consumer; known fields remain available.", yamlSpan);
        }
        return metadata;
    }

    internal static bool IsLogDate(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out _);
}
