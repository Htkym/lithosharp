using LithoSharp.Routing;

namespace LithoSharp.Content;

/// <summary>A snapshot of explicitly declared static inputs, without a claim of runtime publication.</summary>
public sealed class StaticContentCatalog
{
    private readonly Dictionary<ContentEntryId, StaticContentCatalogEntry> byId;
    private readonly Dictionary<string, StaticContentCatalogEntry> byInputPath;

    /// <summary>Creates a catalog from the same declarations used for generated references.</summary>
    // Preserve shipped optional arguments; the longer input-policy overload requires every argument.
#pragma warning disable RS0027
    public StaticContentCatalog(ContentCollectionId collectionId, IEnumerable<StaticContentCatalogEntry> entries,
        string site = "", string variant = "")
        : this(collectionId, entries, string.Empty, ContentInputProfile.Markdown, site, variant)
    {
    }
#pragma warning restore RS0027

    /// <summary>Creates a catalog with a declared discovery root and input policy.</summary>
    public StaticContentCatalog(ContentCollectionId collectionId, IEnumerable<StaticContentCatalogEntry> entries,
        string inputRoot, ContentInputProfile inputProfile, string site, string variant)
    {
        ArgumentNullException.ThrowIfNull(collectionId);
        ArgumentNullException.ThrowIfNull(entries);
        CollectionId = collectionId;
        Site = NormalizeScope(site, nameof(site));
        Variant = NormalizeScope(variant, nameof(variant));
        ArgumentNullException.ThrowIfNull(inputRoot);
        if (inputRoot.Length != 0)
            _ = ContentInputPolicy.NormalizePath(inputRoot);
        InputRoot = inputRoot.Replace('\\', '/');
        if (!Enum.IsDefined(inputProfile))
            throw new ArgumentOutOfRangeException(nameof(inputProfile));
        if (inputProfile == ContentInputProfile.OpenKnowledgeFormat && InputRoot.Length == 0)
            throw new ArgumentException("An OKF catalog requires an explicit bundle input root.", nameof(inputRoot));
        InputProfile = inputProfile;
        var snapshot = entries.ToArray();
        if (snapshot.Any(static entry => entry is null))
            throw new ArgumentException("Catalog entries must not contain null.", nameof(entries));
        byId = snapshot.ToDictionary(static entry => entry.Id);
        byInputPath = new Dictionary<string, StaticContentCatalogEntry>(ContentInputPolicy.PathComparer);
        foreach (var entry in snapshot)
        {
            var path = ContentInputPolicy.RelativeToRoot(entry.SourcePath, InputRoot);
            if (InputRoot.Length != 0 && (entry.InputPath != path || ContentInputPolicy.Classify(path, inputProfile) != entry.Kind))
                throw new ArgumentException($"Catalog input '{entry.SourcePath}' does not match its declared root or input policy.", nameof(entries));
            if (!byInputPath.TryAdd(path, entry))
                throw new ArgumentException($"Catalog inputs collide at normalized relative path '{path}'.", nameof(entries));
        }
        if (InputRoot.Length != 0)
        {
            var routes = new SiteRouteTable();
            foreach (var entry in snapshot)
                routes.Register(entry.Route, entry.Id.Value, new Diagnostics.SiteSourceLocation(entry.SourcePath, 1, 1));
            routes.ValidateOrThrow();
        }
        Entries = Array.AsReadOnly(snapshot);
    }

    /// <summary>The existing collection identity.</summary>
    public ContentCollectionId CollectionId { get; }
    /// <summary>The optional site identity; an empty value denotes the default scope.</summary>
    public string Site { get; }
    /// <summary>The optional variant identity; an empty value denotes the default scope.</summary>
    public string Variant { get; }
    /// <summary>The project-relative discovery root; empty identifies legacy declarations without a checked runtime set.</summary>
    public string InputRoot { get; }
    /// <summary>The classification and exclusion policy used for these declarations.</summary>
    public ContentInputProfile InputProfile { get; }
    /// <summary>The declared membership, in deterministic generated member order.</summary>
    public IReadOnlyList<StaticContentCatalogEntry> Entries { get; }

