using LithoSharp.Content;
using LithoSharp.Diagnostics;
using Syntamark;

namespace LithoSharp.OpenKnowledge;

public enum OkfLinkKind { Concept, ReservedDocument, Auxiliary, External, Unresolved, OutsideBundle }

/// <summary>An original Markdown relation and its optional public URL projection.</summary>
public sealed class OkfLink
{
    internal OkfLink(string sourcePath, MarkdownLink syntax, string? targetPath, string? conceptId, string? publicUrl, OkfLinkKind kind)
        => (SourcePath, Syntax, TargetPath, TargetConceptId, PublicUrl, Kind) = (sourcePath, syntax, targetPath, conceptId, publicUrl, kind);
    public string SourcePath { get; }
    public MarkdownLink Syntax { get; }
    public string? OriginalTarget => Syntax.Target;
    public string? TargetPath { get; }
    public string? TargetConceptId { get; }
    public string? PublicUrl { get; }
    public OkfLinkKind Kind { get; }
    public bool IsResolved => PublicUrl is not null;
    public bool IsConceptEdge => !Syntax.Image && TargetConceptId is not null;
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
        foreach (var document in Documents)
        {
            if (document.Syntax?.Analysis.Links is not { } links)
                continue;
            foreach (var link in links.Where(static link => link.Kind != MarkdownLinkKind.ReferenceDefinition))
            {
                var resolved = ResolveLink(document, link);
                result.Add(resolved);
                if (!resolved.IsResolved)
                    Add(diagnostics, "OKF120", SiteDiagnosticSeverity.Warning, OkfDiagnosticCategory.Quality,
                        $"Link target '{link.Target ?? link.ReferenceLabel}' is unavailable; the original relation was retained.",
                        document.Path, document.Syntax.Analysis, link.TargetRawSpan ?? link.RawSpan);
            }
        }
        return Array.AsReadOnly(result.ToArray());
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
