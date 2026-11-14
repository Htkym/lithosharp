using System.Collections.ObjectModel;
using System.Text;
using LithoSharp.Content;
using LithoSharp.Routing;
using Syntamark;

namespace LithoSharp.Inspection;

/// <summary>An exact replacement guarded by the complete source snapshot and editor version.</summary>
public sealed class DocumentReferenceTextEdit
{
    internal DocumentReferenceTextEdit(DocumentReferenceDocument document, RawSpan span, string replacement)
    {
        SourcePath = document.SourcePath;
        Version = document.Version;
        Start = span.Start;
        Length = span.Length;
        ExpectedText = document.Text.Substring(span.Start, span.Length);
        Replacement = replacement;
    }
    public string SourcePath { get; }
    public long Version { get; }
    public int Start { get; }
    public int Length { get; }
    public string ExpectedText { get; }
    public string Replacement { get; }
}

public sealed class DocumentReferenceMove
{
    internal DocumentReferenceMove(string sourcePath, string destinationPath) => (SourcePath, DestinationPath) = (sourcePath, destinationPath);
    public string SourcePath { get; }
    public string DestinationPath { get; }
}

/// <summary>A reviewable edit set. Manual scopes remain explicit even when the included edits are safe to apply.</summary>
public sealed class DocumentReferenceEditPlan
{
    private readonly IReadOnlyList<DocumentReferenceDocument> inputs;
    internal DocumentReferenceEditPlan(IEnumerable<DocumentReferenceDocument> inputs, IEnumerable<DocumentReferenceTextEdit> edits,
        IEnumerable<DocumentReferenceMove> moves, IEnumerable<string> manualScopes, bool blocked = false)
    {
        this.inputs = Array.AsReadOnly(inputs.ToArray());
        var values = edits.DistinctBy(static edit => (edit.SourcePath, edit.Start, edit.Length, edit.ExpectedText, edit.Replacement))
            .OrderBy(static edit => edit.SourcePath, StringComparer.Ordinal).ThenBy(static edit => edit.Start).ToArray();
        var reasons = manualScopes.Distinct(StringComparer.Ordinal).ToList();
        foreach (var group in values.GroupBy(edit => DocumentReferenceSnapshot.PathKey(edit.SourcePath), StringComparer.OrdinalIgnoreCase))
        {
            var previousEnd = -1;
            var previousStart = -1;
            foreach (var edit in group)
            {
                if (edit.Start < previousEnd || edit.Start == previousStart)
                {
                    blocked = true;
                    reasons.Add("Conflicting source replacements require manual review: " + edit.SourcePath);
                }
                previousStart = edit.Start;
                previousEnd = edit.Start + edit.Length;
            }
        }
        Edits = Array.AsReadOnly(values);
        Moves = Array.AsReadOnly(moves.ToArray());
        ManualScopes = Array.AsReadOnly(reasons.ToArray());
        CanApply = !blocked && (Edits.Count != 0 || Moves.Count != 0);
    }
    public IReadOnlyList<DocumentReferenceTextEdit> Edits { get; }
    public IReadOnlyList<DocumentReferenceMove> Moves { get; }
    public IReadOnlyList<string> ManualScopes { get; }
    public bool IsComplete => ManualScopes.Count == 0;
    public bool CanApply { get; }

