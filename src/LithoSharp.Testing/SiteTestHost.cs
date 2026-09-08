using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Testing;

#pragma warning disable RS0026 // Factory/context and definition overloads have disjoint required argument types.

/// <summary>Generates a site in an isolated temporary directory and checks its outputs.</summary>
/// <remarks>Dispose the host to remove its output, caches, and ownership files. Source files are never copied or deleted.</remarks>
public sealed class SiteTestHost : IAsyncDisposable
{
    private readonly string root;
    private bool disposed;
    private SiteTestHost(string root) { this.root = root; OutputDirectory = Path.Combine(root, "output"); }

    /// <summary>Gets the temporary output directory. It is removed on disposal.</summary>
    public string OutputDirectory { get; }
    /// <summary>Gets the generation result, or null when structured validation failed.</summary>
    public SiteGenerationResult? Result { get; private set; }
    /// <summary>Gets generation or validation diagnostics.</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; private set; } = [];
    /// <summary>Gets whether generation committed successfully.</summary>
    public bool Succeeded => Result is not null;

    /// <summary>Loads and generates a factory's site using the supplied source context.</summary>
    /// <exception cref="ArgumentNullException">Factory or context is null.</exception>
    /// <exception cref="InvalidOperationException">The factory returns null.</exception>
    /// <remarks>Factory, renderer, I/O, and cancellation exceptions propagate. Only structured generation validation is captured.</remarks>
    public static async Task<SiteTestHost> CreateAsync(ISiteFactory factory, SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var definition = await factory.CreateAsync(context, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The site factory returned null.");
        return await CreateAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Generates a definition with isolated output and caches, and quality checks enabled by default.</summary>
    /// <exception cref="ArgumentNullException">The definition is null.</exception>
    /// <remarks>Uses Unix epoch if no timestamp is supplied. Explicit quality options are honored; network checks remain off by default.
    /// Structured route, build-plan, and quality failures return a failed host with diagnostics. Other exceptions propagate after cleanup.</remarks>
    public static async Task<SiteTestHost> CreateAsync(SiteDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();
        var host = new SiteTestHost(SiteGenerator.CreateTemporaryDirectory("lithosharp-test-"));
        try
        {
            var quality = definition.Options.Quality ?? new SiteQualityOptions();
            if (quality.ExternalLinks is { } external)
                quality = new SiteQualityOptions(quality.FailureThreshold, quality.CheckOrphans,
                    new ExternalLinkCheckOptions(Path.Combine(host.root, "external-links.json"),
                        external.CacheDuration, external.MinimumRequestInterval, external.RequestTimeout));
            var options = definition.Options with
            {
                BuildCacheDirectory = Path.Combine(host.root, "build-cache"),
                AssetCacheDirectory = definition.Options.AssetCacheDirectory is null ? null : Path.Combine(host.root, "asset-cache"),
                BuildTimestamp = definition.Options.BuildTimestamp ?? DateTimeOffset.UnixEpoch,
                Quality = quality,
            };
            try
            {
                host.Result = await new SiteGenerator().GenerateWithOptionsAsync(definition.Site, definition.Posts,
                    host.OutputDirectory, false, definition.Customization, options, cancellationToken).ConfigureAwait(false);
                host.Diagnostics = host.Result.BuildReport.Diagnostics;
            }
            catch (SiteRouteValidationException exception) { host.Diagnostics = exception.Diagnostics; }
            catch (SiteBuildPlanValidationException exception) { host.Diagnostics = exception.Diagnostics; }
            catch (SiteQualityValidationException exception) { host.Diagnostics = exception.Diagnostics; }
            return host;
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Asserts that generation succeeded, including any configured quality threshold.</summary>
    /// <exception cref="SiteTestException">Generation failed.</exception>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public void AssertSucceeded()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Succeeded) throw new SiteTestException("Site generation failed: " + string.Join(Environment.NewLine, Diagnostics.Select(d => $"{d.Id}: {d.Message}")));
    }

    /// <summary>Asserts an exact public path, optionally checking its relative output path.</summary>
    /// <exception cref="SiteTestException">Generation failed or the route does not match.</exception>
    /// <exception cref="ArgumentException">The public path is blank.</exception>
    public SiteRoute AssertRoute(string publicPath, string? relativeOutputPath = null)
    {
        AssertSucceeded();
        ArgumentException.ThrowIfNullOrWhiteSpace(publicPath);
        var route = Result!.Routes.FirstOrDefault(route => route.PublicPath == publicPath);
        if (route is null || (relativeOutputPath is not null && route.RelativeOutputPath != relativeOutputPath))
            throw new SiteTestException($"Expected route '{publicPath}' with output '{relativeOutputPath ?? "any"}'.");
        return route;
    }

    /// <summary>Asserts that no generated route has the exact public path.</summary>
    /// <exception cref="SiteTestException">Generation failed or the route exists.</exception>
    /// <exception cref="ArgumentException">The public path is blank.</exception>
    public void AssertNoRoute(string publicPath)
    {
        AssertSucceeded();
        ArgumentException.ThrowIfNullOrWhiteSpace(publicPath);
        if (Result!.Routes.Any(route => route.PublicPath == publicPath)) throw new SiteTestException($"Unexpected route '{publicPath}'.");
    }

    /// <summary>Asserts that a declared artifact exists as a regular contained file, returning its absolute path.</summary>
    /// <exception cref="SiteTestException">Generation failed or the artifact is undeclared or missing.</exception>
    /// <exception cref="ArgumentException">The relative output path is invalid.</exception>
    /// <exception cref="InvalidOperationException">The artifact crosses a link or escapes the output directory.</exception>
    public string AssertArtifact(string relativeOutputPath)
    {
        var path = ArtifactPath(relativeOutputPath);
        var normalized = SiteRoute.NormalizeRelativeOutputPath(relativeOutputPath);
        if (!Result!.Routes.Any(route => route.RelativeOutputPath == normalized) || !File.Exists(path))
            throw new SiteTestException($"Expected generated artifact '{relativeOutputPath}'.");
        using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(OutputDirectory, path);
        return path;
    }

    /// <summary>Asserts that a path is neither declared nor present in the output directory.</summary>
    /// <exception cref="SiteTestException">Generation failed or the artifact exists.</exception>
    /// <exception cref="ArgumentException">The relative output path is invalid.</exception>
    /// <exception cref="InvalidOperationException">The path crosses a link or escapes the output directory.</exception>
    public void AssertNoArtifact(string relativeOutputPath)
    {
        var path = ArtifactPath(relativeOutputPath);
        var normalized = SiteRoute.NormalizeRelativeOutputPath(relativeOutputPath);
        if (Result!.Routes.Any(route => route.RelativeOutputPath == normalized) || Path.Exists(path))
            throw new SiteTestException($"Unexpected artifact '{relativeOutputPath}'.");
    }

    /// <summary>Returns the first diagnostic matching an ID and optional severity and ordinal message substring.</summary>
    /// <exception cref="SiteTestException">No diagnostic matches.</exception>
    /// <exception cref="ArgumentException">The ID is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The severity is undefined.</exception>
    public SiteDiagnostic AssertDiagnostic(string id, SiteDiagnosticSeverity? severity = null, string? messageContains = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (severity is not null && !Enum.IsDefined(severity.Value)) throw new ArgumentOutOfRangeException(nameof(severity));
        return Diagnostics.FirstOrDefault(d => d.Id == id && (severity is null || d.Severity == severity)
            && (messageContains is null || d.Message.Contains(messageContains, StringComparison.Ordinal)))
            ?? throw new SiteTestException($"Expected diagnostic '{id}'.");
    }

    /// <summary>Asserts that no diagnostics meet or exceed the minimum severity.</summary>
    /// <exception cref="SiteTestException">A diagnostic meets the threshold.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The severity is undefined.</exception>
    public void AssertNoDiagnostics(SiteDiagnosticSeverity minimumSeverity = SiteDiagnosticSeverity.Warning)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Enum.IsDefined(minimumSeverity)) throw new ArgumentOutOfRangeException(nameof(minimumSeverity));
        var unexpected = Diagnostics.Where(d => d.Severity >= minimumSeverity).ToArray();
        if (unexpected.Length != 0) throw new SiteTestException(string.Join(Environment.NewLine, unexpected.Select(d => $"{d.Id}: {d.Message}")));
    }

    /// <summary>Opens the HTML artifact for an exact public route without running scripts or network requests.</summary>
    /// <exception cref="SiteTestException">The route or artifact is missing, or does not name HTML.</exception>
    /// <exception cref="InvalidOperationException">The path crosses a link or escapes the output directory.</exception>
    public async Task<SiteTestDocument> OpenPageAsync(string publicPath, CancellationToken cancellationToken = default)
    {
        var route = AssertRoute(publicPath);
        if (!route.RelativeOutputPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) throw new SiteTestException($"Route '{publicPath}' is not an HTML page.");
        var path = AssertArtifact(route.RelativeOutputPath);
        await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(OutputDirectory, path, asynchronous: true);
        using var reader = new StreamReader(stream);
        var document = SiteTestDocument.Parse(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
        document.Route = route;
        return document;
    }

    /// <summary>Removes this host's temporary tree. Repeated disposal is harmless.</summary>
    /// <exception cref="InvalidOperationException">A temporary path has been replaced by a symbolic link or junction.</exception>
    /// <remarks>Rejects linked paths before deleting. The tree is retained if cleanup fails so it can be inspected.</remarks>
    public ValueTask DisposeAsync()
    {
        if (disposed) return ValueTask.CompletedTask;
        SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(root, root);
        if (Directory.Exists(root))
        {
            DeleteTree(root);
        }
        disposed = true;
        return ValueTask.CompletedTask;
    }

    private string ArtifactPath(string relativeOutputPath)
    {
        AssertSucceeded();
        var path = Path.Combine(OutputDirectory, SiteRoute.NormalizeRelativeOutputPath(relativeOutputPath));
        SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(root, path);
        return path;
    }

    private void DeleteTree(string directory)
    {
        SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(root, directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(root, entry);
            if (Directory.Exists(entry)) DeleteTree(entry);
            else File.Delete(entry);
        }
        SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(root, directory);
        Directory.Delete(directory);
    }
}
