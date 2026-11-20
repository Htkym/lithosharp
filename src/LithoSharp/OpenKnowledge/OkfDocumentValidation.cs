using System.Globalization;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using Syntamark;

namespace LithoSharp.OpenKnowledge;

internal static class OkfDocumentValidation
{
    internal static OkfMetadata? FrontMatter(ContentInputKind kind, string inputPath, MarkdownFrontMatterState? state, YamlDocument? yaml,
        int yamlOffset, RawSpan? openingSpan, RawSpan? yamlSpan, DateTimeOffset asOf,
        Action<string, SiteDiagnosticSeverity, OkfDiagnosticCategory, string, RawSpan?> report, bool isVersion03 = false)
    {
        var metadata = yaml is { Status: MarkdownParseStatus.Complete, Root.Kind: MarkdownYamlKind.Mapping }
            ? new OkfMetadata(yaml.Root, asOf, (id, message, node) => report(id, SiteDiagnosticSeverity.Warning,
                OkfDiagnosticCategory.Quality, message,
                node is null ? null : new RawSpan(yamlOffset + node.RawSpan.Start, node.RawSpan.Length)), isVersion03) : null;
        if (kind == ContentInputKind.Concept)
        {
            if (isVersion03 && (state is null or MarkdownFrontMatterState.Absent
                || yaml is { Status: MarkdownParseStatus.Complete, Root.Kind: MarkdownYamlKind.Mapping }
                    && string.IsNullOrWhiteSpace(metadata?.Type)))
            {
                report("OKF122", SiteDiagnosticSeverity.Warning, OkfDiagnosticCategory.Quality,
                    "Markdown without a nonempty string type is not an OKF concept and was skipped.", yamlSpan);
                return metadata;
            }
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
            if (inputPath != "index.md")
                report("OKF006", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Structure,
                    "Only a bundle-root index may carry frontmatter, containing a string okf_version declaration.", openingSpan);
        }
        else if (isVersion03 && kind == ContentInputKind.Log && state != MarkdownFrontMatterState.Absent)
            report("OKF009", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Structure,
                "OKF 0.3 log.md must not contain frontmatter.", openingSpan);
        return metadata;
    }

    internal static bool IsLogDate(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out _);
}