    /// <summary>Validates every version and entire original buffer before returning changed text; performs no file writes.</summary>
    public bool TryApply(IEnumerable<DocumentReferenceDocument> currentDocuments, out IReadOnlyDictionary<string, string> changedDocuments)
    {
        ArgumentNullException.ThrowIfNull(currentDocuments);
        changedDocuments = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
        if (!CanApply)
            return false;
        var current = currentDocuments.ToArray();
        if (current.Any(static document => document is null)
            || current.GroupBy(document => DocumentReferenceSnapshot.PathKey(document.SourcePath), StringComparer.OrdinalIgnoreCase).Any(static group => group.Count() > 1))
            return false;
        DocumentReferenceDocument? Find(string path) => current.FirstOrDefault(document =>
            StringComparer.OrdinalIgnoreCase.Equals(DocumentReferenceSnapshot.PathKey(document.SourcePath), DocumentReferenceSnapshot.PathKey(path)));
        foreach (var captured in inputs)
        {
            var actual = Find(captured.SourcePath);
            if (actual is null || actual.Version != captured.Version || actual.ProjectPath != captured.ProjectPath || actual.IsDynamic != captured.IsDynamic
                || !string.Equals(actual.Text, captured.Text, StringComparison.Ordinal))
                return false;
        }
        foreach (var edit in Edits)
        {
            var actual = Find(edit.SourcePath);
            if (actual is null || actual.Version != edit.Version || edit.Start + edit.Length > actual.Text.Length
                || !actual.Text.AsSpan(edit.Start, edit.Length).SequenceEqual(edit.ExpectedText))
                return false;
        }
        foreach (var move in Moves)
            if (Find(move.SourcePath) is null || Find(move.DestinationPath) is not null)
                return false;
        var changed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in Edits.GroupBy(static edit => edit.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var builder = new StringBuilder(Find(group.Key)!.Text);
            foreach (var edit in group.OrderByDescending(static edit => edit.Start))
                builder.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
            changed.Add(group.Key, builder.ToString());
        }
        foreach (var move in Moves)
        {
            var text = changed.GetValueOrDefault(move.SourcePath) ?? Find(move.SourcePath)!.Text;
            changed.Remove(move.SourcePath);
            changed.Add(move.DestinationPath, text);
        }
        changedDocuments = new ReadOnlyDictionary<string, string>(changed);
        return true;
    }
}

public sealed partial class DocumentReferenceSnapshot
{
    public DocumentReferenceEditPlan PlanHeadingRename(string sourcePath, int offset, string newText, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(newText);
        var source = FindDocument(sourcePath);
        var selected = source?.Syntax.Facts.Headings.FirstOrDefault(heading => Contains(heading.RawSpan, offset));
        if (selected is null && ResolveAt(sourcePath, offset, cancellationToken) is { State: DocumentReferenceState.Resolved } reference
            && reference.Candidates[0] is { Anchor: not null, Location: not null } destination)
        {
            source = FindDocument(destination.Location.SourcePath);
            selected = source?.Syntax.Facts.Headings.FirstOrDefault(heading => heading.Anchor == destination.Anchor);
        }
        if (source is null || selected?.TextRawSpan is not { } content || source.Syntax.Facts.Status != MarkdownParseStatus.Complete
            || SourceRoutes(source.Document).Count != 1 || !SingleLine(newText) || !Editable(source, content))
            return Blocked("An exact, complete, single-context heading snapshot is required for rename.");
        var textMap = selected.Text.Map(new RawSpan(0, selected.Text.Text?.Length ?? 0));
        if (textMap.Precision != MarkdownMappingPrecision.Exact || textMap.RawFragments.Count != 1
            || textMap.RawFragments[0] != content)
            return Blocked("Formatted or decoded heading content requires manual review; no imprecise text edit was proposed.");
        var replacement = EscapeHeading(newText);
        var updated = source.Document.Text.Remove(content.Start, content.Length).Insert(content.Start, replacement);
        var reparsed = ParseDocument(new DocumentReferenceDocument(source.Document.SourcePath, updated, source.Document.Version,
            source.Document.ProjectPath), source.BodyStart, cancellationToken);
        if (reparsed.Facts.Status != MarkdownParseStatus.Complete
            || reparsed.Facts.Headings.Count != source.Syntax.Facts.Headings.Count)
            return Blocked("The replacement changes the heading structure or exceeds supported analysis limits.");
        var oldHeadings = source.Syntax.Facts.Headings;
        var newHeadings = reparsed.Facts.Headings;
        var selectedIndex = Array.FindIndex(oldHeadings.ToArray(), heading => ReferenceEquals(heading, selected));
        if (newHeadings[selectedIndex].Text.Text != newText || oldHeadings.Select(static heading => heading.Anchor).Distinct(StringComparer.Ordinal).Count() != oldHeadings.Count)
            return Blocked("The requested visible heading cannot be represented unambiguously by this profile.");
        var renames = oldHeadings.Select((heading, index) => (Before: heading.Anchor, After: newHeadings[index].Anchor))
            .Where(static item => item.Before != item.After).ToDictionary(static item => item.Before, static item => item.After, StringComparer.Ordinal);
        var edits = new List<DocumentReferenceTextEdit> { new(source.Document, content, replacement) };
        var manual = PlanScopes();
        var route = SourceRoutes(source.Document)[0];
        foreach (var document in parsed)
            foreach (var link in document.Syntax.Facts.Links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = Resolve(document, new(document.Document, link, null), cancellationToken);
                if (resolved.State != DocumentReferenceState.Resolved || resolved.Candidates[0].Anchor is not { } anchor
                    || !renames.TryGetValue(anchor, out var renamed) || !SameDocument(route, resolved.Candidates[0].Route))
                    continue;
                if (link.TargetRawSpan is not { } span || !Editable(document, span))
                {
                    manual.Add("The link destination has no editable exact range: " + document.Document.SourcePath);
                    continue;
                }
                var raw = document.Document.Text.Substring(span.Start, span.Length);
                var marker = raw.IndexOf('#');
                if (marker < 0)
                {
                    manual.Add("The escaped fragment requires an explicit source mapping: " + document.Document.SourcePath);
                    continue;
                }
                var encoded = raw[(marker + 1)..].Contains('%') ? Uri.EscapeDataString(renamed) : renamed;
                edits.Add(new(document.Document, span, raw[..(marker + 1)] + encoded));
            }
        return Plan(edits, [], manual);
    }

