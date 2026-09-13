using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;

namespace LithoSharp.Documentation;

/// <summary>The per-file migration judgment.</summary>
internal enum DocusaurusMigrationVerdict
{
    /// <summary>Copied unchanged and usable as-is.</summary>
    Automatic,
    /// <summary>Safely rewritten by the conversion. The replacement is recorded.</summary>
    Convertible,
    /// <summary>Needs an explicit human decision or edit. Ambiguous conversions stay diagnostic.</summary>
    ManualActionRequired,
    /// <summary>Cannot be converted. Reported with a reason.</summary>
    Unsupported,
}

/// <summary>One migration finding. Compatible (from the tooling plan) is the same no-change category as Automatic.</summary>
/// <param name="Id">A stable LSMIG diagnostic code.</param>
/// <param name="Severity">How the finding blocks publication.</param>
/// <param name="Message">What was found and why.</param>
/// <param name="Line">The 1-based source line.</param>
/// <param name="Replacement">The safe automatic rewrite, when the file is convertible.</param>
/// <param name="ManualStep">The required human operation, when the file needs one.</param>
internal sealed record DocusaurusMigrationIssue(string Id, SiteDiagnosticSeverity Severity, string Message, int Line, string? Replacement, string? ManualStep);

/// <summary>One analyzed input file and its conversion.</summary>
/// <param name="SourcePath">The source-relative forward-slash path.</param>
/// <param name="Verdict">The worst judgment for the file.</param>
/// <param name="ConvertedPath">The destination-relative path, or null when the file is reference-only.</param>
/// <param name="Issues">All findings, worst first.</param>
/// <param name="SourceFingerprint">The lowercase hex SHA-256 of the analyzed source bytes.</param>
/// <param name="Edits">The recorded mechanical rewrites in source line numbers. Empty when the file is copied or only diagnosed.</param>
internal sealed record DocusaurusMigrationFile(string SourcePath, DocusaurusMigrationVerdict Verdict, string? ConvertedPath, IReadOnlyList<DocusaurusMigrationIssue> Issues, string SourceFingerprint, IReadOnlyList<DocusaurusMigrationEdit> Edits);

/// <summary>One recorded mechanical rewrite. Line numbers use the analyzed source.</summary>
/// <param name="Kind">ReplaceLines rewrites StartLine through EndLine; InsertAfter inserts Text lines after StartLine (0 prepends).</param>
/// <param name="StartLine">The 1-based first line.</param>
/// <param name="EndLine">The 1-based last line. InsertAfter uses the anchor line.</param>
/// <param name="Text">The replacement line, or the inserted lines joined with "\n". Empty removes the lines.</param>
internal sealed record DocusaurusMigrationEdit(MigrationActionKind Kind, int StartLine, int EndLine, string Text);

/// <summary>One suggested collection variant wiring. Route prefixes are suggestions the site owner verifies.</summary>
internal sealed record DocusaurusMigrationVariant(string Version, string Locale, string InputDirectory, string SuggestedRoutePrefix);

/// <summary>One blog author suggested for explicit profile registration.</summary>
internal sealed record DocusaurusMigrationAuthor(string Id, string Name);

/// <summary>Site-level conversion guidance. JavaScript configuration is never executed.</summary>
internal sealed record DocusaurusMigrationManifest(
    IReadOnlyList<DocusaurusMigrationVariant> Variants,
    IReadOnlyDictionary<string, string> VersionedSidebars,
    IReadOnlyList<DocusaurusMigrationAuthor> BlogAuthors,
    IReadOnlyList<string> ManualSteps,
    IReadOnlyList<string> UnsupportedNotes);

/// <summary>One converted public route. Derived category indexes are excluded.</summary>
internal sealed record DocusaurusMigrationRoute(string Route, string SourceFile, string Kind);

/// <summary>Migration assumptions the caller states explicitly.</summary>
public sealed record DocusaurusMigrationOptions(
    string BaseUrl = "https://example.test/",
    string DefaultLocale = "en",
    string DocsRoutePrefix = "docs",
    string BlogRoutePrefix = "blog");

/// <summary>The analysis or conversion outcome.</summary>
internal sealed record DocusaurusMigrationResult(
    IReadOnlyList<DocusaurusMigrationFile> Files,
    DocusaurusMigrationManifest Manifest,
    IReadOnlyList<DocusaurusMigrationRoute> ConvertedRoutes,
    IReadOnlyList<string> MissingRoutes,
    IReadOnlyList<string> ExtraRoutes,
    int ExitCode,
    bool WroteOutput);

/// <summary>
/// Docusaurus migration analysis and conversion, reusable from Core, tests,
/// and the tool. The old read-only command and the new converting command run
/// this same processing. Dynamic JavaScript and TypeScript configuration is
/// never executed; statically undecidable wiring becomes manifest manual steps.
/// </summary>
internal static class DocusaurusMigration
{
    /// <summary>Analysis or conversion completed; verdicts and manual steps are in the report.</summary>
    public const int ExitClean = 0;
    /// <summary>The tool failed (input, output, or processing failure).</summary>
    public const int ExitFailure = 1;
    /// <summary>Unconvertible content is present. Exit 2 stays reserved for argument errors.</summary>
    public const int ExitUnconvertible = 3;

    internal const string ConfigNotExecuted = "LSMIG001";
    internal const string UnsupportedImport = "LSMIG002";
    internal const string UnsupportedPlugin = "LSMIG003";
    internal const string NeedsManualAction = "LSMIG004";
    internal const string Unconvertible = "LSMIG005";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex ImportRegex = new("(?:from\\s*|import\\s*)[\"'](?<name>@(?:docusaurus|theme)/[^\"']+)[\"']", RegexOptions.Compiled);
    private static readonly Regex BareImportRegex = new("^ {0,3}import\\s[^;\\n]*?\\bfrom\\s*[\"'](?<name>[^\"']+)[\"']|^ {0,3}import\\s*[\"'](?<name>[^\"']+)[\"']", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ImportStatementRegex = new("import\\s+(?<spec>[^;]+?)\\s+from\\s*[\"'](?<module>@(?:docusaurus|theme)/[^\"']+)[\"']", RegexOptions.Compiled);
    private static readonly Regex HookUseRegex = new("\\b(useBaseUrl|useDocusaurusContext)\\b", RegexOptions.Compiled);
    private static readonly Regex InlineImageRegex = new(@"!\[[^\]]*\]\((?<url>[^)\s]+)", RegexOptions.Compiled);
    private static readonly Regex ReferenceImageRegex = new(@"^\s{0,3}\[[^\]]+\]:\s*(?<url>\S+)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex BlogDateRegex = new("^(?<year>\\d{4})-(?<month>\\d{2})-(?<day>\\d{2})-(?<rest>.+)$", RegexOptions.Compiled);
    private static readonly Regex NestedBlogDateRegex = new("^(?<month>\\d{2})-(?<day>\\d{2})-(?<rest>.+)$", RegexOptions.Compiled);

    /// <summary>Blog front matter keys the migration understands. Tests pin this against MdxBlogFrontMatter.</summary>
    internal static readonly IReadOnlySet<string> BlogFrontMatterKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "title", "slug", "date", "summary", "authors", "tags", "draft", "unlisted", "publish_until", "feed_text", "image", "description",
    };

    /// <summary>Analyzes without writing. Nothing outside the source directory is touched.</summary>
    /// <param name="sourceDirectory">The Docusaurus site directory to analyze.</param>
    /// <param name="expectedRoutes">
    /// Expected routes in canonical <see cref="SiteRoute"/> public-path form
    /// (leading and trailing slash, percent-encoded). Comparison is ordinal-exact
    /// so representation variance surfaces as explicit missing/extra entries
    /// instead of passing silently.
    /// </param>
    /// <param name="options">Route prefixes and the default locale. Null uses defaults.</param>
    /// <param name="cancellationToken">Cancels the analysis.</param>
    public static DocusaurusMigrationResult Analyze(
        string sourceDirectory,
        IReadOnlyList<string>? expectedRoutes = null,
        DocusaurusMigrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Migration source not found: {sourceDirectory}.");
        return Finish(AnalyzeFiles(source, options ?? new(), cancellationToken), expectedRoutes, wroteOutput: false);
    }

    /// <summary>Converts into an explicit separate destination. Existing inputs are never overwritten.</summary>
    /// <param name="sourceDirectory">The Docusaurus site directory to convert.</param>
    /// <param name="destinationDirectory">The separate destination directory. It must not exist yet.</param>
    /// <param name="expectedRoutes">
    /// Expected routes in canonical <see cref="SiteRoute"/> public-path form
    /// (leading and trailing slash, percent-encoded). Comparison is ordinal-exact
    /// so representation variance surfaces as explicit missing/extra entries
    /// instead of passing silently.
    /// </param>
    /// <param name="options">Route prefixes and the default locale. Null uses defaults.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    public static async Task<DocusaurusMigrationResult> ConvertAsync(
        string sourceDirectory,
        string destinationDirectory,
        IReadOnlyList<string>? expectedRoutes = null,
        DocusaurusMigrationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Migration source not found: {sourceDirectory}.");
        if (Within(source, destination) || Within(destination, source))
            throw new ArgumentException("Migration source and destination must not overlap.");
        if (Path.Exists(destination)) throw new IOException("The migration destination already exists; conversion never overwrites it.");
        var plan = AnalyzeFiles(source, options ?? new(), cancellationToken);
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (relative, bytes) in plan.Outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }

            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
            await File.WriteAllBytesAsync(Path.Combine(staging, "migration-manifest.json"),
                JsonSerializer.SerializeToUtf8Bytes(plan.Manifest, json), cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(Path.Combine(staging, "migration-routes.json"),
                JsonSerializer.SerializeToUtf8Bytes(plan.Routes.Select(route => route.Route).ToArray(), json), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.Exists(destination)) throw new IOException("The migration destination already exists; conversion never overwrites it.");
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }

