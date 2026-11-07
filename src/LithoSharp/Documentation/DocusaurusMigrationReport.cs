using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;

namespace LithoSharp.Documentation;

public enum MigrationVerdict
{
    Automatic = 0,
    Convertible = 1,
    ManualActionRequired = 2,
    Unsupported = 3,
}

public enum MigrationActionKind
{
    /// <summary>Replaces the inclusive line range; empty replacement text deletes it.</summary>
    ReplaceLines = 0,
    InsertAfter = 1,
}

public enum MigrationComponentChangeKind
{
    AppearanceChanged,
    InteractionChanged,
    Staticized,
    Deleted,
    Unverified,
}

public enum MigrationFunctionalEquivalence
{
    Equivalent,
    NotEquivalent,
    Unverified,
}

public enum MigrationRouteCategory
{
    Unclassified,
    Document,
    CategoryIndex,
    BlogIndex,
    BlogAuthor,
    BlogTag,
    BlogArchive,
    BlogPagination,
    Other,
}

public sealed class MigrationRouteOracleEntry
{
    public MigrationRouteOracleEntry(
        string path,
        MigrationRouteCategory category,
        string? locale = null,
        string? exclusionReason = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Enum.IsDefined(category))
            throw new ArgumentOutOfRangeException(nameof(category));
        if (exclusionReason is not null && string.IsNullOrWhiteSpace(exclusionReason))
            throw new ArgumentException("An explicit exclusion reason cannot be empty.", nameof(exclusionReason));
        Path = path;
        Category = category;
        Locale = locale;
        ExclusionReason = exclusionReason;
    }

    public string Path { get; }

    public MigrationRouteCategory Category { get; }

    public string? Locale { get; }

    public string? ExclusionReason { get; }
}

public sealed class MigrationRouteOracle
{
    public MigrationRouteOracle(
        IReadOnlyList<MigrationRouteOracleEntry> routes,
        string? sourceVersion = null,
        string? basePath = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var snapshot = routes.ToArray();
        if (snapshot.Any(route => route is null))
            throw new ArgumentException("A route oracle cannot contain null entries.", nameof(routes));
        foreach (var group in snapshot.GroupBy(route => route.Path, StringComparer.Ordinal))
        {
            var classifications = group.Select(route => (route.Category, route.Locale, route.ExclusionReason)).Distinct().Take(2).Count();
            if (classifications > 1)
                throw new ArgumentException($"Route '{group.Key}' has conflicting oracle classifications.", nameof(routes));
        }

        Routes = Array.AsReadOnly(snapshot);
        SourceVersion = sourceVersion;
        BasePath = basePath;
    }

    public IReadOnlyList<MigrationRouteOracleEntry> Routes { get; }

    public string? SourceVersion { get; }

    public string? BasePath { get; }

    /// <summary>Legacy raw paths remain unclassified; no document page set is inferred.</summary>
    public static MigrationRouteOracle FromPaths(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new(paths.Select(path => new MigrationRouteOracleEntry(path, MigrationRouteCategory.Unclassified)).ToArray());
    }
}

public sealed class MigrationIssueReport
{
    internal MigrationIssueReport(
        string id, SiteDiagnosticSeverity severity, string message,
        SiteSourceLocation location, string? replacement, string? manualStep)
    {
        Id = id;
        Severity = severity;
        Message = message;
        Location = location;
        Replacement = replacement;
        ManualStep = manualStep;
    }

    public string Id { get; }

    public SiteDiagnosticSeverity Severity { get; }

    public string Message { get; }

    public SiteSourceLocation Location { get; }

    public string? Replacement { get; }

    public string? ManualStep { get; }
}

/// <summary>Positions refer to the analyzed source; apply only while its fingerprint matches.</summary>
/// <remarks>Apply ReplaceLines in descending line order, then InsertAfter. Do not auto-apply ambiguous or stale suggestions.</remarks>
public sealed class MigrationSuggestedAction
{
    internal MigrationSuggestedAction(
        string sourcePath, MigrationActionKind kind, int startLine, int endLine,
        string replacementText, string sourceFingerprint, bool canApplyAutomatically, string applyCondition)
    {
        SourcePath = sourcePath;
        Kind = kind;
        StartLine = startLine;
        EndLine = endLine;
        ReplacementText = replacementText;
        SourceFingerprint = sourceFingerprint;
        CanApplyAutomatically = canApplyAutomatically;
        ApplyCondition = applyCondition;
    }