    public DocumentReferenceEditPlan PlanTargetChange(string sourcePath, int offset, string newTarget, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(newTarget);
        var source = FindDocument(sourcePath);
        var selected = ResolveAt(sourcePath, offset, cancellationToken).Reference;
        if (source is null || selected?.Link is not { } link || link.TargetRawSpan is not { } span
            || source.Syntax.Facts.Status != MarkdownParseStatus.Complete || !Editable(source, span) || !SingleLine(newTarget))
            return Blocked("A parser-confirmed link destination in a complete source snapshot is required.");
        var replacement = EscapeDestination(newTarget);
        var updated = source.Document.Text.Remove(span.Start, span.Length).Insert(span.Start, replacement);
        var reparsed = ParseDocument(new DocumentReferenceDocument(source.Document.SourcePath, updated, source.Document.Version,
            source.Document.ProjectPath), source.BodyStart, cancellationToken);
        if (reparsed.Facts.Status != MarkdownParseStatus.Complete || !reparsed.Facts.Links.Any(candidate =>
            candidate.TargetRawSpan?.Start == span.Start && candidate.Target == NormalizeDestination(newTarget)))
            return Blocked("The requested destination cannot be represented without changing the recognized link syntax.");
        var manual = PlanScopes();
        if (ResolveDestination(source, NormalizeDestination(newTarget), cancellationToken).State != DocumentReferenceState.Resolved)
            manual.Add("The explicitly requested destination is unresolved or outside the acquired catalog: " + newTarget);
        return Plan([new(source.Document, span, replacement)], [], manual);
    }

