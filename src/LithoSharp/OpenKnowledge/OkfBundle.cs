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
    internal OkfDocument(OkfFile file, string? rawText, MarkdownFootnoteDocument? syntax, OkfMetadata? metadata, bool isVersion03 = false)
    {
        (File, RawText, Syntax, Metadata) = (file, rawText, syntax, metadata);
        IsSkipped = isVersion03 && file.Kind == ContentInputKind.Concept && string.IsNullOrWhiteSpace(metadata?.Type);
        Citations = CreateCitations();
    }
    public OkfFile File { get; }
    public string Path => File.Path;
    public ContentInputKind Kind => File.Kind;
    public bool IsSkipped { get; }
    public bool IsConcept => Kind == ContentInputKind.Concept && !IsSkipped;
    public string? ConceptId => IsConcept ? Path[..^3] : null;
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
    public bool IsAvailable => !IsSkipped && Syntax is { Analysis.BodySpan: not null } && Syntax.Analysis.Status != MarkdownParseStatus.Failed
        && !Syntax.Analysis.Diagnostics.Any(static diagnostic => diagnostic.Origin == "resource" || diagnostic.Id is "LMD002" or "LMD003")
        && (Yaml is null || Yaml.Status == MarkdownParseStatus.Complete)
        && (Kind != ContentInputKind.Concept || !string.IsNullOrWhiteSpace(Metadata?.Type));
    public string? Body => RawText is not null && Syntax?.Analysis.BodySpan is { } body
        ? RawText.Substring(body.Start, body.Length) : null;
}