    /// <summary>Gets a declared input. Undeclared runtime entries are rejected.</summary>
    public StaticContentCatalogEntry GetEntry(ContentEntryId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return byId.TryGetValue(id, out var entry) ? entry
            : throw new KeyNotFoundException($"Entry '{id.Value}' is not declared in collection '{CollectionId.Value}'.");
    }

    /// <summary>Resolves the declared route for use by an explicit runtime route convention.</summary>
    public SiteRoute GetRoute(ContentEntryId id) => GetEntry(id).Route;

    internal StaticContentCatalogEntry GetInput(string relativePath) => byInputPath[relativePath];

    internal IReadOnlyList<Diagnostics.SiteDiagnostic> ValidateInputs(IReadOnlyList<ContentFile> files, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostics.SiteDiagnostic>();
        var discovered = files.Select(static file => file.RelativePath).ToHashSet(ContentInputPolicy.PathComparer);
        foreach (var entry in byInputPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!discovered.Contains(entry.Key))
                diagnostics.Add(ContentDiagnostic.Error("LSC0010", $"Declared static input '{entry.Value.SourcePath}' was not found. Rebuild the catalog after deleting or moving an input.", entry.Key, 1, 1));
        }
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byInputPath.TryGetValue(file.RelativePath, out var entry))
                diagnostics.Add(ContentDiagnostic.Error("LSC0010", $"Discovered input '{file.RelativePath}' is absent from the static catalog. Add it to AdditionalFiles and rebuild.", file.RelativePath, 1, 1));
            else if (!string.Equals(entry.InputPath, file.RelativePath, StringComparison.Ordinal) || entry.Kind != file.Kind)
                diagnostics.Add(ContentDiagnostic.Error("LSC0010", $"Discovered input '{file.RelativePath}' differs from its static catalog path or classification. Rebuild using the same input policy.", file.RelativePath, 1, 1));
        }
        return diagnostics;
    }

    private static string NormalizeScope(string value, string parameter) => value is null
        ? throw new ArgumentNullException(parameter)
        : value.Length == 0 ? string.Empty : ContentIdentity.Normalize(value, parameter);
}

/// <summary>A project-relative input and its existing entry identity and validated route.</summary>
public sealed class StaticContentCatalogEntry
{
    /// <summary>Creates one declared membership record.</summary>
    public StaticContentCatalogEntry(ContentEntryId id, string sourcePath, SiteRoute route)
        : this(id, sourcePath, route, sourcePath, ContentInputKind.Markdown)
    {
    }

    /// <summary>Creates one declared input with its root-relative path and pre-parse classification.</summary>
    public StaticContentCatalogEntry(ContentEntryId id, string sourcePath, SiteRoute route, string inputPath, ContentInputKind kind)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(route);
        Id = id;
        SourcePath = Build.BuildInput.FromFile(sourcePath).Key;
        Route = route;
        InputPath = ContentInputPolicy.NormalizePath(inputPath);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Kind = kind;
        if (kind == ContentInputKind.Concept && !InputPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An OKF concept must have a Markdown input path.", nameof(inputPath));
    }

    /// <summary>The existing entry identity.</summary>
    public ContentEntryId Id { get; }
    /// <summary>The source path relative to MSBuildProjectDirectory.</summary>
    public string SourcePath { get; }
    /// <summary>The route shared by generated references and explicit runtime registration.</summary>
    public SiteRoute Route { get; }
    /// <summary>The path within the declared input root, separate from the project path and public route.</summary>
    public string InputPath { get; }
    /// <summary>The role assigned before parsing or metadata validation.</summary>
    public ContentInputKind Kind { get; }
    /// <summary>The bundle-relative concept identifier, or null for ordinary and reserved inputs.</summary>
    public string? ConceptId => Kind == ContentInputKind.Concept ? InputPath[..^3] : null;
}
