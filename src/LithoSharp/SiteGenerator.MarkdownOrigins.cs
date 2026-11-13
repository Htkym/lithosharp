using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using System.Text.Json;
using Syntamark.Compilation;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    private static string MarkdownOriginIdentity(DocumentSource? source)
    {
        var mapped = source?.SourceMap is { } map ? map.Map(new MdRawRange(0, map.Output.Text.Length)) : null;
        return ComputeTextContentSha256(JsonSerializer.Serialize(new
        {
            source?.FilePath,
            source?.BodyStartOffset,
            source?.BodyStartLine,
            source?.BodyFirstColumnOffset,
            source?.OriginalSourceId,
            Origins = mapped?.Origins.Select(static origin => new
            {
                origin.Source.SourceId,
                origin.Source.Version,
                origin.Source.TextHash,
                origin.Span.Start,
                origin.Span.Length,
            }),
            Directives = source?.InclusionOrigins.Select(static origin => new
            {
                origin.OutputSpan.Start,
                origin.OutputSpan.Length,
                origin.Directive,
            }),
        }));
    }

    private static SiteQualityReport MapQualityOrigins(SiteQualityReport report, RenderContext configuration,
        IReadOnlyList<MarkdownPost> posts, IReadOnlyList<IntegratedContentPage> pages,
        IReadOnlyList<SiteTemplateFile> templateFiles, IReadOnlyDictionary<string, CachedBuildArtifact> inspected)
    {
        var knownSources = pages.ToDictionary(page => page.Route.RelativeOutputPath,
            page => page.Sources.Select(source => source.SourcePath).Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var files = templateFiles.ToDictionary(static file => file.RelativePath, StringComparer.Ordinal);
        foreach (var page in pages)
            if (page.RenderedOrigins is { } origins)
                configuration.HtmlOrigins.TryAdd(page.Route.RelativeOutputPath, origins);
        foreach (var post in posts)
        {
            var path = configuration.Routes.Post(post).RelativeOutputPath;
            knownSources.TryAdd(path, string.IsNullOrWhiteSpace(post.FilePath) ? [] : [post.FilePath]);
            if (!configuration.HtmlOrigins.ContainsKey(path) && files.TryGetValue(path, out var file)
                && configuration.CompiledBodies.GetExisting(post) is { RenderedOrigins: { } origins } compiled)
                configuration.HtmlOrigins[path] = RenderedHtmlOrigins.Embed(file.Content, [new(compiled.Html, origins)]);
        }
        var mappers = configuration.HtmlOrigins.Where(pair => inspected.TryGetValue(pair.Key, out var artifact)
                && artifact.Sha256 == pair.Value.HtmlHash && pair.Value.IsValid())
            .ToDictionary(static pair => pair.Key, static pair => pair.Value.CreateMapper(), StringComparer.Ordinal);
        var diagnostics = report.Diagnostics.Select(diagnostic =>
        {
            if (diagnostic.Location is not { } location)
                return diagnostic;
            var mapped = mappers.TryGetValue(location.FilePath, out var map) ? map(diagnostic) : diagnostic;
            if (mapped.MappingPrecision is not null || !knownSources.TryGetValue(location.FilePath, out var sources))
                return mapped;
            return new SiteDiagnostic(mapped.Id, mapped.Severity, mapped.Message, mapped.Location, mapped.Category,
                mapped.RelatedLocations.Concat(sources.Select(static path => new SiteSourceLocation(path))))
            {
                MappingPrecision = "Unknown",
                SnapshotIdentity = mapped.SnapshotIdentity,
                GeneratedReferenceSpan = mapped.GeneratedReferenceSpan,
            };
        });
        return new(diagnostics)
        {
            HtmlParseCount = report.HtmlParseCount,
            HtmlFactsCacheHitCount = report.HtmlFactsCacheHitCount,
            HtmlReferences = report.HtmlReferences,
            CssReferences = report.CssReferences,
            AnchorReferences = report.AnchorReferences,
        };
    }
}