    public string SourcePath { get; }

    public MigrationActionKind Kind { get; }

    public int StartLine { get; }

    public int EndLine { get; }

    /// <summary>Empty means deletion; inserted lines use newline separators.</summary>
    public string ReplacementText { get; }

    /// <summary>Lowercase SHA-256 of the exact analyzed source bytes.</summary>
    public string SourceFingerprint { get; }

    public bool CanApplyAutomatically { get; }

    public string ApplyCondition { get; }

    public bool MatchesSource(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), SourceFingerprint, StringComparison.Ordinal);
    }

    /// <summary>Encodes as UTF-8 without a BOM; use MatchesSource for BOM-bearing documents.</summary>
    public bool MatchesSourceText(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return MatchesSource(Encoding.UTF8.GetBytes(content));
    }
}

public sealed class MigrationComponentChangeReport
{
    internal MigrationComponentChangeReport(
        string sourcePath,
        string component,
        MigrationComponentChangeKind kind,
        MigrationFunctionalEquivalence functionalEquivalence,
        string note)
    {
        SourcePath = sourcePath;
        Component = component;
        Kind = kind;
        FunctionalEquivalence = functionalEquivalence;
        Note = note;
    }

    public string SourcePath { get; }

    public string Component { get; }

    public MigrationComponentChangeKind Kind { get; }

    public MigrationFunctionalEquivalence FunctionalEquivalence { get; }

    public string Note { get; }

    public bool CanApplyAutomatically => false;
}

public sealed class MigrationFileReport
{
    internal MigrationFileReport(
        string sourcePath, MigrationVerdict verdict, string? convertedPath,
        IReadOnlyList<MigrationIssueReport> issues, IReadOnlyList<MigrationSuggestedAction> suggestedActions,
        string sourceFingerprint, IReadOnlyList<MigrationComponentChangeReport> componentChanges)
    {
        SourcePath = sourcePath;
        Verdict = verdict;
        ConvertedPath = convertedPath;
        Issues = issues;
        SuggestedActions = suggestedActions;
        SourceFingerprint = sourceFingerprint;
        ComponentChanges = componentChanges;
    }

    public string SourcePath { get; }

    public MigrationVerdict Verdict { get; }

    public string? ConvertedPath { get; }

    public IReadOnlyList<MigrationIssueReport> Issues { get; }

    public IReadOnlyList<MigrationSuggestedAction> SuggestedActions { get; }

    /// <summary>Lowercase SHA-256 of the exact analyzed source bytes.</summary>
    public string SourceFingerprint { get; }

    public IReadOnlyList<MigrationComponentChangeReport> ComponentChanges { get; }
}

public sealed class MigrationVariantReport
{
    internal MigrationVariantReport(string version, string locale, string inputDirectory, string suggestedRoutePrefix)
    {
        Version = version;
        Locale = locale;
        InputDirectory = inputDirectory;
        SuggestedRoutePrefix = suggestedRoutePrefix;
    }

    public string Version { get; }

    public string Locale { get; }

    public string InputDirectory { get; }

    public string SuggestedRoutePrefix { get; }
}

public sealed class MigrationAuthorReport
{
    internal MigrationAuthorReport(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }

    public string Name { get; }
}

public sealed class MigrationManifestReport
{
    internal MigrationManifestReport(
        IReadOnlyList<MigrationVariantReport> variants,
        IReadOnlyDictionary<string, string> versionedSidebars,
        IReadOnlyList<MigrationAuthorReport> blogAuthors,
        IReadOnlyList<string> manualSteps,
        IReadOnlyList<string> unsupportedNotes)
    {
        Variants = variants;
        VersionedSidebars = versionedSidebars;
        BlogAuthors = blogAuthors;
        ManualSteps = manualSteps;
        UnsupportedNotes = unsupportedNotes;
    }

    public IReadOnlyList<MigrationVariantReport> Variants { get; }

    public IReadOnlyDictionary<string, string> VersionedSidebars { get; }

    public IReadOnlyList<MigrationAuthorReport> BlogAuthors { get; }

    public IReadOnlyList<string> ManualSteps { get; }

