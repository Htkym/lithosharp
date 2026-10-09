using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests.Fixtures;

// One source is executed by runtime tests and parsed by the actual packed analyzer.
internal static class StaticApiArgumentEvaluationCases
{
    internal static SiteRoute PositionalValid()
    {
        string path = "ok.html";
        return SiteRoute.ForFile(path, path = "https://example.test/");
    }

    internal static SiteRoute NamedValid()
    {
        string path = "ok.html";
        return SiteRoute.ForFile(relativePath: path, baseUrl: path = "https://example.test/");
    }

    internal static SiteRoute NamedReversedValid()
    {
        string path = "https://example.test/";
        return SiteRoute.ForFile(baseUrl: path, relativePath: path = "ok.html");
    }

    internal static SiteRoute PositionalInvalidFirst()
    {
        string path = "../escape";
        return SiteRoute.ForFile(path, path = "https://example.test/");
    }

    internal static SiteRoute NamedReversedInvalidFirst()
    {
        string path = "file:///tmp";
        return SiteRoute.ForFile(baseUrl: path, relativePath: path = "ok.html");
    }

    internal static SiteRoute PositionalSwappedInvalidBoth()
    {
        string path = "https://example.test/";
        return SiteRoute.ForFile(path, path = "ok.html");
    }

    internal static SiteRoute NamedSwappedInvalidBoth()
    {
        string path = "ok.html";
        return SiteRoute.ForFile(baseUrl: path, relativePath: path = "https://example.test/");
    }

    internal static SiteRoute WithinArgumentValid()
    {
        string path = "ok";
        return SiteRoute.ForFile(path + (path = ".html"), path = "https://example.test/");
    }

    internal static SiteAssetOutput OutputNamedReversedInvalidFirst()
    {
        string path = "../escape";
        return new SiteAssetOutput(relativeOutputPath: path, id: path = "asset");
    }

    internal static SiteQualityOptions OptionLaterAssignmentValid()
    {
        var threshold = SiteDiagnosticSeverity.Warning;
        return new SiteQualityOptions(threshold, (threshold = (SiteDiagnosticSeverity)9) == SiteDiagnosticSeverity.Warning);
    }
}
