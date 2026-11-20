using LithoSharp.Content;
using LithoSharp.Diagnostics;
using Syntamark;

namespace LithoSharp.OpenKnowledge;

public enum OkfLinkKind { Concept, ReservedDocument, Auxiliary, External, Unresolved, OutsideBundle, NonConceptDocument, ScopeDescriptor }

/// <summary>An original Markdown relation and its optional public URL projection.</summary>
public sealed class OkfLink
{
    internal OkfLink(string sourcePath, MarkdownLink syntax, string? targetPath, string? conceptId, string? publicUrl, OkfLinkKind kind, bool allowsConceptEdge = true)
        => (SourcePath, Syntax, TargetPath, TargetConceptId, PublicUrl, Kind, this.allowsConceptEdge) = (sourcePath, syntax, targetPath, conceptId, publicUrl, kind, allowsConceptEdge);
    public string SourcePath { get; }
    public MarkdownLink Syntax { get; }
    public string? OriginalTarget => Syntax.Target;
    public string? TargetPath { get; }
    public string? TargetConceptId { get; }
    public string? PublicUrl { get; }
    public OkfLinkKind Kind { get; }
    public bool IsResolved => PublicUrl is not null;
    private readonly bool allowsConceptEdge;
    public bool IsConceptEdge => allowsConceptEdge && !Syntax.Image && TargetConceptId is not null;
}

/// <summary>A v0.3 frontmatter path or URI, resolved without opening any target.</summary>
public sealed record OkfPathReference(string SourcePath, string Field, string OriginalTarget, string? TargetPath,
    string? TargetConceptId, string? PublicUrl, OkfLinkKind Kind)
{
    public bool IsResolved => Kind is OkfLinkKind.Concept or OkfLinkKind.ReservedDocument
        or OkfLinkKind.Auxiliary or OkfLinkKind.NonConceptDocument or OkfLinkKind.External;
    public bool IsConceptEdge => TargetConceptId is not null;
}

public sealed class OkfCitation
{
    internal OkfCitation(MarkdownFootnoteReference reference, OkfSource? source) => (Reference, Source) = (reference, source);
    public MarkdownFootnoteReference Reference { get; }
    public OkfSource? Source { get; }
}

