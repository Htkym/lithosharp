using LithoSharp.Diagnostics;
using LithoSharp.Inspection;
using LithoSharp.Documentation;
using Syntamark.Compilation;

namespace LithoSharp.Content.Compilation;

/// <summary>Diagnostics for explicitly selected static Markdown components.</summary>
public static class MarkdownComponentDiagnosticIds
{
    public const string UnknownComponent = "LSM020";
    public const string InvalidInvocation = "LSM021";
    public const string StaticSchemaUnavailable = "LSM022";
    public const string RenderingFailed = "LSM023";
    public const string ReferenceContextUnavailable = "LSM024";
}

internal sealed record MarkdownComponentRenderPlan(
    IReadOnlyDictionary<int, string> HtmlByDirectiveStart,
    IReadOnlyList<DocumentComponent> Components,
    IReadOnlyList<SiteDiagnostic> Diagnostics);

internal static class MarkdownComponentRendering
{
    internal static MarkdownComponentRenderPlan Prepare(MdAnalysisResult analysis, DocumentSource source,
        MarkdownComponentRegistry registry, ComponentRenderingContext? context, bool renderHtml, CancellationToken cancellationToken)
        => PrepareCore(analysis, source, registry, registry.Schemas.ToDictionary(static schema => schema.Name, StringComparer.Ordinal),
            context, renderHtml, cancellationToken, context?.Document, context?.DocumentSemantics);

    internal static MarkdownComponentRenderPlan Inspect(MdAnalysisResult analysis, DocumentSource source,
        IReadOnlyList<MarkdownComponentSchema> schemas, CancellationToken cancellationToken,
        DocumentSemanticDocument? document = null, DocumentSemanticSnapshot? semantics = null) =>
        PrepareCore(analysis, source, null, schemas.ToDictionary(static schema => schema.Name, StringComparer.Ordinal), null, false,
            cancellationToken, document, semantics);

