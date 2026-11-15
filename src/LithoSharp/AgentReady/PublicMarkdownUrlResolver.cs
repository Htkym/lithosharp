using LithoSharp.Internal;
using LithoSharp.Routing;

namespace LithoSharp.AgentReady;

// Catalogs are snapshots of published routes, not predictions based on source extensions.
internal sealed class PublicMarkdownUrlResolver
{
    private readonly string origin;
    private readonly Uri site;
    private readonly Dictionary<string, SiteRoute?> sources;
    private readonly Dictionary<string, SiteRoute?> publicPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> knownSources;

    internal PublicMarkdownUrlResolver(string baseUrl,
        IEnumerable<KeyValuePair<string, SiteRoute>> pages,
        IEnumerable<KeyValuePair<string, SiteRoute>> assets,
        IEnumerable<string>? additionalSourceIds = null, IEnumerable<SiteRoute>? additionalPublicRoutes = null)
    {
        StaticApiGuards.ValidateAbsoluteUrl(baseUrl);
        site = new Uri(AgentReadyMetadata.RequireHttpUrl(baseUrl));
        if (site.Query.Length != 0)
            throw new ArgumentException("The site base URL must not contain a query.", nameof(baseUrl));
        origin = site.GetLeftPart(UriPartial.Authority);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        sources = new(comparer);
        knownSources = new(comparer);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(assets);
        foreach (var pair in pages.Concat(assets))
        {
            var identity = NormalizeSource(pair.Key)
                ?? throw new ArgumentException("A catalog source identity must be a valid path.");
            ArgumentNullException.ThrowIfNull(pair.Value);
            knownSources.Add(identity);
            Add(sources, identity, pair.Value);
            AddPublicRoute(pair.Value);
        }
        foreach (var route in additionalPublicRoutes ?? [])
            AddPublicRoute(route);
        foreach (var source in additionalSourceIds ?? [])
            knownSources.Add(NormalizeSource(source)
                ?? throw new ArgumentException("An include source identity must be a valid path."));

        void AddPublicRoute(SiteRoute route)
        {
            ArgumentNullException.ThrowIfNull(route);
            Add(publicPaths, new Uri(origin + route.PublicPath).AbsolutePath, route);
            var encodedOutput = string.Join("/", route.RelativeOutputPath.Split('/').Select(Uri.EscapeDataString));
            Add(publicPaths, new Uri(origin + SiteRoute.ForFile(encodedOutput, baseUrl).PublicPath).AbsolutePath, route);
            if (encodedOutput == "index.html" || encodedOutput.EndsWith("/index.html", StringComparison.Ordinal))
            {
                var directory = encodedOutput == "index.html" ? string.Empty : encodedOutput[..^11];
                Add(publicPaths, new Uri(origin + SiteRoute.ForDirectoryIndex(directory, baseUrl).PublicPath).AbsolutePath, route);
            }
        }
    }

    // Returns null when provenance, safety, or catalog membership cannot be established.
    // The caller applies Markdown escaping separately through Syntamark destination edits.
    internal string? Resolve(string target, string sourceId, SiteRoute sourceHtmlRoute)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sourceHtmlRoute);
        if (target.Any(char.IsControl) || target.Contains('\\') || target != target.Trim())
            return null;
        if (!target.StartsWith('/') && Uri.TryCreate(target, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme is "http" or "https")
            {
                try
                {
                    StaticApiGuards.ValidateAbsoluteUrl(target);
                }
                catch (UriFormatException)
                {
                    return null;
                }
                return absolute.UserInfo.Length == 0 ? target : null;
            }
            // Preserve non-fetching contact links accepted by the existing quality policy.
            return absolute.Scheme is "mailto" or "tel"
                && SiteQualityUrlRules.IndependentIssue(target, false) == SiteQualityUrlIssue.None
                ? target : null;
        }
        if (target.StartsWith("//", StringComparison.Ordinal))
        {
            var expanded = site.Scheme + ":" + target;
            try
            {
                StaticApiGuards.ValidateAbsoluteUrl(expanded);
            }
            catch (UriFormatException)
            {
                return null;
            }
            return new Uri(expanded).UserInfo.Length == 0 ? expanded : null;
        }
        var source = NormalizeSource(sourceId);
        if (source is null || !knownSources.Contains(source)
            || (sources.TryGetValue(source, out var sourceRoute) && sourceRoute is null)
            || !publicPaths.TryGetValue(new Uri(origin + sourceHtmlRoute.PublicPath).AbsolutePath, out var current)
            || current is null || !current.Equals(sourceHtmlRoute))
            return null;
        var separator = target.IndexOfAny(['?', '#']);
        var path = separator < 0 ? target : target[..separator];
        var suffix = separator < 0 ? string.Empty : target[separator..];
        if (path.Length == 0)
            return origin + sourceHtmlRoute.PublicPath + suffix;
        if (!path.StartsWith('/'))
        {
            var decoded = DecodeRelativePath(path);
            if (decoded is null)
                return null;
            var slash = source.LastIndexOf('/');
            var candidate = NormalizeSource((slash < 0 ? string.Empty : source[..(slash + 1)]) + decoded);
            if (candidate is not null && sources.TryGetValue(candidate, out var mapped))
                return mapped is null ? null : origin + mapped.PublicPath + suffix;
            // A source document must resolve through its actual publication registration.
            if (Path.GetExtension(decoded).Equals(".md", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(decoded).Equals(".mdx", StringComparison.OrdinalIgnoreCase))
                return null;
        }
        if (SiteQualityUrlRules.Resolve(path, false, new Uri(origin + sourceHtmlRoute.PublicPath), out var address)
            != SiteQualityUrlIssue.None || address.GetLeftPart(UriPartial.Authority) != origin)
            return null;
        return publicPaths.TryGetValue(address.AbsolutePath, out var route) && route is not null
            ? origin + route.PublicPath + suffix : null;
    }

    private static void Add(Dictionary<string, SiteRoute?> catalog, string key, SiteRoute value)
    {
        if (!catalog.TryAdd(key, value) && (catalog[key] is not { } existing || !existing.Equals(value)))
            catalog[key] = null;
    }

    private static string? DecodeRelativePath(string path)
    {
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = Uri.UnescapeDataString(segments[i]);
            if (segment.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || segment.Any(char.IsControl))
                return null;
            segments[i] = segment;
        }
        return string.Join('/', segments);
    }

    private static string? NormalizeSource(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            return null;
        var path = value.Replace('\\', '/');
        if (path.Contains("://", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return null;
        var rooted = path.StartsWith('/');
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count == 0 || segments[^1].EndsWith(':'))
                    return null;
                segments.RemoveAt(segments.Count - 1);
            }
            else
                segments.Add(segment);
        }
        return segments.Count == 0 ? null : (rooted ? "/" : string.Empty) + string.Join('/', segments);
    }
}
