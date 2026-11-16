using System.Text;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using Syntamark;

namespace LithoSharp.OpenKnowledge;

public enum OkfSourceBodyKind { PortableMarkdown, MarkdownWithComponents, Mdx }

/// <summary>Explicit metadata and original Markdown, with a caller-supplied portable alternative for component or MDX input.</summary>
public sealed class OkfNewConcept
{
    public OkfNewConcept(string path, string metadataYaml, string originalMarkdown,
        OkfSourceBodyKind sourceBodyKind = OkfSourceBodyKind.PortableMarkdown, string? portableMarkdown = null)
    {
        _ = ContentInputPolicy.NormalizePath(path);
        if (ContentInputPolicy.Classify(path, ContentInputProfile.OpenKnowledgeFormat) != ContentInputKind.Concept)
            throw new ArgumentException("A new concept requires a non-reserved .md path.", nameof(path));
        ArgumentNullException.ThrowIfNull(metadataYaml);
        ArgumentNullException.ThrowIfNull(originalMarkdown);
        if (!Enum.IsDefined(sourceBodyKind))
            throw new ArgumentOutOfRangeException(nameof(sourceBodyKind));
        Path = path.Replace('\\', '/');
        MetadataYaml = metadataYaml;
        OriginalMarkdown = originalMarkdown;
        SourceBodyKind = sourceBodyKind;
        PortableMarkdown = portableMarkdown;
    }
    public string Path { get; }
    public string MetadataYaml { get; }
    public string OriginalMarkdown { get; }
    public OkfSourceBodyKind SourceBodyKind { get; }
    public string? PortableMarkdown { get; }
}

/// <summary>A new auxiliary snapshot. Scripts and computation files remain inert bytes.</summary>
public sealed class OkfNewAuxiliary
{
    private readonly byte[] bytes;
    public OkfNewAuxiliary(string path, ReadOnlyMemory<byte> content)
    {
        _ = ContentInputPolicy.NormalizePath(path);
        if (ContentInputPolicy.Classify(path, ContentInputProfile.OpenKnowledgeFormat) != ContentInputKind.Auxiliary)
            throw new ArgumentException("Use a concept conversion for Markdown documents.", nameof(path));
        Path = path.Replace('\\', '/');
        bytes = content.ToArray();
    }
    public string Path { get; }
    public byte[] GetOriginalBytes() => (byte[])bytes.Clone();
}

public sealed class OkfWriteResult
{
    internal OkfWriteResult(OkfNewConcept[] documents, OkfFile[] files, SiteDiagnostic[] diagnostics)
        => (Documents, Files, Diagnostics) = (Array.AsReadOnly(documents), Array.AsReadOnly(files), Array.AsReadOnly(diagnostics));
    public IReadOnlyList<OkfNewConcept> Documents { get; }
    public IReadOnlyList<OkfFile> Files { get; }
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
    public bool CanExport => !Diagnostics.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error);
    public async Task ExportAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (!CanExport)
            throw new InvalidOperationException("A failed or nonportable conversion cannot be exported as portable OKF. Original source remains available in Documents.");
        await OkfExport.WriteFilesAsync(Files, null, outputDirectory, cancellationToken).ConfigureAwait(false);
    }
}

