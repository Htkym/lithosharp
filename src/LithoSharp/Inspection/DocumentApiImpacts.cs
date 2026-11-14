using LithoSharp.Documentation;
using Syntamark;

namespace LithoSharp.Inspection;

/// <summary>A parsed guide reference or explicitly mapped snippet affected by selected API changes.</summary>
public sealed class ApiChangeImpact
{
    internal ApiChangeImpact(DocumentKey api, IReadOnlyList<ApiChange> changes, DocumentReferenceDocument document,
        RawSpan range, string? snippetId)
    {
        Api = api;
        Changes = Array.AsReadOnly(changes.ToArray());
        Document = document;
        Location = new(document.SourcePath, range.Start, range.Length);
        SnippetId = snippetId;
    }

    public DocumentKey Api { get; }
    public IReadOnlyList<ApiChange> Changes { get; }
    /// <summary>The exact captured text, version and hash to which Location applies; this is not proof about a later buffer.</summary>
    public DocumentReferenceDocument Document { get; }
    public DocumentReferenceLocation Location { get; }
    /// <summary>Null for a guide link; otherwise the explicitly mapped fence ID.</summary>
    public string? SnippetId { get; }
}

/// <summary>Known impacts plus scopes requiring manual analysis; absence is not proof that an API is unused.</summary>
public sealed class ApiChangeImpactReport
{
    internal ApiChangeImpactReport(IEnumerable<ApiChangeImpact> impacts, IEnumerable<string> manualScopes)
    {
        Impacts = Array.AsReadOnly(impacts.ToArray());
        ManualScopes = Array.AsReadOnly(manualScopes.Distinct(StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<ApiChangeImpact> Impacts { get; }
    public IReadOnlyList<string> ManualScopes { get; }
    public bool IsComplete => ManualScopes.Count == 0;
}

public sealed partial class DocumentReferenceSnapshot
{
    /// <summary>Finds native parsed links and caller-declared snippet API uses for exact API variant keys.</summary>
    /// <remarks>Use an acquired reference/catalog snapshot in which the selected APIs resolve, captured before removal for removed APIs. This reads only captured buffers, never executes snippets and never extracts API uses from C# or prose. Results describe the captured document versions and hashes, not current files.</remarks>
    public ApiChangeImpactReport FindApiImpacts(IReadOnlyDictionary<DocumentKey, IReadOnlyList<ApiChange>> changes,
        IEnumerable<DocumentSnippet> snippets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(snippets);
        cancellationToken.ThrowIfCancellationRequested();
        var selected = new Dictionary<DocumentKey, IReadOnlyList<ApiChange>>();
        foreach (var (key, values) in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSemanticDocument.ValidateKey(key);
            ArgumentNullException.ThrowIfNull(values);
            var captured = values.ToArray();
            if (captured.Any(change => change is null || change.Id != key.Id || string.IsNullOrWhiteSpace(change.Kind)))
                throw new ArgumentException("Each change must have the selected exact API ID and a nonempty kind.", nameof(changes));
            selected.Add(key, Array.AsReadOnly(captured.Distinct().ToArray()));
        }
        var declarations = snippets.ToArray();
        if (declarations.Any(static snippet => snippet is null))
            throw new ArgumentException("Snippet declarations must not contain null.", nameof(snippets));
        if (declarations.GroupBy(snippet => PathKey(snippet.DocumentPath), PathComparer)
            .Any(group => group.GroupBy(static snippet => snippet.Id, StringComparer.Ordinal).Any(ids => ids.Count() > 1)))
            throw new ArgumentException("A document's snippet IDs must be unique.", nameof(snippets));

        var scopes = new List<string>(manualScopes)
        {
            "API uses outside parsed links and declared snippet API references require manual analysis; C# usage is not extracted."
        };
        var targets = new Dictionary<DocumentKey, DocumentSemanticReference>();
        foreach (var key in selected.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Project.Semantics?.Resolve(key);
            if (target?.State == DocumentSemanticReferenceState.Resolved)
                targets.Add(key, target);
            else
                scopes.Add("The selected API does not resolve in the captured catalog; retain a snapshot before removal: " + key);
        }
        var impacts = new List<ApiChangeImpact>();
        foreach (var document in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var link in document.Syntax.Facts.Links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (link.Image || link.Kind == MarkdownLinkKind.ReferenceDefinition || link.Target is not { } destination)
                    continue;
                var keys = selected.Keys.Where(key => !destination.StartsWith("xref:", StringComparison.Ordinal) || key.Id == destination[5..]).ToArray();
                if (keys.Length == 0)
                    continue;
                var resolved = Resolve(document, new(document.Document, link, null), cancellationToken);
                if (resolved.State != DocumentReferenceState.Resolved)
                {
                    scopes.Add("A parsed link could not be assessed for API impact: " + document.Document.SourcePath + " at " + link.RawSpan.Start);
                    continue;
                }
                var route = resolved.Candidates[0].Route;
                foreach (var key in keys)
                    if (selected[key].Count != 0 && targets.TryGetValue(key, out var target)
                        && route.Collection == key.Collection && route.Version == key.Version && route.Locale == key.Locale
                        && route.PublicPath == target.Route!.PublicPath)
                        impacts.Add(new(key, selected[key], document.Document, link.RawSpan, null));
            }
        }
        foreach (var snippet in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = parsed.SingleOrDefault(item => PathComparer.Equals(item.Document.ProjectPath, PathKey(snippet.DocumentPath)));
            if (document is null || document.Syntax.Facts.Status != MarkdownParseStatus.Complete
                || SourceRoutes(document.Document).Count != 1)
            {
                scopes.Add("The mapped snippet has no unambiguous complete captured Markdown source: " + snippet.DocumentPath + "#" + snippet.Id);
                continue;
            }
            var fences = document.Syntax.Facts.Fences.Where(fence => DocumentationVerification.SnippetId().Matches(fence.Info ?? string.Empty)
                .Select(match => match.Groups[1].Value.Normalize(System.Text.NormalizationForm.FormC)).Contains(snippet.Id, StringComparer.Ordinal)).ToArray();
            if (fences.Length != 1 || !fences[0].Closed || DocumentationVerification.SnippetId().Matches(fences[0].Info ?? string.Empty).Count != 1)
            {
                scopes.Add("The mapped snippet is not a unique closed parsed fence: " + snippet.DocumentPath + "#" + snippet.Id);
                continue;
            }
            if (snippet.ApiReferences.Count == 0)
                scopes.Add("No API references were declared for snippet: " + snippet.DocumentPath + "#" + snippet.Id);
            foreach (var key in snippet.ApiReferences)
                if (selected.TryGetValue(key, out var matching) && matching.Count != 0)
                    impacts.Add(new(key, matching, document.Document, fences[0].RawSpan, snippet.Id));
        }
        return new(impacts.OrderBy(static impact => impact.Document.ProjectPath, StringComparer.Ordinal)
            .ThenBy(static impact => impact.Location.Start).ThenBy(static impact => impact.Api.ToString(), StringComparer.Ordinal), scopes);
    }
}
