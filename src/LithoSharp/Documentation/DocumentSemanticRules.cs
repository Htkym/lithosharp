using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;

namespace LithoSharp.Documentation;

public enum DocumentSemanticPublication
{
    Unknown,
    Published,
    Unpublished
}

public enum DocumentSemanticState
{
    Passed,
    Failed,
    Deferred,
    NotApplicable
}

public enum DocumentSemanticStage
{
    Static,
    Catalog,
    Runtime
}

public enum DocumentSemanticRuleKind
{
    RequiredWhen,
    RequiredWhenPublished,
    Reference
}

public enum DocumentSemanticReferenceState
{
    Resolved,
    Unpublished,
    Missing,
    Unknown
}

/// <summary>A collection/version/locale input universe covered by a complete capture.</summary>
public sealed record DocumentSemanticScope(string Collection, string Version, string Locale);
/// <summary>A declarative constraint evaluated without executing document or component code.</summary>
public sealed class DocumentSemanticRule
{
    private DocumentSemanticRule(DocumentSemanticRuleKind kind, string field, string? conditionField, string? expectedValue, bool sameVersion, bool sameLocale, bool publishedTarget, bool publishedSourceOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (field.Split('.').Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A field path must contain nonempty member names.", nameof(field));
        if (conditionField is not null && conditionField.Split('.').Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A condition field path must contain nonempty member names.", nameof(conditionField));
        Kind = kind;
        Field = field.Normalize();
        ConditionField = conditionField?.Normalize();
        ExpectedValue = expectedValue;
        SameVersion = sameVersion;
        SameLocale = sameLocale;
        PublishedTarget = publishedTarget;
        PublishedSourceOnly = publishedSourceOnly;
    }

    public DocumentSemanticRuleKind Kind { get; }
    public string Field { get; }
    public string? ConditionField { get; }
    public string? ExpectedValue { get; }
    public bool SameVersion { get; }
    public bool SameLocale { get; }
    public bool PublishedTarget { get; }
    public bool PublishedSourceOnly { get; }
    public DocumentSemanticStage Stage => Kind == DocumentSemanticRuleKind.RequiredWhen ? DocumentSemanticStage.Static : Kind == DocumentSemanticRuleKind.Reference ? DocumentSemanticStage.Catalog : DocumentSemanticStage.Runtime;

    /// <summary>Requires a nonempty field when another field equals the decoded literal text.</summary>
    public static DocumentSemanticRule RequiredWhen(string field, string conditionField, string expectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conditionField);
        ArgumentNullException.ThrowIfNull(expectedValue);
        return new(DocumentSemanticRuleKind.RequiredWhen, field, conditionField, expectedValue, false, false, false, false);
    }

    /// <summary>Requires a nonempty field when the real publication policy selects this document.</summary>
    public static DocumentSemanticRule RequiredWhenPublished(string field) => new(DocumentSemanticRuleKind.RequiredWhenPublished, field, null, null, false, false, false, true);
    /// <summary>Checks document IDs or explicit collection/version/locale/id objects. Published sources require published targets when requested.</summary>
    public static DocumentSemanticRule Reference(string field, bool sameVersion = true, bool sameLocale = true, bool publishedTarget = true, bool publishedSourceOnly = false) => new(DocumentSemanticRuleKind.Reference, field, null, null, sameVersion, sameLocale, publishedTarget, publishedSourceOnly);
}

/// <summary>Immutable metadata evidence, separate from the catalog of published routes.</summary>
public sealed class DocumentSemanticDocument
{
    public DocumentSemanticDocument(DocumentKey key, string sourcePath, IReadOnlyDictionary<string, object?> values, DocumentSemanticPublication publication, bool isComplete, IReadOnlyDictionary<string, SiteSourceLocation>? locations = null)
    {
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(values);
        if (!Enum.IsDefined(publication))
            throw new ArgumentOutOfRangeException(nameof(publication));
        Key = key;
        SourcePath = sourcePath;
        Values = FreezeMap(values);
        Publication = publication;
        IsComplete = isComplete;
        Locations = new ReadOnlyDictionary<string, SiteSourceLocation>((locations ?? new Dictionary<string, SiteSourceLocation>()).ToDictionary(pair => pair.Key.Normalize(), pair => pair.Value ?? throw new ArgumentException("Field locations must not contain null.", nameof(locations)), StringComparer.Ordinal));
    }