public static class OkfWriter
{
    /// <summary>Creates new OKF snapshots using only explicit metadata and Markdown conversion rules.</summary>
    /// <remarks>No Date, Draft or Sources property is inferred from ordinary content. An explicitly supplied alternative is a producer declaration, not a guarantee about every Markdown implementation.</remarks>
    public static OkfWriteResult Prepare(IEnumerable<OkfNewConcept> concepts, IEnumerable<OkfNewAuxiliary>? auxiliary = null,
        DateTimeOffset? asOf = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(concepts);
        var documents = concepts.ToArray();
        if (documents.Any(static document => document is null))
            throw new ArgumentException("Concepts cannot contain null.", nameof(concepts));
        var assets = (auxiliary ?? []).ToArray();
        if (assets.Any(static asset => asset is null))
            throw new ArgumentException("Auxiliary files cannot contain null.", nameof(auxiliary));
        var diagnostics = new List<SiteDiagnostic>();
        var files = new List<OkfFile>();
        var paths = new HashSet<string>(ContentInputPolicy.PathComparer);
        var now = asOf ?? DateTimeOffset.UtcNow;
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Reserve(document.Path))
                continue;
            if (document.SourceBodyKind != OkfSourceBodyKind.PortableMarkdown && document.PortableMarkdown is null)
            {
                Error("OKFW001", OkfDiagnosticCategory.Unsupported,
                    "Component and MDX input requires an explicit portable Markdown alternative; the original source was retained.", document.Path);
                continue;
            }
            var body = document.PortableMarkdown ?? document.OriginalMarkdown;
            var payload = document.MetadataYaml.TrimEnd('\r', '\n') + "\n";
            var raw = "---\n" + payload + "---\n" + body;
            var analysis = MarkdownParser.Analyze(raw, MarkdownAnalysisParts.All, "okf-new", document.Path,
                options: new MarkdownParseOptions(profileId: MarkdownOptions.DocumentationProfile), cancellationToken: cancellationToken);
            var yaml = analysis.FrontMatter?.GetYamlSyntax(cancellationToken);
            if (analysis.FrontMatter?.YamlSpan is { } yamlSpan
                && !raw.AsSpan(yamlSpan.Start, yamlSpan.Length).SequenceEqual(payload))
            {
                Error("OKFW007", OkfDiagnosticCategory.Structure,
                    "Metadata must be one YAML payload without a frontmatter closing delimiter; no metadata was moved into the body.", document.Path);
                continue;
            }
            if (yaml is null || yaml.Status != MarkdownParseStatus.Complete)
            {
                if (yaml is not null)
                {
                    foreach (var diagnostic in yaml.Diagnostics)
                        Error("OKFW002", diagnostic.Kind == YamlDiagnosticKind.Unsupported ? OkfDiagnosticCategory.Unsupported
                            : diagnostic.Kind == YamlDiagnosticKind.ResourceLimit ? OkfDiagnosticCategory.ResourceLimit : OkfDiagnosticCategory.Structure,
                            diagnostic.Reason + ": " + diagnostic.Message, document.Path);
                    if (yaml.Diagnostics.Count == 0)
                        Error("OKFW002", OkfDiagnosticCategory.Unsupported, "The explicit metadata parse is incomplete.", document.Path);
                }
                else
                    Error("OKFW002", OkfDiagnosticCategory.Structure, "The explicit metadata could not be read as frontmatter.", document.Path);
                continue;
            }
            if (yaml.Root?.Kind != MarkdownYamlKind.Mapping || string.IsNullOrWhiteSpace(OkfMetadata.String(OkfMetadata.Field(yaml.Root, "type"))))
            {
                Error("OKFW003", OkfDiagnosticCategory.Structure, "New concepts require explicit mapping metadata with a nonempty string type.", document.Path);
                continue;
            }
            _ = new OkfMetadata(yaml.Root, now, (id, message, node) =>
            {
                var offset = analysis.FrontMatter!.YamlSpan!.Value.Start;
                OkfBundle.Add(diagnostics, id, id == "OKF118" ? SiteDiagnosticSeverity.Error : SiteDiagnosticSeverity.Warning,
                    OkfDiagnosticCategory.Quality, message, document.Path, analysis,
                    node is null ? null : new RawSpan(offset + node.RawSpan.Start, node.RawSpan.Length));
            });
            if (analysis.Root?.DescendantsAndSelf(cancellationToken).Any(static node => node.Kind == MarkdownSyntaxKind.Directive) == true)
                Error("OKFW004", OkfDiagnosticCategory.Unsupported,
                    "Structured components require a portable Markdown alternative that contains no component directives.", document.Path);
            foreach (var diagnostic in analysis.Diagnostics.Where(static diagnostic => diagnostic.Origin == "resource" || diagnostic.Id is "LMD002" or "LMD003"))
                Error("OKFW005", OkfDiagnosticCategory.ResourceLimit, diagnostic.Reason + ": " + diagnostic.Message, document.Path);
            files.Add(new OkfFile(document.Path, ContentInputKind.Concept, new UTF8Encoding(false, true).GetBytes(raw)));
        }
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Reserve(asset.Path))
                files.Add(new OkfFile(asset.Path, ContentInputKind.Auxiliary, asset.GetOriginalBytes()));
        }
        return new OkfWriteResult(documents, files.ToArray(), diagnostics.ToArray());

        bool Reserve(string path)
        {
            if (paths.Add(ContentInputPolicy.NormalizePath(path)))
                return true;
            Error("OKFW006", OkfDiagnosticCategory.Structure, "New output paths collide after case and NFC normalization.", path);
            return false;
        }
        void Error(string id, OkfDiagnosticCategory category, string message, string path) =>
            OkfBundle.Add(diagnostics, id, SiteDiagnosticSeverity.Error, category, message, path);
    }
}