    public IReadOnlyList<string> UnsupportedNotes { get; }
}

public sealed class MigrationRouteReport
{
    internal MigrationRouteReport(string route, string sourceFile, string kind)
    {
        Route = route;
        SourceFile = sourceFile;
        Kind = kind;
    }

    public string Route { get; }

    public string SourceFile { get; }

    public string Kind { get; }
}

public enum MigrationRouteComparisonStatus
{
    NotCompared,
    Match,
    Differences,
}

public enum MigrationPageSetComparisonStatus
{
    NotRequested,
    NotCompared,
    Match,
    Differences,
}

public sealed class MigrationRouteExclusionReport
{
    internal MigrationRouteExclusionReport(MigrationRouteCategory category, int count, string reason)
    {
        Category = category;
        Count = count;
        Reason = reason;
    }

    public MigrationRouteCategory Category { get; }

    public int Count { get; }

    public string Reason { get; }
}

/// <summary>Locale facets do not add duplicate routes to the overall denominator.</summary>
public sealed class MigrationLocaleCountReport
{
    internal MigrationLocaleCountReport(string locale, int routeCount)
    {
        Locale = locale;
        RouteCount = routeCount;
    }

    public string Locale { get; }

    public int RouteCount { get; }
}

public sealed class MigrationPageSetComparisonReport
{
    internal MigrationPageSetComparisonReport(
        MigrationPageSetComparisonStatus status,
        int? sourcePageCount,
        int? normalizedSourcePageCount,
        int? targetPageCount,
        int? normalizedTargetPageCount,
        IReadOnlyList<string> missingRoutes,
        IReadOnlyList<string> extraRoutes,
        IReadOnlyList<string> rules,
        string? reason)
    {
        Status = status;
        SourcePageCount = sourcePageCount;
        NormalizedSourcePageCount = normalizedSourcePageCount;
        TargetPageCount = targetPageCount;
        NormalizedTargetPageCount = normalizedTargetPageCount;
        MissingRoutes = missingRoutes;
        ExtraRoutes = extraRoutes;
        Rules = rules;
        Reason = reason;
    }

    public MigrationPageSetComparisonStatus Status { get; }

    public int? SourcePageCount { get; }

    public int? NormalizedSourcePageCount { get; }

    public int? TargetPageCount { get; }

    public int? NormalizedTargetPageCount { get; }

    public IReadOnlyList<string> MissingRoutes { get; }

    public IReadOnlyList<string> ExtraRoutes { get; }

    public IReadOnlyList<string> Rules { get; }

    public string? Reason { get; }
}

public sealed class MigrationRouteComparisonReport
{
    internal MigrationRouteComparisonReport(
        MigrationRouteComparisonStatus rawStatus,
        int? sourceRouteCount,
        int targetRouteCount,
        int? excludedRouteCount,
        int? duplicateSourceRouteCount,
        string? sourceVersion,
        string sourceHash,
        string? routeOracleHash,
        IReadOnlyList<MigrationRouteExclusionReport> exclusionRules,
        IReadOnlyList<MigrationLocaleCountReport> locales,
        MigrationPageSetComparisonReport normalizedPageSet)
    {
        RawStatus = rawStatus;
        SourceRouteCount = sourceRouteCount;
        TargetRouteCount = targetRouteCount;
        ExcludedRouteCount = excludedRouteCount;
        DuplicateSourceRouteCount = duplicateSourceRouteCount;
        SourceVersion = sourceVersion;
        SourceHash = sourceHash;
        RouteOracleHash = routeOracleHash;
        ExclusionRules = exclusionRules;
        Locales = locales;
        NormalizedPageSet = normalizedPageSet;
    }

    public MigrationRouteComparisonStatus RawStatus { get; }

    public int? SourceRouteCount { get; }

    public int TargetRouteCount { get; }

    public int? ExcludedRouteCount { get; }

    public int? DuplicateSourceRouteCount { get; }

    public string? SourceVersion { get; }

    /// <summary>SHA-256 of analyzed relative paths and file fingerprints.</summary>
    public string SourceHash { get; }

    public string? RouteOracleHash { get; }

    public IReadOnlyList<MigrationRouteExclusionReport> ExclusionRules { get; }

    public IReadOnlyList<MigrationLocaleCountReport> Locales { get; }

