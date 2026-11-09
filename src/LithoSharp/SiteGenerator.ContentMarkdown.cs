using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    internal MarkdownCompilationResult RenderContentMarkdown(
        RenderContext configuration,
        string pageIdentity,
        string markdown,
        string? sourcePath,
        MarkdownBodyAnalysisSnapshot? bodyAnalysis,
        DocumentSource? bodySource,
        ICollection<SiteDiagnostic> diagnostics)
    {
        var cancellation = configuration.Cancellation;
        cancellation.ThrowIfCancellationRequested();
        var retained = bodyAnalysis is null ? null
            : MarkdownAnalysisAdapter.RetainForBody(bodyAnalysis, markdown, sourcePath);
        var originalSource = retained is not null ? bodySource : null;
        var analyzed = _markdownCompiler.Analyze(markdown,
            originalSource ?? new DocumentSource(sourcePath, 0), cancellation, retained);
        SiteDiagnostic[] mapped = analyzed.Semantics is { } semantics
            ? semantics.Diagnostics.Select(diagnostic => originalSource is not null ? diagnostic
                : new SiteDiagnostic(diagnostic.Id, diagnostic.Severity, diagnostic.Message,
                    string.IsNullOrWhiteSpace(sourcePath) ? null : new SiteSourceLocation(sourcePath),
                    diagnostic.Category, string.IsNullOrWhiteSpace(sourcePath) ? []
                        : diagnostic.RelatedLocations.Select(location => new SiteSourceLocation(location.FilePath))))
                .ToArray()
            : [new SiteDiagnostic(MarkdownAnalysisAdapter.IncompleteAnalysisDiagnosticId,
                SiteDiagnosticSeverity.Error,
                "Markdown analysis is incomplete; unavailable facts were not validated.",
                string.IsNullOrWhiteSpace(sourcePath) ? null : new SiteSourceLocation(sourcePath))];
        var errors = mapped.Where(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0)
            throw new SiteQualityValidationException(new SiteQualityReport(errors));
        if (mapped.Length == 0)
            return analyzed;
        var snapshotIdentity = MarkdownDocumentFingerprints.SourceHash(
            $"{pageIdentity.Length}:{pageIdentity}{_markdownCompiler.Fingerprint.Length}:{_markdownCompiler.Fingerprint}{markdown}");
        foreach (var diagnostic in mapped)
            diagnostics.Add(new SiteDiagnostic(diagnostic.Id, diagnostic.Severity, diagnostic.Message,
                diagnostic.Location, diagnostic.Category, diagnostic.RelatedLocations)
            {
                SnapshotIdentity = snapshotIdentity,
            });
        return analyzed;
    }
}
