using System.Text.RegularExpressions;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;
using LithoSharp.Internal;

namespace LithoSharp.Quality;

internal static class SiteQualityValidator
{
    internal static async Task<SiteQualityReport> ValidateAsync(
        string baseUrl,
        IReadOnlyList<string> textFilePaths,
        Func<string, CancellationToken, Task<string>> readText,
        IReadOnlyDictionary<string, SiteRoute> artifactRoutes,
        IReadOnlySet<string> registeredAssets,
        IReadOnlyDictionary<string, SiteRoute> redirects,
        SiteQualityOptions options,
        CancellationToken cancellationToken,
        IReadOnlyList<LithoSharp.Build.BuildNode>? assetNodes = null,
        LithoSharp.Build.SiteHtmlFactsCache? factsCache = null,
        LithoSharp.Build.BuildHtmlFacts? sharedFacts = null,
        IReadOnlyDictionary<string, HtmlPageIdentity>? pageIdentities = null)
    {
        ArgumentNullException.ThrowIfNull(textFilePaths);
        ArgumentNullException.ThrowIfNull(readText);
        var root = SiteRoute.ForDirectoryIndex(string.Empty, baseUrl);
        var origin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        var pages = new Dictionary<string, ParsedPage>(StringComparer.Ordinal);
        var cssFiles = new Dictionary<string, CssReferenceSnapshot>(StringComparer.Ordinal);
        var diagnostics = new List<SiteDiagnostic>();
        var referencedAssets = new HashSet<string>(StringComparer.Ordinal);
        var resourceRoots = new HashSet<string>(StringComparer.Ordinal);
        var cssReferences = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var links = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var externalLinks = new Dictionary<string, SiteSourceLocation>(StringComparer.Ordinal);
        var anchorReferences = new List<HtmlAnchorReference>();
        var parseCount = 0;
        foreach (var path in textFilePaths.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await readText(path, cancellationToken).ConfigureAwait(false);
            if (path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            {
                var css = CssReferenceSnapshot.Create(path, new Uri(origin + artifactRoutes[path].PublicPath), content, cancellationToken);
                css.RequireComplete();
                cssFiles[path] = css;
            }
            if (!IsHtml(path, content)) continue;
            var route = artifactRoutes[path];
            var pageUri = new Uri(origin + route.PublicPath);
            var identity = pageIdentities is not null && pageIdentities.TryGetValue(path, out var registered)
                ? registered : new HtmlPageIdentity(path, route.PublicPath, null, null);
            if (identity.OutputPath != path || identity.PublicPath != route.PublicPath)
                throw new InvalidOperationException("The reference identity does not match its output route.");
            HtmlReferenceSnapshot snapshot;
            if (factsCache is not null)
            {
                var facts = await factsCache.GetAsync(content, cancellationToken).ConfigureAwait(false);
                if (sharedFacts?.RequiresQualityRaw == true)
                    snapshot = (await sharedFacts.GetQualitySnapshotAsync(content, identity, pageUri, cancellationToken).ConfigureAwait(false)).Snapshot;
                else snapshot = LithoSharp.Build.BuildHtmlFacts.BindDecodedQuality(facts, identity, pageUri, cancellationToken);
            }
            else if (sharedFacts is not null)
            {
                var bound = await sharedFacts.GetQualitySnapshotAsync(content, identity, pageUri, cancellationToken).ConfigureAwait(false);
                snapshot = bound.Snapshot;
                if (bound.NewParse) parseCount++;
            }
            else
            {
                var facts = QualityHtmlFacts.Parse(content, cancellationToken);
                parseCount++;
                snapshot = LithoSharp.Build.BuildHtmlFacts.BindDecodedQuality(facts, identity, pageUri, cancellationToken);
            }
            snapshot.RequireComplete();
            pages.Add(path, new ParsedPage(snapshot, snapshot.Anchors.Select(anchor => anchor.Decoded).ToHashSet(StringComparer.Ordinal)));
            links.Add(path, new HashSet<string>(StringComparer.Ordinal));
        }

        foreach (var pair in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pair.Key;
            var page = pair.Value;
            var snapshot = page.Snapshot;
            if (snapshot.Canonicals.Count != 1)
                Add("LSQ003", SiteDiagnosticSeverity.Error, "A page must declare exactly one canonical URL.", path);
            else
            {
                var canonical = snapshot.Canonicals[0];
                var expected = redirects.TryGetValue(path, out var target) ? target.RelativeOutputPath : path;
                var address = canonical.Address;
                if (string.IsNullOrWhiteSpace(canonical.Href) || address is null
                    || address.Query.Length != 0 || address.Fragment.Length != 0
                    || !TryInternalPath(address, out var canonicalPath) || canonicalPath != expected)
                    Add("LSQ003", SiteDiagnosticSeverity.Error, "The canonical URL must identify this page's public route.", path,
                        snapshot.Positions[canonical.ElementOrdinal]);
            }

            if (!redirects.ContainsKey(path))
            {
                if (string.IsNullOrWhiteSpace(snapshot.Title)) AddMissing("title");
                foreach (var (attribute, name) in new[] { ("name", "description"), ("property", "og:title"), ("property", "og:description"), ("property", "og:url") })
                    if (!snapshot.Metadata.Any(meta => string.Equals(attribute == "name" ? meta.Name : meta.Property,
                        name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(meta.Content))) AddMissing(name);
            }

            foreach (var reference in snapshot.References)
            {
                if (reference.Role is HtmlReferenceRole.Base or HtmlReferenceRole.Canonical) continue;
                CheckReference(reference, path, snapshot.Positions[reference.ElementOrdinal]);
            }
            foreach (var reference in snapshot.CssReferences)
                CheckReference(reference, path, snapshot.Positions[reference.ElementOrdinal]);

            void AddMissing(string name) => Add("LSQ010", SiteDiagnosticSeverity.Warning, $"SEO field '{name}' is missing or empty.", path);
        }

        foreach (var path in textFilePaths.Where(path => path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            foreach (var reference in cssFiles[path].References) CheckReference(reference, path, null);

        foreach (var group in pages.Where(pair => !redirects.ContainsKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value.Snapshot.Title))
                .GroupBy(pair => Regex.Replace(pair.Value.Snapshot.Title.Trim(), "\\s+", " "), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
                foreach (var page in group) Add("LSQ009", SiteDiagnosticSeverity.Warning, $"Page title '{group.Key}' is duplicated.", page.Key);

        if (options.CheckOrphans)
        {
            var byId = assetNodes?.ToDictionary(node => node.Id);
            var owners = assetNodes?.SelectMany(node => node.Artifacts.Select(artifact => (artifact.RelativeOutputPath, Node: node)))
                .ToDictionary(pair => pair.RelativeOutputPath, pair => pair.Node, StringComparer.Ordinal);
            var visitedNodes = new HashSet<LithoSharp.Build.BuildNodeId>();
            var assetQueue = new Queue<LithoSharp.Build.BuildNode>();
            var resources = new Queue<string>(resourceRoots);
            while (resources.Count != 0 || assetQueue.Count != 0)
            {
                while (resources.TryDequeue(out var resource))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (registeredAssets.Contains(resource)) referencedAssets.Add(resource);
                    if (cssReferences.TryGetValue(resource, out var references))
                        foreach (var reference in references) Reach(reference);
                    if (owners is not null && owners.TryGetValue(resource, out var owner)) assetQueue.Enqueue(owner);
                }
                while (assetQueue.TryDequeue(out var node))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!visitedNodes.Add(node.Id)) continue;
                    foreach (var artifact in node.Artifacts) Reach(artifact.RelativeOutputPath);
                    foreach (var dependency in node.Dependencies)
                        if (byId!.TryGetValue(dependency, out var target)) assetQueue.Enqueue(target);
                }
            }
            void Reach(string path)
            {
                if (resourceRoots.Add(path)) resources.Enqueue(path);
            }
            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            if (pages.ContainsKey("index.html")) pending.Enqueue("index.html");
            while (pending.TryDequeue(out var path))
            {
                if (!reachable.Add(path)) continue;
                if (links.TryGetValue(path, out var targets)) foreach (var target in targets) pending.Enqueue(target);
            }
            foreach (var path in pages.Keys.Where(path => !reachable.Contains(path) && !redirects.ContainsKey(path)))
                Add("LSQ007", SiteDiagnosticSeverity.Warning, "Page is not reachable from the site's home page.", path);
            foreach (var path in registeredAssets.Where(path => !referencedAssets.Contains(path)))
                Add("LSQ008", SiteDiagnosticSeverity.Warning, "Registered asset is not reachable through HTML, CSS or declared transitive references.", path);
        }
        if (options.ExternalLinks is not null)
            diagnostics.AddRange(await ExternalLinkChecker.CheckAsync(externalLinks, options.ExternalLinks, cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        return new SiteQualityReport(diagnostics)
        {
            HtmlParseCount = factsCache?.ParseCount ?? parseCount,
            HtmlFactsCacheHitCount = factsCache?.HitCount ?? 0,
            HtmlReferences = Array.AsReadOnly(pages.Values.Select(page => page.Snapshot).ToArray()),
            CssReferences = Array.AsReadOnly(cssFiles.Values.ToArray()),
            AnchorReferences = Array.AsReadOnly(anchorReferences.ToArray()),
        };

        void CheckReference(HtmlReference reference, string path, HtmlSnapshotPosition? element)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = reference.UnresolvedValue;
            var issue = reference.UrlIssue;
            var address = reference.ResolvedUrl!;
            if (issue != SiteQualityUrlIssue.None)
            {
                var message = issue switch
                {
                    SiteQualityUrlIssue.EmptyResource => "A resource URL is empty.",
                    SiteQualityUrlIssue.InvalidReference => $"Invalid URL '{value}'.",
                    _ => $"Unsupported URL '{value}'.",
                };
                Add("LSQ001", SiteDiagnosticSeverity.Error, message, path, element);
                return;
            }
            if (address.Scheme is "mailto" or "tel" or "data") return;
            if (!string.Equals(address.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))
            {
                externalLinks.TryAdd(new UriBuilder(address) { Fragment = string.Empty }.Uri.AbsoluteUri, Location(path, element));
                return;
            }
            if (!TryInternalPath(address, out var targetPath) || !artifactRoutes.ContainsKey(targetPath))
            {
                Add("LSQ001", SiteDiagnosticSeverity.Error, $"Internal target '{value}' does not exist in this site.", path, element);
                return;
            }
            if (cssFiles.ContainsKey(path))
            {
                if (!cssReferences.TryGetValue(path, out var references))
                    cssReferences.Add(path, references = new HashSet<string>(StringComparer.Ordinal));
                references.Add(targetPath);
            }
            else resourceRoots.Add(targetPath);
            if (reference.Navigation && pages.ContainsKey(targetPath) && links.TryGetValue(path, out var targets)) targets.Add(targetPath);
            if (address.Fragment.Length > 1 && pages.TryGetValue(targetPath, out var targetPage))
            {
                var fragment = Uri.UnescapeDataString(address.Fragment[1..]);
                anchorReferences.Add(new(path, targetPath, fragment));
                if (!targetPage.Anchors.Contains(fragment))
                    Add("LSQ002", SiteDiagnosticSeverity.Error, $"Anchor '{fragment}' does not exist in '{targetPath}'.", path, element);
            }
        }

        bool TryInternalPath(Uri address, out string path)
        {
            path = string.Empty;
            if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https") || address.UserInfo.Length != 0
                || !string.Equals(address.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)) return false;
            var publicPath = address.AbsolutePath;
            if (publicPath == root.PublicPath.TrimEnd('/')) publicPath += "/";
            if (!publicPath.StartsWith(root.PublicPath, StringComparison.Ordinal)) return false;
            var relative = publicPath[root.PublicPath.Length..];
            try
            {
                path = (relative.Length == 0 || relative.EndsWith('/')
                    ? SiteRoute.ForDirectoryIndex(relative) : SiteRoute.ForFile(relative)).RelativeOutputPath;
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException) { return false; }
        }

        void Add(string id, SiteDiagnosticSeverity severity, string message, string path, HtmlSnapshotPosition? element = null) =>
            diagnostics.Add(new SiteDiagnostic(id, severity, message, Location(path, element)));

        SiteSourceLocation Location(string path, HtmlSnapshotPosition? element) =>
            element is { Line: { } line, Column: { } column }
                ? new(path, line, column) : new(path);
    }

    internal static Task<SiteQualityReport> ValidateAsync(
        string baseUrl,
        IReadOnlyDictionary<string, string> textFiles,
        IReadOnlyDictionary<string, SiteRoute> artifactRoutes,
        IReadOnlySet<string> registeredAssets,
        IReadOnlyDictionary<string, SiteRoute> redirects,
        SiteQualityOptions options,
        CancellationToken cancellationToken) =>
        ValidateAsync(baseUrl, textFiles.Keys.ToArray(),
            (path, _) => Task.FromResult(textFiles[path]), artifactRoutes, registeredAssets,
            redirects, options, cancellationToken);

    internal static bool IsHtml(string path, string content) => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
        || content.AsSpan().TrimStart().StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
        || content.AsSpan().TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase);

    internal static bool HasRel(QualityHtmlElement element, string value) => (QualityHtmlFacts.Attribute(element, "rel") ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(value, StringComparer.OrdinalIgnoreCase);

    private sealed record ParsedPage(HtmlReferenceSnapshot Snapshot, HashSet<string> Anchors);
}
