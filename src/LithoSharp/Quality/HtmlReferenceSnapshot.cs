using LithoSharp.Documentation;
using LithoSharp.HtmlParsing;
using LithoSharp.Internal;

namespace LithoSharp.Quality;

// Route binding is consumer-owned: equal HTML can belong to different pages and variants.
// Null PageId/Document means the artifact has no registered page/document identity.
internal sealed record HtmlPageIdentity(string OutputPath, string PublicPath, string? PageId, DocumentKey? Document);

internal enum HtmlReferenceRole { Base, Canonical, Navigation, Resource, FormAction, SrcsetCandidate, MetaImage, Css }
internal sealed record HtmlSnapshotPosition(int? Line, int? Column, HtmlSpan? Source);
internal sealed record HtmlAnchorSnapshot(int ElementOrdinal, string Attribute, string Decoded, HtmlRawValueSnapshot? Value);
internal sealed record HtmlMetadataSnapshot(int ElementOrdinal, string? Name, string? Property, string? Content,
    HtmlRawValueSnapshot? ContentValue, HtmlRawValueSnapshot? NameValue = null, HtmlRawValueSnapshot? PropertyValue = null);
internal sealed record HtmlCanonicalSnapshot(int ElementOrdinal, string? Href, Uri? Address, HtmlRawValueSnapshot? Value);

// Value/Source describe the containing attribute, including every srcset candidate's container.
// UnresolvedValue is the actual URL candidate. No exact candidate span is inferred from a string search.
internal sealed record HtmlReference(int ElementOrdinal, string Attribute, int CandidateOrdinal,
    HtmlRawValueSnapshot? Value, string UnresolvedValue, Uri? ResolvedUrl,
    SiteQualityUrlIssue UrlIssue, HtmlReferenceRole Role, bool Navigation, bool Resource,
    string? DecodedContainer = null, HtmlSourceMap? Candidate = null)
{
    internal string? Raw => Value?.Raw;
    internal string? Decoded => Value?.Decoded ?? DecodedContainer;
    internal HtmlSpan? Source => Value?.Source;
    internal HtmlSpan? CandidateSource => Candidate?.Source;
}

internal sealed class HtmlReferenceSnapshot
{
    internal HtmlReferenceSnapshot(HtmlPageIdentity identity, Uri pageUrl, Uri resolutionBase, string title,
        IEnumerable<HtmlSnapshotPosition> positions, IEnumerable<HtmlAnchorSnapshot> anchors,
        IEnumerable<HtmlMetadataSnapshot> metadata, IEnumerable<HtmlCanonicalSnapshot> canonicals,
        IEnumerable<HtmlReference> references, IEnumerable<HtmlReference> cssReferences,
        IEnumerable<HtmlFactsProjectionCoverage> coverage, HtmlSourceMap? titleMaterial = null,
        IEnumerable<HtmlHeadingMaterial>? headings = null, IEnumerable<HtmlFactsProjectionCoverage>? sourceCoverage = null)
    {
        Identity = identity;
        PageUrl = pageUrl;
        ResolutionBase = resolutionBase;
        Title = title;
        Positions = Array.AsReadOnly(positions.ToArray());
        Anchors = Array.AsReadOnly(anchors.ToArray());
        Metadata = Array.AsReadOnly(metadata.ToArray());
        Canonicals = Array.AsReadOnly(canonicals.ToArray());
        References = Array.AsReadOnly(references.ToArray());
        CssReferences = Array.AsReadOnly(cssReferences.ToArray());
        Coverage = Array.AsReadOnly(coverage.ToArray());
        TitleMaterial = titleMaterial;
        Headings = Array.AsReadOnly((headings ?? []).ToArray());
        SourceCoverage = Array.AsReadOnly((sourceCoverage ?? Enum.GetValues<HtmlFactsProjection>()
            .Select(projection => new HtmlFactsProjectionCoverage(projection, HtmlFactsCoverageStatus.NotRequested, []))).ToArray());
    }