    public MigrationPageSetComparisonReport NormalizedPageSet { get; }
}

public sealed class MigrationSummary
{
    internal MigrationSummary(int totalFiles, int automatic, int convertible, int manualActionRequired, int unsupported)
    {
        TotalFiles = totalFiles;
        Automatic = automatic;
        Convertible = convertible;
        ManualActionRequired = manualActionRequired;
        Unsupported = unsupported;
    }

    public int TotalFiles { get; }

    public int Automatic { get; }

    public int Convertible { get; }

    public int ManualActionRequired { get; }

    public int Unsupported { get; }
}

public sealed class DocusaurusMigrationReport
{
    internal DocusaurusMigrationReport(
        string schemaVersion, bool dryRun, bool wroteOutput, string? destination,
        MigrationSummary summary, IReadOnlyList<MigrationFileReport> files,
        MigrationManifestReport manifest, IReadOnlyList<MigrationRouteReport> convertedRoutes,
        IReadOnlyList<string> missingRoutes, IReadOnlyList<string> extraRoutes, int exitCode,
        MigrationRouteComparisonReport routeComparison,
        IReadOnlyList<MigrationComponentChangeReport> componentChanges)
    {
        SchemaVersion = schemaVersion;
        DryRun = dryRun;
        WroteOutput = wroteOutput;
        Destination = destination;
        Summary = summary;
        Files = files;
        Manifest = manifest;
        ConvertedRoutes = convertedRoutes;
        MissingRoutes = missingRoutes;
        ExtraRoutes = extraRoutes;
        ExitCode = exitCode;
        RouteComparison = routeComparison;
        ComponentChanges = componentChanges;
    }

    public string SchemaVersion { get; }

    public bool DryRun { get; }

    public bool WroteOutput { get; }

    public string? Destination { get; }

    public MigrationSummary Summary { get; }

    public IReadOnlyList<MigrationFileReport> Files { get; }

    public MigrationManifestReport Manifest { get; }

    public IReadOnlyList<MigrationRouteReport> ConvertedRoutes { get; }

    public IReadOnlyList<string> MissingRoutes { get; }

    public IReadOnlyList<string> ExtraRoutes { get; }

    /// <summary>0 means complete, 1 failure, and 3 unconvertible content.</summary>
    public int ExitCode { get; }

    public MigrationRouteComparisonReport RouteComparison { get; }

    public IReadOnlyList<MigrationComponentChangeReport> ComponentChanges { get; }

