using Syntamark.Compilation;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Documentation;

namespace LithoSharp.Inspection;

public static class DocumentInspection
{
    /// <summary>Inspects unsaved text without publishing. Without explicit project context, syntax inspection does not establish build-equivalent binding or route resolution.</summary>
    /// <remarks>Does not run MSBuild or a factory on startup or keystrokes.</remarks>
    public static DocumentInfo Inspect(string sourcePath, string text, DocumentInspectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(text);

        var analysis = MarkdownAnalysisAdapter.InspectDocument(text, sourcePath, cancellationToken);
        var locator = new SourceText(text);
        var diagnostics = new List<SiteDiagnostic>();
        IReadOnlyDictionary<string, object?> frontMatter = System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>.Empty;
        var parsedMapping = (LocatedYamlMapping?)null;
        var envelope = analysis.FrontMatter;
        switch (envelope?.State)
        {
            case MdFrontMatterState.Parsed:
            case MdFrontMatterState.Invalid:
                if (envelope.YamlSpan is { } yaml && envelope.ClosingSpan is not null)
                {
                    var yamlLine = locator.GetLineAndColumn(yaml.Start).Line;
                    var parsed = MarkdownContentCollectionLoader<DocumentFrontMatter>.ParseYaml(
                        text.Substring(yaml.Start, yaml.Length), sourcePath, yamlLine, cancellationToken);
                    if (parsed.IsSuccess)
                    {
                        parsedMapping = parsed.Value;
                        frontMatter = UnwrapMapping(parsed.Value!);
                    }
                    else
                    {
                        diagnostics.AddRange(parsed.Diagnostics);
                    }
                }
                break;
            case MdFrontMatterState.Absent:
                diagnostics.Add(new SiteDiagnostic(
                    MarkdownContentDiagnosticIds.MissingFrontMatter,
                    SiteDiagnosticSeverity.Error,
                    "Markdown document must start with YAML front matter.",
                    new SiteSourceLocation(sourcePath, 1, 1)));
                break;
            case MdFrontMatterState.Unterminated:
                diagnostics.Add(new SiteDiagnostic(
                    MarkdownContentDiagnosticIds.UnterminatedFrontMatter,
                    SiteDiagnosticSeverity.Error,
                    "Markdown document has no closing front matter marker.",
                    envelope.FailureSpan is { } failure
                        ? Locate(locator, sourcePath, new SourceSpan(failure.Start, failure.Length))
                        : new SiteSourceLocation(sourcePath)));
                break;
            case MdFrontMatterState.Empty:
                diagnostics.Add(new SiteDiagnostic(
                    MarkdownContentDiagnosticIds.EmptyFrontMatter,
                    SiteDiagnosticSeverity.Error,
                    "YAML front matter must not be empty.",
                    new SiteSourceLocation(sourcePath, 2, 1)));
                break;
        }

        if (MarkdownAnalysisAdapter.IncompleteDiagnostic(analysis, sourcePath) is { } incomplete)
        {
            diagnostics.Add(incomplete);
        }
        var headings = MarkdownAnalysisAdapter.InspectionHeadings(analysis, cancellationToken);
        var links = MarkdownAnalysisAdapter.InspectionLinks(analysis, cancellationToken);
        diagnostics.AddRange(LithoLimits.MapDiagnostics(analysis, sourcePath,
            includeCompatibilityAdvisories: options?.EnableCompatibilityAdvisory == true, cancellationToken: cancellationToken));

        var project = options?.Project;
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
            if (parsedMapping is not null)
            {
                if (string.Equals(project.SchemaName, "document", StringComparison.Ordinal))
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
            [],
            frontMatter,
            options?.Version,
            options?.Locale,
            diagnostics.ToArray(),
            candidates,
            status,
            project?.SchemaName)
        {
            SchemaInspectionScope = project is null ? "unknown"
                : project.SchemaName is "document" or "post" ? "built-in-binding" : "build-required",
            Sections = (analysis.Sections ?? []).Select(section => new DocumentSectionInfo(section.LocalKey,
                section.ParentLocalKey, section.HeadingLocalKey, section.SubtreeSpan is { } span
                    ? Locate(locator, sourcePath, new SourceSpan(span.Start, span.Length)) : null)).ToArray(),
        };
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