    internal HtmlPageIdentity Identity { get; }
    internal Uri PageUrl { get; }
    internal Uri ResolutionBase { get; }
    internal string Title { get; }
    internal IReadOnlyList<HtmlSnapshotPosition> Positions { get; }
    internal IReadOnlyList<HtmlAnchorSnapshot> Anchors { get; }
    internal IReadOnlyList<HtmlMetadataSnapshot> Metadata { get; }
    internal IReadOnlyList<HtmlCanonicalSnapshot> Canonicals { get; }
    internal IReadOnlyList<HtmlReference> References { get; }
    internal IReadOnlyList<HtmlReference> CssReferences { get; }
    internal IReadOnlyList<HtmlFactsProjectionCoverage> Coverage { get; }
    internal HtmlSourceMap? TitleMaterial { get; }
    internal IReadOnlyList<HtmlHeadingMaterial> Headings { get; }
    internal IReadOnlyList<HtmlFactsProjectionCoverage> SourceCoverage { get; }
    internal const string CssProfile = "literal-css-urls/1";

    internal void RequireComplete()
    {
        foreach (var projection in new[] { HtmlFactsProjection.Metadata, HtmlFactsProjection.Anchors,
            HtmlFactsProjection.References, HtmlFactsProjection.InlineCss })
            if (Coverage.Single(c => c.Projection == projection) is { Status: not HtmlFactsCoverageStatus.Complete } coverage)
                throw new HtmlQualitySnapshotIncompleteException(coverage);
        // Requested URL positions are required; supplementary text/search origins
        // are independent and cannot turn valid Quality values into a failed build.
        foreach (var coverage in SourceCoverage.Where(c => c.Projection is HtmlFactsProjection.References or HtmlFactsProjection.InlineCss))
            if (coverage.Status is HtmlFactsCoverageStatus.Partial or HtmlFactsCoverageStatus.Failed)
                throw new HtmlQualitySnapshotIncompleteException(coverage);
    }
}

internal sealed class CssReferenceSnapshot
{
    private CssReferenceSnapshot(string path, Uri resolutionBase, HtmlReference[] references, HtmlFactsProjectionCoverage coverage)
    {
        OutputPath = path;
        ResolutionBase = resolutionBase;
        References = Array.AsReadOnly(references);
        Coverage = coverage;
    }
    internal string OutputPath { get; }
    internal Uri ResolutionBase { get; }
    internal IReadOnlyList<HtmlReference> References { get; }
    internal HtmlFactsProjectionCoverage Coverage { get; }
    internal HtmlFactsProjectionCoverage SourceCoverage => new(HtmlFactsProjection.InlineCss,
        References.Any(reference => reference.Candidate?.Coverage.Status != HtmlFactsCoverageStatus.Complete)
            ? HtmlFactsCoverageStatus.Partial : HtmlFactsCoverageStatus.Complete,
        References.SelectMany(reference => reference.Candidate?.Coverage.Reasons ?? [new("css_candidate_source_unavailable", null)]));

    internal static CssReferenceSnapshot Create(string path, Uri resolutionBase, string css, CancellationToken token)
    {
        var collected = CssUrlScanner.CollectCandidates(css, token, includeImports: true);
        var input = HtmlSourceMap.Identity(css, HtmlFactsProjection.InlineCss);
        var references = collected.Candidates.Select((candidate, ordinal) =>
        {
            token.ThrowIfCancellationRequested();
            var issue = SiteQualityUrlRules.Resolve(candidate.Value, true, resolutionBase, out var address);
            return new HtmlReference(-1, "#stylesheet", ordinal, null, candidate.Value, address, issue,
                HtmlReferenceRole.Css, false, true, candidate.Value,
                input.Compose(candidate.Value, candidate.Source, candidate.Mapping, HtmlFactsProjection.InlineCss));
        }).ToArray();
        return new(path, resolutionBase, references, collected.Coverage);
    }

    internal void RequireComplete()
    {
        if (Coverage.Status != HtmlFactsCoverageStatus.Complete)
            throw new HtmlQualitySnapshotIncompleteException(Coverage);
        if (SourceCoverage.Status != HtmlFactsCoverageStatus.Complete)
            throw new HtmlQualitySnapshotIncompleteException(SourceCoverage);
    }
}

internal sealed class HtmlQualitySnapshotIncompleteException(HtmlFactsProjectionCoverage coverage)
    : InvalidOperationException("Required Quality projection is incomplete: " + coverage.Projection + ": "
        + string.Join(", ", coverage.Reasons.Select(reason => reason.Code)))
{
    internal HtmlFactsProjectionCoverage Coverage { get; } = coverage;
}