    public DocumentKey Key { get; }
    public string SourcePath { get; }
    public IReadOnlyDictionary<string, object?> Values { get; }
    public DocumentSemanticPublication Publication { get; }
    public bool IsComplete { get; }
    public IReadOnlyDictionary<string, SiteSourceLocation> Locations { get; }

    internal SiteSourceLocation Location(string field) => Locations.GetValueOrDefault(field) ?? Locations.GetValueOrDefault(field.Split('[')[0]) ?? new SiteSourceLocation(SourcePath);
    internal bool TryValue(string field, out object? value)
    {
        value = Values;
        foreach (var name in field.Split('.'))
        {
            if (value is not IReadOnlyDictionary<string, object?> mapping || !mapping.TryGetValue(name, out value))
                return false;
        }

        return true;
    }

    internal static DocumentSemanticDocument FromLocated(DocumentKey key, string sourcePath, LocatedYamlMapping? values, DocumentSemanticPublication publication, bool isComplete)
    {
        var locations = new Dictionary<string, SiteSourceLocation>(StringComparer.Ordinal);
        SiteSourceLocation Remap(SiteSourceLocation original) => new(sourcePath, original.Line, original.Column, original.EndLine, original.EndColumn);
        void VisitValue(object? value, string field)
        {
            value = LocatedYamlValue.Unwrap(value);
            if (value is LocatedYamlMapping nested)
                Visit(nested, field);
            else if (value is LocatedYamlSequence sequence)
                for (var index = 0; index < sequence.Count; index++)
                {
                    var item = field + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                    locations[item] = Remap(sequence.GetItemLocation(index));
                    VisitValue(sequence[index], item);
                }
        }

        void Visit(LocatedYamlMapping mapping, string prefix)
        {
            foreach (var pair in mapping)
            {
                var field = prefix.Length == 0 ? pair.Key : prefix + "." + pair.Key;
                locations[field] = Remap(mapping.GetValueLocation(pair.Key));
                VisitValue(pair.Value, field);
            }
        }

        if (values is not null)
            Visit(values, string.Empty);
        return new(key, sourcePath, (IReadOnlyDictionary<string, object?>?)values ?? new Dictionary<string, object?>(), publication, isComplete && values is not null, locations);
    }

    internal static void ValidateKey(DocumentKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _ = new ContentCollectionId(key.Collection);
        _ = new ContentEntryId(key.Version);
        _ = CultureInfo.GetCultureInfo(key.Locale);
        _ = new ContentEntryId(key.Id);
    }

    private static IReadOnlyDictionary<string, object?> FreezeMap(IReadOnlyDictionary<string, object?> values, int depth = 0) => new ReadOnlyDictionary<string, object?>(values.ToDictionary(pair => pair.Key.Normalize(), pair => Freeze(pair.Value, depth + 1), StringComparer.Ordinal));
    private static object? Freeze(object? value, int depth)
    {
        if (depth > 64)
            throw new ArgumentException("Semantic metadata exceeds the nesting limit.", nameof(value));
        value = LocatedYamlValue.Unwrap(value);
        if (value is double number && !double.IsFinite(number) || value is float single && !float.IsFinite(single))
            throw new ArgumentException("Semantic numeric metadata must be finite; native scalar text can preserve other YAML values.", nameof(value));
        return value switch
        {
            null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DocumentKey => value,
            IReadOnlyDictionary<string, object?> mapping => FreezeMap(mapping, depth),
            IReadOnlyList<object?> list => Array.AsReadOnly(list.Select(item => Freeze(item, depth + 1)).ToArray()),
            JsonElement element => FreezeJson(element, depth),
            _ => throw new ArgumentException("Semantic metadata accepts only scalar, mapping, sequence, or document-key values.", nameof(value)),
        };
    }

