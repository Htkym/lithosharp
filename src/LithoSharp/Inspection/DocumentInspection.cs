using Syntamark.Compilation;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Documentation;

namespace LithoSharp.Inspection;

/// <summary>文書検査の入口を表します。</summary>
public static class DocumentInspection
{
    /// <summary>未保存を含むMarkdown文書を一度だけ解析し、文書情報を返します。</summary>
    /// <param name="sourcePath">文書のsource path。位置情報の識別に使います。</param>
    /// <param name="text">文書全体のテキスト。front matterを含む形式です。</param>
    /// <param name="options">routeやversionなどの追加情報。ない場合は <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消token。</param>
    /// <returns>解析結果の不変snapshot。未完成の構文でも取得できた情報を返します。</returns>
    /// <exception cref="ArgumentException"><paramref name="sourcePath"/> が空白です。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> が <see langword="null"/> です。</exception>
    /// <remarks>
    /// 検査は公開出力を更新しません。contextなしではYAML構文解析と本文解析のみを行い、
    /// 実buildのfront matter bindingやroute解決と同等とは扱いません。
    /// 実projectの評価は呼び出し側が明示操作で取得した
    /// <see cref="DocumentInspectionOptions.Project"/> からのみ受け取ります。
    /// 起動直後やキー入力ごとのMSBuild・factory実行は行いません。
    /// </remarks>
    public static DocumentInfo Inspect(string sourcePath, string text, DocumentInspectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(text);

        var split = FrontMatterSplitter.TrySplit(text, cancellationToken);
        var diagnostics = new List<SiteDiagnostic>();
        string body;
        int bodyStartOffset;
        IReadOnlyDictionary<string, object?> frontMatter = System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>.Empty;
        var parsedMapping = (LocatedYamlMapping?)null;
        switch (split.Status)
        {
            case FrontMatterSplitStatus.Ok:
                body = split.Body;
                bodyStartOffset = split.BodyStartOffset;
                var parsed = MarkdownContentCollectionLoader<DocumentFrontMatter>.ParseYaml(split.Yaml, sourcePath, 2, cancellationToken);
                if (parsed.IsSuccess)
                {
                    parsedMapping = parsed.Value;
                    frontMatter = UnwrapMapping(parsed.Value!);
                }
                else
                {
                    diagnostics.AddRange(parsed.Diagnostics);
                }
                break;
            case FrontMatterSplitStatus.MissingFrontMatter:
                body = text;
                bodyStartOffset = 0;
                diagnostics.Add(new SiteDiagnostic(
                    MarkdownContentDiagnosticIds.MissingFrontMatter,
                    SiteDiagnosticSeverity.Error,
                    "Markdown document must start with YAML front matter.",
                    new SiteSourceLocation(sourcePath, 1, 1)));
                break;
            case FrontMatterSplitStatus.UnterminatedFrontMatter:
                body = text;
                bodyStartOffset = 0;
                diagnostics.Add(new SiteDiagnostic(
                    MarkdownContentDiagnosticIds.UnterminatedFrontMatter,
                    SiteDiagnosticSeverity.Error,
                    "Markdown document has no closing front matter marker.",
                    new SiteSourceLocation(sourcePath, split.FailureLine, 1)));
                break;
            default:
                body = split.Body;
                bodyStartOffset = split.BodyStartOffset;
                diagnostics.Add(new SiteDiagnostic(
                    MarkdownContentDiagnosticIds.EmptyFrontMatter,
                    SiteDiagnosticSeverity.Error,
                    "YAML front matter must not be empty.",
                    new SiteSourceLocation(sourcePath, 2, 1)));
                break;
        }

        var locator = new SourceText(text);
        var analyzed = new LithoMarkdownCompiler().AnalyzeForInspection(body, new DocumentSource(sourcePath, bodyStartOffset)
        {
            BodyStartLine = locator.GetLineAndColumn(bodyStartOffset).Line,
        }, cancellationToken);
        var semantics = analyzed.Semantics!;
        diagnostics.AddRange(semantics.Diagnostics);
        if (options?.EnableCompatibilityAdvisory == true)
        {
            var bodyStartLine = locator.GetLineAndColumn(bodyStartOffset).Line;
            foreach (var advisory in LithoLimits.FindCompatibilityAdvisories(
                body, sourcePath, bodyStartLine, cancellationToken))
            {
                if (!diagnostics.Any(existing => existing.Id == advisory.Id))
                {
                    diagnostics.Add(advisory);
                }
            }
        }

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
            semantics.Title,
            semantics.Headings.Select(heading => new DocumentHeadingInfo(heading.Text, heading.Id, heading.RawLevel, heading.OutputLevel, Locate(locator, sourcePath, heading.Span))).ToArray(),
            semantics.Links.Select(link => new DocumentLinkInfo(link.RawText, link.Url, link.Title, link.IsImage, Locate(locator, sourcePath, link.Span))).ToArray(),
            semantics.Assets.Select(asset => new DocumentAssetInfo(asset.Url, Locate(locator, sourcePath, asset.Span))).ToArray(),
            route,
            semantics.Components.Select(component => new DocumentComponentInfo(component.Name, Locate(locator, sourcePath, component.Span))).ToArray(),
            frontMatter,
            options?.Version,
            options?.Locale,
            diagnostics.ToArray(),
            candidates,
            status,
            project?.SchemaName);
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
