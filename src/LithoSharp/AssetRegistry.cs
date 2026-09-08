using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;

namespace LithoSharp;

/// <summary>Declares an input file that is published as a site asset.</summary>
public sealed class SiteAsset
{
    /// <summary>Creates an asset declaration.</summary>
    /// <param name="id">A stable, nonempty identifier without surrounding whitespace or control characters.</param>
    /// <param name="inputRoot">The directory containing the source file.</param>
    /// <param name="relativeInputPath">A literal filesystem path beneath the input root.</param>
    /// <param name="relativeOutputPath">A literal relative output path before fingerprinting.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">An identifier or path is invalid, rooted, or contains traversal segments.</exception>
    /// <remarks>Registration reads a regular file beneath the input root and outside the output directory. Missing files and symbolic links fail before output is changed.</remarks>
    public SiteAsset(string id, string inputRoot, string relativeInputPath, string relativeOutputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputRoot);
        ArgumentNullException.ThrowIfNull(relativeInputPath);
        ArgumentNullException.ThrowIfNull(relativeOutputPath);
        Id = new BuildNodeId(id).Value;
        InputRoot = Path.GetFullPath(inputRoot);
        RelativeInputPath = SiteRoute.NormalizeRelativeOutputPath(relativeInputPath);
        RelativeOutputPath = SiteRoute.NormalizeRelativeOutputPath(relativeOutputPath);
    }

    /// <summary>Stable asset identifier.</summary>
    public string Id { get; }
    /// <summary>Directory containing the source file.</summary>
    public string InputRoot { get; }
    /// <summary>Source path relative to <see cref="InputRoot"/>.</summary>
    public string RelativeInputPath { get; }
    /// <summary>Output path before fingerprinting.</summary>
    public string RelativeOutputPath { get; }
}

/// <summary>A URL for a registered asset.</summary>
public sealed class AssetUrl
{
    internal AssetUrl(SiteAsset asset, SiteRoute route)
    {
        Asset = asset;
        Route = route;
    }

    /// <summary>The registered asset. Transformed outputs use a synthetic declaration whose input path is not a source file.</summary>
    public SiteAsset Asset { get; }
    /// <summary>The asset route.</summary>
    public SiteRoute Route { get; }
    /// <summary>The public URL.</summary>
    public string Value => Route.PublicPath;
    /// <summary>Content fingerprint used in the published path.</summary>
    public string Fingerprint { get; internal init; } = string.Empty;
    /// <summary>Integrity hash for the published bytes.</summary>
    public string Integrity { get; internal init; } = string.Empty;
    /// <summary>Converts this URL to a quoted HTML attribute value.</summary>
    public HtmlAttributeValue ToAttributeValue() => new(Value);
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Reports a stable asset reference diagnostic.</summary>
public sealed class AssetRegistryException : InvalidOperationException
{
    internal AssetRegistryException(SiteDiagnostic diagnostic) : base(diagnostic.Message) => Diagnostic = diagnostic;
    /// <summary>The diagnostic associated with this failure.</summary>
    public SiteDiagnostic Diagnostic { get; }
}

/// <summary>Registers and fingerprints source assets for one site build.</summary>
/// <remarks>Obtain this instance from a rendering context. Declare assets through <see cref="SiteGenerationOptions.Assets"/>. Duplicate identifiers fail registration.</remarks>
public sealed class AssetRegistry
{
    private readonly IReadOnlyDictionary<string, RegisteredAsset> assets;
    private readonly IReadOnlyList<BuildNode>? additionalNodes;
    internal IReadOnlySet<string> ExecutedTransformNodes { get; private init; } = new HashSet<string>(StringComparer.Ordinal);

    internal static AssetRegistry Empty { get; } = new(
        new ReadOnlyDictionary<string, RegisteredAsset>(new Dictionary<string, RegisteredAsset>(StringComparer.Ordinal)));

    internal static async Task<AssetRegistry> CreateAsync(
        IEnumerable<SiteAsset> definitions,
        string? baseUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var map = new Dictionary<string, RegisteredAsset>(StringComparer.Ordinal);
        foreach (var asset in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(asset);
            if (map.ContainsKey(asset.Id))
                throw new ArgumentException($"Asset identifier '{asset.Id}' is registered more than once.", nameof(definitions));
            var sourcePath = Path.GetFullPath(Path.Combine(asset.InputRoot, asset.RelativeInputPath.Replace('/', Path.DirectorySeparatorChar)));
            await using var source = BuildInputFingerprint.OpenVerifiedContainedRead(asset.InputRoot, sourcePath, asynchronous: true);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            var bytes = buffer.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var outputPath = FingerprintedPath(asset.RelativeOutputPath, hash);
            map.Add(asset.Id, new RegisteredAsset(asset, bytes, hash, SiteRoute.ForFile(EscapeRoutePath(outputPath), baseUrl)));
        }

        return new AssetRegistry(new ReadOnlyDictionary<string, RegisteredAsset>(map));
    }