    private static object? FreezeJson(JsonElement element, int depth) => element.ValueKind switch
    {
        JsonValueKind.Object => FreezeMap(element.EnumerateObject().ToDictionary(pair => pair.Name, pair => (object?)pair.Value, StringComparer.Ordinal), depth),
        JsonValueKind.Array => Array.AsReadOnly(element.EnumerateArray().Select(item => Freeze(item, depth + 1)).ToArray()),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new ArgumentException("Undefined JSON is not semantic metadata."),
    };
}

/// <summary>The result of resolving one declared document identity against captured catalog evidence.</summary>
public sealed class DocumentSemanticReference
{
    internal DocumentSemanticReference(DocumentKey key, DocumentSemanticReferenceState state, SiteRoute? route, SiteSourceLocation? location)
    {
        Key = key;
        State = state;
        Route = route;
        Location = location;
    }

    public DocumentKey Key { get; }
    public DocumentSemanticReferenceState State { get; }
    public SiteRoute? Route { get; }
    public SiteSourceLocation? Location { get; }
}

/// <summary>Published routing plus captured metadata. Completeness must cover the entire declared reference universe.</summary>
public sealed partial class DocumentSemanticSnapshot
{
    private readonly IReadOnlyDictionary<DocumentKey, DocumentSemanticDocument> documents;
    public DocumentSemanticSnapshot(DocumentCatalog catalog, IEnumerable<DocumentSemanticDocument> documents, bool isComplete, IEnumerable<DocumentSemanticScope>? completeScopes = null)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ArgumentNullException.ThrowIfNull(documents);
        this.documents = new ReadOnlyDictionary<DocumentKey, DocumentSemanticDocument>(documents.ToDictionary(document => (document ?? throw new ArgumentException("Documents must not contain null.", nameof(documents))).Key));
        foreach (var document in this.documents.Values)
        {
            var published = FindPage(document.Key) is not null;
            if (document.Publication == DocumentSemanticPublication.Published && !published || document.Publication == DocumentSemanticPublication.Unpublished && published)
                throw new ArgumentException("Publication evidence must agree with the published catalog.", nameof(documents));
        }

        IsComplete = isComplete;
        CompleteScopes = completeScopes is null ? null : Array.AsReadOnly(completeScopes.Select(scope =>
        {
            ArgumentNullException.ThrowIfNull(scope);
            DocumentSemanticDocument.ValidateKey(new(scope.Collection, scope.Version, scope.Locale, "scope"));
            return scope;
        }).Distinct().ToArray());
    }

    public DocumentCatalog Catalog { get; }
    public IReadOnlyList<DocumentSemanticDocument> Documents => Array.AsReadOnly(documents.Values.OrderBy(document => document.Key.ToString(), StringComparer.Ordinal).ToArray());
    public bool IsComplete { get; }
    /// <summary>Null explicitly asserts a global universe; otherwise absence is proved only within these scopes.</summary>
    public IReadOnlyList<DocumentSemanticScope>? CompleteScopes { get; }

    public DocumentSemanticReference Resolve(DocumentKey key)
    {
        DocumentSemanticDocument.ValidateKey(key);
        if (FindPage(key) is { } page)
            return new(key, DocumentSemanticReferenceState.Resolved, page.Route, new SiteSourceLocation(documents.TryGetValue(key, out var evidence) ? evidence.SourcePath : page.SourcePath));
        if (documents.TryGetValue(key, out var document))
            return new(key, document.Publication == DocumentSemanticPublication.Unpublished ? DocumentSemanticReferenceState.Unpublished : DocumentSemanticReferenceState.Unknown, null, new SiteSourceLocation(document.SourcePath));
        var complete = IsComplete && (CompleteScopes is null || CompleteScopes.Contains(new(key.Collection, key.Version, key.Locale)));
        return new(key, complete ? DocumentSemanticReferenceState.Missing : DocumentSemanticReferenceState.Unknown, null, null);
    }