public sealed partial class OkfBundle
{
    private IReadOnlyList<OkfLink> ResolveLinks(List<SiteDiagnostic> diagnostics)
    {
        var result = new List<OkfLink>();
        var targets = IsVersion03 ? new Version03Targets(this) : null;
        foreach (var document in Documents)
        {
            if (document.IsSkipped || document.Syntax?.Analysis.Links is not { } links)
                continue;
            foreach (var link in links.Where(static link => link.Kind != MarkdownLinkKind.ReferenceDefinition))
            {
                var resolved = targets is null ? ResolveLink(document, link) : ResolveVersion03Link(targets, document, link);
                result.Add(resolved);
                if (!resolved.IsResolved && (targets is null || resolved.Kind != OkfLinkKind.NonConceptDocument))
                    Add(diagnostics, "OKF120", SiteDiagnosticSeverity.Warning, OkfDiagnosticCategory.Quality,
                        $"Link target '{link.Target ?? link.ReferenceLabel}' is unavailable; the original relation was retained.",
                        document.Path, document.Syntax.Analysis, link.TargetRawSpan ?? link.RawSpan);
            }
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static OkfLink ResolveVersion03Link(Version03Targets targets, OkfDocument document, MarkdownLink link)
    {
        var target = targets.Resolve(document.Path, link.Target, markdownLink: true);
        return new(document.Path, link, target.Path, target.ConceptId, target.PublicUrl, target.Kind, document.IsConcept && target.Path != document.Path);
    }

    private IReadOnlyList<OkfPathReference> ResolvePathReferences()
    {
        if (!IsVersion03)
            return [];
        var targets = new Version03Targets(this);
        var result = new List<OkfPathReference>();
        foreach (var document in Documents)
        {
            if (!document.IsConcept || document.Metadata is not { } metadata)
                continue;

            AddField("resource", metadata.Resource);
            var sources = metadata.GetField("sources");
            if (sources?.Kind == MarkdownYamlKind.Mapping)
                AddField("sources[0].resource", OkfMetadata.String(OkfMetadata.Field(sources, "resource")), allowScope: true);
            else if (sources?.Kind == MarkdownYamlKind.Sequence)
                for (var i = 0; i < sources.Children.Count; i++)
                    AddField($"sources[{i}].resource", OkfMetadata.String(OkfMetadata.Field(sources.Children[i], "resource")), allowScope: true);
            AddField("computation", OkfMetadata.String(metadata.Computation));
            AddField("executor.resource", OkfMetadata.String(OkfMetadata.Field(metadata.Executor, "resource")));
            AddField("attester.resource", OkfMetadata.String(OkfMetadata.Field(metadata.Attester, "resource")));
            AddField("superseded_by", metadata.SupersededBy);

            void AddField(string name, string? value, bool allowScope = false)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return;
                // The specification encodes spaces in path fields. Free-form scope labels are not URI references.
                var target = allowScope && value.Any(char.IsWhiteSpace)
                    ? new Version03Target(OkfLinkKind.ScopeDescriptor)
                    : targets.Resolve(document.Path, value, markdownLink: false);
                result.Add(new(document.Path, name, value, target.Path, target.ConceptId, target.PublicUrl, target.Kind));
            }
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private readonly record struct Version03Target(OkfLinkKind Kind, string? Path = null, string? ConceptId = null,
        string? PublicUrl = null);

    private sealed class Version03Targets(OkfBundle bundle)
    {
        private readonly Dictionary<string, OkfFile> files = bundle.Files.ToDictionary(static file => file.Path, StringComparer.Ordinal);
        private readonly Dictionary<string, OkfDocument> documents = bundle.Documents.ToDictionary(static doc => doc.Path, StringComparer.Ordinal);

        internal Version03Target Resolve(string sourcePath, string? target, bool markdownLink)
        {
            if (target is null || target.Any(char.IsControl) || !markdownLink && target.Any(char.IsWhiteSpace))
                return new(OkfLinkKind.Unresolved);
            if (target.Contains('\\'))
                return new(OkfLinkKind.OutsideBundle);
            if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
            {
                if (absolute.Scheme is "javascript" or "vbscript" or "data")
                    return new(OkfLinkKind.Unresolved);
                if (absolute.Scheme != Uri.UriSchemeFile)
                    return new(OkfLinkKind.External, PublicUrl: target);
                // Uri also interprets bundle-root paths as file URIs; only explicit file URIs are external.
                if (target.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || !target.StartsWith('/'))
                    return new(OkfLinkKind.OutsideBundle);
            }
            if (target.StartsWith("//", StringComparison.Ordinal))
                return new(OkfLinkKind.External, PublicUrl: target);

            var separator = target.IndexOfAny(['?', '#']);
            var path = separator < 0 ? target : target[..separator];
            var suffix = separator < 0 ? string.Empty : target[separator..];
            var pieces = new List<string>();
            if (!path.StartsWith('/'))
                pieces.AddRange(sourcePath.Split('/').SkipLast(1));
            foreach (var encoded in path.Split('/'))
            {
                if (encoded.Length == 0)
                    continue;
                var segment = Uri.UnescapeDataString(encoded);
                if (segment == ".")
                    continue;
                if (segment == "..")
                {
                    if (pieces.Count == 0)
                        return new(OkfLinkKind.OutsideBundle);
                    pieces.RemoveAt(pieces.Count - 1);
                }
                else if (segment.IndexOfAny(['/', '\\']) >= 0 || segment.Any(char.IsControl))
                    return new(OkfLinkKind.OutsideBundle);
                else
                    pieces.Add(segment);
            }
            if (path.Length != 0 && (path.EndsWith('/') || pieces.Count == 0))
            {
                if (!markdownLink)
                    return new(OkfLinkKind.Unresolved);
                pieces.Add("index.md");
            }
            var relative = path.Length == 0 ? sourcePath : string.Join('/', pieces);
            string normalized;
            try
            {
                normalized = ContentInputPolicy.NormalizePath(relative);
            }
            catch (ArgumentException)
            {
                return new(OkfLinkKind.OutsideBundle);
            }
            if (!files.TryGetValue(relative, out var file))
                return new(OkfLinkKind.Unresolved, relative);

            documents.TryGetValue(relative, out var document);
            var conceptId = document?.IsConcept == true ? document.ConceptId : null;
            var kind = conceptId is not null ? OkfLinkKind.Concept : file.Kind switch
            {
                ContentInputKind.Auxiliary => OkfLinkKind.Auxiliary,
                ContentInputKind.Index or ContentInputKind.Log => OkfLinkKind.ReservedDocument,
                _ => OkfLinkKind.NonConceptDocument,
            };
            if (kind == OkfLinkKind.NonConceptDocument || bundle.Catalog is not { } catalog)
                return new(kind, relative, conceptId);
            var fragmentAt = suffix.IndexOf('#');
            if (markdownLink && fragmentAt >= 0 && suffix.Length > fragmentAt + 1 && kind != OkfLinkKind.Auxiliary)
            {
                var fragment = Uri.UnescapeDataString(suffix[(fragmentAt + 1)..]);
                if (document?.Syntax?.Analysis.Status != MarkdownParseStatus.Complete
                    || !(document.Syntax.Analysis.Headings?.Any(heading => heading.Anchor == fragment) ?? false))
                    return new(OkfLinkKind.Unresolved, relative, conceptId);
            }
            return new(kind, relative, conceptId, catalog.GetInput(normalized).Route.PublicPath + suffix);
        }
    }

    private OkfLink ResolveLink(OkfDocument document, MarkdownLink link)
    {
        var target = link.Target;
        if (target is null)
            return new(document.Path, link, null, null, null, OkfLinkKind.Unresolved);
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute) && absolute.Scheme is "javascript" or "vbscript" or "data")
            return new(document.Path, link, null, null, null, OkfLinkKind.Unresolved);
        if (target.StartsWith("//", StringComparison.Ordinal)
            || absolute is not null && absolute.Scheme != Uri.UriSchemeFile)
            return new(document.Path, link, null, null, target, OkfLinkKind.External);
        var separator = target.IndexOfAny(['?', '#']);
        var path = separator < 0 ? target : target[..separator];
        var suffix = separator < 0 ? string.Empty : target[separator..];
        var pieces = new List<string>();
        if (!path.StartsWith('/'))
            pieces.AddRange(document.Path.Split('/').SkipLast(1));
        foreach (var encoded in path.Split('/'))
        {
            if (encoded.Length == 0 || encoded == ".")
                continue;
            string segment;
            try
            {
                segment = Uri.UnescapeDataString(encoded);
            }
            catch (UriFormatException)
            {
                return new(document.Path, link, null, null, null, OkfLinkKind.Unresolved);
            }
            if (segment == "..")
            {
                if (pieces.Count == 0)
                    return new(document.Path, link, null, null, null, OkfLinkKind.OutsideBundle);
                pieces.RemoveAt(pieces.Count - 1);
            }
            else if (segment.IndexOfAny(['/', '\\', '\0']) >= 0)
                return new(document.Path, link, null, null, null, OkfLinkKind.OutsideBundle);
            else
                pieces.Add(segment);
        }
        string relative;
        try
        {
            relative = path.Length == 0 ? ContentInputPolicy.NormalizePath(document.Path)
                : ContentInputPolicy.NormalizePath(string.Join('/', pieces) + (path.EndsWith('/') || pieces.Count == 0
                    ? (pieces.Count == 0 ? "index.md" : "/index.md") : string.Empty));
        }
        catch (ArgumentException)
        {
            return new(document.Path, link, null, null, null, OkfLinkKind.OutsideBundle);
        }
        if (Catalog is null)
            return new(document.Path, link, relative, null, null, OkfLinkKind.Unresolved);
        StaticContentCatalogEntry? entry;
        try
        {
            entry = Catalog.GetInput(relative);
        }
        catch (KeyNotFoundException)
        {
            entry = null;
        }
        if (entry is null)
            return new(document.Path, link, relative, relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? relative[..^3] : null,
                null, OkfLinkKind.Unresolved);
        var kind = entry.Kind == ContentInputKind.Concept ? OkfLinkKind.Concept
            : entry.Kind == ContentInputKind.Auxiliary ? OkfLinkKind.Auxiliary : OkfLinkKind.ReservedDocument;
        var originalPath = filesByPath[entry.InputPath].Path;
        var originalConceptId = entry.Kind == ContentInputKind.Concept ? originalPath[..^3] : null;
        var fragmentAt = suffix.IndexOf('#');
        if (fragmentAt >= 0 && suffix.Length > fragmentAt + 1 && entry.Kind != ContentInputKind.Auxiliary)
        {
            var fragment = Uri.UnescapeDataString(suffix[(fragmentAt + 1)..]);
            var targetDocument = Documents.FirstOrDefault(doc => doc.Path == originalPath);
            if (targetDocument?.Syntax?.Analysis.Status != MarkdownParseStatus.Complete
                || !(targetDocument.Syntax.Analysis.Headings?.Any(heading => heading.Anchor == fragment) ?? false))
                return new(document.Path, link, originalPath, originalConceptId, null, OkfLinkKind.Unresolved);
        }
        return new(document.Path, link, originalPath, originalConceptId, entry.Route.PublicPath + suffix, kind);
    }
}