    internal static async Task<AssetRegistry> CreateAsync(
        IEnumerable<SiteAsset> definitions,
        IEnumerable<SiteAssetTransform> transforms,
        string? publicDirectory,
        string? cacheDirectory,
        string? baseUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        var declarations = transforms.ToArray();
        if (declarations.Any(t => t is null) || declarations.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != declarations.Length)
            throw new ArgumentException("Transform identifiers must be distinct and declarations must not be null.", nameof(transforms));
        var registry = await CreateAsync(definitions, baseUrl, cancellationToken).ConfigureAwait(false);
        var map = registry.assets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (publicDirectory is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(publicDirectory);
            var publicRoot = Path.GetFullPath(publicDirectory);
            foreach (var sourcePath in EnumeratePublicFiles(publicRoot, cancellationToken))
            {
                var relative = Path.GetRelativePath(publicRoot, sourcePath).Replace(Path.DirectorySeparatorChar, '/');
                var asset = new SiteAsset("public:" + relative, publicRoot, relative, relative);
                await using var source = BuildInputFingerprint.OpenVerifiedContainedRead(publicRoot, sourcePath, asynchronous: true);
                using var buffer = new MemoryStream();
                await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                var bytes = buffer.ToArray();
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                map.Add(asset.Id, new RegisteredAsset(asset, bytes, hash, SiteRoute.ForFile(EscapeRoutePath(relative), baseUrl)));
            }
        }
        var ids = map.Keys.ToHashSet(StringComparer.Ordinal);
        var declaredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var transform in declarations)
        {
            foreach (var input in transform.Inputs)
                if (!map.TryGetValue(input.Id, out var registered) || !ReferenceEquals(registered.Asset, input))
                    throw new ArgumentException($"Transform input '{input.Id}' is not registered.", nameof(transforms));
            foreach (var output in transform.Outputs)
                if (!ids.Add(output.Id) || !declaredPaths.Add(output.RelativeOutputPath))
                    throw new ArgumentException($"Transform output '{output.Id}' repeats an identifier or output path.", nameof(transforms));
        }
        var nodes = registry.CreateBuildNodes().ToList();
        var executedTransforms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in map.Values.Where(value => !registry.assets.ContainsKey(value.Asset.Id)))
            nodes.Add(CopyNode(value));
        foreach (var transform in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (transform.ValidateInputs is not null)
                await transform.ValidateInputs(cancellationToken).ConfigureAwait(false);
            var inputs = transform.Inputs.ToDictionary(input => input, input => map[input.Id].Bytes);
            var inputUrls = transform.Inputs.ToDictionary(input => input, input => Url(map[input.Id]));
            var key = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                transform.Id, transform.ImplementationFingerprint,
                Handler = Content.SiteContentCollection.CaptureRendererIdentity(transform.Handler),
                Validator = transform.ValidateInputs is null ? null : Content.SiteContentCollection.CaptureRendererIdentity(transform.ValidateInputs),
                Inputs = transform.Inputs.Select(input => new { input.Id, input.RelativeInputPath, Hash = map[input.Id].Fingerprint, Url = map[input.Id].Route.PublicPath }),
                Outputs = transform.Outputs.Select(output => new { output.Id, output.RelativeOutputPath })
            }))));
            var cachePath = cacheDirectory is null ? null : Path.Combine(Path.GetFullPath(cacheDirectory), key + ".json");
            var bytesByOutput = cachePath is null ? null : ReadTransformCache(cachePath, key, transform);
            if (bytesByOutput is null)
            {
                executedTransforms.Add("asset-transform:" + transform.Id);
                var context = new SiteAssetTransformContext(inputs, transform.Outputs, inputUrls);
                await transform.Handler(context, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var output in transform.Outputs)
                    if (!context.Written.ContainsKey(output.Id))
                        throw new InvalidOperationException($"Transform '{transform.Id}' did not write output '{output.Id}'.");
                bytesByOutput = context.Written;
                if (cachePath is not null) WriteTransformCache(cachePath, key, bytesByOutput);
            }
            var owner = new BuildNodeId("asset-transform:" + transform.Id);
            foreach (var output in transform.Outputs)
            {
                var bytes = bytesByOutput[output.Id];
                var synthetic = new SiteAsset(output.Id, Path.GetPathRoot(Environment.CurrentDirectory)!, output.RelativeOutputPath, output.RelativeOutputPath);
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                map.Add(output.Id, new RegisteredAsset(synthetic, bytes, hash,
                    SiteRoute.ForFile(EscapeRoutePath(FingerprintedPath(output.RelativeOutputPath, hash)), baseUrl), output));
            }
            nodes.Add(new BuildNode(owner,
                transform.Inputs.Select(input => BuildInput.FromFile(input.RelativeInputPath, map[input.Id].Fingerprint))
                    .Append(BuildInput.FromConfiguration("transform", key)),
                transform.Inputs.Select(input => new BuildNodeId("asset:" + input.Id)),
                transform.Outputs.Select(output => new BuildArtifact(new BuildArtifactId("asset:" + output.Id), owner, map[output.Id].Route.RelativeOutputPath))));
        }
        return new AssetRegistry(new ReadOnlyDictionary<string, RegisteredAsset>(map), nodes.AsReadOnly())
        { ExecutedTransformNodes = executedTransforms };
    }

    private static IEnumerable<string> EnumeratePublicFiles(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(directory)!, directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Public entry '{entry}' must not be a symbolic link.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var file in EnumeratePublicFiles(entry, cancellationToken)) yield return file;
            }
            else yield return entry;
        }
    }

    private static BuildNode CopyNode(RegisteredAsset value) => new(new BuildNodeId("asset:" + value.Asset.Id),
        [BuildInput.FromFile(value.Asset.RelativeInputPath, value.Fingerprint)],
        artifacts: [new BuildArtifact(new BuildArtifactId("asset:" + value.Asset.Id), new BuildNodeId("asset:" + value.Asset.Id), value.Route.RelativeOutputPath)]);

    internal AssetRegistry WithGenerated(IEnumerable<SiteGeneratedAsset> declarations, string baseUrl)
    {
        var values = declarations.ToArray();
        if (values.Length == 0) return this;
        var map = assets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var nodes = CreateBuildNodes().ToList();
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            var synthetic = new SiteAsset(value.Id, Path.GetPathRoot(Environment.CurrentDirectory)!,
                value.RelativeOutputPath, value.RelativeOutputPath);
            var hash = Convert.ToHexStringLower(SHA256.HashData(value.Bytes));
            if (!map.TryAdd(value.Id, new RegisteredAsset(synthetic, value.Bytes, hash,
                    SiteRoute.ForFile(EscapeRoutePath(value.RelativeOutputPath), baseUrl))))
                throw new ArgumentException($"Asset identifier '{value.Id}' is registered more than once.", nameof(declarations));
            var owner = new BuildNodeId("asset:" + value.Id);
            nodes.Add(new BuildNode(owner,
                value.Inputs.Append(BuildInput.FromValue("asset.bytes", hash)),
                value.ReferencedAssetIds.Select(id => new BuildNodeId("asset:" + id)),
                [new BuildArtifact(new BuildArtifactId("asset:" + value.Id), owner, value.RelativeOutputPath)]));
        }
        foreach (var value in values)
            foreach (var id in value.ReferencedAssetIds)
                if (!map.ContainsKey(id)) throw new ArgumentException($"Asset '{value.Id}' references unregistered asset '{id}'.");
        return new AssetRegistry(new ReadOnlyDictionary<string, RegisteredAsset>(map), nodes.AsReadOnly())
        { ExecutedTransformNodes = ExecutedTransformNodes };
    }

    private static AssetUrl Url(RegisteredAsset value) => new(value.Asset, value.Route) { Fingerprint = value.Fingerprint, Integrity = value.Integrity };
    private AssetRegistry(IReadOnlyDictionary<string, RegisteredAsset> assets, IReadOnlyList<BuildNode>? additionalNodes = null)
    {
        this.assets = assets;
        this.additionalNodes = additionalNodes;
    }

    /// <summary>Gets a registered asset URL.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="asset"/> is <see langword="null"/>.</exception>
    /// <exception cref="AssetRegistryException">The asset is not registered in this build. The diagnostic ID is <c>LSA001</c>.</exception>
    public AssetUrl GetUrl(SiteAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return assets.TryGetValue(asset.Id, out var value) && ReferenceEquals(value.Asset, asset)
            ? new AssetUrl(value.Asset, value.Route) { Fingerprint = value.Fingerprint, Integrity = value.Integrity }
            : throw new AssetRegistryException(new SiteDiagnostic(
                "LSA001", SiteDiagnosticSeverity.Error,
                $"Asset '{asset.Id}' is not registered."));
    }

    /// <summary>Opens a read-only snapshot of a registered transformed output.</summary>
    /// <exception cref="ArgumentNullException">The output is null.</exception>
    /// <exception cref="AssetRegistryException">The same output declaration is not registered in this build.</exception>
    public Stream OpenRead(SiteAssetOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return assets.TryGetValue(output.Id, out var value) && ReferenceEquals(value.Output, output) ? new MemoryStream(value.Bytes, writable: false) : throw new AssetRegistryException(new SiteDiagnostic("LSA001", SiteDiagnosticSeverity.Error, $"Asset output '{output.Id}' is not registered."));
    }

    /// <summary>Gets a registered transformed output URL.</summary>
    /// <exception cref="ArgumentNullException">The output is null.</exception>
    /// <exception cref="AssetRegistryException">The same output declaration is not registered in this build.</exception>
    public AssetUrl GetUrl(SiteAssetOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return assets.TryGetValue(output.Id, out var value) && ReferenceEquals(value.Output, output)
            ? new AssetUrl(value.Asset, value.Route) { Fingerprint = value.Fingerprint, Integrity = value.Integrity }
            : throw new AssetRegistryException(new SiteDiagnostic("LSA001", SiteDiagnosticSeverity.Error, $"Asset output '{output.Id}' is not registered."));
    }

    /// <summary>Read-only copy, fingerprint and transform build nodes.</summary>
    public IReadOnlyList<BuildNode> BuildNodes => CreateBuildNodes();

    internal IReadOnlyList<BuildNode> CreateBuildNodes()
    {
        if (additionalNodes is not null) return additionalNodes;
        return Array.AsReadOnly(assets.Values.Select(CopyNode).ToArray());
    }

    internal IReadOnlyList<(string RelativePath, byte[] Bytes)> Files =>
        assets.Values.OrderBy(value => value.Route.RelativeOutputPath, StringComparer.Ordinal)
            .Select(value => (value.Route.RelativeOutputPath, value.Bytes))
            .ToArray();

    private static string FingerprintedPath(string path, string hash)
    {
        var directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;
        var file = Path.GetFileName(path);
        var extension = Path.GetExtension(file);
        var stem = extension.Length == 0 ? file : file[..^extension.Length];
        var fingerprinted = $"{stem}.{hash}{extension}";
        return directory.Length == 0 ? fingerprinted : $"{directory}/{fingerprinted}";
    }

    private static string EscapeRoutePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    internal IEnumerable<(SiteAsset Asset, SiteRoute Route)> RegisteredRoutes =>
        assets.Values.Select(value => (value.Asset, value.Route));

    private sealed record RegisteredAsset(SiteAsset Asset, byte[] Bytes, string Fingerprint, SiteRoute Route, SiteAssetOutput? Output = null)
    {
        public string Integrity => "sha256-" + Convert.ToBase64String(Convert.FromHexString(Fingerprint));
    }

    private sealed record CachedOutput(byte[] Bytes, string Hash);
    private sealed record TransformCache(int Version, string Key, Dictionary<string, CachedOutput> Outputs);

    private static IReadOnlyDictionary<string, byte[]>? ReadTransformCache(string path, string key, SiteAssetTransform transform)
    {
        SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(path)!, path);
        try
        {
            using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(Path.GetDirectoryName(path)!, path);
            var cache = JsonSerializer.Deserialize<TransformCache>(stream);
            if (cache is null || cache.Version != 1 || cache.Key != key || cache.Outputs is null || cache.Outputs.Count != transform.Outputs.Count) return null;
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var output in transform.Outputs)
            {
                if (!cache.Outputs.TryGetValue(output.Id, out var entry) || entry is null || entry.Bytes is null
                    || entry.Hash != Convert.ToHexStringLower(SHA256.HashData(entry.Bytes))) return null;
                result.Add(output.Id, entry.Bytes);
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void WriteTransformCache(string path, string key, IReadOnlyDictionary<string, byte[]> outputs)
    {
        SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(path)!, path);
        var temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(path)!, temp);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new TransformCache(1, key, outputs.ToDictionary(pair => pair.Key,
                    pair => new CachedOutput(pair.Value, Convert.ToHexStringLower(SHA256.HashData(pair.Value))), StringComparer.Ordinal)));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