    private DocumentPage? FindPage(DocumentKey key)
    {
        try
        {
            return Catalog.Resolve(key);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }
}

public sealed class DocumentSemanticAssessment
{
    internal DocumentSemanticAssessment(DocumentKey document, DocumentSemanticRule rule, DocumentSemanticState state, IEnumerable<SiteDiagnostic>? diagnostics = null)
    {
        Document = document;
        Rule = rule;
        State = state;
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
    }

    public DocumentKey Document { get; }
    public DocumentSemanticRule Rule { get; }
    public DocumentSemanticState State { get; }
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
}

/// <summary>One evaluator for editor metadata and real builds. Deferred is never a successful validation.</summary>
public sealed partial class DocumentSemanticRuleSet
{
    /// <summary>Requires an explicit supported version and published prerequisites for published documentation.</summary>
    public static DocumentSemanticRuleSet Documentation { get; } = new([DocumentSemanticRule.RequiredWhenPublished("supported_version"), DocumentSemanticRule.Reference("prerequisites")]);

    public DocumentSemanticRuleSet(IEnumerable<DocumentSemanticRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Rules = Array.AsReadOnly(rules.Select(rule => rule ?? throw new ArgumentException("Rules must not contain null.", nameof(rules))).ToArray());
        if (Rules.GroupBy(rule => (rule.Kind, rule.Field, rule.ConditionField, rule.ExpectedValue)).Any(group => group.Skip(1).Any()))
            throw new ArgumentException("Duplicate semantic rules are not allowed.", nameof(rules));
    }

    public IReadOnlyList<DocumentSemanticRule> Rules { get; }

    public IReadOnlyList<DocumentSemanticAssessment> Evaluate(DocumentSemanticDocument document, DocumentSemanticSnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Array.AsReadOnly(Rules.Select(rule => EvaluateRule(document, snapshot, rule)).ToArray());
    }

