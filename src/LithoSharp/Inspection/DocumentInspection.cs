using Syntamark.Compilation;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Documentation;
using LithoSharp.OpenKnowledge;

namespace LithoSharp.Inspection;

public static class DocumentInspection
{
    /// <summary>Inspects unsaved text without publishing. Without explicit project context, syntax inspection does not establish build-equivalent binding or route resolution.</summary>
    /// <remarks>Does not run MSBuild or a factory on startup or keystrokes.</remarks>
    public static DocumentInfo Inspect(string sourcePath, string text, DocumentInspectionOptions? options = null, CancellationToken cancellationToken = default)
        => InspectCore(sourcePath, text, options, null, cancellationToken);

    internal static DocumentInfo InspectWithAnalysis(string sourcePath, string text, DocumentInspectionOptions? options,
        Func<string, int, MarkdownOptions, CancellationToken, IncrementalInspectionAnalysis> analyzeBody,
        CancellationToken cancellationToken)
        => InspectCore(sourcePath, text, options, analyzeBody, cancellationToken);

    private static DocumentInfo InspectCore(string sourcePath, string text, DocumentInspectionOptions? options,
        Func<string, int, MarkdownOptions, CancellationToken, IncrementalInspectionAnalysis>? analyzeBody,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(text);

        var locator = new SourceText(text);
        var project = options?.Project;
        var input = project?.InputFor(sourcePath);
        var isOkf = input?.InputProfile == ContentInputProfile.OpenKnowledgeFormat;
        var diagnostics = new List<SiteDiagnostic>();
        IReadOnlyDictionary<string, object?> frontMatter = System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>.Empty;
        LocatedYamlMapping? parsedMapping = null;
        Syntamark.YamlDocument? yamlSyntax = null;
        var yamlOffset = 0;
        var bodyOffset = 0;
        var body = text;
        var header = MdParser.ScanFrontMatter(text, new MdOptions(), cancellationToken);
        if (header.State == MdFrontMatterScanState.Closed && header.Status == MdParseStatus.Complete
            && header.YamlSpan is { } yaml && header.BodySpan is { } bodySpan && header.ClosingSpan is not null)
        {
            yamlOffset = yaml.Start;
            bodyOffset = bodySpan.Start;
            body = text.Substring(bodySpan.Start, bodySpan.Length);
            var yamlText = text.Substring(yaml.Start, yaml.Length);
            yamlSyntax = Syntamark.YamlParser.Parse(yamlText,
                isOkf ? null : MarkdownContentCollectionLoader<DocumentFrontMatter>.TypedYamlOptions, cancellationToken);
            if (isOkf)
            {
                foreach (var diagnostic in yamlSyntax.Diagnostics)
                    diagnostics.Add(new SiteDiagnostic("OKF002", SiteDiagnosticSeverity.Error,
                        diagnostic.Reason + ": " + diagnostic.Message,
                        Locate(locator, sourcePath, new SourceSpan(yamlOffset + diagnostic.RawSpan.Start, diagnostic.RawSpan.Length)),
                        diagnostic.Kind switch
                        {
                            Syntamark.YamlDiagnosticKind.Unsupported => OkfDiagnosticCategory.Unsupported.ToString(),
                            Syntamark.YamlDiagnosticKind.ResourceLimit => OkfDiagnosticCategory.ResourceLimit.ToString(),
                            Syntamark.YamlDiagnosticKind.Canceled => OkfDiagnosticCategory.Canceled.ToString(),
                            _ => OkfDiagnosticCategory.Structure.ToString(),
                        }, null));
                if (yamlSyntax is { Status: Syntamark.MarkdownParseStatus.Complete, Root.Kind: Syntamark.MarkdownYamlKind.Mapping })
                {
                    var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var field in yamlSyntax.Root.Entries)
                        if (field.Key is { Kind: Syntamark.MarkdownYamlKind.Scalar, ScalarKind: Syntamark.YamlScalarKind.String, Value: { } key })
                            fields.TryAdd(key, field.Value);
                    frontMatter = new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(fields);
                }
            }
            else if (string.IsNullOrWhiteSpace(yamlText))
                diagnostics.Add(new SiteDiagnostic(MarkdownContentDiagnosticIds.EmptyFrontMatter, SiteDiagnosticSeverity.Error,
                    "YAML front matter must not be empty.", new SiteSourceLocation(sourcePath, 2, 1)));
            else
            {
                var parsed = MarkdownContentCollectionLoader<DocumentFrontMatter>.ParseYaml(yamlSyntax,
                    sourcePath, locator.GetLineAndColumn(yaml.Start).Line, cancellationToken);
                if (parsed.IsSuccess)
                {
                    parsedMapping = parsed.Value;
                    frontMatter = UnwrapMapping(parsed.Value!);
                }
                else
                    diagnostics.AddRange(parsed.Diagnostics);
            }
        }
        else if (header.State == MdFrontMatterScanState.Absent && header.Status == MdParseStatus.Complete)
        {
            if (!isOkf)
                diagnostics.Add(new SiteDiagnostic(MarkdownContentDiagnosticIds.MissingFrontMatter, SiteDiagnosticSeverity.Error,
                    "Markdown document must start with YAML front matter.", new SiteSourceLocation(sourcePath, 1, 1)));
        }
        else if (header.State == MdFrontMatterScanState.Unterminated && header.FailureSpan is { } failure && header.FailureLine is > 0)
            diagnostics.Add(new SiteDiagnostic(MarkdownContentDiagnosticIds.UnterminatedFrontMatter, SiteDiagnosticSeverity.Error,
                "Markdown document has no closing front matter marker.", Locate(locator, sourcePath, new SourceSpan(failure.Start, failure.Length))));
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            body = string.Empty;
            diagnostics.Add(new SiteDiagnostic(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId, SiteDiagnosticSeverity.Error,
                "Markdown front matter scanning is incomplete; unavailable facts were not validated.",
                header.Diagnostics.FirstOrDefault()?.RawSpan is { } span
                    ? Locate(locator, sourcePath, new SourceSpan(span.Start, span.Length)) : new SiteSourceLocation(sourcePath)));
        }
        var markdownOptions = options?.EffectiveMarkdown ?? new MarkdownOptions();
        var prepared = analyzeBody?.Invoke(body, bodyOffset, markdownOptions, cancellationToken);
        var analysis = prepared?.Analysis ?? MarkdownAnalysisAdapter.InspectBody(body, sourcePath, markdownOptions, cancellationToken);
        if (!string.Equals(analysis.RawText, body, StringComparison.Ordinal))
            throw new InvalidOperationException("Inspection analysis does not describe the exact Markdown body.");
        var bodyLine = locator.GetLineAndColumn(bodyOffset).Line;
        if (MarkdownAnalysisAdapter.IncompleteDiagnostic(analysis, sourcePath, bodyLine) is { } incomplete)
            diagnostics.Add(incomplete);
        var headings = MarkdownAnalysisAdapter.InspectionHeadings(analysis, cancellationToken, bodyOffset);
        var links = MarkdownAnalysisAdapter.InspectionLinks(analysis, cancellationToken, bodyOffset);
        diagnostics.AddRange(LithoLimits.MapDiagnostics(analysis, sourcePath, bodyLine,
            includeCompatibilityAdvisories: options?.EnableCompatibilityAdvisory == true, cancellationToken: cancellationToken));
        if (isOkf && header.Status == MdParseStatus.Complete && header.State is MdFrontMatterScanState.Absent or MdFrontMatterScanState.Closed)
        {
            _ = OkfDocumentValidation.FrontMatter(input!.InputKind, input.InputPath ?? sourcePath,
                header.State == MdFrontMatterScanState.Absent ? Syntamark.MarkdownFrontMatterState.Absent
                    : yamlSyntax?.Root is null && yamlSyntax?.Status == Syntamark.MarkdownParseStatus.Complete
                        ? Syntamark.MarkdownFrontMatterState.Empty : Syntamark.MarkdownFrontMatterState.Parsed,
                yamlSyntax, yamlOffset,
                header.OpeningSpan is { } opening ? new Syntamark.RawSpan(opening.Start, opening.Length) : null,
                header.YamlSpan is { } payload ? new Syntamark.RawSpan(payload.Start, payload.Length) : null, project!.AcquiredAt,
                (id, severity, category, message, span) => diagnostics.Add(new SiteDiagnostic(id, severity, message,
                    span is { } value ? Locate(locator, sourcePath, new SourceSpan(value.Start, value.Length)) : new SiteSourceLocation(sourcePath),
                    category.ToString(), null)));
            if (input.InputKind == ContentInputKind.Log)
                foreach (var heading in headings.Where(static heading => heading.RawLevel == 2))
                    if (!OkfDocumentValidation.IsLogDate(heading.Text))
                        diagnostics.Add(new SiteDiagnostic("OKF008", SiteDiagnosticSeverity.Error,
                            "Log date headings must use a valid YYYY-MM-DD date.", Locate(locator, sourcePath, heading.Span), OkfDiagnosticCategory.Structure.ToString(), null));
        }
        var semanticDocument = isOkf ? null : CurrentSemanticDocument(project, sourcePath, parsedMapping);
        var componentSchemas = markdownOptions.Components?.Schemas ?? options?.Project?.MarkdownComponents ?? [];
        var components = markdownOptions.ProfileId == MarkdownOptions.DocumentationProfile
            ? MarkdownComponentRendering.Inspect(analysis, new DocumentSource(sourcePath, bodyOffset) { BodyStartLine = bodyLine },
                componentSchemas, cancellationToken, semanticDocument, project?.Semantics)
            : null;
        if (components is not null)
            diagnostics.AddRange(components.Diagnostics);
        var staticSchema = isOkf ? null : project?.SchemaFor(sourcePath);
        IReadOnlyList<DocumentSemanticAssessment> semanticAssessments = [];
        if (semanticDocument is not null && project?.Semantics is { } semanticSnapshot
            && project.SemanticRules.TryGetValue(semanticDocument.Key.Collection, out var rules))
        {
            semanticAssessments = rules.Evaluate(semanticDocument, semanticSnapshot);
            diagnostics.AddRange(semanticAssessments.SelectMany(assessment => assessment.Diagnostics));
        }
        var candidates = project is null
            ? (IReadOnlyList<ProjectRouteCandidate>)[]
            : MatchCandidates(project, sourcePath, options?.Version, options?.Locale);
        string? route = options?.Route;
        var status = DocumentProjectStatus.NoContext;
        if (project is not null)
        {
            // Front matter binding uses the same binder as real builds for the
            // built-in schemas only. Custom schemas are never bound here: user
            // constructors, setters, and validators must not run per keystroke.
            if (!isOkf && parsedMapping is not null)
            {
                if (staticSchema is not null)
                {
                    diagnostics.AddRange(staticSchema.Schema.Validate(parsedMapping,
                        new SiteSourceLocation(sourcePath, locator.GetLineAndColumn(yamlOffset).Line, 1), cancellationToken));
                }
                else if (string.Equals(project.SchemaName, "document", StringComparison.Ordinal))
                {
                    diagnostics.AddRange(new ReflectionContentFrontMatterBinder<DocumentFrontMatter>()
                        .Bind(parsedMapping, new SiteSourceLocation(sourcePath, 2, 1)).Diagnostics);
                }
                else if (string.Equals(project.SchemaName, "post", StringComparison.Ordinal))
                {
                    diagnostics.AddRange(new ReflectionContentFrontMatterBinder<PostFrontMatter>()
                        .Bind(parsedMapping, new SiteSourceLocation(sourcePath, 2, 1)).Diagnostics);
                }
            }

            if (candidates.Count == 1)
            {
                status = DocumentProjectStatus.Resolved;
                route = candidates[0].PublicPath;
            }
            else if (candidates.Count > 1)
            {
                status = DocumentProjectStatus.Ambiguous;
                route = null;
            }
            else if (IsDraft(frontMatter))
            {
                status = DocumentProjectStatus.Draft;
                route = null;
            }
            else
            {
                status = DocumentProjectStatus.Unresolved;
                route = null;
            }
        }

        return new DocumentInfo(
            options?.DocumentId ?? sourcePath,
            sourcePath,
            DocumentSemantics.TitleOf(headings),
            headings.Select(heading => new DocumentHeadingInfo(heading.Text, heading.Id, heading.RawLevel, heading.OutputLevel,
                Locate(locator, sourcePath, heading.Span))
            { LocalKey = heading.LocalKey }).ToArray(),
            links.Select(link => new DocumentLinkInfo(link.RawText, link.Url, link.Title, link.IsImage, Locate(locator, sourcePath, link.Span))).ToArray(),
            links.Where(link => link.IsImage).Select(link => new DocumentAssetInfo(link.Url, Locate(locator, sourcePath, link.Span))).ToArray(),
            route,
            components?.Components.Select(component => new DocumentComponentInfo(component.Name, Locate(locator, sourcePath, component.Span))).ToArray() ?? [],
            frontMatter,
            options?.Version,
            options?.Locale,
            diagnostics.ToArray(),
            candidates,
            status,
            project?.SchemaName)
        {
            SchemaInspectionScope = isOkf ? "okf-native-metadata" : staticSchema is not null ? "static-schema-build-required" : project is null ? "unknown"
                : project.SchemaName is "document" or "post" ? "built-in-binding" : "build-required",
            FrontMatterSyntax = yamlSyntax,
            FrontMatterStartOffset = yamlOffset,
            StaticSchema = staticSchema,
            MarkdownSyntax = analysis,
            IncrementalAnalysis = prepared?.Metrics,
            BodyStartOffset = bodyOffset,
            MarkdownComponents = componentSchemas,
            SemanticAssessments = semanticAssessments,
            Sections = (analysis.Sections ?? []).Select(section => new DocumentSectionInfo(section.LocalKey,
                section.ParentLocalKey, section.HeadingLocalKey, section.SubtreeSpan is { } span
                    ? Locate(locator, sourcePath, new SourceSpan(span.Start + bodyOffset, span.Length)) : null)).ToArray(),
        };
    }

    private static DocumentSemanticDocument? CurrentSemanticDocument(ProjectInspectionSnapshot? project,
        string sourcePath, LocatedYamlMapping? parsedMapping)
    {
        if (project?.Semantics is not { } snapshot)
            return null;
        var path = sourcePath.Replace('\\', '/').Normalize();
        var matches = snapshot.Documents.Where(document => string.Equals(document.SourcePath.Replace('\\', '/').Normalize(),
            path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Take(2).ToArray();
        return matches.Length == 1 ? DocumentSemanticDocument.FromLocated(matches[0].Key, sourcePath, parsedMapping,
            DocumentSemanticPublication.Unknown, parsedMapping is not null) : null;
    }

    private static IReadOnlyList<ProjectRouteCandidate> MatchCandidates(
        ProjectInspectionSnapshot project, string sourcePath, string? version, string? locale)
    {
        var normalized = sourcePath.Replace('\\', '/');
        return project.Routes.Where(candidate =>
            (version is null || string.Equals(candidate.Version, version, StringComparison.Ordinal)) &&
            (locale is null || string.Equals(candidate.Locale, locale, StringComparison.Ordinal)) &&
            (string.Equals(candidate.SourcePath, normalized, StringComparison.Ordinal) ||
                normalized.EndsWith('/' + candidate.SourcePath, StringComparison.Ordinal))).ToArray();
    }

    private static bool IsDraft(IReadOnlyDictionary<string, object?> frontMatter) =>
        frontMatter.TryGetValue("draft", out var value) &&
        (value is bool draft && draft ||
            value is string text && string.Equals(text, "true", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, object?> UnwrapMapping(LocatedYamlMapping mapping)
    {
        var result = new Dictionary<string, object?>(mapping.Count, StringComparer.Ordinal);
        foreach (var entry in mapping)
        {
            result[entry.Key] = UnwrapValue(entry.Value);
        }
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>(result);
    }

    private static object? UnwrapValue(object? value) => value switch
    {
        LocatedYamlMapping mapping => UnwrapMapping(mapping),
        LocatedYamlSequence sequence => Array.AsReadOnly(sequence.Select(UnwrapValue).ToArray()),
        _ => LocatedYamlValue.Unwrap(value),
    };

    private static SiteSourceLocation Locate(SourceText locator, string sourcePath, SourceSpan span)
    {
        if (span.End <= locator.Text.Length)
        {
            var (line, column) = locator.GetLineAndColumn(span.Start);
            var (endLine, endColumn) = locator.GetLineAndColumn(span.End);
            return new SiteSourceLocation(sourcePath, line, column, endLine, endColumn);
        }

        return new SiteSourceLocation(sourcePath);
    }
}
