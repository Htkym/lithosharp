using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LithoSharp.Content;
using LithoSharp.OpenKnowledge;
using Syntamark;

namespace LithoSharp.Inspection;

public enum DocumentReferenceState { Resolved, Ambiguous, Unresolved, Unavailable }

/// <summary>An immutable editor buffer. ProjectPath is the explicit path used by the acquired project catalog.</summary>
public sealed class DocumentReferenceDocument
{
    public DocumentReferenceDocument(string sourcePath, string text, long version, string? projectPath = null, bool isDynamic = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(text);
        if (projectPath is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        if (version < 0)
            throw new ArgumentOutOfRangeException(nameof(version));
        SourcePath = sourcePath;
        ProjectPath = DocumentReferenceSnapshot.PathKey(projectPath ?? sourcePath);
        Text = text;
        Version = version;
        IsDynamic = isDynamic;
        TextHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.Unicode.GetBytes(text)));
    }
    public string SourcePath { get; }
    public string ProjectPath { get; }
    public string Text { get; }
    public long Version { get; }
    public string TextHash { get; }
    public bool IsDynamic { get; }
}

public sealed class DocumentReferenceLocation
{
    internal DocumentReferenceLocation(string sourcePath, int start, int length)
        => (SourcePath, Start, Length) = (sourcePath, start, length);
    public string SourcePath { get; }
    public int Start { get; }
    public int Length { get; }
}

public sealed class DocumentReferenceTarget
{
    internal DocumentReferenceTarget(ProjectRouteCandidate route, string? anchor, DocumentReferenceLocation? location)
        => (Route, Anchor, Location) = (route, anchor, location);
    public ProjectRouteCandidate Route { get; }
    public ContentInputKind InputKind => Route.InputKind;
    public string? Anchor { get; }
    public DocumentReferenceLocation? Location { get; }
}

/// <summary>A parser-recognized link or heading; no positions are inferred from string searches.</summary>
public sealed class DocumentReferenceOccurrence
{
    internal DocumentReferenceOccurrence(DocumentReferenceDocument document, MarkdownLink? link, MarkdownHeading? heading)
        => (Document, Link, Heading) = (document, link, heading);
    public DocumentReferenceDocument Document { get; }
    public MarkdownLink? Link { get; }
    public MarkdownHeading? Heading { get; }
    public RawSpan Range => Link?.RawSpan ?? Heading!.RawSpan;
    public RawSpan? TargetSpan => Link?.TargetRawSpan;
}

public sealed class DocumentReferenceResolution
{
    internal DocumentReferenceResolution(DocumentReferenceState state, DocumentReferenceOccurrence? reference,
        IEnumerable<DocumentReferenceTarget> candidates, ProjectReferenceRelation? relation = null)
        => (State, Reference, Candidates, Relation) = (state, reference, Array.AsReadOnly(candidates.ToArray()), relation);
    public DocumentReferenceState State { get; }
    public DocumentReferenceOccurrence? Reference { get; }
    public IReadOnlyList<DocumentReferenceTarget> Candidates { get; }
    public ProjectReferenceRelation? Relation { get; }
    public OkfLinkKind? RelationKind => Relation?.Kind;
}

public sealed class DocumentReferenceCompletion
{
    internal DocumentReferenceCompletion(string label, string insertText, DocumentReferenceTarget target)
        => (Label, InsertText, Target) = (label, insertText, target);
    public string Label { get; }
    public string InsertText { get; }
    public DocumentReferenceTarget Target { get; }
}

public sealed class DocumentReferenceSearch
{
    internal DocumentReferenceSearch(DocumentReferenceState state, IEnumerable<DocumentReferenceLocation> locations, IEnumerable<string> manualScopes)
        => (State, Locations, ManualScopes) = (state, Array.AsReadOnly(locations.ToArray()), Array.AsReadOnly(manualScopes.Distinct(StringComparer.Ordinal).ToArray()));
    public DocumentReferenceState State { get; }
    public IReadOnlyList<DocumentReferenceLocation> Locations { get; }
    public IReadOnlyList<string> ManualScopes { get; }
    public bool IsComplete => ManualScopes.Count == 0;
}

