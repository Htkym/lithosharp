using System.ComponentModel;
using System.Runtime.InteropServices;
using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.Internal;
using LithoSharp.Routing;

namespace LithoSharp.Quality;

/// <summary>Checks an existing output tree without generating files, evaluating a project, or writing caches.</summary>
public static class GeneratedSiteInspector
{
    /// <summary>Inspects current HTML, CSS, local targets and native immediate redirects using the published base URL.</summary>
    /// <param name="outputDirectory">An existing output directory. Links and reparse points, including ancestors, are rejected.</param>
    /// <param name="baseUrl">The published absolute HTTP or HTTPS site URL, including its base path.</param>
    /// <param name="cancellationToken">Cancels reading and inspection.</param>
    /// <returns>Fresh diagnostics for the current output. This does not establish source freshness or asset ownership.</returns>
    public static async Task<SiteQualityReport> InspectAsync(
        string outputDirectory, string baseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        cancellationToken.ThrowIfCancellationRequested();
        var siteRoot = SiteRoute.ForDirectoryIndex(string.Empty, baseUrl);
        var publishedUrl = new Uri(baseUrl);
        if (publishedUrl.UserInfo.Length != 0)
            throw new ArgumentException("The base URL must not contain credentials.", nameof(baseUrl));
        var origin = publishedUrl.GetLeftPart(UriPartial.Authority);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (Path.GetDirectoryName(root) is null)
            throw new ArgumentException("The output directory must not be a file system root.", nameof(outputDirectory));
        EnsureSafeAncestors(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Output directory '{root}' does not exist.");
        var paths = EnumerateFiles(root, cancellationToken);
        var routes = paths.ToDictionary(path => path,
            path => SiteRoute.ForFile(string.Join('/', path.Split('/').Select(Uri.EscapeDataString)), baseUrl),
            StringComparer.Ordinal);
        if (routes.Any(pair => pair.Key != pair.Value.RelativeOutputPath))
            throw new InvalidOperationException("Output paths must use normalized Unicode names.");
        var routeTable = new SiteRouteTable();
        foreach (var route in routes.Values)
            routeTable.Register(route, route.RelativeOutputPath);
        routeTable.ValidateOrThrow();
        var textPaths = paths.Where(path => IsHtml(path) || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!textPaths.Any(IsHtml))
            throw new InvalidOperationException("The output contains no .html or .htm pages to inspect.");
        var inspected = new Dictionary<string, CachedBuildArtifact>(StringComparer.Ordinal);
        var redirects = new Dictionary<string, SiteRoute>(StringComparer.Ordinal);
        var redirectDiagnostics = new List<SiteDiagnostic>();
        await using var facts = new BuildHtmlFacts(cancellationToken);
        await facts.BeginQualityAsync().ConfigureAwait(false);
        var report = await SiteQualityValidator.ValidateAsync(baseUrl, textPaths, Read,
            routes, new HashSet<string>(StringComparer.Ordinal), redirects, new(), cancellationToken,
            sharedFacts: facts).ConfigureAwait(false);
        CheckRedirectGraph();
        if (!paths.SequenceEqual(EnumerateFiles(root, cancellationToken), StringComparer.Ordinal))
            throw new IOException("The output file inventory changed during inspection.");
        foreach (var artifact in inspected.Values)
        {
            EnsureEntryType(Path.Combine(root, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)), directory: false);
            await SiteGenerator.VerifyQualityTextAsync(root, artifact, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new SiteQualityReport(report.Diagnostics.Concat(redirectDiagnostics))
        {
            HtmlParseCount = facts.Counters.ParseCount,
        };

        async Task<string> Read(string path, CancellationToken token)
        {
            EnsureEntryType(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), directory: false);
            var content = await SiteGenerator.ReadQualityTextAsync(root, path, inspected, token, strictUtf8: true).ConfigureAwait(false);
            if (!IsHtml(path))
                return content;
            var pageFacts = (await facts.GetQualityAsync(content, token).ConfigureAwait(false)).Facts;
            var refreshes = pageFacts.Elements.Where(element => element.Name == "meta"
                && string.Equals(QualityHtmlFacts.Attribute(element, "http-equiv"), "refresh", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (refreshes.Length == 0)
                return content;
            var value = refreshes.Length == 1 ? QualityHtmlFacts.Attribute(refreshes[0], "content")?.Trim() : null;
            var resolutionBase = new Uri(origin + routes[path].PublicPath);
            if (pageFacts.BaseHref is { } declaredBase && Uri.TryCreate(resolutionBase, declaredBase, out var parsedBase))
                resolutionBase = parsedBase;
            if (value is null || !value.StartsWith("0;url=", StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(resolutionBase, value[6..], out var target)
                || target.Query.Length != 0 || target.Fragment.Length != 0
                || !SiteQualityUrlRules.TryInternalOutputPath(target, origin, siteRoot.PublicPath, out var targetPath))
                redirectDiagnostics.Add(new("LSQ001", SiteDiagnosticSeverity.Error,
                    "Only one native immediate redirect to a local site target can be inspected.", new(path)));
            else
            {
                var route = SiteRoute.ForFile(string.Join('/', targetPath.Split('/').Select(Uri.EscapeDataString)), baseUrl);
                redirects.Add(path, route);
                if (!routes.ContainsKey(targetPath))
                    redirectDiagnostics.Add(new("LSQ006", SiteDiagnosticSeverity.Error,
                        $"Redirect target '{target.AbsolutePath}' does not exist.", new(path)));
            }
            return content;
        }

        void CheckRedirectGraph()
        {
            var processed = new HashSet<string>(StringComparer.Ordinal);
            var cycles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in redirects.Keys)
            {
                var path = new List<string>();
                var positions = new Dictionary<string, int>(StringComparer.Ordinal);
                var current = source;
                while (redirects.TryGetValue(current, out var target) && !processed.Contains(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (positions.TryGetValue(current, out var cycleStart))
                    {
                        cycles.UnionWith(path.Skip(cycleStart));
                        break;
                    }
                    positions.Add(current, path.Count);
                    path.Add(current);
                    current = target.RelativeOutputPath;
                }
                processed.UnionWith(path);
            }
            foreach (var (source, target) in redirects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cycles.Contains(source))
                    redirectDiagnostics.Add(new("LSQ004", SiteDiagnosticSeverity.Error, "Redirect participates in a cycle.", new(source)));
                else if (redirects.ContainsKey(target.RelativeOutputPath))
                    redirectDiagnostics.Add(new("LSQ005", SiteDiagnosticSeverity.Error,
                        "Redirect chains are not supported; target the final route directly.", new(source)));
            }
        }
    }

    private static bool IsHtml(string path) => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

    private static string[] EnumerateFiles(string root, CancellationToken token)
    {
        EnsureSafeAncestors(root);
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            EnsureSafeAncestors(directory);
            EnsureEntryType(directory, directory: true);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                RejectReparsePoint(path, attributes);
                EnsureEntryType(path, (attributes & FileAttributes.Directory) != 0);
                if ((attributes & FileAttributes.Device) != 0)
                    throw new InvalidOperationException($"Output path '{path}' is not a regular file or directory.");
                var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains('\\'))
                    throw new InvalidOperationException($"Output path '{path}' contains a literal backslash.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(path);
                else
                    files.Add(relative);
            }
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    private static void EnsureEntryType(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
            return;
        ushort mode;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                const int atCurrentDirectory = -100, noFollow = 0x100;
                const uint typeMask = 1;
                if (LinuxStatx(atCurrentDirectory, path, noFollow, typeMask, out var status) != 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                if ((status.Mask & typeMask) == 0)
                    throw new IOException($"The file type of output path '{path}' could not be verified.");
                mode = status.Mode;
            }
            else if (OperatingSystem.IsMacOS()
                && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64)
            {
                DarwinFileType status;
                var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? DarwinLStatX64(path, out status) : DarwinLStatArm64(path, out status);
                if (result != 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                mode = status.Mode;
            }
            else
                throw new PlatformNotSupportedException("Output file type verification requires Windows, Linux statx, or macOS x64/arm64.");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new PlatformNotSupportedException("Native output file type verification is unavailable.", exception);
        }
        const int fileTypeMask = 0xf000, directoryType = 0x4000, regularFileType = 0x8000;
        if ((mode & fileTypeMask) != (directory ? directoryType : regularFileType))
            throw new InvalidOperationException($"Output path '{path}' is not a regular file or directory.");
    }

    // Project only the type fields while reserving the complete native result buffers.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileType
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinFileType
    {
        [FieldOffset(4)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int LinuxStatx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxFileType status);

    // Darwin x64 versions the 64-bit inode ABI; arm64 uses it without a suffix.
    [DllImport("libSystem.B.dylib", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int DarwinLStatX64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out DarwinFileType status);

    [DllImport("libSystem.B.dylib", EntryPoint = "lstat", SetLastError = true)]
    private static extern int DarwinLStatArm64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out DarwinFileType status);

    private static void EnsureSafeAncestors(string path)
    {
        for (DirectoryInfo? current = new(path); current is not null; current = current.Parent)
            if (current.Exists)
                RejectReparsePoint(current.FullName, current.Attributes);
    }

    private static void RejectReparsePoint(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Output path '{path}' contains a link or reparse point.");
    }
}
