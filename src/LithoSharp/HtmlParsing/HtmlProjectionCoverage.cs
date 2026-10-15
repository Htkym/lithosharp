namespace LithoSharp.HtmlParsing;

internal enum HtmlFactsCoverageStatus { Complete, Partial, Failed, NotRequested }
internal enum HtmlFactsProjection { RawAttributes, Metadata, Anchors, References, InlineCss, Search, Headings }
internal sealed record HtmlFactsCoverageReason(string Code, HtmlSpan? Source);

internal sealed class HtmlFactsProjectionCoverage
{
    internal HtmlFactsProjectionCoverage(HtmlFactsProjection projection, HtmlFactsCoverageStatus status,
        IEnumerable<HtmlFactsCoverageReason> reasons)
    {
        Projection = projection;
        Status = status;
        Reasons = Array.AsReadOnly(reasons.ToArray());
    }

    internal HtmlFactsProjection Projection { get; }
    internal HtmlFactsCoverageStatus Status { get; }
    internal IReadOnlyList<HtmlFactsCoverageReason> Reasons { get; }
}