/// <summary>A bounded Markdown reference view over an explicitly acquired route catalog and exact editor buffers.</summary>
/// <remarks>Construction performs no disk access or user-code execution. Missing captures do not prove that references are absent.</remarks>
public sealed partial class DocumentReferenceSnapshot
{
    private sealed record Parsed(DocumentReferenceDocument Document, MarkdownSyntaxDocument Syntax, int BodyStart);
    private readonly IReadOnlyList<Parsed> parsed;
    private readonly IReadOnlyList<string> manualScopes;
    private readonly MarkdownParseOptions options;
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public DocumentReferenceSnapshot(ProjectInspectionSnapshot project, IEnumerable<DocumentReferenceDocument> documents,
        bool catalogComplete = false, CancellationToken cancellationToken = default)
    {
        Project = project ?? throw new ArgumentNullException(nameof(project));
        ArgumentNullException.ThrowIfNull(documents);
        options = new MarkdownParseOptions(MarkdownFrontMatterRecovery.TreatAsBody, profileId: project.MarkdownProfileId);
        var captures = documents.Take(65).ToArray();
        if (captures.Any(static document => document is null))
            throw new ArgumentException("Document captures must not contain null.", nameof(documents));
        if (captures.GroupBy(document => PathKey(document.SourcePath), PathComparer).Any(static group => group.Count() != 1))
            throw new ArgumentException("Document captures must have unique physical identities.", nameof(documents));
        if (captures.GroupBy(static document => document.ProjectPath, PathComparer).Any(static group => group.Count() != 1))
            throw new ArgumentException("Project-relative document captures must be unambiguous.", nameof(documents));
        var scopes = new List<string>();
        if (captures.Length > 64)
            scopes.Add("The 64-document editor reference limit was reached; remaining inputs were not inspected.");
        var available = new List<Parsed>();
        long textBytes = 0;
        var nodes = 0;
        foreach (var document in captures.Take(64))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Project.InputFor(document.ProjectPath)?.InputKind == ContentInputKind.Auxiliary)
                continue;
            if (document.IsDynamic || document.ProjectPath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
            {
                scopes.Add("Dynamic MDX references require separate analysis: " + document.SourcePath);
                continue;
            }
            if (!document.ProjectPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                scopes.Add("The input is outside the declared Markdown reference scope: " + document.SourcePath);
                continue;
            }
            if (document.Text.Length > options.MaxInputUtf16 || textBytes + 2L * document.Text.Length > 16 * 1024 * 1024)
            {
                scopes.Add("The bounded editor reference input budget was exceeded: " + document.SourcePath);
                continue;
            }
            var header = MarkdownParser.ScanFrontMatter(document.Text, cancellationToken: cancellationToken);
            if (header.Status != MarkdownParseStatus.Complete
                || header.State is MarkdownFrontMatterScanState.Unterminated or MarkdownFrontMatterScanState.Unknown)
            {
                scopes.Add("Unavailable frontmatter boundaries cannot establish editable body ranges: " + document.SourcePath);
                continue;
            }
            if (header.State == MarkdownFrontMatterScanState.Closed && header.BodySpan is null)
            {
                scopes.Add("The header has no confirmed body range: " + document.SourcePath);
                continue;
            }
            var bodyStart = header.State == MarkdownFrontMatterScanState.Closed ? header.BodySpan!.Value.Start : 0;
            var syntax = ParseDocument(document, bodyStart, cancellationToken);
            var count = syntax.Root?.DescendantsAndSelf(cancellationToken).Take(262145 - nodes).Count() ?? 0;
            if (nodes + count > 262144)
            {
                scopes.Add("The editor reference syntax-node budget was exceeded: " + document.SourcePath);
                continue;
            }
            nodes += count;
            textBytes += 2L * document.Text.Length;
            available.Add(new Parsed(document, syntax, bodyStart));
            if (syntax.Facts.Status != MarkdownParseStatus.Complete || syntax.Root is null)
                scopes.Add("Incomplete Markdown analysis cannot certify all references: " + document.SourcePath);
        }
        parsed = Array.AsReadOnly(available.ToArray());
        Documents = Array.AsReadOnly(available.Select(static item => item.Document).ToArray());
        CatalogComplete = catalogComplete;
        foreach (var document in available)
            if (SourceRoutes(document.Document).Count > 1)
                scopes.Add("Multiple acquired public routes require an explicit source-context choice: " + document.Document.SourcePath);
        if (!catalogComplete)
            scopes.Add("The acquired catalog does not establish a complete Markdown input universe.");
        foreach (var route in project.Routes.Where(InSelectedScope))
            if (route.InputKind != ContentInputKind.Auxiliary
                && !available.Any(item => PathComparer.Equals(item.Document.ProjectPath, PathKey(route.SourcePath))))
                scopes.Add("No versioned editor snapshot was supplied for " + route.SourcePath);
        manualScopes = Array.AsReadOnly(scopes.Distinct(StringComparer.Ordinal).ToArray());
    }

    public ProjectInspectionSnapshot Project { get; }
    public IReadOnlyList<DocumentReferenceDocument> Documents { get; }
    public bool CatalogComplete { get; }
    public IReadOnlyList<string> ManualScopes => manualScopes;

    private MarkdownSyntaxDocument ParseDocument(DocumentReferenceDocument document, int bodyStart, CancellationToken cancellationToken)
    {
        var raw = document.Text;
        var bodyOnly = Project.MarkdownProfileId == MarkdownOptions.CommonMarkProfile
            || Project.InputFor(document.ProjectPath)?.InputProfile == ContentInputProfile.OpenKnowledgeFormat;
        if (bodyOnly && bodyStart != 0)
        {
            // A length-preserving header projection leaves body UTF-16 ranges and line endings exact.
            var projected = raw.ToCharArray();
            for (var index = 0; index < bodyStart; index++)
            {
                if ((index & 127) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (projected[index] is not ('\r' or '\n'))
                    projected[index] = ' ';
            }
            raw = new string(projected);
        }
        return MarkdownParser.ParseWithSyntax(raw, Project.ProjectId, document.ProjectPath,
            document.Version.ToString(CultureInfo.InvariantCulture),
            bodyOnly
                ? new MarkdownParseOptions(MarkdownInputKind.Body, profileId: Project.MarkdownProfileId) : options,
            cancellationToken);
    }

    public DocumentReferenceResolution ResolveAt(string sourcePath, int offset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = FindDocument(sourcePath);
        if (document is null || offset < 0 || offset > document.Document.Text.Length)
            return new(DocumentReferenceState.Unavailable, null, []);
        var link = document.Syntax.Facts.Links.Where(link => link.RawSpan.Start >= document.BodyStart && Contains(link.RawSpan, offset))
            .OrderBy(static link => link.RawSpan.Length).FirstOrDefault();
        if (link is not null)
            return Resolve(document, new(document.Document, link, null), cancellationToken);
        var heading = document.Syntax.Facts.Headings.FirstOrDefault(heading => heading.RawSpan.Start >= document.BodyStart && Contains(heading.RawSpan, offset));
        return heading is null ? new(DocumentReferenceState.Unavailable, null, [])
            : Resolve(document, new(document.Document, null, heading), cancellationToken);
    }

    public IReadOnlyList<DocumentReferenceCompletion> Complete(string sourcePath, string prefix = "", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(prefix);
        var source = FindDocument(sourcePath);
        if (source is null || SourceRoutes(source.Document).Count != 1)
            return [];
        var sourceRoute = SourceRoutes(source.Document)[0];
        var suggestions = new List<DocumentReferenceCompletion>();
        if (prefix.StartsWith("xref:", StringComparison.Ordinal))
        {
            foreach (var page in Project.Semantics?.Catalog.Pages ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (page.Key.Version == Project.Version && page.Key.Locale == Project.Locale)
                {
                    var text = "xref:" + page.Key.Id;
                    if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        suggestions.Add(new(page.Key.Collection + ": " + page.Key.Id, text,
                            Target(RouteFor(page), null)));
                }
            }
        }
        else if (prefix.Contains('#'))
        {
            var marker = prefix.IndexOf('#');
            var destination = prefix[..marker];
            var candidates = ResolveDestination(source, destination, cancellationToken).Candidates;
            foreach (var target in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var document = FindProjectDocument(target.Route.SourcePath);
                if (document?.Syntax.Facts.Status != MarkdownParseStatus.Complete)
                    continue;
                foreach (var heading in document.Syntax.Facts.Headings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var text = destination + "#" + heading.Anchor;
                    if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        suggestions.Add(new(heading.Text.Text ?? heading.Anchor, text, Target(target.Route, heading.Anchor)));
                }
            }
        }
        else
        {
            foreach (var route in Project.Routes.Where(InSelectedScope))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sourceRoute.InputProfile == ContentInputProfile.OpenKnowledgeFormat)
                {
                    if (!SameInputRoot(sourceRoute, route) || sourceRoute.InputPath is null || route.InputPath is null)
                        continue;
                    var inputText = prefix.StartsWith('/') ? "/" + EncodePath(route.InputPath)
                        : EncodePath(RelativeProjectPath(sourceRoute.InputPath, route.InputPath));
                    if (prefix.StartsWith("./", StringComparison.Ordinal) && !inputText.StartsWith('.'))
                        inputText = "./" + inputText;
                    if (inputText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        suggestions.Add(new(route.InputKind + ": " + route.InputPath + " (" + route.PublicPath + ")", inputText, Target(route, null)));
                    continue;
                }
                var relative = RelativeProjectPath(source.Document.ProjectPath, PathKey(route.SourcePath));
                var relativeText = EncodePath(relative);
                var aliased = Project.Routes.Where(InSelectedScope).Where(candidate =>
                    PathComparer.Equals(PathKey(candidate.SourcePath), PathKey(route.SourcePath)))
                    .Select(static candidate => candidate.PublicPath).Distinct(StringComparer.Ordinal).Skip(1).Any();
                var text = prefix.StartsWith('/') || aliased ? route.PublicPath : relativeText;
                if (prefix.StartsWith("./", StringComparison.Ordinal) && !text.StartsWith('.'))
                {
                    if (!text.StartsWith('/'))
                        text = "./" + text;
                    relativeText = "./" + relativeText;
                }
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || aliased && relativeText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    suggestions.Add(new(route.Collection + "/" + route.Version + "/" + route.Locale + ": " + route.SourcePath + " (" + route.PublicPath + ")",
                        text, Target(route, null)));
            }
        }
        return Array.AsReadOnly(suggestions.OrderBy(static suggestion => suggestion.InsertText, StringComparer.Ordinal)
            .ThenBy(static suggestion => suggestion.Label, StringComparer.Ordinal).ToArray());
    }

    public DocumentReferenceSearch FindReferences(string sourcePath, int offset, bool includeDeclaration = false, CancellationToken cancellationToken = default)
    {
        var resolution = ResolveAt(sourcePath, offset, cancellationToken);
        if (resolution.State != DocumentReferenceState.Resolved)
            return new(resolution.State, [], manualScopes);
        var target = resolution.Candidates[0];
        var locations = new List<DocumentReferenceLocation>();
        foreach (var document in parsed)
            foreach (var link in document.Syntax.Facts.Links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = Resolve(document, new(document.Document, link, null), cancellationToken);
                if (candidate.State == DocumentReferenceState.Resolved && SameTarget(target, candidate.Candidates[0], target.Anchor is not null))
                    locations.Add(new(document.Document.SourcePath, link.RawSpan.Start, link.RawSpan.Length));
            }
        if (includeDeclaration && target.Location is not null)
            locations.Add(target.Location);
        return new(DocumentReferenceState.Resolved,
            locations.DistinctBy(static location => (location.SourcePath, location.Start, location.Length)), manualScopes);
    }

    private DocumentReferenceResolution Resolve(Parsed document, DocumentReferenceOccurrence occurrence, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = SourceRoutes(document.Document);
        if (sources.Count != 1)
            return new(sources.Count > 1 ? DocumentReferenceState.Ambiguous : DocumentReferenceState.Unavailable,
                occurrence, sources.Select(route => Target(route, occurrence.Heading?.Anchor)));
        if (occurrence.Heading is { } heading)
            return new(DocumentReferenceState.Resolved, occurrence, [Target(sources[0], heading.Anchor)]);
        if (sources[0].InputProfile == ContentInputProfile.OpenKnowledgeFormat && occurrence.Link is { } link)
        {
            var relations = sources[0].Relations.Where(relation => relation.Target == link.Target && relation.IsImage == link.Image)
                .DistinctBy(static relation => (relation.Target, relation.TargetSourcePath, relation.Kind, relation.IsImage)).ToArray();
            if (relations.Length > 1)
                return new(DocumentReferenceState.Ambiguous, occurrence, []);
            if (relations.Length == 1)
                return ResolveRelation(sources[0], relations[0], occurrence, cancellationToken);
        }
        var resolved = ResolveDestination(document, occurrence.Link?.Target, cancellationToken);
        return new(resolved.State, occurrence, resolved.Candidates, resolved.Relation);
    }

    private DocumentReferenceResolution ResolveRelation(ProjectRouteCandidate source, ProjectReferenceRelation relation,
        DocumentReferenceOccurrence occurrence, CancellationToken cancellationToken)
    {
        if (relation.Kind is OkfLinkKind.Unresolved or OkfLinkKind.OutsideBundle or OkfLinkKind.External
            || relation.TargetSourcePath is null)
            return new(DocumentReferenceState.Unavailable, occurrence, [], relation);
        var anchor = Fragment(relation.Target);
        var targets = new List<DocumentReferenceTarget>();
        foreach (var route in Project.Routes.Where(InSelectedScope))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SameInputRoot(source, route) && PathComparer.Equals(PathKey(route.SourcePath), PathKey(relation.TargetSourcePath))
                && (relation.Kind == OkfLinkKind.Concept && route.InputKind == ContentInputKind.Concept
                    || relation.Kind == OkfLinkKind.ReservedDocument && route.InputKind is ContentInputKind.Index or ContentInputKind.Log
                    || relation.Kind == OkfLinkKind.Auxiliary && route.InputKind == ContentInputKind.Auxiliary))
                targets.Add(Target(route, anchor));
        }
        return new(TargetState(targets, anchor, unavailableWhenMissing: true), occurrence, targets, relation);
    }

    private DocumentReferenceResolution ResolveDestination(Parsed source, string? destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (destination is null)
            return new(DocumentReferenceState.Unavailable, null, []);
        var xref = destination.StartsWith("xref:", StringComparison.Ordinal);
        var hash = xref ? -1 : destination.IndexOf('#');
        var fragment = xref ? null : Fragment(destination);
        var path = xref ? destination : (hash < 0 ? destination : destination[..hash]).Split('?')[0];
        var sourceRoutes = SourceRoutes(source.Document);
        if (sourceRoutes.Count != 1)
            return new(sourceRoutes.Count > 1 ? DocumentReferenceState.Ambiguous : DocumentReferenceState.Unavailable, null, []);
        var sourceRoute = sourceRoutes[0];
        IEnumerable<ProjectRouteCandidate> matches;
        if (sourceRoute.InputProfile == ContentInputProfile.OpenKnowledgeFormat)
        {
            // The acquired bundle identity, rather than the site's public mount, defines OKF root-relative paths.
            if (sourceRoute.InputPath is null || sourceRoute.InputRoot is null)
                return new(DocumentReferenceState.Unavailable, null, []);
            var inputPath = ResolveProjectPath(sourceRoute.InputPath, path);
            matches = Project.Routes.Where(InSelectedScope).Where(route => SameInputRoot(sourceRoute, route)
                && inputPath is not null && route.InputPath is not null && PathComparer.Equals(PathKey(route.InputPath), inputPath));
        }
        else if (path.StartsWith("xref:", StringComparison.Ordinal))
        {
            var id = path[5..];
            matches = (Project.Semantics?.Catalog.Pages ?? []).Where(page => page.Key.Version == Project.Version
                && page.Key.Locale == Project.Locale && page.Key.Id == id).Select(RouteFor);
            if (Project.Semantics is null)
                return new(DocumentReferenceState.Unavailable, null, []);
        }
        else
        {
            if (path.StartsWith("//", StringComparison.Ordinal) || Uri.TryCreate(path, UriKind.Absolute, out var absolute)
                && !path.StartsWith('/') && absolute.Scheme != Uri.UriSchemeFile)
                return new(DocumentReferenceState.Unavailable, null, []);
            var projectPath = ResolveProjectPath(source.Document.ProjectPath, path);
            var publicPath = ResolvePublicPath(source, path);
            matches = Project.Routes.Where(InSelectedScope).Where(route =>
                projectPath is not null && PathComparer.Equals(PathKey(route.SourcePath), projectPath)
                || publicPath is not null && UrlKey(route.PublicPath) == publicPath);
        }
        var targets = matches
            .Select(route =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Target(route, fragment);
            }).ToArray();
        var state = TargetState(targets, fragment, !CatalogComplete || xref || sourceRoute.InputProfile == ContentInputProfile.OpenKnowledgeFormat);
        return new(state, null, targets);
    }

    private DocumentReferenceState TargetState(IReadOnlyList<DocumentReferenceTarget> targets, string? fragment, bool unavailableWhenMissing) =>
        targets.Count > 1 ? DocumentReferenceState.Ambiguous : targets.Count == 0
            ? unavailableWhenMissing ? DocumentReferenceState.Unavailable : DocumentReferenceState.Unresolved
            : fragment is not null && targets[0].InputKind != ContentInputKind.Auxiliary && targets[0].Location is null
                ? FindProjectDocument(targets[0].Route.SourcePath)?.Syntax.Facts.Status == MarkdownParseStatus.Complete
                    ? DocumentReferenceState.Unresolved : DocumentReferenceState.Unavailable : DocumentReferenceState.Resolved;

    private static string? Fragment(string? destination)
    {
        var hash = destination?.IndexOf('#') ?? -1;
        return hash < 0 || hash == destination!.Length - 1 ? null : Decode(destination[(hash + 1)..]);
    }

    private static bool SameInputRoot(ProjectRouteCandidate left, ProjectRouteCandidate right) =>
        left.InputProfile == ContentInputProfile.OpenKnowledgeFormat && right.InputProfile == ContentInputProfile.OpenKnowledgeFormat
        && left.InputRoot is not null && right.InputRoot is not null
        && PathComparer.Equals(PathKey(left.InputRoot).TrimEnd('/'), PathKey(right.InputRoot).TrimEnd('/'));

    private DocumentReferenceTarget Target(ProjectRouteCandidate route, string? anchor)
    {
        var document = FindProjectDocument(route.SourcePath);
        var heading = anchor is null ? null : document?.Syntax.Facts.Headings.FirstOrDefault(heading => heading.Anchor == anchor);
        var span = heading?.TextRawSpan ?? heading?.RawSpan;
        return new(route, anchor, document is null || anchor is not null && span is null ? null
            : new DocumentReferenceLocation(document.Document.SourcePath, span?.Start ?? 0, span?.Length ?? 0));
    }

    private ProjectRouteCandidate RouteFor(Documentation.DocumentPage page) => Project.Routes.FirstOrDefault(route =>
            route.Collection == page.Key.Collection && route.Version == page.Key.Version && route.Locale == page.Key.Locale
            && route.PublicPath == page.Route.PublicPath)
        ?? new(page.SourcePath, page.Route.PublicPath, Project.ProjectId, page.Key.Collection, page.Key.Version,
            page.Key.Locale, Documentation.DocumentPublicationState.Published);

    private IReadOnlyList<ProjectRouteCandidate> SourceRoutes(DocumentReferenceDocument document) =>
        Project.Routes.Where(InSelectedScope).Where(route => PathComparer.Equals(PathKey(route.SourcePath), document.ProjectPath)).ToArray();
    private bool InSelectedScope(ProjectRouteCandidate route) => route.ProjectId == Project.ProjectId
        && route.Collection == Project.Collection && route.Version == Project.Version && route.Locale == Project.Locale;
    private Parsed? FindDocument(string sourcePath) => parsed.FirstOrDefault(item => PathComparer.Equals(PathKey(item.Document.SourcePath), PathKey(sourcePath)));
    private Parsed? FindProjectDocument(string projectPath) => parsed.FirstOrDefault(item => PathComparer.Equals(item.Document.ProjectPath, PathKey(projectPath)));
    internal static string PathKey(string path) => path.Replace('\\', '/').Normalize(NormalizationForm.FormC);
    private static bool Contains(RawSpan span, int offset) => span.Start <= offset && offset <= span.End;
    private static string? Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value).Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
    private static string? UrlKey(string path) => Decode(path);
    private string? ResolvePublicPath(Parsed source, string path)
    {
        var routes = SourceRoutes(source.Document);
        if (routes.Count != 1 || !routes[0].PublicPath.StartsWith('/'))
            return null;
        return Uri.TryCreate("https://catalog.invalid" + routes[0].PublicPath, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, path, out var resolved)
            && resolved.Host == "catalog.invalid" ? UrlKey(resolved.AbsolutePath) : null;
    }
    private static bool SameTarget(DocumentReferenceTarget left, DocumentReferenceTarget right, bool includeAnchor) =>
        left.Route.ProjectId == right.Route.ProjectId && left.Route.Collection == right.Route.Collection
        && left.Route.Version == right.Route.Version && left.Route.Locale == right.Route.Locale
        && PathComparer.Equals(PathKey(left.Route.SourcePath), PathKey(right.Route.SourcePath))
        && (!includeAnchor || left.Anchor == right.Anchor);

    private static string? ResolveProjectPath(string source, string destination)
    {
        if (destination.Length == 0)
            return source;
        var pieces = destination.StartsWith('/') ? new List<string>() : source.Split('/').SkipLast(1).ToList();
        foreach (var segment in destination.Split('/'))
        {
            var decoded = Decode(segment);
            if (decoded is null || decoded.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
                return null;
            if (decoded.Length == 0 || decoded == ".")
                continue;
            if (decoded == "..")
            {
                if (pieces.Count == 0)
                    return null;
                pieces.RemoveAt(pieces.Count - 1);
            }
            else
                pieces.Add(decoded);
        }
        return pieces.Count == 0 ? "index.md" : string.Join('/', pieces) + (destination.EndsWith('/') ? "/index.md" : string.Empty);
    }

    private static string RelativeProjectPath(string from, string to)
    {
        var origin = from.Split('/').SkipLast(1).ToArray();
        var target = to.Split('/');
        var common = 0;
        while (common < origin.Length && common < target.Length && PathComparer.Equals(origin[common], target[common]))
            common++;
        return string.Join('/', Enumerable.Repeat("..", origin.Length - common).Concat(target.Skip(common)));
    }
    private static string EncodePath(string path) => string.Join('/', path.Split('/').Select(segment => segment is "." or ".." ? segment : Uri.EscapeDataString(segment)));
}
