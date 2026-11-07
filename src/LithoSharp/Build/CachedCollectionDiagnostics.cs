using LithoSharp.Diagnostics;

namespace LithoSharp.Build;

// A missing outcome is unknown. A current outcome with no diagnostics is known empty.
internal sealed record CachedCollectionDiagnostics(
    int Version,
    IReadOnlyList<CachedCollectionDiagnostic> Diagnostics)
{
    internal const int CurrentVersion = 2;

    internal bool IsValid() => Version == CurrentVersion && Diagnostics is not null
        && Diagnostics.All(diagnostic => diagnostic is not null && diagnostic.IsValid());

    internal static CachedCollectionDiagnostics Capture(IReadOnlyList<SiteDiagnostic> diagnostics) =>
        new(CurrentVersion, diagnostics.Select(CachedCollectionDiagnostic.Capture).ToArray());

    internal CachedCollectionDiagnostics Snapshot() =>
        new(Version, Diagnostics.Select(diagnostic => diagnostic with
        {
            RelatedLocations = diagnostic.RelatedLocations.ToArray(),
        }).ToArray());

    internal IReadOnlyList<SiteDiagnostic> Restore() =>
        Array.AsReadOnly(Diagnostics.Select(diagnostic => diagnostic.Restore()).ToArray());
}

internal sealed record CachedCollectionDiagnostic(
    string Id,
    SiteDiagnosticSeverity Severity,
    string Message,
    CachedCollectionSourceLocation? Location,
    string? Category,
    IReadOnlyList<CachedCollectionSourceLocation> RelatedLocations,
    string? SnapshotIdentity = null)
{
    internal bool IsValid() => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Message)
        && Enum.IsDefined(Severity) && Severity != SiteDiagnosticSeverity.Error
        && (Category is null || !string.IsNullOrWhiteSpace(Category))
        && (Location is null || Location.IsValid())
        && SnapshotIdentity is { Length: 64 }
        && SnapshotIdentity.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
        && RelatedLocations is not null
        && RelatedLocations.All(location => location is not null && location.IsValid());

    internal static CachedCollectionDiagnostic Capture(SiteDiagnostic diagnostic) =>
        new(diagnostic.Id, diagnostic.Severity, diagnostic.Message,
            diagnostic.Location is null ? null : CachedCollectionSourceLocation.Capture(diagnostic.Location),
            diagnostic.Category, diagnostic.RelatedLocations.Select(CachedCollectionSourceLocation.Capture).ToArray(),
            diagnostic.SnapshotIdentity);

    internal SiteDiagnostic Restore() => new(Id, Severity, Message, Location?.Restore(), Category,
        RelatedLocations.Select(location => location.Restore()))
    { SnapshotIdentity = SnapshotIdentity };
}

internal sealed record CachedCollectionSourceLocation(
    string FilePath,
    int? Line,
    int? Column,
    int? EndLine,
    int? EndColumn)
{
    internal bool IsValid() => !string.IsNullOrWhiteSpace(FilePath)
        && Line is not < 1 && Column is not < 1 && EndLine is not < 1 && EndColumn is not < 1
        && (Column is null || Line is not null)
        && (EndColumn is null || EndLine is not null)
        && (EndLine is null || Line is not null)
        && !(EndLine < Line || (EndLine == Line && EndColumn < Column));

    internal static CachedCollectionSourceLocation Capture(SiteSourceLocation location) =>
        new(location.FilePath, location.Line, location.Column, location.EndLine, location.EndColumn);

    internal SiteSourceLocation Restore() => new(FilePath, Line, Column, EndLine, EndColumn);
}
