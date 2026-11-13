using System.Security.Cryptography;
using System.Text;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;
using Syntamark;

namespace LithoSharp.OpenKnowledge;

public sealed class OkfReadOptions
{
    public DateTimeOffset? AsOf { get; init; }
    public bool StrictLint { get; init; }
    public int MaxFiles { get; init; } = 10000;
    /// <summary>The maximum number of files and directories inspected during discovery.</summary>
    public int MaxDiscoveryEntries { get; init; } = 100000;
    public long MaxFileBytes { get; init; } = 4194304;
    public long MaxBundleBytes { get; init; } = 268435456;
    public string? BaseUrl { get; init; }
}

/// <summary>One verified file snapshot; export never transforms these bytes.</summary>
public sealed class OkfFile
{
    private readonly byte[] bytes;
    internal OkfFile(string path, ContentInputKind kind, byte[] bytes)
    {
        Path = path;
        Kind = kind;
        this.bytes = bytes;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
    public string Path { get; }
    public ContentInputKind Kind { get; }
    public string Sha256 { get; }
    public long Length => bytes.LongLength;
    public byte[] GetOriginalBytes() => (byte[])bytes.Clone();
    internal ReadOnlyMemory<byte> Bytes => bytes;
}

public sealed class OkfDocument
{
    internal OkfDocument(OkfFile file, string? rawText, MarkdownFootnoteDocument? syntax, OkfMetadata? metadata)
    {
        (File, RawText, Syntax, Metadata) = (file, rawText, syntax, metadata);
        Citations = CreateCitations();
    }
    public OkfFile File { get; }
    public string Path => File.Path;
    public ContentInputKind Kind => File.Kind;
    public string? ConceptId => Kind == ContentInputKind.Concept ? Path[..^3] : null;
    public string? RawText { get; }
    public MarkdownFootnoteDocument? Syntax { get; }
    public YamlDocument? Yaml => Syntax?.Analysis.FrontMatter?.GetYamlSyntax();
    public OkfMetadata? Metadata { get; }
    public IReadOnlyList<OkfCitation> Citations { get; }
    private IReadOnlyList<OkfCitation> CreateCitations()
    {
        var sources = (Metadata?.Sources ?? []).Where(static source => source.Id is not null)
            .GroupBy(static source => source.Id!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Skip(1).Any() ? null : group.First(), StringComparer.Ordinal);
        var definitions = (Syntax?.Definitions ?? []).Where(static definition => !definition.IsDuplicate)
            .GroupBy(static definition => definition.NormalizedLabel, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Skip(1).Any() ? null : group.First().Label, StringComparer.Ordinal);
        return Array.AsReadOnly((Syntax?.References ?? []).Select(reference =>
        {
            if (!sources.TryGetValue(reference.Label, out var source)
                && definitions.TryGetValue(reference.NormalizedLabel, out var label) && label is not null)
                sources.TryGetValue(label, out source);
            return new OkfCitation(reference, source);
        }).ToArray());
    }
    public string? LegacyCitationText
    {
        get
        {
            if (Metadata?.GetField("sources") is not null || RawText is null || Syntax?.Analysis is not { } analysis)
                return null;
            var heading = analysis.Headings?.FirstOrDefault(static heading => heading.Text.Text == "Citations");
            if (heading is null)
                return null;
            var section = analysis.Sections?.FirstOrDefault(section => section.HeadingLocalKey == heading?.LocalKey);
            return section?.SubtreeSpan is { } span ? RawText.Substring(span.Start, span.Length) : null;
        }
    }
    public bool IsAvailable => Syntax is { Analysis.BodySpan: not null } && Syntax.Analysis.Status != MarkdownParseStatus.Failed
        && !Syntax.Analysis.Diagnostics.Any(static diagnostic => diagnostic.Origin == "resource" || diagnostic.Id is "LMD002" or "LMD003")
        && (Yaml is null || Yaml.Status == MarkdownParseStatus.Complete)
        && (Kind != ContentInputKind.Concept || !string.IsNullOrWhiteSpace(Metadata?.Type));
    public string? Body => RawText is not null && Syntax?.Analysis.BodySpan is { } body
        ? RawText.Substring(body.Start, body.Length) : null;
}

/// <summary>A nonexecuting OKF v0.2 reader over immutable file snapshots.</summary>
public sealed partial class OkfBundle
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IReadOnlyDictionary<string, OkfFile> filesByPath;
    private OkfBundle(string inputRoot, OkfReadOptions options, DateTimeOffset asOf, OkfFile[] files,
        OkfDocument[] documents, List<SiteDiagnostic> diagnostics, bool snapshotComplete)
    {
        InputRoot = inputRoot;
        AsOf = asOf;
        Files = Array.AsReadOnly(files);
        filesByPath = files.ToDictionary(static file => ContentInputPolicy.NormalizePath(file.Path), ContentInputPolicy.PathComparer);
        Documents = Array.AsReadOnly(documents);
        SnapshotComplete = snapshotComplete;
        DeclaredVersion = OkfMetadata.String(documents.FirstOrDefault(static doc => doc.Path == "index.md")?.Yaml?.Root is { } root
            ? OkfMetadata.Field(root, "okf_version") : null);
        Catalog = CreateCatalog(options.BaseUrl, diagnostics);
        Links = ResolveLinks(diagnostics);
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        IsComplete = snapshotComplete && documents.All(static document => document.Syntax?.Analysis.Status == MarkdownParseStatus.Complete)
            && !diagnostics.Any(static diagnostic => diagnostic.Category is "Unsupported" or "ResourceLimit" or "Canceled" or "Io");
        Conformance = diagnostics.Any(static diagnostic => diagnostic.Category == "Structure" && diagnostic.Severity == SiteDiagnosticSeverity.Error)
            ? OkfConformance.NonConformant
            : snapshotComplete && documents.All(static document => document.Syntax?.Analysis.FrontMatter?.State
                is MarkdownFrontMatterState.Absent or MarkdownFrontMatterState.Parsed or MarkdownFrontMatterState.Empty)
                && !diagnostics.Any(static diagnostic => diagnostic.Category is "Unsupported" or "ResourceLimit" or "Canceled" or "Io")
                && (DeclaredVersion is null or "0.2") ? OkfConformance.Conformant : OkfConformance.NotAssessed;
        IsSuccess = IsConformant && !diagnostics.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error)
            && (!options.StrictLint || !diagnostics.Any(static diagnostic => diagnostic.Severity >= SiteDiagnosticSeverity.Warning));
        SnapshotFingerprint = Hash(string.Join('\n', files.Select(static file => file.Path + ":" + file.Sha256)));
    }
    public string InputRoot { get; }
    public string SpecificationVersion => "0.2";
    public string? DeclaredVersion { get; }
    public DateTimeOffset AsOf { get; }
    public IReadOnlyList<OkfFile> Files { get; }
    public IReadOnlyList<OkfDocument> Documents { get; }
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
    public bool SnapshotComplete { get; }
    public bool IsComplete { get; }
    public OkfConformance Conformance { get; }
    public bool IsConformant => Conformance == OkfConformance.Conformant;
    public bool IsSuccess { get; }
    public string SnapshotFingerprint { get; }
    public StaticContentCatalog? Catalog { get; }
    public IReadOnlyList<OkfLink> Links { get; }

    public static async Task<OkfBundle> ReadAsync(string inputDirectory, OkfReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);
        options ??= new OkfReadOptions();
        if (options.MaxFiles < 1 || options.MaxDiscoveryEntries < 1 || options.MaxFileBytes < 1 || options.MaxBundleBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "OKF read limits must be positive.");
        var root = Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(inputDirectory));
        var asOf = options.AsOf ?? DateTimeOffset.UtcNow;
        var diagnostics = new List<SiteDiagnostic>();
        var files = new List<OkfFile>();
        var documents = new List<OkfDocument>();
        var snapshotComplete = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(root) & FileAttributes.Directory) == 0)
                throw new IOException("The bundle input path must be a directory.");
            var discovered = ContentPath.Discover(root, ContentInputProfile.OpenKnowledgeFormat, cancellationToken,
                options.MaxFiles, options.MaxDiscoveryEntries);
            diagnostics.AddRange(discovered.Diagnostics.Select(static diagnostic => new SiteDiagnostic(
                diagnostic.Id == ContentPath.DiscoveryLimitDiagnosticId ? "OKF900" : diagnostic.Id, diagnostic.Severity,
                diagnostic.Message, diagnostic.Location, diagnostic.Id == ContentPath.DiscoveryLimitDiagnosticId ? "ResourceLimit"
                    : diagnostic.Id == "LSC0007" ? "Structure" : "Io", diagnostic.RelatedLocations)));
            snapshotComplete = discovered.Diagnostics.Count == 0;
            long total = 0;
            foreach (var file in discovered.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var originalPath = System.IO.Path.GetRelativePath(root, file.FullPath).Replace('\\', '/');
                try
                {
                    await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(root, file.FullPath, asynchronous: true);
                    var length = stream.Length;
                    if (length > options.MaxFileBytes || length > options.MaxBundleBytes - total || length > int.MaxValue)
                    {
                        Add(diagnostics, "OKF900", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.ResourceLimit,
                            length > options.MaxFileBytes ? $"MaxFileBytes ({options.MaxFileBytes}) was exceeded."
                                : length > options.MaxBundleBytes - total ? $"MaxBundleBytes ({options.MaxBundleBytes}) was exceeded."
                                : $"ManagedArrayLength ({int.MaxValue}) was exceeded.", originalPath);
                        snapshotComplete = false;
                        continue;
                    }
                    var bytes = new byte[(int)length];
                    await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                    if (stream.Length != length || stream.ReadByte() != -1)
                        throw new IOException("The file changed during capture.");
                    BuildInputFingerprint.VerifyOpenedContainedFile(root, file.FullPath, stream.SafeFileHandle);
                    total += bytes.Length;
                    var captured = new OkfFile(originalPath, file.Kind, bytes);
                    files.Add(captured);
                    if (file.Kind != ContentInputKind.Auxiliary)
                        documents.Add(ReadDocument(captured, asOf, diagnostics, cancellationToken));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or System.Security.SecurityException)
                {
                    Add(diagnostics, "OKF901", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Io, exception.Message, originalPath);
                    snapshotComplete = false;
                }
            }
            var after = snapshotComplete
                ? ContentPath.Discover(root, ContentInputProfile.OpenKnowledgeFormat, cancellationToken,
                    options.MaxFiles, options.MaxDiscoveryEntries) : discovered;
            if (snapshotComplete && (after.Diagnostics.Count != 0 || !discovered.Files.Select(static file => file.FullPath).SequenceEqual(after.Files.Select(static file => file.FullPath), StringComparer.Ordinal)))
            {
                Add(diagnostics, "OKF901", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Io,
                    "The bundle file set changed during capture; the snapshot is incomplete.", ".");
                snapshotComplete = false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Add(diagnostics, "OKF902", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Canceled,
                "Reading the bundle was canceled; the result is partial.", ".");
            snapshotComplete = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Add(diagnostics, "OKF901", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Io, exception.Message, root);
            snapshotComplete = false;
        }
        return new OkfBundle(root, options, asOf, files.ToArray(), documents.ToArray(), diagnostics, snapshotComplete);
    }

    private static OkfDocument ReadDocument(OkfFile file, DateTimeOffset asOf, List<SiteDiagnostic> diagnostics, CancellationToken token)
    {
        string raw;
        try
        {
            raw = StrictUtf8.GetString(file.Bytes.Span);
        }
        catch (DecoderFallbackException exception)
        {
            Add(diagnostics, "OKF001", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Structure,
                "Markdown must be UTF-8. " + exception.Message, file.Path);
            return new OkfDocument(file, null, null, null);
        }
        var syntax = MarkdownParser.AnalyzeFootnotes(raw, "okf", file.Path, file.Sha256, cancellationToken: token);
        var analysis = syntax.Analysis;
        var frontMatter = analysis.FrontMatter;
        var yaml = frontMatter?.GetYamlSyntax(token);
        var yamlOffset = frontMatter?.YamlSpan?.Start ?? 0;
        if (yaml is not null)
            foreach (var diagnostic in yaml.Diagnostics)
                Add(diagnostics, "OKF002", SiteDiagnosticSeverity.Error, diagnostic.Kind switch
                {
                    YamlDiagnosticKind.Unsupported => OkfDiagnosticCategory.Unsupported,
                    YamlDiagnosticKind.ResourceLimit => OkfDiagnosticCategory.ResourceLimit,
                    YamlDiagnosticKind.Canceled => OkfDiagnosticCategory.Canceled,
                    _ => OkfDiagnosticCategory.Structure,
                }, diagnostic.Reason + ": " + diagnostic.Message, file.Path, analysis,
                    new RawSpan(yamlOffset + diagnostic.RawSpan.Start, diagnostic.RawSpan.Length));
        foreach (var diagnostic in analysis.Diagnostics.Where(diagnostic => diagnostic.Id != "LMD005" || yaml is null || yaml.Diagnostics.Count == 0))
        {
            var category = diagnostic.Origin == "resource" || diagnostic.Id is "LMD002" or "LMD003" ? OkfDiagnosticCategory.ResourceLimit
                : diagnostic.Origin == "frontmatter" ? OkfDiagnosticCategory.Structure : OkfDiagnosticCategory.Quality;
            Add(diagnostics, "OKF003", category == OkfDiagnosticCategory.Quality ? SiteDiagnosticSeverity.Warning : SiteDiagnosticSeverity.Error,
                category, diagnostic.Reason + ": " + diagnostic.Message, file.Path, analysis, diagnostic.RawSpan);
        }
        var metadata = OkfDocumentValidation.FrontMatter(file.Kind, file.Path,
            frontMatter?.State, yaml, yamlOffset,
            frontMatter?.OpeningSpan, frontMatter?.YamlSpan, asOf,
            (id, severity, category, message, span) => Add(diagnostics, id, severity, category, message, file.Path, analysis, span));
        if (file.Kind == ContentInputKind.Log && analysis.Headings is { } headings)
            foreach (var heading in headings.Where(static heading => heading.RawLevel == 2))
                if (!OkfDocumentValidation.IsLogDate(heading.Text.Text))
                    Add(diagnostics, "OKF008", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Structure,
                        "Log date headings must use a valid YYYY-MM-DD date.", file.Path, analysis, heading.TextRawSpan ?? heading.RawSpan);
        return new OkfDocument(file, raw, syntax, metadata);
    }

    private StaticContentCatalog? CreateCatalog(string? baseUrl, List<SiteDiagnostic> diagnostics)
    {
        var entries = Files.Select(file => new StaticContentCatalogEntry(new ContentEntryId(file.Path), "bundle/" + file.Path,
            SiteRoute.ForFile(string.Join('/', (file.Kind == ContentInputKind.Auxiliary ? file.Path : file.Path[..^3] + ".html")
                .Split('/').Select(Uri.EscapeDataString)), baseUrl),
            file.Path, file.Kind)).ToArray();
        var routes = new SiteRouteTable();
        foreach (var entry in entries)
            routes.Register(entry.Route, entry.Id.Value, new SiteSourceLocation(entry.InputPath));
        var validation = routes.Validate();
        if (validation.Diagnostics.Count != 0)
        {
            diagnostics.AddRange(validation.Diagnostics.Select(static diagnostic => new SiteDiagnostic(
                diagnostic.Id, diagnostic.Severity, diagnostic.Message, diagnostic.Location, "Structure", diagnostic.RelatedLocations)));
            return null;
        }
        return new StaticContentCatalog(new ContentCollectionId("okf"), entries, "bundle", ContentInputProfile.OpenKnowledgeFormat, string.Empty, string.Empty);
    }

    internal static void Add(List<SiteDiagnostic> diagnostics, string id, SiteDiagnosticSeverity severity,
        OkfDiagnosticCategory category, string message, string path, MarkdownAnalysisResult? analysis = null, RawSpan? span = null)
    {
        SiteSourceLocation location;
        if (analysis is not null && span is { } range && range.End <= (analysis.BodySpan?.End ?? int.MaxValue))
        {
            var start = analysis.GetLinePosition(range.Start);
            var end = analysis.GetLinePosition(range.End);
            location = new SiteSourceLocation(path, start.Line, start.Column, end.Line, end.Column);
        }
        else
            location = new SiteSourceLocation(path);
        diagnostics.Add(new SiteDiagnostic(id, severity, message, location, category.ToString(), null));
    }
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
