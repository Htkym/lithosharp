using System.Collections.ObjectModel;
using System.Security.Cryptography;
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
    /// <remarks>Registration reads a regular file beneath the input root. Missing files and symbolic links fail before output is changed.</remarks>
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

    /// <summary>The registered asset.</summary>
    public SiteAsset Asset { get; }
    /// <summary>The asset route.</summary>
    public SiteRoute Route { get; }
    /// <summary>The public URL.</summary>
    public string Value => Route.PublicPath;
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

    private AssetRegistry(IReadOnlyDictionary<string, RegisteredAsset> assets) => this.assets = assets;

    /// <summary>Gets a registered asset URL.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="asset"/> is <see langword="null"/>.</exception>
    /// <exception cref="AssetRegistryException">The asset is not registered in this build. The diagnostic ID is <c>LSA001</c>.</exception>
    public AssetUrl GetUrl(SiteAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return assets.TryGetValue(asset.Id, out var value) && ReferenceEquals(value.Asset, asset)
            ? new AssetUrl(value.Asset, value.Route)
            : throw new AssetRegistryException(new SiteDiagnostic(
                "LSA001", SiteDiagnosticSeverity.Error,
                $"Asset '{asset.Id}' is not registered."));
    }

    internal IReadOnlyList<BuildNode> CreateBuildNodes()
    {
        return assets.Values.Select(value => new BuildNode(
            new BuildNodeId($"asset:{value.Asset.Id}"),
            [BuildInput.FromFile(value.Asset.RelativeInputPath, value.Fingerprint)],
            artifacts: [new BuildArtifact(
                new BuildArtifactId($"asset:{value.Asset.Id}"),
                new BuildNodeId($"asset:{value.Asset.Id}"),
                value.Route.RelativeOutputPath)])).ToArray();
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

    private sealed record RegisteredAsset(SiteAsset Asset, byte[] Bytes, string Fingerprint, SiteRoute Route);
}