    /// <summary>Plans a physical move and known Markdown references. A new public route is never inferred from the filename.</summary>
    public DocumentReferenceEditPlan PlanMove(string sourcePath, string newSourcePath, string? newPublicPath = null, string? newProjectPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(newSourcePath);
        var source = FindDocument(sourcePath);
        if (source is null || source.Syntax.Facts.Status != MarkdownParseStatus.Complete || SourceRoutes(source.Document).Count != 1)
            return Blocked("Moving a document requires an exact versioned snapshot and one acquired route context.");
        var sourceRoute = SourceRoutes(source.Document)[0];
        if (newProjectPath is null && Path.IsPathRooted(newSourcePath))
            return Blocked("An absolute destination requires an explicitly acquired project-relative destination path.");
        string projectPath;
        string? movedInputPath = null;
        try
        {
            if (Path.IsPathRooted(sourceRoute.SourcePath))
            {
                if (sourceRoute.InputRoot is null || !Path.IsPathRooted(sourceRoute.InputRoot) || sourceRoute.InputPath is null || newProjectPath is null
                    || !Path.IsPathRooted(newSourcePath) || !PathComparer.Equals(PathKey(newProjectPath), PathKey(newSourcePath)))
                    return Blocked("The acquired absolute input identity requires an explicit input root and destination identity.");
                var relative = Path.GetRelativePath(sourceRoute.InputRoot, newSourcePath);
                movedInputPath = ContentInputPolicy.NormalizePath(relative);
                projectPath = PathKey(newProjectPath);
            }
            else
                projectPath = ContentInputPolicy.NormalizePath(newProjectPath ?? newSourcePath);
        }
        catch (ArgumentException)
        {
            return Blocked("The destination must remain inside the declared project input root.");
        }
        if (!projectPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || Project.Routes.Where(InSelectedScope).Any(route => PathComparer.Equals(PathKey(route.SourcePath), projectPath))
            || parsed.Any(item => PathComparer.Equals(PathKey(item.Document.SourcePath), PathKey(newSourcePath))))
            return Blocked("The destination changes the input kind or conflicts with an acquired document.");
        if (newPublicPath is not null && (!ValidPublicPath(newPublicPath)
            || Project.Routes.Where(InSelectedScope).Any(route => !SameDocument(sourceRoute, route) && UrlKey(route.PublicPath) == UrlKey(newPublicPath))))
            return Blocked("The explicitly supplied public route is invalid or already owned.");
        var edits = new List<DocumentReferenceTextEdit>();
        var manual = PlanScopes();
        foreach (var document in parsed)
            foreach (var link in document.Syntax.Facts.Links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolution = Resolve(document, new(document.Document, link, null), cancellationToken);
                if (resolution.State != DocumentReferenceState.Resolved || link.Target is null)
                    continue;
                var target = resolution.Candidates[0];
                var incoming = SameDocument(sourceRoute, target.Route);
                var outgoing = ReferenceEquals(document, source);
                if (!incoming && !outgoing)
                    continue;
                if (link.TargetRawSpan is not { } span || !Editable(document, span))
                {
                    manual.Add("An affected link lacks an exact editable range: " + document.Document.SourcePath);
                    continue;
                }
                var targetPath = link.Target.Split('?', '#')[0];
                if (targetPath.StartsWith("xref:", StringComparison.Ordinal))
                    continue;
                var raw = document.Document.Text.Substring(span.Start, span.Length);
                var rawSuffix = RawSuffix(raw);
                var rawPath = raw[..(raw.Length - rawSuffix.Length)];
                var fromRoutes = SourceRoutes(document.Document);
                var bundleForm = fromRoutes.Count == 1 && fromRoutes[0].InputProfile == ContentInputProfile.OpenKnowledgeFormat;
                if (bundleForm && (!SameInputRoot(fromRoutes[0], sourceRoute) || movedInputPath is null
                    || fromRoutes[0].InputPath is null || target.Route.InputPath is null))
                {
                    manual.Add("The affected bundle relation needs explicit source and destination input identities: " + document.Document.SourcePath);
                    continue;
                }
                var projectTarget = ResolveProjectPath(bundleForm ? fromRoutes[0].InputPath! : document.Document.ProjectPath, targetPath);
                var sourceForm = projectTarget is not null && PathComparer.Equals(projectTarget,
                    bundleForm ? PathKey(target.Route.InputPath!) : PathKey(target.Route.SourcePath));
                string nextPath;
                if (sourceForm)
                {
                    if (targetPath.Length == 0)
                        continue;
                    var from = bundleForm ? outgoing ? movedInputPath! : fromRoutes[0].InputPath!
                        : outgoing ? projectPath : document.Document.ProjectPath;
                    var to = bundleForm ? incoming ? movedInputPath! : target.Route.InputPath!
                        : incoming ? projectPath : PathKey(target.Route.SourcePath);
                    nextPath = targetPath.StartsWith('/') ? "/" + to : RelativeProjectPath(from, to);
                    if (targetPath.StartsWith("./", StringComparison.Ordinal) && !nextPath.StartsWith('.'))
                        nextPath = "./" + nextPath;
                }
                else
                {
                    if (newPublicPath is null && (incoming || outgoing && !targetPath.StartsWith('/')))
                    {
                        manual.Add("The document has public-URL references; supply the new route explicitly: " + document.Document.SourcePath);
                        continue;
                    }
                    if (targetPath.StartsWith('/'))
                    {
                        if (!incoming)
                            continue;
                        nextPath = newPublicPath!;
                    }
                    else
                    {
                        if (fromRoutes.Count != 1)
                        {
                            manual.Add("The relative public URL has multiple possible source routes: " + document.Document.SourcePath);
                            continue;
                        }
                        var from = outgoing ? newPublicPath! : fromRoutes[0].PublicPath;
                        var to = incoming ? newPublicPath! : target.Route.PublicPath;
                        if (!RelativePublicPath(from, to, out nextPath))
                        {
                            manual.Add("The public URL cannot be rebased within the acquired origin: " + document.Document.SourcePath);
                            continue;
                        }
                        if (targetPath.StartsWith("./", StringComparison.Ordinal) && !nextPath.StartsWith('.'))
                            nextPath = "./" + nextPath;
                    }
                }
                var rewritten = EncodeLike(nextPath, rawPath, targetPath, nextIsUrl: !sourceForm) + rawSuffix;
                if (rewritten != raw)
                    edits.Add(new(document.Document, span, rewritten));
            }
        return Plan(edits, [new(source.Document.SourcePath, newSourcePath)], manual);
    }

