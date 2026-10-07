using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: MigrationReplay <run-config.json>");
    return 2;
}

var configPath = Path.GetFullPath(args[0]);
var config = JsonSerializer.Deserialize<ReplayConfig>(await File.ReadAllTextAsync(configPath), jsonOptions)
    ?? throw new InvalidDataException("Candidate replay config is empty.");
if (config.Action == "serve-check")
{
    using var candidate = JsonDocument.Parse(await File.ReadAllBytesAsync(config.CandidateReportPath));
    var root = candidate.RootElement;
    var generatedRoutes = root.GetProperty("generatedRoutes").EnumerateArray().Select(route => new ServedRoute(
        route.GetProperty("publicPath").GetString()!,
        route.GetProperty("relativeOutputPath").GetString()!)).ToArray();
    var selectedRoutes = root.GetProperty("selectedExpectedRoutes").EnumerateArray()
        .Select(route => new ServedPage(
            route.GetProperty("route").GetString()!,
            route.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString()! : "doc"))
        .ToArray();
    var outputRoot = root.GetProperty("candidateOutputDirectory").GetString()!;
    var serveReport = await ServeAndProbeAsync(outputRoot, generatedRoutes, selectedRoutes);
    await WriteJsonAsync(config.ServeReportPath, new
    {
        schemaVersion = "1.0",
        siteId = config.SiteId,
        candidateOutputHash = root.GetProperty("candidateBuild").GetProperty("actualOutputHash").GetString(),
        operations = serveReport,
    });
    Console.WriteLine($"serve report: {Path.GetFullPath(config.ServeReportPath)}");
    return 0;
}
if (config.Action != "candidate-build") throw new ArgumentException($"Unsupported replay action '{config.Action}'.");

var source = Path.GetFullPath(config.SourceDirectory);
var oracle = ReadOracle(config.RouteOracle);
var routeSourceMap = ReadSourceMap(config.RouteSourceMap);
var options = new DocusaurusMigrationOptions(config.BaseUrl, config.DefaultLocale)
{
    CompareNormalizedPageSet = true,
};
var fullReport = DocusaurusMigrationReport.AnalyzeWithOracle(source, oracle, options);
await WriteJsonAsync(config.FullReportPath, fullReport);
await WriteJsonAsync(config.ManualPatchReportPath, CreateManualPatchReport(fullReport));
var selected = SelectCandidatePages(fullReport, routeSourceMap, config.CandidatePageLimit);
if (selected.Count == 0)
    throw new InvalidOperationException("No clean document routes were available for the bounded candidate build.");

var selectedPaths = selected.Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
var candidateOracle = new MigrationRouteOracle(
    oracle.Routes.Select(route => route.Category == MigrationRouteCategory.Document
        && route.ExclusionReason is null && !selectedPaths.Contains(route.Path)
            ? new MigrationRouteOracleEntry(route.Path, route.Category, route.Locale,
                "Outside this site's bounded candidate page scope; original document route retained in the source denominator.")
            : route).ToArray(),
    oracle.SourceVersion,
    oracle.BasePath);

var convertedDirectory = Path.GetFullPath(config.ConvertedDirectory);
var candidateSource = Path.GetFullPath(config.CandidateSourceDirectory);
var candidateOutput = Path.GetFullPath(config.CandidateOutputDirectory);
if (!Directory.Exists(convertedDirectory)) throw new DirectoryNotFoundException($"The standalone migration-conversion stage is missing: {convertedDirectory}");
foreach (var path in new[] { candidateSource, candidateOutput })
    if (Path.Exists(path)) throw new IOException($"Candidate replay never overwrites an existing path: {path}");

await CreateCandidateSourceAsync(convertedDirectory, candidateSource, fullReport, selected);
var report = DocusaurusMigrationReport.AnalyzeWithOracle(candidateSource, candidateOracle, options);
var variantDirectories = report.Manifest.Variants
    .Select(variant => Path.Combine(candidateSource, variant.InputDirectory.Replace('/', Path.DirectorySeparatorChar)))
    .ToArray();
foreach (var directory in variantDirectories) Directory.CreateDirectory(directory);