/// <summary>A nonexecuting, version-selected OKF reader over immutable file snapshots.</summary>
public sealed partial class OkfBundle
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly IReadOnlyDictionary<string, OkfFile> filesByPath;
    private OkfBundle(string inputRoot, OkfReadOptions options, DateTimeOffset asOf, OkfFile[] files,
        OkfDocument[] documents, List<SiteDiagnostic> diagnostics, bool snapshotComplete, string? declaredVersion)
    {
        InputRoot = inputRoot;
        AsOf = asOf;
        Files = Array.AsReadOnly(files);
        filesByPath = files.ToDictionary(static file => ContentInputPolicy.NormalizePath(file.Path), ContentInputPolicy.PathComparer);
        Documents = Array.AsReadOnly(documents);
        SnapshotComplete = snapshotComplete;
        DeclaredVersion = declaredVersion;
        Catalog = CreateCatalog(options.BaseUrl, diagnostics);
        Links = SpecificationVersion is not null ? ResolveLinks(diagnostics) : [];
        PathReferences = ResolvePathReferences();
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
        IsComplete = snapshotComplete && documents.All(static document => document.Syntax?.Analysis.Status == MarkdownParseStatus.Complete)
            && !diagnostics.Any(static diagnostic => diagnostic.Category is "Unsupported" or "ResourceLimit" or "Canceled" or "Io");
        Conformance = diagnostics.Any(static diagnostic => diagnostic.Category == "Structure" && diagnostic.Severity == SiteDiagnosticSeverity.Error)
            ? OkfConformance.NonConformant
            : snapshotComplete && documents.All(static document => document.Syntax?.Analysis.FrontMatter?.State
                is MarkdownFrontMatterState.Absent or MarkdownFrontMatterState.Parsed or MarkdownFrontMatterState.Empty)
                && !diagnostics.Any(static diagnostic => diagnostic.Category is "Unsupported" or "ResourceLimit" or "Canceled" or "Io")
                && SpecificationVersion is not null ? OkfConformance.Conformant : OkfConformance.NotAssessed;
        IsSuccess = IsConformant && !diagnostics.Any(static diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error)
            && (!options.StrictLint || !diagnostics.Any(static diagnostic => diagnostic.Severity >= SiteDiagnosticSeverity.Warning));
        SnapshotFingerprint = Hash(string.Join('\n', files.Select(static file => file.Path + ":" + file.Sha256)));
    }
    public string InputRoot { get; }
    public string? SpecificationVersion => DeclaredVersion is "0.2" or "0.3" ? DeclaredVersion : null;
    internal bool IsVersion03 => DeclaredVersion == "0.3";
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
    public IReadOnlyList<OkfPathReference> PathReferences { get; }

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
        OkfDocument? capturedIndex = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootAttributes = File.GetAttributes(root);
            if ((rootAttributes & FileAttributes.Directory) == 0)
                throw new IOException("The bundle input path must be a directory.");
            if ((rootAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException("The bundle input root must not be a symbolic link, junction, or device.");
            SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(root, root);
            var rootIndexPath = Path.Combine(root, "index.md");
            var hasExactRootIndex = false;
            foreach (var entry in Directory.EnumerateFileSystemEntries(root).Take(options.MaxDiscoveryEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(entry) != "index.md")
                    continue;
                hasExactRootIndex = true;
                break;
            }
            if (hasExactRootIndex)
            {
                try
                {
                    await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(root, rootIndexPath, asynchronous: true);
                    var length = stream.Length;
                    if (length <= options.MaxFileBytes && length <= options.MaxBundleBytes && length <= int.MaxValue)
                    {
                        var bytes = new byte[(int)length];
                        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                        if (stream.Length != length || stream.ReadByte() != -1)
                            throw new IOException("The root index changed during capture.");
                        BuildInputFingerprint.VerifyOpenedContainedFile(root, rootIndexPath, stream.SafeFileHandle);
                        capturedIndex = ReadDocument(new OkfFile("index.md", ContentInputKind.Index, bytes), asOf, diagnostics, cancellationToken);
                        files.Add(capturedIndex.File);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                    or System.ComponentModel.Win32Exception or System.Security.SecurityException)
                {
                    // Discovery records the unsafe or unreadable path without an unguarded fallback read.
                }
            }
            var useVersion03 = CapturedVersion(capturedIndex) == "0.3";
            var discovered = ContentPath.Discover(root, ContentInputProfile.OpenKnowledgeFormat, cancellationToken,
                options.MaxFiles, options.MaxDiscoveryEntries, useVersion03);
            diagnostics.AddRange(discovered.Diagnostics.Select(static diagnostic => new SiteDiagnostic(
                diagnostic.Id == ContentPath.DiscoveryLimitDiagnosticId ? "OKF900" : diagnostic.Id, diagnostic.Severity,
                diagnostic.Message, diagnostic.Location, diagnostic.Id == ContentPath.DiscoveryLimitDiagnosticId ? "ResourceLimit"
                    : diagnostic.Id == "LSC0007" ? "Structure" : "Io", diagnostic.RelatedLocations)));
            snapshotComplete = discovered.Diagnostics.Count == 0;
            if (snapshotComplete && capturedIndex is not null && !discovered.Files.Any(file => file.FullPath == rootIndexPath))
            {
                Add(diagnostics, "OKF901", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Io,
                    "The bundle root index disappeared or changed identity during capture.", "index.md");
                snapshotComplete = false;
            }
            long total = capturedIndex?.File.Length ?? 0;
            foreach (var file in discovered.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var originalPath = System.IO.Path.GetRelativePath(root, file.FullPath).Replace('\\', '/');
                if (capturedIndex is not null && ContentInputPolicy.PathComparer.Equals(ContentInputPolicy.NormalizePath(originalPath), "index.md"))
                {
                    if (originalPath != "index.md")
                    {
                        Add(diagnostics, "OKF901", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Io,
                            "The bundle root index changed spelling during capture.", originalPath);
                        snapshotComplete = false;
                    }
                    continue;
                }
                if (files.Count >= options.MaxFiles)
                {
                    if (!diagnostics.Any(static diagnostic => diagnostic.Id == "OKF900"))
                        Add(diagnostics, "OKF900", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.ResourceLimit,
                            $"MaxFiles ({options.MaxFiles}) was exceeded.", originalPath);
                    snapshotComplete = false;
                    continue;
                }
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
                    options.MaxFiles, options.MaxDiscoveryEntries, useVersion03) : discovered;
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            Add(diagnostics, "OKF901", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Io, exception.Message, root);
            snapshotComplete = false;
        }
        string? declaredVersion = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootIndex = files.FirstOrDefault(static file => file.Path == "index.md");
            if (rootIndex is not null)
            {
                var index = capturedIndex ?? ReadDocument(rootIndex, asOf, diagnostics, cancellationToken);
                documents.Add(index);
                var version = index.Yaml?.Root is { } mapping ? OkfMetadata.Field(mapping, "okf_version") : null;
                declaredVersion = CapturedVersion(index);
                if (declaredVersion is not null and not ("0.2" or "0.3"))
                    Add(diagnostics, "OKF007", SiteDiagnosticSeverity.Warning, OkfDiagnosticCategory.Unsupported,
                        "The declared OKF version is unsupported. Supported versions are 0.2 and 0.3; inspection retains the snapshot without claiming conformance.", "index.md");
                if (index.Yaml?.Status == MarkdownParseStatus.Complete && version is not null
                    && declaredVersion is null)
                {
                    Add(diagnostics, "OKF011", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Structure,
                        "okf_version must be a quoted string: use okf_version: \"0.2\" or okf_version: \"0.3\" in the bundle-root index.md.", "index.md");
                    declaredVersion = null;
                }
                if (snapshotComplete && (index.Syntax?.Analysis.FrontMatter?.State is MarkdownFrontMatterState.Absent or MarkdownFrontMatterState.Empty
                    || index.Yaml is { Status: MarkdownParseStatus.Complete, Root.Kind: MarkdownYamlKind.Mapping } && version is null))
                    MissingVersion();
                if (index.Yaml is { Status: MarkdownParseStatus.Complete, Root: { Kind: not MarkdownYamlKind.Mapping } })
                    Add(diagnostics, "OKF011", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Structure,
                        "The bundle-root index.md frontmatter must be a YAML mapping containing a quoted okf_version.", "index.md");
                if (capturedIndex is null && declaredVersion == "0.3")
                {
                    Add(diagnostics, "OKF901", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Io,
                        "The bundle version changed during capture. Read the bundle again to apply its discovery rules safely.", "index.md");
                    snapshotComplete = false;
                }
            }
            else if (snapshotComplete)
                MissingVersion();
            foreach (var captured in files.Where(static file => file.Kind != ContentInputKind.Auxiliary && file.Path != "index.md"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                documents.Add(ReadDocument(captured, asOf, diagnostics, cancellationToken, declaredVersion == "0.3", declaredVersion is "0.2" or "0.3"));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!diagnostics.Any(static diagnostic => diagnostic.Id == "OKF902"))
                Add(diagnostics, "OKF902", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Canceled,
                    "Reading the bundle was canceled; the result is partial.", ".");
            snapshotComplete = false;
        }
        return new OkfBundle(root, options, asOf, files.OrderBy(static file => file.Path, StringComparer.Ordinal).ToArray(),
            documents.OrderBy(static doc => doc.Path, StringComparer.Ordinal).ToArray(), diagnostics, snapshotComplete, declaredVersion);

        void MissingVersion() => Add(diagnostics, "OKF010", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.PublicationPolicy,
            "LithoSharp requires an explicit OKF version. Add a bundle-root index.md with YAML frontmatter containing okf_version: \"0.2\" for existing bundles or okf_version: \"0.3\" for v0.3 bundles.", "index.md");
    }

    private static string? CapturedVersion(OkfDocument? index)
    {
        if (index?.Yaml is not { Status: MarkdownParseStatus.Complete, Root.Kind: MarkdownYamlKind.Mapping } yaml)
            return null;
        var node = OkfMetadata.Field(yaml.Root, "okf_version");
        return node is not null && node.GetRawText().TrimStart() is ['\'' or '"', ..]
            && OkfMetadata.String(node) is { Length: > 0 } value
            && System.Text.RegularExpressions.Regex.IsMatch(value, @"\A[0-9]+\.[0-9]+\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            ? value : null;
    }

    private static OkfDocument ReadDocument(OkfFile file, DateTimeOffset asOf, List<SiteDiagnostic> diagnostics, CancellationToken token,
        bool isVersion03 = false, bool validateProfile = true)
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
        var metadata = validateProfile ? OkfDocumentValidation.FrontMatter(file.Kind, file.Path,
            frontMatter?.State, yaml, yamlOffset,
            frontMatter?.OpeningSpan, frontMatter?.YamlSpan, asOf,
            (id, severity, category, message, span) => Add(diagnostics, id, severity, category, message, file.Path, analysis, span), isVersion03) : null;
        if (validateProfile && file.Kind == ContentInputKind.Log && analysis.Headings is { } headings)
            foreach (var heading in headings.Where(static heading => heading.RawLevel == 2))
                if (!OkfDocumentValidation.IsLogDate(heading.Text.Text))
                    Add(diagnostics, "OKF008", SiteDiagnosticSeverity.Error, OkfDiagnosticCategory.Structure,
                        "Log date headings must use a valid YYYY-MM-DD date.", file.Path, analysis, heading.TextRawSpan ?? heading.RawSpan);
        return new OkfDocument(file, raw, syntax, metadata, isVersion03);
    }

    private StaticContentCatalog? CreateCatalog(string? baseUrl, List<SiteDiagnostic> diagnostics)
    {
        var skipped = Documents.Where(static doc => doc.IsSkipped).Select(static doc => doc.Path).ToHashSet(StringComparer.Ordinal);
        var entries = Files.Where(file => !skipped.Contains(file.Path)).Select(file => new StaticContentCatalogEntry(new ContentEntryId(file.Path), "bundle/" + file.Path,
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
        return new StaticContentCatalog(new ContentCollectionId("okf"), entries, "bundle", ContentInputProfile.OpenKnowledgeFormat, string.Empty, string.Empty, IsVersion03);
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
