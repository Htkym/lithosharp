using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;

namespace LithoSharp.Quality;

internal static class SiteQualityValidator
{
    private static readonly Regex CssUrls = new("url\\(\\s*(?:\"(?<url>[^\"]*)\"|'(?<url>[^']*)'|(?<url>[^)\\s]*))\\s*\\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static async Task<SiteQualityReport> ValidateAsync(
        string baseUrl,
        IReadOnlyList<string> textFilePaths,
        Func<string, CancellationToken, Task<string>> readText,
        IReadOnlyDictionary<string, SiteRoute> artifactRoutes,
        IReadOnlySet<string> registeredAssets,
        IReadOnlyDictionary<string, SiteRoute> redirects,
        SiteQualityOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(textFilePaths);
        ArgumentNullException.ThrowIfNull(readText);
        var root = SiteRoute.ForDirectoryIndex(string.Empty, baseUrl);
        var origin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        var pages = new Dictionary<string, ParsedPage>(StringComparer.Ordinal);
        var parser = new HtmlParser(new HtmlParserOptions { IsKeepingSourceReferences = true });
        var diagnostics = new List<SiteDiagnostic>();
        var referencedAssets = new HashSet<string>(StringComparer.Ordinal);
        var links = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var externalLinks = new Dictionary<string, SiteSourceLocation>(StringComparer.Ordinal);
        foreach (var path in textFilePaths.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await readText(path, cancellationToken).ConfigureAwait(false);
            if (!IsHtml(path, content)) continue;
            var route = artifactRoutes[path];
            using var document = await parser.ParseDocumentAsync(content, cancellationToken).ConfigureAwait(false);
            var pageUri = new Uri(origin + route.PublicPath);
            var resolutionBase = pageUri;
            var baseHref = document.QuerySelector("base[href]")?.GetAttribute("href");
            if (baseHref is not null && Uri.TryCreate(pageUri, baseHref, out var declaredBase)) resolutionBase = declaredBase;
            pages.Add(path, new ParsedPage(route, resolutionBase,
                document.QuerySelectorAll("[id],a[name]").SelectMany(element =>
                    new[] { element.GetAttribute("id"), element.LocalName == "a" ? element.GetAttribute("name") : null })
                .Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).ToHashSet(StringComparer.Ordinal),
                document.Title));
            links.Add(path, new HashSet<string>(StringComparer.Ordinal));
        }