    /// <summary>Read-only analysis. Legacy expectedRoutes are unclassified raw paths and are excluded from normalized page-set comparison.</summary>
    public static DocusaurusMigrationReport Analyze(
        string sourceDirectory,
        IReadOnlyList<string>? expectedRoutes = null,
        DocusaurusMigrationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        AnalyzeWithOracle(sourceDirectory,
            expectedRoutes is null ? null : MigrationRouteOracle.FromPaths(expectedRoutes),
            options, cancellationToken);

    /// <summary>Read-only analysis; a missing oracle is recorded as not-compared. Normalized page-set comparison is opt-in.</summary>
    public static DocusaurusMigrationReport AnalyzeWithOracle(
        string sourceDirectory,
        MigrationRouteOracle? routeOracle,
        DocusaurusMigrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? new();
        ValidateNormalizedInputs(routeOracle, effectiveOptions);
        return FromResult(DocusaurusMigration.AnalyzeWithOracle(sourceDirectory, routeOracle, effectiveOptions, cancellationToken),
            dryRun: true, destination: null, routeOracle, effectiveOptions);
    }

    /// <summary>Writes only to a new destination; never overwrites the source. Legacy expectedRoutes do not classify normalized page sets.</summary>
    public static async Task<DocusaurusMigrationReport> ConvertAsync(
        string sourceDirectory,
        string destinationDirectory,
        IReadOnlyList<string>? expectedRoutes = null,
        DocusaurusMigrationOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await ConvertWithOracleAsync(sourceDirectory, destinationDirectory,
            expectedRoutes is null ? null : MigrationRouteOracle.FromPaths(expectedRoutes),
            options, cancellationToken).ConfigureAwait(false);

    /// <summary>Writes only to a new destination; a missing oracle is recorded as not-compared.</summary>
    public static async Task<DocusaurusMigrationReport> ConvertWithOracleAsync(
        string sourceDirectory,
        string destinationDirectory,
        MigrationRouteOracle? routeOracle,
        DocusaurusMigrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? new();
        ValidateNormalizedInputs(routeOracle, effectiveOptions);
        var fullDestination = Path.GetFullPath(destinationDirectory);
        return FromResult(await DocusaurusMigration.ConvertWithOracleAsync(
            sourceDirectory, fullDestination, routeOracle, effectiveOptions, cancellationToken).ConfigureAwait(false),
            dryRun: false, destination: fullDestination, routeOracle, effectiveOptions);
    }

    internal static DocusaurusMigrationReport FromResult(
        DocusaurusMigrationResult result,
        bool dryRun,
        string? destination,
        MigrationRouteOracle? routeOracle,
        DocusaurusMigrationOptions options)
    {
        var files = result.Files.Select(file =>
        {
            var verdict = file.Verdict switch
            {
                DocusaurusMigrationVerdict.Automatic => MigrationVerdict.Automatic,
                DocusaurusMigrationVerdict.Convertible => MigrationVerdict.Convertible,
                DocusaurusMigrationVerdict.ManualActionRequired => MigrationVerdict.ManualActionRequired,
                _ => MigrationVerdict.Unsupported,
            };
            var issues = file.Issues.Select(issue => new MigrationIssueReport(
                issue.Id, issue.Severity, issue.Message,
                new SiteSourceLocation(file.SourcePath, issue.Line, null),
                issue.Replacement, issue.ManualStep)).ToArray();
            var automatic = verdict is MigrationVerdict.Automatic or MigrationVerdict.Convertible;
            var manual = issues.FirstOrDefault(issue => issue.ManualStep is not null)?.ManualStep;
            var actions = file.Edits.Select(edit => new MigrationSuggestedAction(
                file.SourcePath, edit.Kind, edit.StartLine, edit.EndLine, edit.Text, file.SourceFingerprint,
                automatic,
                automatic
                    ? $"Apply only when '{file.SourcePath}' still matches fingerprint {file.SourceFingerprint}; re-run the migration analysis after any edit."
                    : $"Not automatically applicable: '{file.SourcePath}' needs manual actions (verdict {verdict})."
                        + (manual is null ? "" : $" First step: {manual}"))).ToArray();
            var componentChanges = (file.ComponentChanges ?? []).Select(change => new MigrationComponentChangeReport(
                file.SourcePath, change.Component, change.Kind, change.FunctionalEquivalence, change.Note)).ToArray();
            return new MigrationFileReport(file.SourcePath, verdict, file.ConvertedPath, issues, actions, file.SourceFingerprint, componentChanges);
        }).ToArray();
        var componentReport = files.SelectMany(file => file.ComponentChanges).ToArray();
        return new DocusaurusMigrationReport(
            ToolingContracts.MigrationReport.SchemaVersion, dryRun, result.WroteOutput, destination,
            new MigrationSummary(
                files.Length,
                files.Count(file => file.Verdict == MigrationVerdict.Automatic),
                files.Count(file => file.Verdict == MigrationVerdict.Convertible),
                files.Count(file => file.Verdict == MigrationVerdict.ManualActionRequired),
                files.Count(file => file.Verdict == MigrationVerdict.Unsupported)),
            files,
            new MigrationManifestReport(
                result.Manifest.Variants.Select(variant => new MigrationVariantReport(
                    variant.Version, variant.Locale, variant.InputDirectory, variant.SuggestedRoutePrefix)).ToArray(),
                new SortedDictionary<string, string>(
                    result.Manifest.VersionedSidebars.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                    StringComparer.Ordinal),
                result.Manifest.BlogAuthors.Select(author => new MigrationAuthorReport(author.Id, author.Name)).ToArray(),
                result.Manifest.ManualSteps.ToArray(),
                result.Manifest.UnsupportedNotes.ToArray()),
            result.ConvertedRoutes.Select(route => new MigrationRouteReport(route.Route, route.SourceFile, route.Kind)).ToArray(),
            result.MissingRoutes.ToArray(),
            result.ExtraRoutes.ToArray(),
            result.ExitCode,
            CreateRouteComparison(result, routeOracle, options),
            componentReport);
    }

    private static MigrationRouteComparisonReport CreateRouteComparison(
        DocusaurusMigrationResult result,
        MigrationRouteOracle? routeOracle,
        DocusaurusMigrationOptions options)
    {
        var targetRoutes = result.ConvertedRoutes.Select(route => route.Route)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (routeOracle is null)
        {
            var pageStatus = options.CompareNormalizedPageSet
                ? MigrationPageSetComparisonStatus.NotCompared
                : MigrationPageSetComparisonStatus.NotRequested;
            return new(
                MigrationRouteComparisonStatus.NotCompared,
                sourceRouteCount: null,
                targetRoutes.Length,
                excludedRouteCount: null,
                duplicateSourceRouteCount: null,
                result.SourceVersion,
                result.SourceHash,
                routeOracleHash: null,
                [],
                [],
                new MigrationPageSetComparisonReport(pageStatus, null, null, null, null, [], [], [],
                    options.CompareNormalizedPageSet ? "No source route oracle was supplied; empty raw differences do not indicate a pass." : null));
        }

        var uniqueOracleRoutes = routeOracle.Routes.GroupBy(route => route.Path, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        var exclusions = uniqueOracleRoutes.Where(route => route.Category != MigrationRouteCategory.Document || route.ExclusionReason is not null)
            .GroupBy(route => (route.Category, Reason: route.ExclusionReason ?? DefaultExclusionReason(route.Category)))
            .OrderBy(group => group.Key.Category)
            .ThenBy(group => group.Key.Reason, StringComparer.Ordinal)
            .Select(group => new MigrationRouteExclusionReport(group.Key.Category, group.Count(), group.Key.Reason)).ToArray();
        var locales = uniqueOracleRoutes.Where(route => !string.IsNullOrWhiteSpace(route.Locale))
            .GroupBy(route => route.Locale!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new MigrationLocaleCountReport(group.Key, group.Count())).ToArray();
        var normalized = CreatePageSetComparison(routeOracle, options, uniqueOracleRoutes, targetRoutes);
        var rawStatus = result.MissingRoutes.Count == 0 && result.ExtraRoutes.Count == 0
            ? MigrationRouteComparisonStatus.Match
            : MigrationRouteComparisonStatus.Differences;
        return new(
            rawStatus,
            uniqueOracleRoutes.Length,
            targetRoutes.Length,
            exclusions.Sum(item => item.Count),
            routeOracle.Routes.Count - uniqueOracleRoutes.Length,
            routeOracle.SourceVersion ?? result.SourceVersion,
            result.SourceHash,
            FingerprintOracle(routeOracle),
            exclusions,
            locales,
            normalized);
    }

    private static MigrationPageSetComparisonReport CreatePageSetComparison(
        MigrationRouteOracle routeOracle,
        DocusaurusMigrationOptions options,
        IReadOnlyList<MigrationRouteOracleEntry> uniqueOracleRoutes,
        IReadOnlyList<string> targetRoutes)
    {
        if (!options.CompareNormalizedPageSet)
            return new(MigrationPageSetComparisonStatus.NotRequested, null, null, null, null, [], [], [], null);

        var sourceRoutes = uniqueOracleRoutes.Where(route => route.Category == MigrationRouteCategory.Document
            && route.ExclusionReason is null).ToArray();
        if (sourceRoutes.Length == 0)
        {
            return new(MigrationPageSetComparisonStatus.NotCompared, null, null, null, null, [], [], [],
                "The oracle contains no non-excluded routes classified as Document; derived and unclassified routes are not used as a page-set denominator.");
        }

        var sourceBasePath = routeOracle.BasePath;
        var targetBasePath = SiteRoute.ForDirectoryIndex(string.Empty, options.BaseUrl).PublicPath;
        var normalizedSource = sourceRoutes.Select(route => NormalizePublicPagePath(route.Path, sourceBasePath))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var normalizedTarget = targetRoutes.Select(route => NormalizePublicPagePath(route, targetBasePath))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var missing = normalizedSource.Except(normalizedTarget, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extra = normalizedTarget.Except(normalizedSource, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var rules = new List<string>();
        if (sourceBasePath is not null && CanonicalBasePath(sourceBasePath) != "/")
            rules.Add("Strip the explicitly declared source base path when it is an exact segment prefix.");
        if (targetBasePath != "/")
            rules.Add("Strip the target --base-url path when it is an exact segment prefix.");
        rules.Add("Treat a page route with or without its trailing slash as the same directory page.");
        rules.Add("Canonicalize each path segment once through SiteRoute (strict UTF-8 percent decoding, NFC, then canonical percent encoding).");
        rules.Add("Compare with ordinal, case-sensitive semantics; do not case-fold or recursively decode percent escapes.");
        return new(
            missing.Length == 0 && extra.Length == 0 ? MigrationPageSetComparisonStatus.Match : MigrationPageSetComparisonStatus.Differences,
            sourceRoutes.Length,
            normalizedSource.Length,
            targetRoutes.Count,
            normalizedTarget.Length,
            missing,
            extra,
            rules,
            null);
    }

    private static void ValidateNormalizedInputs(MigrationRouteOracle? routeOracle, DocusaurusMigrationOptions options)
    {
        if (!options.CompareNormalizedPageSet || routeOracle is null)
            return;
        _ = SiteRoute.ForDirectoryIndex(string.Empty, options.BaseUrl);
        if (routeOracle.BasePath is not null)
            _ = CanonicalBasePath(routeOracle.BasePath);
        foreach (var route in routeOracle.Routes.Where(route => route.Category == MigrationRouteCategory.Document
            && route.ExclusionReason is null))
            _ = NormalizePublicPagePath(route.Path, routeOracle.BasePath);
    }

    private static string NormalizePublicPagePath(string path, string? basePath)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.Contains('\\') || path.IndexOfAny(['?', '#']) >= 0 || path.Contains("//", StringComparison.Ordinal))
            throw new ArgumentException($"Route '{path}' must be an unambiguous absolute public path for normalized comparison.", nameof(path));

        var canonical = SiteRoute.ForDirectoryIndex(path[1..].TrimEnd('/')).PublicPath;
        var prefix = basePath is null ? "/" : CanonicalBasePath(basePath);
        if (prefix != "/" && canonical.StartsWith(prefix, StringComparison.Ordinal))
        {
            var remainder = canonical[prefix.Length..];
            canonical = remainder.Length == 0 ? "/" : "/" + remainder;
        }
        return canonical;
    }

    private static string CanonicalBasePath(string basePath)
    {
        if (!basePath.StartsWith("/", StringComparison.Ordinal) || basePath.StartsWith("//", StringComparison.Ordinal)
            || basePath.Contains('\\') || basePath.IndexOfAny(['?', '#']) >= 0 || basePath.Contains("//", StringComparison.Ordinal))
            throw new ArgumentException($"Base path '{basePath}' must be an absolute public path.", nameof(basePath));
        return SiteRoute.ForDirectoryIndex(basePath[1..].Trim('/')).PublicPath;
    }

    private static string DefaultExclusionReason(MigrationRouteCategory category) => category switch
    {
        MigrationRouteCategory.CategoryIndex => "Generated category indexes do not have a one-to-one source document.",
        MigrationRouteCategory.BlogIndex => "Generated blog indexes are outside the migrated article page set.",
        MigrationRouteCategory.BlogAuthor => "Generated author indexes are outside the migrated article page set.",
        MigrationRouteCategory.BlogTag => "Generated tag indexes are outside the migrated article page set.",
        MigrationRouteCategory.BlogArchive => "Generated archive indexes are outside the migrated article page set.",
        MigrationRouteCategory.BlogPagination => "Generated pagination routes are outside the migrated article page set.",
        MigrationRouteCategory.Unclassified => "A path-only oracle does not declare route kinds, so the route is not assumed to be a document page.",
        _ => "The route is outside the declared document page-set scope.",
    };

    private static string FingerprintOracle(MigrationRouteOracle routeOracle)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sourceVersion = routeOracle.SourceVersion,
            basePath = routeOracle.BasePath,
            routes = routeOracle.Routes
                .OrderBy(route => route.Path, StringComparer.Ordinal)
                .ThenBy(route => route.Category)
                .ThenBy(route => route.Locale, StringComparer.Ordinal)
                .ThenBy(route => route.ExclusionReason, StringComparer.Ordinal)
                .Select(route => new
                {
                    path = route.Path,
                    category = route.Category.ToString(),
                    route.Locale,
                    reason = route.ExclusionReason,
                }).ToArray(),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }
}