        return Finish(plan, expectedRoutes, wroteOutput: true);
    }

    private sealed record MigrationPlan(
        IReadOnlyList<DocusaurusMigrationFile> Files,
        DocusaurusMigrationManifest Manifest,
        IReadOnlyList<DocusaurusMigrationRoute> Routes,
        IReadOnlyList<KeyValuePair<string, byte[]>> Outputs);

    private static DocusaurusMigrationResult Finish(MigrationPlan plan, IReadOnlyList<string>? expectedRoutes, bool wroteOutput)
    {
        var actual = plan.Routes.Select(route => route.Route).ToArray();
        IReadOnlyList<string> missing = expectedRoutes is null
            ? []
            : expectedRoutes.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        IReadOnlyList<string> extra = expectedRoutes is null
            ? []
            : actual.Except(expectedRoutes, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var exit = plan.Files.Any(file => file.Verdict == DocusaurusMigrationVerdict.Unsupported) ? ExitUnconvertible : ExitClean;
        return new(plan.Files, plan.Manifest, plan.Routes, missing, extra, exit, wroteOutput);
    }

    private static MigrationPlan AnalyzeFiles(string source, DocusaurusMigrationOptions options, CancellationToken cancellationToken)
    {
        var files = new List<DocusaurusMigrationFile>();
        var outputs = new List<KeyValuePair<string, byte[]>>();
        var variants = new List<DocusaurusMigrationVariant>();
        var sidebars = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var authors = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var manualSteps = new List<string>();
        var unsupported = new List<string>();
        var routes = new List<DocusaurusMigrationRoute>();
        var versions = new List<string>();
        var versionDirs = new SortedSet<string>(StringComparer.Ordinal);
        var seenVariants = new HashSet<(string Version, string Locale, string Input)>();
        var seenIds = new HashSet<(string Input, string Id)>();

        // Docusaurus serves docs/ as "next" under docs/next/ whenever versions.json lists
        // versions, and serves versions.json[0] at the version-less docs/ path. Read the list
        // before analyzing documents so converted routes use the same prefixes. Malformed
        // content is reported again with file context during enumeration.
        var versionsPath = Path.Combine(source, "versions.json");
        if (File.Exists(versionsPath))
        {
            try
            {
                versions = ReadVersions(File.ReadAllBytes(versionsPath), static (_, _) => { });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                versions = [];
            }
        }

        void AddVariant(string version, string locale, string input, string prefix)
        {
            if (seenVariants.Add((version, locale, input)))
                variants.Add(new(version, locale, input, prefix));
        }

        foreach (var file in Enumerate(source, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            var segments = relative.Split('/');
            var name = segments[^1];
            var extension = Path.GetExtension(name);
            var issues = new List<DocusaurusMigrationIssue>();
            var verdict = DocusaurusMigrationVerdict.Automatic;
            string? converted = relative;
            var bytes = File.ReadAllBytes(file);
            var fingerprint = Fingerprint(bytes);
            IReadOnlyList<DocusaurusMigrationEdit> edits = [];

            void Raise(DocusaurusMigrationVerdict level, DocusaurusMigrationIssue issue)
            {
                issues.Add(issue);
                if (level > verdict) verdict = level;
            }

            if ((name.StartsWith("docusaurus.config", StringComparison.Ordinal) || name.StartsWith("sidebars.", StringComparison.Ordinal))
                && extension is not ".md" and not ".mdx")
            {
                Raise(DocusaurusMigrationVerdict.ManualActionRequired, new(ConfigNotExecuted, SiteDiagnosticSeverity.Warning,
                    "JavaScript configuration was not executed. Map routes, variants, sidebars and plugins explicitly to the C# preset.", 1,
                    null, "Define the equivalent C# collections, variants, and sidebars explicitly; see the migration manifest."));
                // Configuration files stay reference-only inputs.
                converted = null;
            }
            else if (relative == "versions.json")
            {
                versions = ReadVersions(bytes, Raise);
                if (verdict == DocusaurusMigrationVerdict.Automatic)
                    Raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                        "versions.json is copied and wired into the manifest version list.", 1,
                        "Copied to the destination with manifest variant entries.", "Register one docs variant per version from the manifest."));
            }
            else if (segments[0] == "versioned_sidebars" && extension == ".json")
            {
                var version = SidebarVersion(name);
                if (version is null || !IsJson(bytes))
                {
                    Raise(DocusaurusMigrationVerdict.Unsupported, new(Unconvertible, SiteDiagnosticSeverity.Error,
                        $"Versioned sidebar '{relative}' needs a 'version-<name>-sidebars.json' name with JSON content.", 1, null, null));
                    unsupported.Add($"{relative}: versioned sidebar is not statically readable.");
                }
                else
                {
                    sidebars[version] = relative;
                    Raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                        $"Versioned sidebar for '{version}' is copied; sidebar definitions stay explicit C#.", 1,
                        "Copied to the destination with a manifest entry.", $"Define the '{version}' sidebar in C# from '{relative}'."));
                }
            }
            else if (name is "_category_.json" or "_category_.yml" or "_category_.yaml")
            {
                if (!IsStructured(bytes, extension))
                {
                    Raise(DocusaurusMigrationVerdict.Unsupported, new(Unconvertible, SiteDiagnosticSeverity.Error,
                        $"Category file '{relative}' is not parseable.", 1, null, null));
                    unsupported.Add($"{relative}: category file is not parseable.");
                }
            }
            else if (relative is "blog/authors.yml" or "blog/authors.yaml")
            {
                foreach (var author in ReadAuthors(bytes, relative, Raise)) authors[author.Id] = author.Name;
                if (verdict == DocusaurusMigrationVerdict.Automatic)
                    Raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                        "Blog authors are copied into manifest author suggestions.", 1,
                        "Copied to the destination with manifest author entries.", "Register blog author profiles from the manifest."));
            }
            else if (extension is ".md" or ".mdx")
            {
                var document = AnalyzeDocument(relative, segments, bytes, options, versions, seenIds, authors, Raise, cancellationToken);
                converted = document.ConvertedPath;
                bytes = document.Bytes;
                edits = document.Edits;
                if (document.Route is not null) routes.Add(document.Route);
            }
            else if (ScanText(bytes, Raise) == TextScan.Unsupported)
            {
                unsupported.Add($"{relative}: unsupported Docusaurus construct.");
            }

            if (segments[0] == "versioned_docs" && segments.Length > 2 && segments[1].StartsWith("version-", StringComparison.Ordinal))
                versionDirs.Add(segments[1][8..]);
            files.Add(new(relative, verdict, converted,
                issues.OrderByDescending(issue => issue.Severity).ThenBy(issue => issue.Line).ToArray(),
                fingerprint, edits));
            if (converted is not null) outputs.Add(new(converted, bytes));
        }

        foreach (var version in versions)
            if (!versionDirs.Contains(version))
                RaiseFile(files, "versions.json", DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                    $"versions.json lists '{version}' without a versioned_docs directory.", 1, null, $"Add versioned_docs/version-{version}/ or remove the entry."));
        foreach (var version in versionDirs)
            if (!versions.Contains(version, StringComparer.Ordinal))
                manualSteps.Add($"versioned_docs/version-{version}/ has no versions.json entry; register its variant explicitly or add the entry.");

        foreach (var group in routes.GroupBy(route => route.Route, StringComparer.Ordinal).Where(group => group.Count() > 1))
            foreach (var route in group)
                RaiseFile(files, route.SourceFile, DocusaurusMigrationVerdict.Unsupported, new(Unconvertible, SiteDiagnosticSeverity.Error,
                    $"Converted route '{route.Route}' collides with another document.", 1, null, null));

        ValidateCategoryLinks(files, outputs, seenIds);

        var ordered = files.OrderBy(file => file.SourcePath, StringComparer.Ordinal).ToArray();

        if (ordered.Any(file => file.SourcePath.StartsWith("docs/", StringComparison.Ordinal)))
            AddVariant(versions.Count > 0 ? "next" : "current", options.DefaultLocale, "docs",
                versions.Count > 0 ? $"{options.DocsRoutePrefix}/next" : options.DocsRoutePrefix);
        foreach (var version in versionDirs)
            AddVariant(version, options.DefaultLocale, $"versioned_docs/version-{version}",
                string.Equals(version, versions.FirstOrDefault(), StringComparison.Ordinal)
                    ? options.DocsRoutePrefix
                    : $"{options.DocsRoutePrefix}/{version}");
        foreach (var (version, locale) in ordered
            .Where(file => file.SourcePath.StartsWith("i18n/", StringComparison.Ordinal))
            .Select(file => file.SourcePath.Split('/'))
            .Where(parts => parts.Length > 4 && parts[2] == "docusaurus-plugin-content-docs")
            .Select(I18nVariant)
            .Where(pair => pair.Version is not null)
            .Select(pair => (pair.Version!, pair.Locale))
            .Distinct())
        {
            var input = $"i18n/{locale}/docusaurus-plugin-content-docs/{(version == "current" ? "current" : "version-" + version)}";
            AddVariant(version, locale, input, $"{locale}/{options.DocsRoutePrefix}{(version == "current" ? "" : "/" + version)}");
        }

        manualSteps.Add("Register docs and blog collections with variant input directories and route prefixes from the manifest.");
        manualSteps.Add("Define explicit sidebars in C#; sidebars.js is never executed.");
        if (authors.Count > 0) manualSteps.Add("Register blog author profiles from the manifest author list.");
        if (ordered.Any(file => file.SourcePath.StartsWith("static/", StringComparison.Ordinal)))
            manualSteps.Add("Serve the converted static/ directory from the site root.");

        var manifest = new DocusaurusMigrationManifest(
            variants.OrderBy(variant => variant.Version, StringComparer.Ordinal).ThenBy(variant => variant.Locale, StringComparer.Ordinal).ToArray(),
            new SortedDictionary<string, string>(sidebars, StringComparer.Ordinal),
            authors.Select(pair => new DocusaurusMigrationAuthor(pair.Key, pair.Value)).ToArray(),
            manualSteps.ToArray(),
            unsupported.Order(StringComparer.Ordinal).ToArray());
        return new(ordered,
            manifest,
            routes.OrderBy(route => route.Route, StringComparer.Ordinal).ToArray(),
            outputs.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
    }

    private static (string? Version, string Locale) I18nVariant(string[] parts)
    {
        if (parts[3] == "current") return ("current", parts[1]);
        return parts[3].StartsWith("version-", StringComparison.Ordinal) ? (parts[3][8..], parts[1]) : (null, parts[1]);
    }

    private static void RaiseFile(List<DocusaurusMigrationFile> files, string path, DocusaurusMigrationVerdict level, DocusaurusMigrationIssue issue)
    {
        var index = files.FindIndex(file => file.SourcePath == path || file.ConvertedPath == path);
        if (index < 0) return;
        var file = files[index];
        files[index] = file with
        {
            Verdict = level > file.Verdict ? level : file.Verdict,
            Issues = file.Issues.Concat([issue]).OrderByDescending(item => item.Severity).ThenBy(item => item.Line).ToArray(),
        };
    }

    private static void ValidateCategoryLinks(
        List<DocusaurusMigrationFile> files,
        List<KeyValuePair<string, byte[]>> outputs,
        HashSet<(string Input, string Id)> seenIds)
    {
        // A category link to a missing document id fails the build while Docusaurus
        // tolerates it: neutralize dangling doc links so the category stays readable.
        // generated-index links always resolve; exotic block shapes stay manual-only.
        foreach (var file in files.Where(file => file.ConvertedPath is not null
            && file.SourcePath.Split('/')[^1] is "_category_.json" or "_category_.yml" or "_category_.yaml").ToArray())
        {
            var output = outputs.FindIndex(pair => pair.Key == file.ConvertedPath);
            if (output < 0 || !TryDecode(outputs[output].Value, out var text)) continue;
            if (file.SourcePath.EndsWith(".json", StringComparison.Ordinal))
            {
                // JSON link blocks keep their bytes; doc-type links still need human
                // verification (generated-index links always resolve).
                if (text.Contains("\"link\"", StringComparison.Ordinal) && text.Contains("\"doc\"", StringComparison.Ordinal))
                    RaiseFile(files, file.SourcePath, DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                        $"Category link in '{file.SourcePath}' needs human verification (JSON link blocks are not rewritten).", 1,
                        null, "Verify the category link target."));
                continue;
            }

            var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
            var drops = new List<int>();
            var exotic = new List<int>();
            for (var index = 0; index < lines.Count; index++)
            {
                if (!lines[index].StartsWith("link:", StringComparison.Ordinal)
                    || !string.IsNullOrWhiteSpace(lines[index]["link:".Length..])) continue;
                var end = index;
                while (end + 1 < lines.Count && lines[end + 1].Length > 0
                    && lines[end + 1][0] is ' ' or '\t') end++;
                var block = lines[(index + 1)..(end + 1)];
                var type = block.Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("type:", StringComparison.Ordinal));
                if (type is null || !type["type:".Length..].Trim().Equals("doc", StringComparison.Ordinal)) continue;
                var idLine = block.Select((line, offset) => (line, offset)).FirstOrDefault(pair => pair.line.TrimStart().StartsWith("id:", StringComparison.Ordinal));
                if (idLine == default || block.Any(line =>
                    {
                        var trimmed = line.Trim();
                        return trimmed.Contains(':') && !trimmed.StartsWith("type:", StringComparison.Ordinal)
                            && !trimmed.StartsWith("id:", StringComparison.Ordinal);
                    }))
                {
                    exotic.Add(index + 1);
                    continue;
                }

                var id = YamlScalar(idLine.line.Trim()["id:".Length..].Trim());
                var input = CategoryVariantInput(file.SourcePath);
                if (input is not null && seenIds.Contains((input, id))) continue;
                for (var line = index + 1; line <= end + 1; line++) drops.Add(line);
                var at = files.FindIndex(item => item.SourcePath == file.SourcePath);
                var current = files[at];
                files[at] = current with
                {
                    Verdict = DocusaurusMigrationVerdict.ManualActionRequired > current.Verdict
                        ? DocusaurusMigrationVerdict.ManualActionRequired : current.Verdict,
                    Issues = current.Issues.Concat([new DocusaurusMigrationIssue(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                        $"Category link id '{id}' matches no converted document; the link is removed, the category stays.", index + 1,
                        null, "Restore the link with a valid document id, or keep the unlinked category.")])
                        .OrderByDescending(item => item.Severity).ThenBy(item => item.Line).ToArray(),
                    Edits = current.Edits.Concat(drops.Skip(drops.Count - (end - index + 1))
                        .Select(line => new DocusaurusMigrationEdit(MigrationActionKind.ReplaceLines, line, line, ""))).ToArray(),
                };
            }

            foreach (var line in exotic)
                RaiseFile(files, file.SourcePath, DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                    $"Category link in '{file.SourcePath}' has an unexpected shape; verify its target.", line,
                    null, "Verify the category link target and shape."));
            if (drops.Count == 0) continue;
            foreach (var line in drops.OrderByDescending(line => line)) lines.RemoveAt(line - 1);
            outputs[output] = new(outputs[output].Key, StrictUtf8.GetBytes(string.Join(newline, lines)));
        }
    }

    private static string? CategoryVariantInput(string sourcePath)
    {
        var segments = sourcePath.Split('/');
        if (segments[0] == "docs") return "docs";
        if (segments[0] == "versioned_docs" && segments.Length > 1 && segments[1].StartsWith("version-", StringComparison.Ordinal))
            return $"versioned_docs/{segments[1]}";
        if (segments[0] == "i18n" && segments.Length > 4 && segments[2] == "docusaurus-plugin-content-docs"
            && (segments[3] == "current" || segments[3].StartsWith("version-", StringComparison.Ordinal)))
            return string.Join('/', segments[..4]);
        return null;
    }

    private static string YamlScalar(string value)
    {
        var text = value.Trim();
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
            return text[1..^1];
        var hash = text.IndexOf(" #", StringComparison.Ordinal);
        return (hash < 0 ? text : text[..hash]).Trim();
    }

    private sealed record AnalyzedDocument(string? ConvertedPath, byte[] Bytes, string Kind, DocusaurusMigrationRoute? Route, IReadOnlyList<DocusaurusMigrationEdit> Edits);

    private static string Fingerprint(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void AddLinkRewriteEdits(
        List<DocusaurusMigrationEdit> edits, string before, string after,
        SortedSet<int> drops, List<(int Line, string Text)> replacements)
    {
        if (string.Equals(before, after, StringComparison.Ordinal)) return;
        var beforeLines = before.Split(["\r\n", "\n"], StringSplitOptions.None);
        var afterLines = after.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (beforeLines.Length != afterLines.Length) return;
        var skip = new HashSet<int>(drops);
        foreach (var (line, _) in replacements) skip.Add(line);
        for (var index = 0; index < beforeLines.Length; index++)
            if (!skip.Contains(index + 1) && !string.Equals(beforeLines[index], afterLines[index], StringComparison.Ordinal))
                edits.Add(new(MigrationActionKind.ReplaceLines, index + 1, index + 1, afterLines[index]));
    }

    private static AnalyzedDocument AnalyzeDocument(
        string relative, string[] segments, byte[] bytes,
        DocusaurusMigrationOptions options, IReadOnlyList<string> versions, HashSet<(string Input, string Id)> seenIds, SortedDictionary<string, string> authors,
        Action<DocusaurusMigrationVerdict, DocusaurusMigrationIssue> raise,
        CancellationToken cancellationToken)
    {
        // scope is the variant- or collection-relative segments used for ids and slugs.
        string[] scope;
        string? variantInput;
        string kind;
        string prefix;
        if (segments[0] == "docs")
        {
            scope = segments[1..];
            variantInput = "docs";
            kind = "doc";
            // With versions.json present, docs/ is the unreleased "next" version.
            prefix = versions.Count > 0 ? $"{options.DocsRoutePrefix}/next" : options.DocsRoutePrefix;
        }
        else if (segments[0] == "blog")
        {
            scope = segments[1..];
            variantInput = null;
            kind = "blog";
            prefix = options.BlogRoutePrefix;
        }
        else if (segments[0] == "versioned_docs" && segments.Length > 2 && segments[1].StartsWith("version-", StringComparison.Ordinal))
        {
            scope = segments[2..];
            var version = segments[1][8..];
            variantInput = $"versioned_docs/version-{version}";
            kind = "doc";
            // versions.json[0] is served at the version-less path; later versions keep theirs.
            prefix = string.Equals(version, versions.FirstOrDefault(), StringComparison.Ordinal)
                ? options.DocsRoutePrefix
                : $"{options.DocsRoutePrefix}/{version}";
        }
        else if (segments[0] == "i18n" && segments.Length > 4 && segments[2] == "docusaurus-plugin-content-docs"
            && (segments[3] == "current" || segments[3].StartsWith("version-", StringComparison.Ordinal)))
        {
            scope = segments[4..];
            var version = segments[3] == "current" ? "current" : segments[3][8..];
            variantInput = $"i18n/{segments[1]}/docusaurus-plugin-content-docs/{segments[3]}";
            kind = "doc";
            prefix = $"{segments[1]}/{options.DocsRoutePrefix}{(version == "current" ? "" : "/" + version)}";
        }
        else if (segments[0] == "i18n" && segments.Length > 3 && segments[2] == "docusaurus-plugin-content-blog")
        {
            scope = segments[3..];
            variantInput = null;
            kind = "blog";
            prefix = $"{segments[1]}/{options.BlogRoutePrefix}";
        }
        else
        {
            raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                $"Document '{relative}' is outside the docs, blog, versioned, and translated inputs; wire its collection explicitly.", 1,
                null, "Register a collection with an explicit input directory and route prefix, or move the file."));
            return new(relative, bytes, Kind: "doc", Route: null, Edits: []);
        }

        // Import-only partials keep their text; only their imports are judged and rewritten.
        // They never bind front matter or routes, matching the collection loaders.
        if (scope.Any(segment => segment.StartsWith('_')))
        {
            if (!TryDecode(bytes, out var partial)) return new(relative, bytes, kind, null, Edits: []);
            var drops = new SortedSet<int>();
            var partialLink = ScanImports(partial, drops, raise);
            ScanRelativeImages(partial, relative, 1, raise);
            var rewritten = partialLink ? RewriteLinkToAttributes(partial) : partial;
            var partialEdits = new List<DocusaurusMigrationEdit>();
            AddLinkRewriteEdits(partialEdits, partial, rewritten, drops, []);
            foreach (var drop in drops) partialEdits.Add(new(MigrationActionKind.ReplaceLines, drop, drop, ""));
            return new(relative, DropLines(rewritten, drops), kind, null, partialEdits);
        }

        if (!TryDecode(bytes, out var text))
        {
            raise(DocusaurusMigrationVerdict.Unsupported, new(Unconvertible, SiteDiagnosticSeverity.Error,
                $"Document '{relative}' is not UTF-8 text.", 1, null, null));
            return new(relative, bytes, kind, null, Edits: []);
        }

        var split = FrontMatterSplitter.TrySplit(text, cancellationToken);
        var titleEdits = new List<DocusaurusMigrationEdit>();
        if (split.Status == FrontMatterSplitStatus.MissingFrontMatter)
        {
            // Docusaurus derives the title from the first heading or file name: inject the
            // same minimal front matter instead of leaving the page unrouted. The prepend is
            // recorded as InsertAfter(0, 0, ...) so replays reproduce the converted bytes.
            var derived = DeriveTitle(text, scope);
            var header = "---\ntitle: " + YamlQuote(derived) + "\n---";
            titleEdits.Add(new(MigrationActionKind.InsertAfter, 0, 0, header));
            text = header + "\n\n" + text;
            split = FrontMatterSplitter.TrySplit(text, cancellationToken);
            raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                $"Front matter title is derived: '{derived}'.", 1,
                $"Set title '{derived}'.", null));
        }

        if (split.Status != FrontMatterSplitStatus.Ok)
        {
            raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                $"Document '{relative}' has no usable YAML front matter; add at least a title.", split.FailureLine,
                null, "Add front matter with a title. The title cannot be invented safely."));
            return new(relative, bytes, kind, null, Edits: []);
        }

        var parsed = MarkdownContentCollectionLoader<DocumentFrontMatter>.ParseYaml(split.Yaml, relative, 2, cancellationToken);
        if (!parsed.IsSuccess)
        {
            foreach (var diagnostic in parsed.Diagnostics)
                raise(DocusaurusMigrationVerdict.Unsupported, new(Unconvertible, SiteDiagnosticSeverity.Error,
                    diagnostic.Message, diagnostic.Location?.Line ?? 2, null, null));
            return new(relative, bytes, kind, null, Edits: []);
        }

        var mapping = parsed.Value!;
        string? slug = Scalar(mapping, "slug");
        string? id = Scalar(mapping, "id");
        var lineDrops = new SortedSet<int>();
        var injections = new List<string>();
        var replacements = new List<(int Line, string Text)>();
        var convertedRelative = relative;

        void SetSlug(string value)
        {
            var text = value.Length == 0 ? "slug: \"\"" : "slug: " + value;
            if (mapping.ContainsKey("slug")) replacements.Add((mapping.GetKeyLocation("slug").Line ?? 2, text));
            else injections.Add(text);
        }

        if (kind == "doc")
        {
            var bound = new ReflectionContentFrontMatterBinder<DocumentFrontMatter>().Bind(mapping, new SiteSourceLocation(relative, 2, 1));
            foreach (var diagnostic in bound.Diagnostics)
            {
                if (diagnostic.Id == ContentFrontMatterDiagnosticIds.UnknownField)
                    raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                        diagnostic.Message + " Map it to DocumentFrontMatter or remove it.", diagnostic.Location?.Line ?? 2,
                        null, "Map the field to DocumentFrontMatter or remove it."));
                else
                    raise(DocusaurusMigrationVerdict.Unsupported, new(Unconvertible, SiteDiagnosticSeverity.Error,
                        diagnostic.Message, diagnostic.Location?.Line ?? 2, null, null));
            }

            // The table of contents renders h2-h3: deeper requests bind for compatibility
            // but keep their visible range note.
            if (Scalar(mapping, "toc_max_heading_level") is { } tocMax
                && int.TryParse(tocMax, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var tocLevel)
                && tocLevel > 3)
                raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                    $"The table of contents renders h2-h3; requested max level is {tocLevel}.", 2,
                    null, "Narrow toc_max_heading_level to 3 or accept the h2-h3 range."));

            var stem = Path.GetFileNameWithoutExtension(scope[^1]);
            var parentId = string.Join('/', scope[..^1].Select(DocumentCatalog.RemoveNumericPrefix));
            if (stem is "README" or "index")
            {
                if (stem == "README")
                {
                    convertedRelative = relative[..^Path.GetFileName(relative).Length] + "index" + Path.GetExtension(relative);
                    raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                        $"README is renamed to '{convertedRelative}'.", 1,
                        $"Rename to '{convertedRelative}'.", null));
                }

                // An explicit slug always wins: index completion only fills in
                // a missing slug so expected routes like `/docs/landing/` survive.
                if (slug is null)
                {
                    var targetSlug = parentId;
                    SetSlug(targetSlug);
                    raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                        $"Directory index slug is set to '{targetSlug}'.", 2, $"Set slug '{targetSlug}'.", null));
                    slug = targetSlug;
                }
            }
            else if (slug is not null)
            {
                var normalized = slug.Trim('/');
                var qualified = slug.StartsWith('/') || parentId.Length == 0 ? normalized : parentId + "/" + normalized;
                if (!string.Equals(slug, qualified, StringComparison.Ordinal))
                {
                    SetSlug(qualified);
                    raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                        $"Relative slug is qualified to '{qualified}'.", 2, $"Set slug '{qualified}'.", null));
                }

                slug = qualified;
            }

            scope = scope[..^1].Concat([Path.GetFileName(convertedRelative)]).ToArray();
            var routeId = id ?? DocumentCatalog.DefaultId(string.Join('/', scope));
            // Explicit ids collide in real sites (docusaurus.io ships three id:introduction
            // pages); routes stay slug-based, so only the collection identity is disambiguated.
            // Enumeration order is deterministic, so the winner is stable.
            var uniqueId = routeId;
            var discriminator = 2;
            while (!seenIds.Add((variantInput!, uniqueId))) uniqueId = $"{routeId}-{discriminator++}";
            if (!string.Equals(uniqueId, routeId, StringComparison.Ordinal))
            {
                if (id is not null)
                    replacements.Add((mapping.GetKeyLocation("id").Line ?? 2, "id: " + uniqueId));
                else
                    injections.Add("id: " + uniqueId);
                raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                    $"Duplicate id '{routeId}' in '{variantInput}'; using unique id '{uniqueId}'.", 2,
                    $"Set id '{uniqueId}'.", null));
            }

            return FinishDocument(text, convertedRelative, kind,
                SiteRoute.ForDirectoryIndex(prefix.Trim('/') + "/" + (slug ?? routeId).Trim('/'), options.BaseUrl).PublicPath,
                lineDrops, injections, replacements, split.Body, relative, text.AsSpan(0, split.BodyStartOffset).Count('\n') + 1, raise, titleEdits);
        }

        foreach (var key in mapping.Keys)
            if (!BlogFrontMatterKeys.Contains(key))
                raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                    $"Unknown blog front matter field '{key}'. Map it to MdxBlogFrontMatter or remove it.", mapping.GetKeyLocation(key).Line ?? 2,
                    null, $"Map '{key}' to MdxBlogFrontMatter or remove it."));

        var blogStem = Path.GetFileNameWithoutExtension(scope[^1]);
        var blogDirectory = string.Join('/', scope[..^1]);
        var dateMatch = BlogDateRegex.Match(blogStem);
        var nestedMatch = NestedBlogDateRegex.Match(blogStem);
        var nestedYear = blogDirectory.Length == 4 && blogDirectory.All(static ch => ch is >= '0' and <= '9')
            ? blogDirectory : null;
        // Colocated assets layout (blog/2021/05-12-slug/index.mdx): the year comes from
        // the grandparent directory, month and day from the parent directory.
        System.Text.RegularExpressions.Match? parentDate = null;
        string? indexNestedYear = null;
        if ((blogStem is "index" or "README") && scope.Length >= 3)
        {
            var candidate = NestedBlogDateRegex.Match(scope[^2]);
            if (candidate.Success && scope[^3].Length == 4
                && scope[^3].All(static ch => ch is >= '0' and <= '9')
                && DateOnly.TryParse($"{scope[^3]}-{candidate.Groups["month"].Value}-{candidate.Groups["day"].Value}",
                    System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                parentDate = candidate;
                indexNestedYear = scope[^3];
            }
        }

        string? date = Scalar(mapping, "date");
        if (date is null && !dateMatch.Success && nestedYear is not null && nestedMatch is { Success: true }
            && DateOnly.TryParse($"{nestedYear}-{nestedMatch.Groups["month"].Value}-{nestedMatch.Groups["day"].Value}",
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            // Old year-directory layout (blog/2017/12-14-slug): the year comes from the
            // directory, month and day from the file name, matching Docusaurus routing.
            date = $"{nestedYear}-{nestedMatch.Groups["month"].Value}-{nestedMatch.Groups["day"].Value}";
            injections.Add("date: " + date);
            raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                $"Blog date is taken from the directory and file name: '{date}'.", 2, $"Set date '{date}'.", null));
        }
        if (date is null && !dateMatch.Success && parentDate is not null && indexNestedYear is not null)
        {
            date = $"{indexNestedYear}-{parentDate.Groups["month"].Value}-{parentDate.Groups["day"].Value}";
            injections.Add("date: " + date);
            raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                $"Blog date is taken from the directory and file name: '{date}'.", 2, $"Set date '{date}'.", null));
        }
        if ((date is null || !DateTimeOffset.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            && dateMatch.Success && DateOnly.TryParse($"{dateMatch.Groups["year"].Value}-{dateMatch.Groups["month"].Value}-{dateMatch.Groups["day"].Value}",
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            date = $"{dateMatch.Groups["year"].Value}-{dateMatch.Groups["month"].Value}-{dateMatch.Groups["day"].Value}";
            injections.Add("date: " + date);
            raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                $"Blog date is taken from the file name: '{date}'.", 2, $"Set date '{date}'.", null));
        }

        if (date is null || !DateTimeOffset.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDate))
        {
            raise(DocusaurusMigrationVerdict.Unsupported, new(Unconvertible, SiteDiagnosticSeverity.Error,
                $"Blog entry '{relative}' has no usable date.", 2, null, null));
            return new(relative, bytes, kind, null, Edits: []);
        }

        var namePart = dateMatch.Success ? dateMatch.Groups["rest"].Value
            : nestedYear is not null && nestedMatch is { Success: true } ? nestedMatch.Groups["rest"].Value
            : parentDate is not null ? parentDate.Groups["rest"].Value
            : blogStem;
        // A year directory consumed for the date never repeats inside the slug.
        var slugDirectory = dateMatch.Success ? blogDirectory : "";
        if (slug is null)
        {
            if ((blogStem is "index" or "README") && parentDate is null && nestedYear is null && blogDirectory.Length > 0)
            {
                // Folder post without date info (blog/releases/3.8/index.mdx): Docusaurus
                // serves the directory path, so no dated slug is derived from the post date.
                slug = blogDirectory;
                injections.Add("slug: " + slug);
                raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                    $"Blog slug is set to '{slug}' to preserve the directory route.", 2, $"Set slug '{slug}'.", null));
            }
            else
            {
                slug = $"{parsedDate:yyyy/MM/dd}/" + (slugDirectory.Length == 0 ? namePart : slugDirectory + "/" + namePart);
                injections.Add("slug: " + slug);
                raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                    $"Blog slug is set to '{slug}' to preserve the dated route.", 2, $"Set slug '{slug}'.", null));
            }
        }
        else
        {
            var normalized = slug.Trim('/');
            if (!string.Equals(slug, normalized, StringComparison.Ordinal))
            {
                SetSlug(normalized);
                raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                    $"Blog slug is normalized to '{normalized}'.", 2, $"Set slug '{normalized}'.", null));
            }

            slug = normalized;
        }

        // Docusaurus accepts a scalar authors value; the LithoSharp binder needs a list.
        if (mapping.TryGetValue("authors", out var authorsValue)
            && LocatedYamlValue.Unwrap(authorsValue) is { } authorsUnwrapped
            && authorsUnwrapped is not IReadOnlyList<object?>)
        {
            var author = authorsUnwrapped.ToString()!;
            var item = author.Length > 0 && author.All(static ch => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-' or '.' or '/')
                ? author : "\"" + author.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var authorsLine = mapping.GetKeyLocation("authors").Line ?? 2;
            replacements.Add((authorsLine, "authors: [" + item + "]"));
            raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                $"Blog scalar authors value is wrapped into a list.", authorsLine,
                $"Set authors '[{item}]'.", null));
        }

        // Inline `{key: ID, ...}` author objects reference a registered profile with
        // per-post overrides: collapse them to the ID so the strict binder accepts them.
        // Other object shapes stay manual (replacing them would invent profile data).
        if (mapping.TryGetValue("authors", out var authorsListValue)
            && LocatedYamlValue.Unwrap(authorsListValue) is IReadOnlyList<object?>)
        {
            var authorsKeyLine = mapping.GetKeyLocation("authors").Line ?? 2;
            var sourceLines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
            var keyIndent = sourceLines[authorsKeyLine - 1].TakeWhile(static ch => ch is ' ' or '\t').Count();
            for (var index = authorsKeyLine; index < sourceLines.Length; index++)
            {
                var line = sourceLines[index];
                if (line.Length > 0 && line.TakeWhile(static ch => ch is ' ' or '\t').Count() <= keyIndent) break;
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^(\s*)-\s*key:\s*(\S.*)$");
                if (!match.Success) continue;
                var indent = match.Groups[1].Value;
                var authorId = YamlScalar(match.Groups[2].Value);
                var end = index;
                while (end + 1 < sourceLines.Length && sourceLines[end + 1].Length > indent.Length + 2
                    && sourceLines[end + 1].StartsWith(indent, StringComparison.Ordinal)
                    && sourceLines[end + 1][indent.Length] is ' ' or '\t'
                    && !System.Text.RegularExpressions.Regex.IsMatch(sourceLines[end + 1].Substring(indent.Length), @"^-\s")) end++;
                // A blank line inside the item would orphan the remaining lines: skip those.
                if (authorId.Length == 0 || (end + 1 < sourceLines.Length && sourceLines[end + 1].Length == 0
                    && end + 2 < sourceLines.Length && sourceLines[end + 2].Length > indent.Length + 2)) continue;
                replacements.Add((index + 1, indent + "- " + YamlQuote(authorId)));
                for (var drop = index + 2; drop <= end + 1; drop++) lineDrops.Add(drop);
                raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                    $"Blog inline author is collapsed to profile id '{authorId}'.", index + 1,
                    $"Set author '{authorId}'.", null));
                if (authors.Count > 0 && !authors.ContainsKey(authorId))
                    raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                        $"Blog author '{authorId}' needs an explicit profile.", index + 1,
                        null, "Register blog author profiles explicitly."));
            }
        }

        if (Scalar(mapping, "title") is null)
            raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                $"Blog entry '{relative}' has no title.", 2, null, "Add an explicit title."));
        foreach (var author in BlogAuthors(mapping, relative, raise))
            if (authors.Count == 0 || !authors.ContainsKey(author))
                raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                    $"Blog author '{author}' needs an explicit profile.", 2, null, "Register blog author profiles explicitly."));

        ScanImports(text, lineDrops, raise);
        return FinishDocument(text, convertedRelative, kind,
            SiteRoute.ForDirectoryIndex(prefix.Trim('/') + "/" + slug.Trim('/'), options.BaseUrl).PublicPath,
            lineDrops, injections, replacements, split.Body, relative, text.AsSpan(0, split.BodyStartOffset).Count('\n') + 1, raise, titleEdits);
    }

    private static byte[] DropLines(string text, SortedSet<int> lineDrops)
    {
        if (lineDrops.Count == 0) return StrictUtf8.GetBytes(text);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        foreach (var drop in lineDrops.OrderByDescending(line => line)) lines.RemoveAt(drop - 1);
        return StrictUtf8.GetBytes(string.Join(newline, lines));
    }

    private static AnalyzedDocument FinishDocument(
        string text, string convertedRelative, string kind, string route,
        SortedSet<int> lineDrops, List<string> injections, List<(int Line, string Text)> replacements, string body, string relative, int bodyFirstLine,
        Action<DocusaurusMigrationVerdict, DocusaurusMigrationIssue> raise,
        IReadOnlyList<DocusaurusMigrationEdit>? leadingEdits = null)
    {
        var exactLinkRemoved = ScanImports(text, lineDrops, raise);
        ScanRelativeImages(body, relative, bodyFirstLine, raise);
        var preRewrite = text;
        if (exactLinkRemoved) text = RewriteLinkToAttributes(text);
        var edits = new List<DocusaurusMigrationEdit>();
        // Prepended front matter (InsertAfter 0) replays before every other insertion.
        if (leadingEdits is not null) edits.AddRange(leadingEdits);
        AddLinkRewriteEdits(edits, preRewrite, text, lineDrops, replacements);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        foreach (var (line, replacement) in replacements)
        {
            lines[line - 1] = replacement;
            edits.Add(new(MigrationActionKind.ReplaceLines, line, line, replacement));
        }
        foreach (var drop in lineDrops.OrderByDescending(line => line))
        {
            lines.RemoveAt(drop - 1);
        }
        foreach (var drop in lineDrops) edits.Add(new(MigrationActionKind.ReplaceLines, drop, drop, ""));
        lines.InsertRange(1, injections);
        if (injections.Count > 0) edits.Add(new(MigrationActionKind.InsertAfter, 1, 1, string.Join("\n", injections)));
        return new(convertedRelative, StrictUtf8.GetBytes(string.Join(newline, lines)), kind,
            new(route, convertedRelative, kind), edits);
    }

    private static IEnumerable<string> BlogAuthors(
        LocatedYamlMapping mapping, string relative,
        Action<DocusaurusMigrationVerdict, DocusaurusMigrationIssue> raise)
    {
        if (!mapping.TryGetValue("authors", out var value) || LocatedYamlValue.Unwrap(value) is not { } unwrapped) yield break;
        if (unwrapped is not IReadOnlyList<object?> list)
        {
            yield return unwrapped.ToString()!;
            yield break;
        }

        foreach (var item in list)
        {
            var content = LocatedYamlValue.Unwrap(item);
            if (content is IReadOnlyDictionary<string, object?>)
                raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                    $"Blog entry '{relative}' uses an inline author object; map it to a profile ID.", mapping.GetValueLocation("authors").Line ?? 2,
                    null, "Replace the inline author with a registered profile ID."));
            else if (content?.ToString() is { Length: > 0 } author) yield return author;
        }
    }

    private enum TextScan { Clean, Unsupported }

    private static TextScan ScanText(byte[] bytes, Action<DocusaurusMigrationVerdict, DocusaurusMigrationIssue> raise)
    {
        if (!TryDecode(bytes, out var text)) return TextScan.Clean;
        var worst = TextScan.Clean;
        foreach (Match match in ImportRegex.Matches(text))
        {
            var name = match.Groups["name"].Value;
            if (DocusaurusProfile.IsSupportedImport(name)) continue;
            worst = TextScan.Unsupported;
            raise(DocusaurusMigrationVerdict.Unsupported, new(UnsupportedImport, SiteDiagnosticSeverity.Error,
                "Unsupported import requires an explicit replacement: " + name, LineOf(text, match.Index), null, null));
        }

        if (text.Contains("@docusaurus/plugin-", StringComparison.Ordinal) || text.Contains("@docusaurus/theme-", StringComparison.Ordinal))
        {
            worst = TextScan.Unsupported;
            raise(DocusaurusMigrationVerdict.Unsupported, new(UnsupportedPlugin, SiteDiagnosticSeverity.Error,
                "Docusaurus plugins and themes are not loaded by LithoSharp. Select a supported C# or Remark/Rehype extension.", 1, null, null));
        }

        return worst;
    }

    private static void ScanRelativeImages(string body, string relative, int firstLine, Action<DocusaurusMigrationVerdict, DocusaurusMigrationIssue> raise)
    {
        // Markdown image URLs stay literal: MDX never bundles them. Fenced code is not content.
        var inFence = false;
        var lines = body.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence) continue;
            foreach (Match match in InlineImageRegex.Matches(lines[index]))
                if (IsRelativeAsset(match.Groups["url"].Value))
                {
                    raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                        $"Relative image '{match.Groups["url"].Value}' in '{relative}' is not bundled; use a JS import or serve the file.", firstLine + index,
                        null, "Replace the markdown image with a JS import, or copy the file next to the published output."));
                    return;
                }
        }

        foreach (Match match in ReferenceImageRegex.Matches(body))
            if (IsRelativeAsset(match.Groups["url"].Value.Trim('<', '>', '"')))
            {
                raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                    $"Relative image '{match.Groups["url"].Value}' in '{relative}' is not bundled; use a JS import or serve the file.", firstLine + LineOf(body, match.Index) - 1,
                    null, "Replace the markdown image with a JS import, or copy the file next to the published output."));
                return;
            }
    }

    private static bool IsRelativeAsset(string url) =>
        url.Length > 0 && (url[0] == '.'
            || (url.IndexOf(':') < 0 && url[0] != '/' && url[0] != '#' && url[0] != '{' && url[0] != '<'));

    private static bool ScanImports(string text, SortedSet<int> lineDrops, Action<DocusaurusMigrationVerdict, DocusaurusMigrationIssue> raise)
    {
        var exactLinkRemoved = false;
        // Regular fenced samples document imports without executing them: match on blanked
        // text (offsets are preserved) so samples neither report nor lose lines. Docusaurus
        // mdx-code-block fences wrap executable MDX, so their inner imports stay visible
        // and report here. TryRemovableImport and line lookups below keep reading the
        // original text at the same offsets.
        foreach (Match match in ImportRegex.Matches(BlankNonExecutable(text)))
        {
            var name = match.Groups["name"].Value;
            var line = LineOf(text, match.Index);
            if (DocusaurusProfile.IsSupportedImport(name)) continue;
            if (name is "@docusaurus/Link" or "@docusaurus/Translate")
            {
                if (TryRemovableImport(text, match, name, out var importLine, out var component, out var alias))
                {
                    lineDrops.Add(importLine);
                    raise(DocusaurusMigrationVerdict.Convertible, new(NeedsManualAction, SiteDiagnosticSeverity.Info,
                        $"Default '{component}' import is removed; the bare component resolves from the runtime map.", importLine,
                        $"Remove line {importLine}; use bare <{component}> without an import.", null));
                    if (name == "@docusaurus/Link")
                    {
                        exactLinkRemoved = true;
                    }

                    continue;
                }

                raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                    alias is null
                        ? $"'{name}' has no matching import path; rewrite the usage to the bare component."
                        : $"'{name}' is imported as '{alias}'; rewrite usages to the bare <{(name == "@docusaurus/Link" ? "Link" : "Translate")}> component without an import.",
                    line,
                    null, $"Remove the '{name}' import and use the bare component."));
                continue;
            }

            if (name is "@docusaurus/useBaseUrl" or "@docusaurus/useDocusaurusContext" || HookUseRegex.IsMatch(ImportLine(text, match)))
            {
                raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                    $"'{name}' has no LithoSharp export; rewrite call sites to usePageContext from @lithosharp/runtime.", line,
                    null, "Rewrite call sites to usePageContext from @lithosharp/runtime."));
                continue;
            }

            // The worker provides SSR-safe shims for these hooks so vendored components
            // and mdx-code-block demos render statically. Direct document usage builds
            // with degraded output (empty locations, current version) and needs human
            // verification, but it is not an unsupported blocker.
            if (name is "@docusaurus/router" or "@docusaurus/useBrokenLinks" or "@docusaurus/useIsBrowser"
                or "@docusaurus/theme-common" or "@docusaurus/plugin-content-docs/client")
            {
                raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                    $"'{name}' renders with static fallback values; verify the output or rewrite call sites to usePageContext from @lithosharp/runtime.", line,
                    null, "Verify the static fallback output, or rewrite call sites to usePageContext from @lithosharp/runtime."));
                continue;
            }

            raise(DocusaurusMigrationVerdict.Unsupported, new(UnsupportedImport, SiteDiagnosticSeverity.Error,
                "Unsupported import requires an explicit replacement: " + name, line, null, null));
        }

        // Prose and fenced samples that merely mention plugin or theme package names are not
        // convertible inputs: only real imports (matched above) report. Code files keep the
        // strict substring check in ScanText.
        ScanBareImports(text, raise);
        return exactLinkRemoved;
    }

    private static void ScanBareImports(string text, Action<DocusaurusMigrationVerdict, DocusaurusMigrationIssue> raise)
    {
        // Bare third-party imports (react-tweet) never match ImportRegex, and the worker only
        // provides react itself: flag them for a human install-or-rewrite decision instead of
        // failing silently at build time. Relative imports resolve from the converted tree and
        // fail explicitly at build when they cannot, so they stay quiet here. Fenced samples
        // are blanked first so documented import examples never report.
        var visible = BlankNonExecutable(text);
        foreach (Match match in BareImportRegex.Matches(visible))
        {
            var name = match.Groups["name"].Value;
            if (name.StartsWith("@docusaurus/", StringComparison.Ordinal) || name.StartsWith("@theme/", StringComparison.Ordinal))
                continue;
            if (name is "react" || name is "react-dom" || name.StartsWith("react-dom/", StringComparison.Ordinal)
                || name is "react/jsx-runtime" || name.StartsWith("@lithosharp/", StringComparison.Ordinal)
                || name is "lithosharp:live-runtime" || name.StartsWith("./", StringComparison.Ordinal)
                || name.StartsWith("../", StringComparison.Ordinal))
                continue;
            var line = LineOf(text, match.Index);
            raise(DocusaurusMigrationVerdict.ManualActionRequired, new(NeedsManualAction, SiteDiagnosticSeverity.Warning,
                $"Bare package import '{name}' is not installed by migration; install it in the target project or rewrite the usage.", line,
                null, $"Install '{name}' in the target site project, or replace the usage."));
        }
    }

    private static string BlankNonExecutable(string text, bool blankDirectives = true)
    {
        // ESM imports only execute at the top level of a document: blank fenced code
        // and (optionally) ::: directive bodies so documented import examples never report.
        // Docusaurus mdx-code-block fences wrap executable MDX (imports, component
        // definitions, JSX open/close tags spanning markdown) rather than display code,
        // so their inner content stays visible like top-level prose. Only top-level
        // mdx-code-block fences unwrap: samples inside outer fenced blocks stay blanked.
        // Fence length is tracked so outer ```` blocks enclose inner ``` samples.
        var lines = text.Split('\n');
        var inFence = false;
        var fenceLength = 0;
        var fenceChar = '\0';
        var inMdxBlock = false;
        var mdxLength = 0;
        var mdxChar = '\0';
        var inDirective = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            var markerLength = FenceMarkerLength(trimmed);
            if (markerLength > 0)
            {
                var markerChar = trimmed[0];
                if (inMdxBlock)
                {
                    if (markerChar == mdxChar && markerLength >= mdxLength)
                        inMdxBlock = false;
                    else
                        lines[index] = new string(' ', lines[index].Length);
                    continue;
                }

                if (!inFence)
                {
                    var rest = trimmed[markerLength..];
                    if (IsMdxCodeBlockOpen(rest))
                    {
                        inMdxBlock = true;
                        mdxLength = markerLength;
                        mdxChar = markerChar;
                        continue;
                    }

                    inFence = true;
                    fenceLength = markerLength;
                    fenceChar = markerChar;
                    continue;
                }

                if (markerChar == fenceChar && markerLength >= fenceLength)
                    inFence = false;
                continue;
            }

            if (blankDirectives && trimmed.StartsWith(":::", StringComparison.Ordinal))
            {
                inDirective = !inDirective;
                continue;
            }

            if (inFence || inDirective) lines[index] = new string(' ', lines[index].Length);
        }

        return string.Join('\n', lines);
    }

    private static int FenceMarkerLength(string trimmed)
    {
        if (trimmed.Length == 0 || (trimmed[0] != '`' && trimmed[0] != '~')) return 0;
        var length = 0;
        while (length < trimmed.Length && trimmed[length] == trimmed[0]) length++;
        return length >= 3 ? length : 0;
    }

    private static bool IsMdxCodeBlockOpen(string rest) =>
        rest.StartsWith("mdx-code-block", StringComparison.Ordinal)
        && (rest.Length == "mdx-code-block".Length || char.IsWhiteSpace(rest["mdx-code-block".Length]));

    private static string ImportLine(string text, Match match)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
        var end = text.IndexOf('\n', match.Index);
        return text[start..(end < 0 ? text.Length : end)];
    }

    private static bool TryRemovableImport(string text, Match match, string module, out int line, out string component, out string? alias)
    {
        line = 0;
        alias = null;
        component = module == "@docusaurus/Link" ? "Link" : "Translate";
        var single = ImportLine(text, match);
        if (!single.Contains("import", StringComparison.Ordinal) || !single.Contains(module, StringComparison.Ordinal)) return false;
        var statement = ImportStatementRegex.Match(single);
        if (!statement.Success || statement.Groups["module"].Value != module) return false;
        var spec = statement.Groups["spec"].Value.Trim();
        if (spec.StartsWith('{'))
        {
            if (module != "@docusaurus/Translate") return false;
            var names = spec.Trim('{', '}', ' ').Split(',').Select(part => part.Trim().Split(" as ")[0].Trim()).ToArray();
            if (names.Length != 1 || names[0] != "Translate") return false;
            line = LineOf(text, match.Index);
            return true;
        }

        if (spec.StartsWith('*')) return false;
        // A default import is only removable when it binds the bare component
        // name with no siblings: `import MyLink from ...` leaves `<MyLink>`
        // usages behind, and `import Link, {...} from ...` would drop siblings.
        var comma = spec.IndexOf(',');
        var local = (comma < 0 ? spec : spec[..comma]).Trim();
        if (local.Length == 0 || comma >= 0)
        {
            alias = local.Length == 0 ? null : local;
            return false;
        }

        if (!string.Equals(local, component, StringComparison.Ordinal))
        {
            alias = local;
            return false;
        }

        line = LineOf(text, match.Index);
        return true;
    }

    /// <summary>
    /// Rewrites Docusaurus <c>to</c> attributes to <c>href</c> on bare
    /// <c>&lt;Link&gt;</c> usages outside fenced code. The runtime map resolves
    /// bare <c>Link</c> with <c>href</c>; <c>to</c> alone would render an empty
    /// link target. Line breaks are preserved so diagnostic lines stay valid.
    /// </summary>
    internal static string RewriteLinkToAttributes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        var inFence = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence) continue;
            lines[index] = RewriteLinkToInLine(lines[index]);
        }

        return string.Join(newline, lines);
    }

    private static string RewriteLinkToInLine(string line)
    {
        var result = new StringBuilder(line.Length);
        var cursor = 0;
        while (true)
        {
            var open = line.IndexOf("<Link", cursor, StringComparison.Ordinal);
            if (open < 0
                || (open + 5 < line.Length && line[open + 5] is not (' ' or '\t' or '\r' or '\n' or '>' or '/')))
            {
                result.Append(line, cursor, line.Length - cursor);
                return result.ToString();
            }

            // Find the tag end, respecting quotes and JSX expression braces.
            var end = -1;
            var quote = '\0';
            var depth = 0;
            for (var index = open + 5; index < line.Length; index++)
            {
                var ch = line[index];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                }
                else if (ch is '"' or '\'')
                {
                    quote = ch;
                }
                else if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                }
                else if (ch == '>' && depth <= 0)
                {
                    end = index;
                    break;
                }
            }

            if (end < 0)
            {
                result.Append(line, cursor, line.Length - cursor);
                return result.ToString();
            }

            result.Append(line, cursor, open - cursor);
            result.Append(RewriteTagToAttribute(line, open, end));
            cursor = end + 1;
        }
    }

    private static string RewriteTagToAttribute(string line, int open, int end)
    {
        // Scan attribute names outside quotes and braces: only a bare `to`
        // becomes `href`, and only when the tag has no `href` already.
        var names = new List<(int Start, int Length, string Name)>();
        var index = open + 5;
        while (index < end)
        {
            while (index < end && char.IsWhiteSpace(line[index])) index++;
            if (index >= end || line[index] is '/' or '>') break;
            if (line[index] is '"' or '\'' or '{')
            {
                index = SkipAttributeValue(line, index, end);
                continue;
            }

            var start = index;
            while (index < end && (char.IsLetterOrDigit(line[index]) || line[index] is '-' or '_' or ':')) index++;
            if (index == start)
            {
                index++;
                continue;
            }

            var name = line[start..index];
            names.Add((start, index - start, name));
            var after = index;
            while (after < end && char.IsWhiteSpace(line[after])) after++;
            index = after < end && line[after] == '='
                ? SkipAttributeValue(line, after + 1, end)
                : after;
        }

        var to = names.FirstOrDefault(candidate => candidate.Name == "to");
        if (to.Name is null || names.Any(candidate => candidate.Name == "href"))
        {
            return line[open..(end + 1)];
        }

        return line[open..to.Start] + "href" + line[(to.Start + to.Length)..(end + 1)];
    }

    private static int SkipAttributeValue(string line, int index, int end)
    {
        while (index < end && char.IsWhiteSpace(line[index])) index++;
        if (index >= end) return end;
        if (line[index] is '"' or '\'')
        {
            var quote = line[index++];
            while (index < end && line[index] != quote) index++;
            return Math.Min(index + 1, end);
        }

        if (line[index] == '{')
        {
            var depth = 0;
            var quote = '\0';
            while (index < end)
            {
                var ch = line[index];
                if (quote != '\0')
                {
                    if (ch == quote) quote = '\0';
                }
                else if (ch is '"' or '\'')
                {
                    quote = ch;
                }
                else if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0) return index + 1;
                }

                index++;
            }

            return end;
        }

        while (index < end && !char.IsWhiteSpace(line[index]) && line[index] is not ('>' or '/')) index++;
        return index;
    }

    private static List<string> ReadVersions(byte[] bytes, Action<DocusaurusMigrationVerdict, DocusaurusMigrationIssue> raise)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Array
                || json.RootElement.EnumerateArray().Any(element => element.ValueKind != JsonValueKind.String))
                throw new JsonException("versions.json must be an array of version strings.");
            return json.RootElement.EnumerateArray().Select(element => element.GetString()!).ToList();
        }
        catch (JsonException exception)
        {
            raise(DocusaurusMigrationVerdict.Unsupported, new(Unconvertible, SiteDiagnosticSeverity.Error,
                $"versions.json is not readable: {exception.Message}", 1, null, null));
            return [];
        }
    }

    private static string? SidebarVersion(string fileName)
    {
        const string prefix = "version-";
        const string suffix = "-sidebars.json";
        return fileName.StartsWith(prefix, StringComparison.Ordinal) && fileName.EndsWith(suffix, StringComparison.Ordinal)
            && fileName.Length > prefix.Length + suffix.Length
            ? fileName[prefix.Length..^suffix.Length] : null;
    }

    private static IReadOnlyList<DocusaurusMigrationAuthor> ReadAuthors(byte[] bytes, string relative, Action<DocusaurusMigrationVerdict, DocusaurusMigrationIssue> raise)
    {
        if (!TryDecode(bytes, out var text))
        {
            raise(DocusaurusMigrationVerdict.Unsupported, new(Unconvertible, SiteDiagnosticSeverity.Error,
                $"Authors file '{relative}' is not UTF-8 text.", 1, null, null));
            return [];
        }

        var parsed = MarkdownContentCollectionLoader<DocumentFrontMatter>.ParseYaml(text, relative, 1, CancellationToken.None);
        if (!parsed.IsSuccess || parsed.Value is not LocatedYamlMapping mapping)
        {
            raise(DocusaurusMigrationVerdict.Unsupported, new(Unconvertible, SiteDiagnosticSeverity.Error,
                $"Authors file '{relative}' is not a YAML mapping.", 1, null, null));
            return [];
        }

        return mapping.Select(pair => new DocusaurusMigrationAuthor(pair.Key,
            LocatedYamlValue.Unwrap(pair.Value) is IReadOnlyDictionary<string, object?> nested
                && nested.TryGetValue("name", out var name) && LocatedYamlValue.Unwrap(name)?.ToString() is { Length: > 0 } text ? text : pair.Key)).ToArray();
    }

    private static bool IsJson(byte[] bytes)
    {
        try
        {
            using var _ = JsonDocument.Parse(bytes);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsStructured(byte[] bytes, string extension)
    {
        if (extension == ".json") return IsJson(bytes);
        if (!TryDecode(bytes, out var text)) return false;
        return MarkdownContentCollectionLoader<DocumentFrontMatter>.ParseYaml(text, "category", 1, CancellationToken.None).IsSuccess;
    }

    private static string? Scalar(LocatedYamlMapping mapping, string key) =>
        mapping.TryGetValue(key, out var value) ? LocatedYamlValue.Unwrap(value)?.ToString() : null;

    private static string DeriveTitle(string text, string[] scope)
    {
        foreach (var line in BlankNonExecutable(text, blankDirectives: false).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '#') continue;
            var level = 0;
            while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
            if (level < trimmed.Length && trimmed[level] is ' ' or '\t')
            {
                var title = trimmed[level..].Trim().TrimEnd('#').Trim();
                if (title.Length > 0) return title;
            }
        }

        var stem = Path.GetFileNameWithoutExtension(scope[^1]);
        return stem is "README" or "index" && scope.Length > 1 ? scope[^2] : stem;
    }

    private static string YamlQuote(string value)
    {
        if (value.Length > 0 && value.All(static ch => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or ' ' or '_' or '-' or '.' or '/')
            && !value.Contains(": ", StringComparison.Ordinal) && !value.Contains(" #", StringComparison.Ordinal))
            return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static bool TryDecode(byte[] bytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;

    private static IEnumerable<string> Enumerate(string source, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        // Dependency and tool outputs never migrate: node_modules, .git, and
        // build outputs stay out, matching the tool's content commands.
        // Authors and versions feed later verdicts, so they analyze first. The order stays deterministic.
        return Directory.EnumerateFiles(source, "*", options)
            .Where(file => !Path.GetRelativePath(source, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "node_modules" or ".git" or "bin" or "obj"))
            .OrderBy(file => Path.GetRelativePath(source, file).Replace('\\', '/') is "blog/authors.yml" or "blog/authors.yaml" or "versions.json" ? 0 : 1)
            .ThenBy(file => file, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