    private static MarkdownComponentRenderPlan PrepareCore(MdAnalysisResult analysis, DocumentSource source,
        MarkdownComponentRegistry? registry, IReadOnlyDictionary<string, MarkdownComponentSchema> schemas,
        ComponentRenderingContext? context, bool renderHtml, CancellationToken cancellationToken,
        DocumentSemanticDocument? document, DocumentSemanticSnapshot? semantics)
    {
        var html = new Dictionary<int, string>();
        var components = new List<DocumentComponent>();
        var diagnostics = new List<SiteDiagnostic>();
        if (analysis.Root is null)
            return new MarkdownComponentRenderPlan(html, components, diagnostics);
        var text = new SourceText(analysis.RawText);
        var pending = new Stack<MdSyntaxNode>();
        pending.Push(analysis.Root);
        while (pending.TryPop(out var node))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node.Directive is not { Name: "component" } directive || node.RawSpan is not { } envelope)
            {
                for (var index = node.Children.Count - 1; index >= 0; index--)
                    pending.Push(node.Children[index]);
                continue;
            }
            // Explicit components are attribute-only. Their bodies never execute nested components.
            var selectors = directive.Attributes.Where(static attribute => attribute.Name == "name").ToArray();
            var name = selectors.Length == 1 ? selectors[0].Value?.Text : null;
            components.Add(new DocumentComponent(name ?? string.Empty,
                new SourceSpan(source.BodyStartOffset + envelope.Start, envelope.Length)));
            if (!directive.IsComplete)
            {
                var failures = directive.Diagnostics;
                if (failures.Count == 0)
                    Error("The component directive is incomplete.", directive.OpeningMarkerRawSpan);
                foreach (var failure in failures)
                    Error(failure.Message, failure.RawSpan ?? directive.HeaderRawSpan);
                continue;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                Error("A component directive requires one nonempty 'name' attribute.", directive.NameRawSpan);
                continue;
            }
            if (!schemas.TryGetValue(name, out var descriptor))
            {
                diagnostics.Add(new SiteDiagnostic(MarkdownComponentDiagnosticIds.UnknownComponent, SiteDiagnosticSeverity.Error,
                    $"Markdown component '{name}' is not registered.", Locate(selectors[0].ValueRawSpan ?? selectors[0].RawSpan)));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(analysis.RawText.Substring(directive.ContentRawSpan.Start, directive.ContentRawSpan.Length)))
            {
                Error("Static Markdown components accept attributes and an empty body.", directive.ContentRawSpan);
                continue;
            }
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var keyLocations = new Dictionary<string, SiteSourceLocation>(StringComparer.Ordinal);
            var valueLocations = new Dictionary<string, SiteSourceLocation>(StringComparer.Ordinal);
            foreach (var attribute in directive.Attributes.Where(static attribute => attribute.Name != "name"))
            {
                var key = attribute.Name.Normalize(System.Text.NormalizationForm.FormC);
                values.Add(key, attribute.Value!.Text);
                if (Locate(attribute.NameRawSpan) is { } keyLocation)
                    keyLocations.Add(key, keyLocation);
                if (Locate(attribute.ValueRawSpan ?? attribute.RawSpan) is { } valueLocation)
                    valueLocations.Add(key, valueLocation);
            }
            var location = Locate(directive.NameRawSpan);
            IReadOnlyDictionary<string, object?> located = location is null ? values
                : new LocatedYamlMapping(values, keyLocations, valueLocations, location);
            if (descriptor.SemanticRulesJson is { } rulesJson)
            {
                var rules = DocumentSemanticRuleSet.ParseJson(rulesJson);
                var deferred = document is null;
                var failed = false;
                if (document is not null)
                {
                    var metadata = located is LocatedYamlMapping mapping
                        ? DocumentSemanticDocument.FromLocated(document.Key, source.FilePath ?? document.SourcePath,
                            mapping, document.Publication, document.IsComplete)
                        : new DocumentSemanticDocument(document.Key, source.FilePath ?? document.SourcePath,
                            values, document.Publication, document.IsComplete);
                    var results = rules.Evaluate(metadata, semantics);
                    diagnostics.AddRange(results.SelectMany(static result => result.Diagnostics));
                    failed = results.Any(static result => result.State == DocumentSemanticState.Failed);
                    deferred = results.Any(static result => result.State == DocumentSemanticState.Deferred);
                }
                else
                    diagnostics.Add(new SiteDiagnostic("LSDOC214", SiteDiagnosticSeverity.Info,
                        "The current document identity is unavailable; component reference constraints were not validated.", location,
                        "document-semantics", []));
                if (deferred && renderHtml)
                    diagnostics.Add(new SiteDiagnostic(MarkdownComponentDiagnosticIds.ReferenceContextUnavailable, SiteDiagnosticSeverity.Error,
                        $"Component '{name}' requires captured document identity and catalog evidence for its declared prop constraints.", location));
                if (failed || deferred && renderHtml)
                    continue;
            }
            if (!renderHtml)
            {
                if (descriptor.SchemaJson is { } schema)
                {
                    try
                    {
                        diagnostics.AddRange(GeneratedFrontMatterSchema.Parse(schema).Validate(located, location, cancellationToken));
                    }
                    catch (ArgumentException failure)
                    {
                        diagnostics.Add(new SiteDiagnostic(MarkdownComponentDiagnosticIds.StaticSchemaUnavailable, SiteDiagnosticSeverity.Info,
                            $"Component '{name}' has no supported static props schema: {failure.Message}", location));
                    }
                }
                else
                    diagnostics.Add(new SiteDiagnostic(MarkdownComponentDiagnosticIds.StaticSchemaUnavailable, SiteDiagnosticSeverity.Info,
                        $"Component '{name}' has no generated static props schema; its custom binder runs only during an explicit build.", location));
                continue;
            }
            if (context is null)
            {
                diagnostics.Add(new SiteDiagnostic(MarkdownComponentDiagnosticIds.RenderingFailed, SiteDiagnosticSeverity.Error,
                    "Component rendering requires a site rendering context.", location));
                continue;
            }
            try
            {
                if (registry is null || !registry.TryGet(name, out var registration))
                    throw new InvalidOperationException("The component registration is unavailable.");
                var result = registration.Render(located, location, context);
                diagnostics.AddRange(result.Diagnostics);
                if (result.IsSuccess)
                    html.Add(envelope.Start - (analysis.BodySpan?.Start ?? 0), result.Value!);
            }
            catch (Exception failure) when (failure is ArgumentException or FormatException or InvalidOperationException)
            {
                diagnostics.Add(new SiteDiagnostic(MarkdownComponentDiagnosticIds.RenderingFailed, SiteDiagnosticSeverity.Error,
                    $"Component '{name}' could not render: {failure.Message}", location));
            }
        }
        return new MarkdownComponentRenderPlan(html, components, diagnostics);

        void Error(string message, MdRawRange span) => diagnostics.Add(new SiteDiagnostic(
            MarkdownComponentDiagnosticIds.InvalidInvocation, SiteDiagnosticSeverity.Error, message, Locate(span)));

        SiteSourceLocation? Locate(MdRawRange span)
        {
            if (string.IsNullOrWhiteSpace(source.FilePath))
                return null;
            var (line, column) = text.GetLineAndColumn(span.Start);
            var (endLine, endColumn) = text.GetLineAndColumn(span.End);
            return new SiteSourceLocation(source.FilePath, source.BodyStartLine + line - 1, column,
                source.BodyStartLine + endLine - 1, endColumn);
        }
    }
}