    private static DocumentSemanticAssessment EvaluateRule(DocumentSemanticDocument document, DocumentSemanticSnapshot? snapshot, DocumentSemanticRule rule)
    {
        DocumentSemanticAssessment Result(DocumentSemanticState state, params SiteDiagnostic[] diagnostics) => new(document.Key, rule, state, diagnostics);
        SiteDiagnostic Diagnostic(string id, string message, SiteDiagnosticSeverity severity = SiteDiagnosticSeverity.Error, SiteSourceLocation? related = null) => new(id, severity, message, document.Location(rule.Field), "document-semantics", related is null ? [] : [related]);
        DocumentSemanticAssessment Deferred(string message) => Result(DocumentSemanticState.Deferred, Diagnostic("LSDOC214", message, SiteDiagnosticSeverity.Info));
        if (!document.IsComplete)
            return Deferred("Document metadata is incomplete; this semantic constraint was not validated.");
        if (rule.PublishedSourceOnly)
        {
            if (document.Publication == DocumentSemanticPublication.Unknown)
                return Deferred("Publication depends on the build policy; this semantic constraint requires a captured publication decision.");
            if (document.Publication == DocumentSemanticPublication.Unpublished)
                return Result(DocumentSemanticState.NotApplicable);
        }

        if (rule.Kind == DocumentSemanticRuleKind.RequiredWhen)
        {
            if (!document.TryValue(rule.ConditionField!, out var condition) || !GeneratedContentBindingContext.ConditionalMatches(condition, rule.ExpectedValue!))
                return Result(DocumentSemanticState.NotApplicable);
        }

        document.TryValue(rule.Field, out var value);
        if (rule.Kind != DocumentSemanticRuleKind.Reference)
            return Missing(value) ? Result(DocumentSemanticState.Failed, Diagnostic("LSDOC210", $"Field '{rule.Field}' is required by the declared document constraint.")) : Result(DocumentSemanticState.Passed);
        if (Missing(value))
            return Result(DocumentSemanticState.NotApplicable);
        if (snapshot is null)
            return Deferred("The document catalog is unavailable; referenced documents were not validated.");
        var targets = value is IReadOnlyList<object?> list ? list : [value];
        var diagnostics = new List<SiteDiagnostic>();
        var deferred = false;
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var location = document.Location(value is IReadOnlyList<object?> ? rule.Field + "[" + index.ToString(CultureInfo.InvariantCulture) + "]" : rule.Field);
            SiteDiagnostic ReferenceDiagnostic(string id, string message, SiteDiagnosticSeverity severity = SiteDiagnosticSeverity.Error, SiteSourceLocation? related = null) => new(id, severity, message, location, "document-semantics", related is null ? [] : [related]);
            if (!TryTarget(document.Key, target, out var key))
            {
                diagnostics.Add(ReferenceDiagnostic("LSDOC211", $"Field '{rule.Field}' requires document IDs or explicit collection/version/locale/id references."));
                continue;
            }

            var reference = snapshot.Resolve(key!);
            if (rule.SameVersion && key!.Version != document.Key.Version)
                diagnostics.Add(ReferenceDiagnostic("LSDOC213", $"Document reference '{key}' must use version '{document.Key.Version}'.", related: reference.Location));
            if (rule.SameLocale && key!.Locale != document.Key.Locale)
                diagnostics.Add(ReferenceDiagnostic("LSDOC213", $"Document reference '{key}' must use locale '{document.Key.Locale}'.", related: reference.Location));
            switch (reference.State)
            {
                case DocumentSemanticReferenceState.Missing:
                    diagnostics.Add(ReferenceDiagnostic("LSDOC211", $"Referenced document '{key}' is absent from the complete input catalog."));
                    break;
                case DocumentSemanticReferenceState.Unpublished when rule.PublishedTarget && document.Publication == DocumentSemanticPublication.Published:
                    diagnostics.Add(ReferenceDiagnostic("LSDOC212", $"Published document '{document.Key}' must not reference unpublished document '{key}'.", related: reference.Location));
                    break;
                case DocumentSemanticReferenceState.Unpublished when rule.PublishedTarget && document.Publication == DocumentSemanticPublication.Unknown:
                    deferred = true;
                    diagnostics.Add(ReferenceDiagnostic("LSDOC214", $"Reference '{key}' exists, but checking its publication requires the source document's build publication decision.", SiteDiagnosticSeverity.Info, reference.Location));
                    break;
                case DocumentSemanticReferenceState.Unknown:
                    deferred = true;
                    diagnostics.Add(ReferenceDiagnostic("LSDOC214", $"Referenced document '{key}' cannot be confirmed from the captured catalog.", SiteDiagnosticSeverity.Info, reference.Location));
                    break;
            }
        }

        return new(document.Key, rule, diagnostics.Any(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error) ? DocumentSemanticState.Failed : deferred ? DocumentSemanticState.Deferred : DocumentSemanticState.Passed, diagnostics);
    }

    private static bool Missing(object? value) => GeneratedContentBindingContext.ConditionalMissing(value);
    private static bool TryTarget(DocumentKey source, object? value, out DocumentKey? key)
    {
        key = value as DocumentKey;
        if (key is null && value is string id && !string.IsNullOrWhiteSpace(id))
            key = source with
            {
                Id = id
            };
        if (key is null && value is IReadOnlyDictionary<string, object?> map && map.Count == 4 && map.TryGetValue("collection", out var collection) && collection is string c && map.TryGetValue("version", out var version) && version is string v && map.TryGetValue("locale", out var locale) && locale is string l && map.TryGetValue("id", out var documentId) && documentId is string i)
            key = new(c, v, l, i);
        if (key is null)
            return false;
        try
        {
            DocumentSemanticDocument.ValidateKey(key);
            return true;
        }
        catch (ArgumentException)
        {
            key = null;
            return false;
        }
    }
}
