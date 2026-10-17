using LithoSharp.Routing;

namespace LithoSharp.Content;

/// <summary>A snapshot of explicitly declared static inputs, without a claim of runtime publication.</summary>
public sealed class StaticContentCatalog
{
    private readonly Dictionary<ContentEntryId, StaticContentCatalogEntry> byId;

    /// <summary>Creates a catalog from the same declarations used for generated references.</summary>
    public StaticContentCatalog(ContentCollectionId collectionId, IEnumerable<StaticContentCatalogEntry> entries,
        string site = "", string variant = "")
    {
        ArgumentNullException.ThrowIfNull(collectionId);
        ArgumentNullException.ThrowIfNull(entries);
        CollectionId = collectionId;
        Site = NormalizeScope(site, nameof(site));
        Variant = NormalizeScope(variant, nameof(variant));
        var snapshot = entries.ToArray();
        if (snapshot.Any(static entry => entry is null))
            throw new ArgumentException("Catalog entries must not contain null.", nameof(entries));
        byId = snapshot.ToDictionary(static entry => entry.Id);
        Entries = Array.AsReadOnly(snapshot);
    }

    /// <summary>The existing collection identity.</summary>
    public ContentCollectionId CollectionId { get; }
    /// <summary>The optional site identity; an empty value denotes the default scope.</summary>
    public string Site { get; }
    /// <summary>The optional variant identity; an empty value denotes the default scope.</summary>
    public string Variant { get; }
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

    private static string NormalizeScope(string value, string parameter) => value is null
        ? throw new ArgumentNullException(parameter)
        : value.Length == 0 ? string.Empty : ContentIdentity.Normalize(value, parameter);
}

/// <summary>A project-relative input and its existing entry identity and validated route.</summary>
public sealed class StaticContentCatalogEntry
{
    /// <summary>Creates one declared membership record.</summary>
    public StaticContentCatalogEntry(ContentEntryId id, string sourcePath, SiteRoute route)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(route);
        Id = id;
        SourcePath = Build.BuildInput.FromFile(sourcePath).Key;
        Route = route;
    }

    /// <summary>The existing entry identity.</summary>
    public ContentEntryId Id { get; }
    /// <summary>The source path relative to MSBuildProjectDirectory.</summary>
    public string SourcePath { get; }
    /// <summary>The route shared by generated references and explicit runtime registration.</summary>
    public SiteRoute Route { get; }
}