        foreach (var pair in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pair.Key;
            var page = pair.Value;
            var content = await readText(path, cancellationToken).ConfigureAwait(false);
            using var document = await parser.ParseDocumentAsync(content, cancellationToken).ConfigureAwait(false);
            var canonicalElements = document.QuerySelectorAll("link")
                .Where(element => HasRel(element, "canonical")).ToArray();
            if (canonicalElements.Length != 1)
                Add("LSQ003", SiteDiagnosticSeverity.Error, "A page must declare exactly one canonical URL.", path);
            else
            {
                var canonical = canonicalElements[0].GetAttribute("href");
                var expected = redirects.TryGetValue(path, out var target) ? target.RelativeOutputPath : path;
                if (string.IsNullOrWhiteSpace(canonical) || !Uri.TryCreate(page.ResolutionBase, canonical, out var address)
                    || address.Query.Length != 0 || address.Fragment.Length != 0
                    || !TryInternalPath(address, out var canonicalPath) || canonicalPath != expected)
                    Add("LSQ003", SiteDiagnosticSeverity.Error, "The canonical URL must identify this page's public route.", path, canonicalElements[0]);
            }

            if (!redirects.ContainsKey(path))
            {
                if (string.IsNullOrWhiteSpace(document.Title)) AddMissing("title");
                foreach (var (attribute, name) in new[] { ("name", "description"), ("property", "og:title"), ("property", "og:description"), ("property", "og:url") })
                    if (!document.QuerySelectorAll("meta").Any(element =>
                        string.Equals(element.GetAttribute(attribute), name, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(element.GetAttribute("content")))) AddMissing(name);
            }

            foreach (var element in document.QuerySelectorAll("[href],[src],[srcset],[poster],object[data],form[action],meta[property]"))
            {
                if (element.LocalName == "base" || HasRel(element, "canonical")) continue;
                if (element.HasAttribute("href")) Check(element.GetAttribute("href")!, element.LocalName is "a" or "area", element.LocalName is not ("a" or "area"), element);
                if (element.HasAttribute("src")) Check(element.GetAttribute("src")!, false, true, element);
                if (element.HasAttribute("poster")) Check(element.GetAttribute("poster")!, false, true, element);
                if (element.LocalName == "object" && element.HasAttribute("data")) Check(element.GetAttribute("data")!, false, true, element);
                if (element.LocalName == "form" && element.HasAttribute("action")) Check(element.GetAttribute("action")!, true, false, element);
                if (element.HasAttribute("srcset"))
                    foreach (var candidate in SrcsetUrls(element.GetAttribute("srcset")!)) Check(candidate, false, true, element);
                if (string.Equals(element.GetAttribute("property"), "og:image", StringComparison.OrdinalIgnoreCase))
                    Check(element.GetAttribute("content") ?? string.Empty, false, true, element);
            }
            foreach (var element in document.QuerySelectorAll("style,[style]"))
                CheckCss(element.LocalName == "style" ? element.TextContent : element.GetAttribute("style")!, page.ResolutionBase, path, element);

            void AddMissing(string name) => Add("LSQ010", SiteDiagnosticSeverity.Warning, $"SEO field '{name}' is missing or empty.", path);
            void Check(string value, bool navigation, bool resource, IElement element) =>
                CheckReference(value, page.ResolutionBase, path, navigation, resource, element);
        }

        foreach (var path in textFilePaths.Where(path => path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            CheckCss(await readText(path, cancellationToken).ConfigureAwait(false), new Uri(origin + artifactRoutes[path].PublicPath), path, null);

        foreach (var group in pages.Where(pair => !redirects.ContainsKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value.Title))
                .GroupBy(pair => Regex.Replace(pair.Value.Title!.Trim(), "\\s+", " "), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
                foreach (var page in group) Add("LSQ009", SiteDiagnosticSeverity.Warning, $"Page title '{group.Key}' is duplicated.", page.Key);

        if (options.CheckOrphans)
        {
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
                Add("LSQ008", SiteDiagnosticSeverity.Warning, "Registered asset has no HTML or CSS reference.", path);
        }
        if (options.ExternalLinks is not null)
            diagnostics.AddRange(await ExternalLinkChecker.CheckAsync(externalLinks, options.ExternalLinks, cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        return new SiteQualityReport(diagnostics);

        void CheckCss(string css, Uri address, string path, IElement? element)
        {
            // ponytail: inspect literal CSS url() values; generated dynamic assets must declare their dependency.
            foreach (Match match in CssUrls.Matches(css))
                CheckReference(match.Groups["url"].Value, address, path, false, true, element);
        }

        void CheckReference(string value, Uri resolutionBase, string path, bool navigation, bool resource, IElement? element)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resource && string.IsNullOrWhiteSpace(value))
            {
                Add("LSQ001", SiteDiagnosticSeverity.Error, "A resource URL is empty.", path, element);
                return;
            }
            if (!Uri.TryCreate(resolutionBase, value.Trim(), out var address))
            {
                Add("LSQ001", SiteDiagnosticSeverity.Error, $"Invalid URL '{value}'.", path, element);
                return;
            }
            if (address.Scheme is "mailto" or "tel" or "data") return;
            if (address.Scheme is not ("http" or "https") || address.UserInfo.Length != 0)
            {
                Add("LSQ001", SiteDiagnosticSeverity.Error, $"Unsupported URL '{value}'.", path, element);
                return;
            }
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
            if (registeredAssets.Contains(targetPath)) referencedAssets.Add(targetPath);
            if (navigation && pages.ContainsKey(targetPath) && links.TryGetValue(path, out var targets)) targets.Add(targetPath);
            if (address.Fragment.Length > 1 && pages.TryGetValue(targetPath, out var targetPage))
            {
                var fragment = Uri.UnescapeDataString(address.Fragment[1..]);
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

        void Add(string id, SiteDiagnosticSeverity severity, string message, string path, IElement? element = null) =>
            diagnostics.Add(new SiteDiagnostic(id, severity, message, Location(path, element)));
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

    private static bool HasRel(IElement element, string value) => (element.GetAttribute("rel") ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(value, StringComparer.OrdinalIgnoreCase);

    private static SiteSourceLocation Location(string path, IElement? element) => element?.SourceReference is { } source
        ? new SiteSourceLocation(path, source.Position.Line, source.Position.Column) : new SiteSourceLocation(path);

    private static IEnumerable<string> SrcsetUrls(string value)
    {
        var index = 0;
        while (index < value.Length)
        {
            while (index < value.Length && (char.IsWhiteSpace(value[index]) || value[index] == ',')) index++;
            var start = index;
            while (index < value.Length && !char.IsWhiteSpace(value[index])) index++;
            var url = value[start..index];
            if (url.EndsWith(',')) { yield return url.TrimEnd(','); continue; }
            if (url.Length != 0) yield return url;
            while (index < value.Length && value[index] != ',') index++;
        }
    }

    private sealed record ParsedPage(SiteRoute Route, Uri ResolutionBase, HashSet<string> Anchors, string? Title);
}