var workerDirectory = Path.GetFullPath(config.WorkerDirectory);
var docs = new DocumentationSite(new MdxOptions(candidateSource, workerDirectory));
try
{
    var variants = report.Manifest.Variants.Select(variant => new DocumentVariant(
        variant.Version,
        variant.Locale,
        Path.Combine(candidateSource, variant.InputDirectory.Replace('/', Path.DirectorySeparatorChar)),
        variant.SuggestedRoutePrefix)).ToArray();
    if (variants.Length > 0) docs.AddCollection(new("guide", variants) { UseMdx = true });

    var hasBlog = Directory.Exists(Path.Combine(candidateSource, "blog"))
        && Directory.EnumerateFiles(Path.Combine(candidateSource, "blog"), "*", SearchOption.AllDirectories)
            .Any(path => Path.GetExtension(path) is ".md" or ".mdx");
    if (hasBlog)
    {
        docs.AddBlog(new MdxBlogCollection("news", Path.Combine(candidateSource, "blog"), "blog")
        {
            Authors = report.Manifest.BlogAuthors.ToDictionary(author => author.Id, author => new BlogAuthor(author.Name), StringComparer.Ordinal),
        });
    }

    var build = await new SiteGenerator().GenerateWithOptionsAsync(
        new SiteSettings { BaseUrl = config.BaseUrl, Title = config.SiteId },
        [],
        candidateOutput,
        clean: true,
        new() { Template = new DocsSiteTemplate { EnableSearch = true } },
        new()
        {
            Extensions = [docs],
            BuildTimestamp = DateTimeOffset.Parse(config.BuildTimestamp, System.Globalization.CultureInfo.InvariantCulture),
        },
        CancellationToken.None);

    var errors = build.BuildReport.Diagnostics
        .Where(diagnostic => diagnostic.Severity == LithoSharp.Diagnostics.SiteDiagnosticSeverity.Error)
        .ToArray();
    if (errors.Length > 0)
        throw new InvalidOperationException("Candidate build produced error diagnostics: "
            + string.Join(" | ", errors.Select(diagnostic => diagnostic.Message)));

    var builtRoutes = build.Routes.Select(route => route.PublicPath).ToHashSet(StringComparer.Ordinal);
    var selectedConvertedPaths = selected.Select(item => report.Files.Single(file => file.SourcePath == item.SourcePath))
        .Select(file => file.ConvertedPath ?? throw new InvalidOperationException($"Selected page '{file.SourcePath}' has no converted path."))
        .ToHashSet(StringComparer.Ordinal);
    var convertedToSource = report.Files.ToDictionary(
        file => file.ConvertedPath ?? file.SourcePath, file => file.SourcePath, StringComparer.Ordinal);
    var selectedExpectedRoutes = report.ConvertedRoutes
        .Where(route => selectedConvertedPaths.Contains(route.SourceFile))
        .Select(route => new
        {
            route = route.Route,
            sourcePath = convertedToSource.TryGetValue(route.SourceFile, out var sourcePath) ? sourcePath : route.SourceFile,
        })
        .Select(item => new
        {
            item.route,
            item.sourcePath,
            kind = item.sourcePath.StartsWith("blog/", StringComparison.Ordinal) ? "blog" : "doc",
        })
        .DistinctBy(item => item.route, StringComparer.Ordinal)
        .OrderBy(item => item.route, StringComparer.Ordinal)
        .ToArray();
    var missingBuiltRoutes = selectedExpectedRoutes.Select(item => item.route).Except(builtRoutes, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    if (missingBuiltRoutes.Length > 0)
        throw new InvalidOperationException("The generated candidate omitted selected routes: " + string.Join(", ", missingBuiltRoutes));

    var actualOutputHash = await HashTreeAsync(candidateOutput);
    var unAppliedManualChanges = fullReport.ComponentChanges.Select(change => new
    {
        sourcePath = change.SourcePath,
        component = change.Component,
        kind = change.Kind.ToString(),
        functionalEquivalence = change.FunctionalEquivalence.ToString(),
        sourceFingerprint = fullReport.Files.Single(file => file.SourcePath == change.SourcePath).SourceFingerprint,
        beforeSha256 = fullReport.Files.Single(file => file.SourcePath == change.SourcePath).SourceFingerprint,
        afterSha256 = (string?)null,
        reason = change.Note,
        status = "not-applied",
        rerunCondition = "Apply an explicit reviewed patch, then rerun analysis against the changed source hash.",
    }).ToArray();

    var result = new
    {
        schemaVersion = "1.0",
        siteId = config.SiteId,
        state = "passed",
        fullSourceHash = fullReport.RouteComparison.SourceHash,
        fullMigrationExitCode = fullReport.ExitCode,
        fullRawRouteComparisonStatus = fullReport.RouteComparison.RawStatus.ToString(),
        sourceVersion = report.RouteComparison.SourceVersion,
        sourceHash = report.RouteComparison.SourceHash,
        routeOracleHash = report.RouteComparison.RouteOracleHash,
        migrationExitCode = report.ExitCode,
        rawRouteComparison = new
        {
            status = report.RouteComparison.RawStatus.ToString(),
            missing = report.MissingRoutes,
            extra = report.ExtraRoutes,
            sourceRouteCount = report.RouteComparison.SourceRouteCount,
            targetRouteCount = report.RouteComparison.TargetRouteCount,
        },
        candidatePageSet = new
        {
            status = report.RouteComparison.NormalizedPageSet.Status.ToString(),
            sourcePageCount = report.RouteComparison.NormalizedPageSet.SourcePageCount,
            normalizedSourcePageCount = report.RouteComparison.NormalizedPageSet.NormalizedSourcePageCount,
            targetPageCount = report.RouteComparison.NormalizedPageSet.TargetPageCount,
            normalizedTargetPageCount = report.RouteComparison.NormalizedPageSet.NormalizedTargetPageCount,
            missing = report.RouteComparison.NormalizedPageSet.MissingRoutes,
            extra = report.RouteComparison.NormalizedPageSet.ExtraRoutes,
            exclusions = report.RouteComparison.ExclusionRules,
            normalizationRules = report.RouteComparison.NormalizedPageSet.Rules,
        },
        selectedPages = selected.Select(item => new { item.Path, item.SourcePath, item.Locale, item.Version, item.Format }).ToArray(),
        selectedPageLimit = config.CandidatePageLimit,
        candidateBuild = new
        {
            diagnosticCount = build.BuildReport.Diagnostics.Count,
            warningCount = build.BuildReport.Diagnostics.Count(diagnostic => diagnostic.Severity == LithoSharp.Diagnostics.SiteDiagnosticSeverity.Warning),
            generatedRouteCount = build.Routes.Count,
            selectedRouteCount = selectedExpectedRoutes.Length,
            actualOutputHash,
            compiledMdxModules = docs.MdxMetrics.CompiledModules,
            renderedMdxPages = docs.MdxMetrics.RenderedPages,
        },
        candidateOutputDirectory = candidateOutput,
        generatedRoutes = build.Routes.Select(route => new { publicPath = route.PublicPath, relativeOutputPath = route.RelativeOutputPath }).ToArray(),
        selectedExpectedRoutes,
        manualComponentChanges = unAppliedManualChanges,
        manualPatchReport = Path.GetFullPath(config.ManualPatchReportPath),
        knownLimits = new[]
        {
            "The candidate build is bounded to the selected clean document/blog pages; excluded source routes remain visible in the route denominator.",
            "Manual component differences are recorded but not applied automatically.",
            "Browser interaction/hydration is not exercised by the static HTTP smoke.",
        },
    };

    var reportPath = Path.GetFullPath(config.CandidateReportPath);
    await WriteJsonAsync(reportPath, result);
    Console.WriteLine($"candidate report: {reportPath}");
}
finally
{
    await docs.DisposeAsync();
}

return 0;

static MigrationRouteOracle ReadOracle(string filePath)
{
    using var json = JsonDocument.Parse(File.ReadAllBytes(filePath));
    var root = json.RootElement;
    var routes = root.GetProperty("routes").EnumerateArray().Select(entry => new MigrationRouteOracleEntry(
        entry.GetProperty("path").GetString()!,
        Enum.Parse<MigrationRouteCategory>(NormalizeEnum(entry.GetProperty("kind").GetString()!), ignoreCase: true),
        entry.TryGetProperty("locale", out var locale) && locale.ValueKind == JsonValueKind.String ? locale.GetString() : null,
        entry.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null)).ToArray();
    var sourceVersion = root.TryGetProperty("sourceVersion", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
    var basePath = root.TryGetProperty("basePath", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
    return new MigrationRouteOracle(routes, sourceVersion, basePath);
}

static object CreateManualPatchReport(DocusaurusMigrationReport report)
{
    var components = report.ComponentChanges.ToDictionary(change => (change.SourcePath, change.Component));
    var unapplied = report.Files.SelectMany(file => file.Issues
            .Where(issue => issue.ManualStep is not null)
            .Select(issue =>
            {
                var component = components.Values.FirstOrDefault(value => value.SourcePath == file.SourcePath
                    && (issue.Message.Contains(value.Component, StringComparison.Ordinal)
                        || value.Note.Contains(issue.Message, StringComparison.Ordinal)));
                return new
                {
                    sourcePath = file.SourcePath,
                    sourceHash = file.SourceFingerprint,
                    before = new { line = issue.Location.Line, diagnosticId = issue.Id, summary = issue.Message },
                    after = (object?)null,
                    reason = issue.ManualStep,
                    functionalChange = component is null ? null : new
                    {
                        kind = component.Kind.ToString(),
                        equivalence = component.FunctionalEquivalence.ToString(),
                        note = component.Note,
                    },
                    status = "not-applied",
                    rerunCondition = $"Re-run migration after the manual edit; the source must be reviewed against fingerprint {file.SourceFingerprint}.",
                };
            })).ToArray();
    return new { schemaVersion = "1.0", appliedPatches = Array.Empty<object>(), unappliedManualActions = unapplied };
}

async Task WriteJsonAsync(string path, object value)
{
    var full = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllTextAsync(full, JsonSerializer.Serialize(value, jsonOptions)).ConfigureAwait(false);
}

static string NormalizeEnum(string value) => string.Concat(value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
    .Select((part, index) => index == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

static IReadOnlyList<RouteSource> ReadSourceMap(string filePath)
{
    using var json = JsonDocument.Parse(File.ReadAllBytes(filePath));
    return json.RootElement.GetProperty("routes").EnumerateArray().Select(entry => new RouteSource(
        entry.GetProperty("path").GetString()!,
        entry.TryGetProperty("sourcePath", out var sourcePath) && sourcePath.ValueKind == JsonValueKind.String ? sourcePath.GetString() : null,
        entry.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String ? version.GetString() : null,
        entry.TryGetProperty("locale", out var locale) && locale.ValueKind == JsonValueKind.String ? locale.GetString() : null,
        entry.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String ? format.GetString() : null)).ToArray();
}

static List<RouteSource> SelectCandidatePages(
    DocusaurusMigrationReport report,
    IReadOnlyList<RouteSource> sourceMap,
    int limit)
{
    var reportFiles = report.Files.ToDictionary(file => file.SourcePath, StringComparer.Ordinal);
    var candidates = sourceMap.Where(route => route.SourcePath is not null
            && reportFiles.TryGetValue(route.SourcePath, out var file)
            && file.Verdict is MigrationVerdict.Automatic or MigrationVerdict.Convertible
            && file.ComponentChanges.Count == 0)
        .OrderBy(route => route.Path, StringComparer.Ordinal)
        .ToArray();
    var selected = new List<RouteSource>();
    var selectedPaths = new HashSet<string>(StringComparer.Ordinal);

    void AddFirst(IEnumerable<RouteSource> group)
    {
        var candidate = group.FirstOrDefault(route => !selectedPaths.Contains(route.Path));
        if (candidate is null || selected.Count >= limit) return;
        selected.Add(candidate);
        selectedPaths.Add(candidate.Path);
    }

    foreach (var group in candidates.GroupBy(route => (route.Version, route.Locale))
                 .OrderBy(group => group.Key.Version, StringComparer.Ordinal).ThenBy(group => group.Key.Locale, StringComparer.Ordinal))
        AddFirst(group);

    if (!selected.Any(route => route.Format == "mdx")) AddFirst(candidates.Where(route => route.Format == "mdx"));
    if (!selected.Any(route => route.SourcePath!.StartsWith("blog/", StringComparison.Ordinal)))
        AddFirst(candidates.Where(route => route.SourcePath!.StartsWith("blog/", StringComparison.Ordinal)));
    if (!selected.Any(route => !route.SourcePath!.StartsWith("blog/", StringComparison.Ordinal)))
        AddFirst(candidates.Where(route => !route.SourcePath!.StartsWith("blog/", StringComparison.Ordinal)));
    foreach (var candidate in candidates)
    {
        if (selected.Count >= limit) break;
        if (selectedPaths.Add(candidate.Path)) selected.Add(candidate);
    }
    return selected;
}

static Task CreateCandidateSourceAsync(
    string convertedDirectory,
    string candidateSource,
    DocusaurusMigrationReport candidateReport,
    IReadOnlyList<RouteSource> selected)
{
    Directory.CreateDirectory(candidateSource);
    var selectedSourceFiles = selected.Select(item => item.SourcePath!).ToHashSet(StringComparer.Ordinal);
    var selectedFiles = candidateReport.Files.Where(file => selectedSourceFiles.Contains(file.SourcePath))
        .Select(file => file.ConvertedPath ?? throw new InvalidOperationException($"Selected document '{file.SourcePath}' has no converted path."))
        .ToHashSet(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(convertedDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var relative = Path.GetRelativePath(convertedDirectory, file).Replace('\\', '/');
        var extension = Path.GetExtension(relative);
        var isDocument = extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mdx", StringComparison.OrdinalIgnoreCase);
        if (isDocument && !selectedFiles.Contains(relative)) continue;
        if (!isDocument && !IsCandidateSupportFile(relative)) continue;
        var target = Path.Combine(candidateSource, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: false);
    }
    return Task.CompletedTask;
}

static bool IsCandidateSupportFile(string relative) =>
    relative.StartsWith("docs/", StringComparison.Ordinal)
    || relative.StartsWith("blog/", StringComparison.Ordinal)
    || relative.StartsWith("versioned_docs/", StringComparison.Ordinal)
    || relative.StartsWith("versioned_sidebars/", StringComparison.Ordinal)
    || relative.StartsWith("i18n/", StringComparison.Ordinal)
    || relative.StartsWith("static/", StringComparison.Ordinal)
    || relative == "versions.json"
    || relative == "package.json"
    || relative.StartsWith("sidebars.", StringComparison.Ordinal)
    || relative.StartsWith("docusaurus.config", StringComparison.Ordinal);

static async Task<object> ServeAndProbeAsync(
    string outputRoot,
    IReadOnlyList<ServedRoute> routes,
    IReadOnlyList<ServedPage> selectedRoutes)
{
    var routeFiles = routes.ToDictionary(route => route.PublicPath, route => route.RelativeOutputPath, StringComparer.Ordinal);
    using var listener = new HttpListener();
    var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start();
    var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();
    var origin = $"http://127.0.0.1:{port}/";
    listener.Prefixes.Add(origin);
    listener.Start();
    using var stop = new CancellationTokenSource();
    var serving = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            var requestPath = context.Request.Url?.AbsolutePath ?? "/";
            if (routeFiles.TryGetValue(requestPath, out var relative))
            {
                var filePath = Path.GetFullPath(Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                var prefix = Path.GetFullPath(outputRoot) + Path.DirectorySeparatorChar;
                if (filePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(filePath))
                {
                    var bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = ContentType(filePath);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                }
                else context.Response.StatusCode = 404;
            }
            else context.Response.StatusCode = 404;
            context.Response.Close();
        }
    }, stop.Token);

    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var pages = new List<object>();
        var navigationLinks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in selectedRoutes)
        {
            var route = page.Route;
            using var response = await client.GetAsync(new Uri(new Uri(origin), route.TrimStart('/'))).ConfigureAwait(false);
            var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"Serve smoke returned {(int)response.StatusCode} for '{route}'.");
            if (!html.Contains("<html", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Served route '{route}' is not HTML.");
            if (page.Kind == "blog")
            {
                // Blog entries use the blog layout without a docs sidebar. The
                // publication date and title heading prove the dated article
                // rendered instead of an empty shell.
                RequireMarker(html, route, "<main", "article body");
                RequireMarker(html, route, "<h1", "title heading");
                RequireMarker(html, route, "<time", "publication date");
                pages.Add(new { route, kind = "blog", statusCode = (int)response.StatusCode, hasArticleBody = true, hasTitleHeading = true, hasPublishDate = true });
                continue;
            }

            var sidebar = Regex.Match(html, "<aside\\b[^>]*id=[\"']docs-sidebar[\"'][^>]*>(?<body>[\\s\\S]*?)</aside>", RegexOptions.IgnoreCase);
            if (!sidebar.Success) throw new InvalidDataException($"Served route '{route}' has no documentation navigation sidebar.");
            foreach (Match link in Regex.Matches(sidebar.Groups["body"].Value, "\\bhref=[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase))
            {
                var href = WebUtility.HtmlDecode(link.Groups["url"].Value);
                if (!Uri.TryCreate(new Uri(origin), href, out var linkUri)
                    || !string.Equals(linkUri.Host, "127.0.0.1", StringComparison.Ordinal)
                    || !routeFiles.ContainsKey(linkUri.AbsolutePath)) continue;
                navigationLinks.Add(linkUri.AbsolutePath);
            }
            pages.Add(new { route, kind = "doc", statusCode = (int)response.StatusCode, hasNavigation = true });
        }

        foreach (var link in navigationLinks)
        {
            using var response = await client.GetAsync(new Uri(new Uri(origin), link.TrimStart('/'))).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new HttpRequestException($"Navigation link '{link}' returned {(int)response.StatusCode}.");
        }

        var stylesheet = routes.FirstOrDefault(route => route.RelativeOutputPath.EndsWith(".css", StringComparison.OrdinalIgnoreCase));
        object asset;
        if (stylesheet is null) asset = new { status = "not-found", reason = "Candidate output has no generated CSS route." };
        else
        {
            using var response = await client.GetAsync(new Uri(new Uri(origin), stylesheet.PublicPath.TrimStart('/'))).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"Serve smoke could not fetch stylesheet '{stylesheet.PublicPath}'.");
            asset = new { status = "passed", route = stylesheet.PublicPath, statusCode = (int)response.StatusCode };
        }

        var searchIndex = routes.FirstOrDefault(route => route.RelativeOutputPath.EndsWith("search-index.json", StringComparison.OrdinalIgnoreCase));
        object search;
        if (searchIndex is null) search = new { status = "not-generated", reason = "No search index route was generated for this candidate." };
        else
        {
            using var response = await client.GetAsync(new Uri(new Uri(origin), searchIndex.PublicPath.TrimStart('/'))).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new HttpRequestException($"Serve smoke could not fetch search index '{searchIndex.PublicPath}'.");
            var searchJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var searchData = JsonDocument.Parse(searchJson);
            var mappedSelectedPages = selectedRoutes.Count(page => searchJson.Contains(page.Route, StringComparison.Ordinal));
            if (mappedSelectedPages == 0) throw new InvalidDataException("The served search index contains none of the selected candidate page routes.");
            search = new { status = "passed", route = searchIndex.PublicPath, statusCode = (int)response.StatusCode, mappedSelectedPages };
        }

        return new { status = "passed", origin, pages, navigation = new { checkedLinks = navigationLinks.Count, status = "passed" }, asset, search };
    }
    finally
    {
        stop.Cancel();
        listener.Stop();
        try { await serving.ConfigureAwait(false); }
        catch (HttpListenerException) { }
    }
}

static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".css" => "text/css; charset=utf-8",
    ".js" => "text/javascript; charset=utf-8",
    ".json" => "application/json; charset=utf-8",
    ".svg" => "image/svg+xml",
    ".png" => "image/png",
    ".jpg" or ".jpeg" => "image/jpeg",
    ".webp" => "image/webp",
    _ => "text/html; charset=utf-8",
};

static void RequireMarker(string html, string route, string marker, string what)
{
    if (!html.Contains(marker, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"Served route '{route}' has no {what} ({marker}).");
}

static async Task<string> HashTreeAsync(string root)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        hash.AppendData(Encoding.UTF8.GetBytes(relative + "\0"));
        await using var stream = File.OpenRead(file);
        hash.AppendData(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }
    return Convert.ToHexStringLower(hash.GetHashAndReset());
}

internal sealed class ReplayConfig
{
    public string Action { get; init; } = "candidate-build";
    public required string SiteId { get; init; }
    public required string SourceDirectory { get; init; }
    public required string RouteOracle { get; init; }
    public required string RouteSourceMap { get; init; }
    public required string FullReportPath { get; init; }
    public required string ManualPatchReportPath { get; init; }
    public required string ConvertedDirectory { get; init; }
    public required string CandidateSourceDirectory { get; init; }
    public required string CandidateOutputDirectory { get; init; }
    public required string CandidateReportPath { get; init; }
    public required string ServeReportPath { get; init; }
    public required string BaseUrl { get; init; }
    public required string DefaultLocale { get; init; }
    public required string WorkerDirectory { get; init; }
    public required string BuildTimestamp { get; init; }
    public int CandidatePageLimit { get; init; } = 12;
}

internal sealed record RouteSource(string Path, string? SourcePath, string? Version, string? Locale, string? Format);
internal sealed record ServedRoute(string PublicPath, string RelativeOutputPath);
internal sealed record ServedPage(string Route, string Kind);
