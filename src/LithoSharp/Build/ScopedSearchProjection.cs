using System.Collections.ObjectModel;
using LithoSharp.Documentation;
using LithoSharp.Routing;

namespace LithoSharp.Build;

// A derived artifact declaration with optional static body text; sections always use rendered page bodies.
internal sealed record ScopedSearchProjection
{
    internal ScopedSearchProjection(string id, string relativeOutputPath, string collection, string version, string locale)
        : this(id, relativeOutputPath, collection, version, locale, ReadOnlyDictionary<string, string>.Empty)
    {
    }

    internal ScopedSearchProjection(string id, string relativeOutputPath, string collection, string version, string locale,
        IEnumerable<KeyValuePair<string, string>> bodyTextOverrides)
    {
        Id = new BuildNodeId(id).Value;
        RelativeOutputPath = SiteRoute.NormalizeRelativeOutputPath(relativeOutputPath);
        DocumentSemanticDocument.ValidateKey(new(collection, version, locale, "search"));
        Collection = collection;
        Version = version;
        Locale = locale;
        BodyTextOverrides = new ReadOnlyDictionary<string, string>(bodyTextOverrides.ToDictionary(
            static item => item.Key, static item => item.Value, StringComparer.Ordinal));
    }

    internal string Id { get; }
    internal string RelativeOutputPath { get; }
    internal string Collection { get; }
    internal string Version { get; }
    internal string Locale { get; }
    internal IReadOnlyDictionary<string, string> BodyTextOverrides { get; }
    internal BuildNodeId NodeId => new("asset:" + Id);
    internal bool Includes(DocumentKey? document) => document is not null && document.Collection == Collection
        && document.Version == Version && document.Locale == Locale;
}
