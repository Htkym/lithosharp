using LithoSharp.Diagnostics;

namespace LithoSharp.Inspection;

public sealed class DocumentHeadingInfo
{
    internal DocumentHeadingInfo(string text, string? id, int rawLevel, int outputLevel, SiteSourceLocation? location)
    {
        Text = text;
        Id = id;
        RawLevel = rawLevel;
        OutputLevel = outputLevel;
        Location = location;
    }

    public string Text { get; }

    public string? Id { get; }

    public int RawLevel { get; }

    public int OutputLevel { get; }

    public SiteSourceLocation? Location { get; }
}

public sealed class DocumentLinkInfo
{
    internal DocumentLinkInfo(string rawText, string url, string? title, bool isImage, SiteSourceLocation? location)
    {
        RawText = rawText;
        Url = url;
        Title = title;
        IsImage = isImage;
        Location = location;
    }

    public string RawText { get; }

    public string Url { get; }

    public string? Title { get; }

    public bool IsImage { get; }

    public SiteSourceLocation? Location { get; }
}

public sealed class DocumentAssetInfo
{
    internal DocumentAssetInfo(string url, SiteSourceLocation? location)
    {
        Url = url;
        Location = location;
    }

    public string Url { get; }

    public SiteSourceLocation? Location { get; }
}

public sealed class DocumentComponentInfo
{
    internal DocumentComponentInfo(string name, SiteSourceLocation? location)
    {
        Name = name;
        Location = location;
    }

    public string Name { get; }

    public SiteSourceLocation? Location { get; }
}

public sealed class DocumentInspectionOptions
{
    public string? DocumentId { get; init; }

    public string? Route { get; init; }

    public string? Version { get; init; }

    public string? Locale { get; init; }

    /// <summary>Adds LIT003-LIT005 advisories without changing interpretation or rendering; disabled by default.</summary>
    public bool EnableCompatibilityAdvisory { get; init; }

    /// <summary>An explicitly acquired context; null limits inspection to syntax. Inspection never updates source or published output.</summary>
    public ProjectInspectionSnapshot? Project { get; init; }
}

public static class CompatibilityAdvisoryOption
{
    public const string On = "on";

    public const string Off = "off";

    public static bool TryParse(string? value, out bool enabled)
    {
        if (string.Equals(value, On, StringComparison.Ordinal))
        {
            enabled = true;
            return true;
        }

        if (string.Equals(value, Off, StringComparison.Ordinal))
        {
            enabled = false;
            return true;
        }

        enabled = false;
        return false;
    }
}

public sealed class DocumentInfo
{
    internal DocumentInfo(
        string documentId,
        string sourcePath,
        string? title,
        IReadOnlyList<DocumentHeadingInfo> headings,
        IReadOnlyList<DocumentLinkInfo> links,
        IReadOnlyList<DocumentAssetInfo> assets,
        string? route,
        IReadOnlyList<DocumentComponentInfo> components,
        IReadOnlyDictionary<string, object?> frontMatter,
        string? version,
        string? locale,
        IReadOnlyList<SiteDiagnostic> diagnostics,
        IReadOnlyList<ProjectRouteCandidate>? routeCandidates = null,
        DocumentProjectStatus projectStatus = DocumentProjectStatus.NoContext,
        string? schemaName = null)
    {
        DocumentId = documentId;
        SourcePath = sourcePath;
        Title = title;
        Headings = Array.AsReadOnly(headings.ToArray());
        Links = Array.AsReadOnly(links.ToArray());
        Assets = Array.AsReadOnly(assets.ToArray());
        Route = route;
        Components = Array.AsReadOnly(components.ToArray());
        FrontMatter = frontMatter;
        Version = version;
        Locale = locale;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        RouteCandidates = Array.AsReadOnly((routeCandidates ?? []).ToArray());
        ProjectStatus = projectStatus;
        SchemaName = schemaName;
    }

    public string DocumentId { get; }

    public string SourcePath { get; }

    public string? Title { get; }

    public IReadOnlyList<DocumentHeadingInfo> Headings { get; }

    public IReadOnlyList<DocumentLinkInfo> Links { get; }

    public IReadOnlyList<DocumentAssetInfo> Assets { get; }

    public string? Route { get; }

    public IReadOnlyList<DocumentComponentInfo> Components { get; }

    public IReadOnlyDictionary<string, object?> FrontMatter { get; }

    public string? Version { get; }

    public string? Locale { get; }

    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }

    public IReadOnlyList<ProjectRouteCandidate> RouteCandidates { get; }

    public DocumentProjectStatus ProjectStatus { get; }

    public string? SchemaName { get; }
}