    private DocumentReferenceEditPlan Plan(IEnumerable<DocumentReferenceTextEdit> edits, IEnumerable<DocumentReferenceMove> moves,
        IEnumerable<string> scopes) => new(Documents, edits, moves, scopes);
    private DocumentReferenceEditPlan Blocked(string reason) => new(Documents, [], [], PlanScopes().Append(reason), blocked: true);
    private List<string> PlanScopes() => manualScopes.Concat([
        "C# symbol references and dynamic expressions are outside this Markdown snapshot and require separate review."]).ToList();
    private static bool SameDocument(ProjectRouteCandidate left, ProjectRouteCandidate right) =>
        left.ProjectId == right.ProjectId && left.Collection == right.Collection && left.Version == right.Version
        && left.Locale == right.Locale && PathComparer.Equals(PathKey(left.SourcePath), PathKey(right.SourcePath));
    private static bool Editable(Parsed source, RawSpan span) => source.Syntax.Facts.Status == MarkdownParseStatus.Complete
        && span.Start >= source.BodyStart && span.End <= source.Document.Text.Length
        && Boundary(source.Document.Text, span.Start) && Boundary(source.Document.Text, span.End);
    private static bool Boundary(string text, int offset) => offset == 0 || offset == text.Length
        || !char.IsHighSurrogate(text[offset - 1]) || !char.IsLowSurrogate(text[offset]);
    private static bool SingleLine(string value)
    {
        if (value.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
            return false;
        try
        {
            _ = new UTF8Encoding(false, true).GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
    private static string EscapeHeading(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~')
                builder.Append('\\');
            builder.Append(character);
        }
        return builder.ToString();
    }
    private static string NormalizeDestination(string value) => value.Replace(" ", "%20", StringComparison.Ordinal)
        .Replace("<", "%3C", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal)
        .Replace("\"", "%22", StringComparison.Ordinal).Replace("[", "%5B", StringComparison.Ordinal).Replace("]", "%5D", StringComparison.Ordinal);
    private static string EscapeDestination(string value) => NormalizeDestination(value).Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
    private static string RawSuffix(string raw)
    {
        var marker = raw.IndexOfAny(['?', '#']);
        if (marker < 0)
            return string.Empty;
        if (marker > 0 && raw[marker - 1] == '\\')
            marker--;
        return raw[marker..];
    }
    private static string EncodeLike(string nextPath, string oldRawPath, string oldTargetPath, bool nextIsUrl)
    {
        var raw = oldRawPath.Split('/');
        var decoded = oldTargetPath.Split('/').Select(Decode).ToArray();
        var next = nextPath.Split('/');
        for (var index = 0; index < next.Length; index++)
        {
            if (nextIsUrl)
                next[index] = Decode(next[index]) ?? next[index];
            if (index < raw.Length && index < decoded.Length && decoded[index] == next[index])
                next[index] = raw[index];
            else
            {
                var matches = Enumerable.Range(0, Math.Min(raw.Length, decoded.Length)).Where(at => decoded[at] == next[index]).Take(2).ToArray();
                next[index] = matches.Length == 1 ? raw[matches[0]]
                    : next[index] is "." or ".." ? next[index] : Uri.EscapeDataString(next[index]);
            }
        }
        return string.Join('/', next);
    }

    private static bool RelativePublicPath(string from, string to, out string relative)
    {
        relative = string.Empty;
        if (!from.StartsWith('/') || !to.StartsWith('/')
            || !Uri.TryCreate("https://catalog.invalid" + from, UriKind.Absolute, out var source)
            || !Uri.TryCreate("https://catalog.invalid" + to, UriKind.Absolute, out var target))
            return false;
        relative = source.MakeRelativeUri(target).OriginalString;
        return !relative.Contains(':', StringComparison.Ordinal);
    }

    private static bool ValidPublicPath(string path)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || !SingleLine(path))
            return false;
        try
        {
            _ = path.EndsWith('/') ? SiteRoute.ForDirectoryIndex(path[1..]) : SiteRoute.ForFile(path[1..]);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            return false;
        }
    }
}
