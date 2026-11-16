using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LithoSharp.Build;
using LithoSharp.Routing;

namespace LithoSharp.Quality;

/// <summary>A versioned inventory of published URLs, decoded HTML anchors and exact artifact content hashes.</summary>
public sealed class SitePublicationManifest
{
    private const string CurrentScope = "all-output-files-and-directory-index-aliases/1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private SitePublicationManifest(ManifestData data)
    {
        Data = data;
        ExcludedOutputPaths = Array.AsReadOnly(data.ExcludedOutputPaths!);
    }

    private ManifestData Data { get; }
    public int Version => Data.Version;
    public string SiteId => Data.SiteId!;
    /// <summary>The exact configured URL, including its base path. Comparison does not silently migrate it.</summary>
    public string BaseUrl => Data.BaseUrl!;
    public string Scope => Data.Scope!;
    public string ContentIdentity => Data.ContentIdentity!;
    public IReadOnlyList<string> ExcludedOutputPaths { get; }

    /// <summary>Serializes a candidate; this does not establish publication success or write a baseline.</summary>
    public string ToJson() => JsonSerializer.Serialize(Data, Json);

    /// <summary>Reads only the supported complete schema and verifies its content identity.</summary>
    public static SitePublicationManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var data = JsonSerializer.Deserialize<ManifestData>(json, Json)
            ?? throw new JsonException("The publication manifest is empty.");
        if (data.Version != 1 || data.Scope != CurrentScope || string.IsNullOrWhiteSpace(data.SiteId)
            || string.IsNullOrWhiteSpace(data.BaseUrl) || data.ExcludedOutputPaths is null || data.Entries is null
            || data.Entries.Length == 0 || data.ContentIdentity is null)
            throw new JsonException("The publication manifest schema or identity is incomplete or unsupported.");
        _ = SiteRoute.ForDirectoryIndex(string.Empty, data.BaseUrl);
        if (new Uri(data.BaseUrl).UserInfo.Length != 0)
            throw new JsonException("The publication base URL must not contain credentials.");
        var exclusions = ValidateExclusions(data.ExcludedOutputPaths);
        if (!exclusions.SequenceEqual(data.ExcludedOutputPaths, StringComparer.Ordinal))
            throw new JsonException("Publication exclusions must be sorted, unique, exact normalized output paths.");
        var outputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in data.Entries)
        {
            if (entry is null || entry.OutputPath is null || !outputs.Add(entry.OutputPath)
                || SiteRoute.NormalizeRelativeOutputPath(entry.OutputPath) != entry.OutputPath
                || exclusions.Contains(entry.OutputPath, StringComparer.Ordinal)
                || entry.Urls is null || !Urls(entry.OutputPath, data.BaseUrl).SequenceEqual(entry.Urls, StringComparer.Ordinal)
                || entry.Anchors is null || entry.Anchors.Any(anchor => anchor is null || anchor.Length == 0)
                || !entry.Anchors.SequenceEqual(entry.Anchors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                || entry.Sha256 is null || entry.Sha256.Length != 64 || entry.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
                throw new JsonException("The publication manifest contains an invalid artifact.");
            if (entry.RedirectTarget is { } target && SiteRoute.NormalizeRelativeOutputPath(target) != target)
                throw new JsonException("The publication manifest contains an invalid redirect target.");
        }
        if (!data.Entries.Select(entry => entry.OutputPath).SequenceEqual(outputs.Order(StringComparer.Ordinal)))
            throw new JsonException("Publication artifacts must be sorted by output path.");
        var redirectSources = data.Entries.Where(entry => entry.RedirectTarget is not null).Select(entry => entry.OutputPath).ToHashSet(StringComparer.Ordinal);
        if (data.Entries.Any(entry => entry.RedirectTarget is { } target
                && (!outputs.Contains(target) || redirectSources.Contains(target))))
            throw new JsonException("Publication redirects must target an included final artifact.");
        if (!string.Equals(data.ContentIdentity, Identity(data), StringComparison.Ordinal))
            throw new JsonException("The publication manifest content identity does not match its inventory.");
        return new(data);
    }

    internal static IReadOnlyList<string> ValidateExclusions(IEnumerable<string> values)
    {
        var paths = values.ToArray();
        foreach (var path in paths)
            if (string.IsNullOrEmpty(path) || SiteRoute.NormalizeRelativeOutputPath(path) != path)
                throw new ArgumentException("Publication exclusions must be exact normalized output-relative file paths.", nameof(values));
        return Array.AsReadOnly(paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    internal static async Task<SitePublicationManifest> CaptureAsync(string root, string baseUrl, string siteId,
        IReadOnlyList<string> exclusions, IReadOnlyDictionary<string, SiteRoute> routes,
        IReadOnlyList<HtmlReferenceSnapshot> pages, IReadOnlyDictionary<string, SiteRoute> redirects,
        IDictionary<string, CachedBuildArtifact> inspected, CancellationToken token)
    {
        var anchors = pages.ToDictionary(page => page.Identity.OutputPath,
            page => page.Anchors.Select(anchor => anchor.Decoded).Where(anchor => anchor.Length != 0)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var entries = new List<EntryData>();
        foreach (var (path, route) in routes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (exclusions.Contains(path, StringComparer.Ordinal))
                continue;
            if (!inspected.TryGetValue(path, out var artifact))
            {
                await using var source = BuildInputFingerprint.OpenVerifiedContainedRead(root,
                    Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), asynchronous: true);
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, token).ConfigureAwait(false));
                artifact = new(path, path, source.Length, hash);
                inspected.Add(path, artifact);
            }
            entries.Add(new()
            {
                OutputPath = path,
                Urls = Urls(route.RelativeOutputPath, baseUrl),
                Sha256 = artifact.Sha256,
                Anchors = anchors.GetValueOrDefault(path, []),
                RedirectTarget = redirects.TryGetValue(path, out var target) ? target.RelativeOutputPath : null,
            });
        }
        var data = new ManifestData
        {
            Version = 1,
            SiteId = siteId,
            BaseUrl = baseUrl,
            Scope = CurrentScope,
            ExcludedOutputPaths = exclusions.ToArray(),
            Entries = entries.ToArray(),
        };
        if (entries.Count == 0 || entries.Any(entry => entry.RedirectTarget is { } target && exclusions.Contains(target, StringComparer.Ordinal)))
            throw new ArgumentException("Publication exclusions must leave an inventory and must not exclude included redirects' final targets.", nameof(exclusions));
        data.ContentIdentity = Identity(data);
        return new(data);
    }

    internal IReadOnlyList<EntryData> Entries => Data.Entries!;

    private static string[] Urls(string outputPath, string baseUrl)
    {
        var origin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        var encoded = string.Join('/', outputPath.Split('/').Select(Uri.EscapeDataString));
        var file = origin + SiteRoute.ForFile(encoded, baseUrl).PublicPath;
        return outputPath == "index.html" || outputPath.EndsWith("/index.html", StringComparison.Ordinal)
            ? [file, origin + SiteRoute.ForDirectoryIndex(encoded[..^10], baseUrl).PublicPath] : [file];
    }

    private static string Identity(ManifestData data)
    {
        var content = JsonSerializer.Serialize(new { data.Version, data.SiteId, data.BaseUrl, data.Scope, data.ExcludedOutputPaths, data.Entries }, Json);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }

    private sealed class ManifestData
    {
        public ManifestData() { }
        public int Version { get; set; }
        public string? SiteId { get; set; }
        public string? BaseUrl { get; set; }
        public string? Scope { get; set; }
        public string[]? ExcludedOutputPaths { get; set; }
        public string? ContentIdentity { get; set; }
        public EntryData[]? Entries { get; set; }
    }

    internal sealed class EntryData
    {
        public EntryData() { }
        public string? OutputPath { get; set; }
        public string[]? Urls { get; set; }
        public string? Sha256 { get; set; }
        public string[]? Anchors { get; set; }
        public string? RedirectTarget { get; set; }
    }
}
