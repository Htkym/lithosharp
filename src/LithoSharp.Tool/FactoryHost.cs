using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using LithoSharp.Quality;
using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.Routing;

namespace LithoSharp.Tool;

internal static class FactoryHost
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        string? responsePath = null;
        string? outputDirectory = null;
        var format = SiteDiagnosticFormat.Text;
        HostResponse response;
        try
        {
            // Keep the response channel available even when another host argument is invalid.
            var responseIndex = Array.IndexOf(args, "--response");
            if (responseIndex >= 0 && responseIndex + 1 < args.Length)
                responsePath = Path.GetFullPath(args[responseIndex + 1]);
            var options = ParseArguments(args);
            responsePath = Path.GetFullPath(Required(options, "--response"));
            var assemblyPath = Path.GetFullPath(Required(options, "--assembly"));
            var project = Path.GetFullPath(Required(options, "--project"));
            var projectDirectory = Path.TrimEndingDirectorySeparator(
                File.Exists(project) ? Path.GetDirectoryName(project)! : project);
            if (!Directory.Exists(projectDirectory))
                throw new ArgumentException($"Project directory '{projectDirectory}' does not exist.");
            var command = Required(options, "--command");
            if (command is not ("build" or "check" or "inspect" or "clean"))
                throw new ArgumentException($"Unknown host command '{command}'.");
            format = options.GetValueOrDefault("--format", "text") switch
            {
                "text" => SiteDiagnosticFormat.Text,
                "json" => SiteDiagnosticFormat.Json,
                "sarif" => SiteDiagnosticFormat.Sarif,
                var value => throw new ArgumentException($"Unknown diagnostic format '{value}'."),
            };
            cancellationToken.ThrowIfCancellationRequested();
            var loadContext = new FactoryLoadContext(assemblyPath);
            var originalDirectory = Directory.GetCurrentDirectory();
            SiteDefinition? definition = null;
            try
            {
                Directory.SetCurrentDirectory(projectDirectory);
                var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
                var factories = assembly.GetExportedTypes().Where(type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
                    && typeof(ISiteFactory).IsAssignableFrom(type)
                    && type.GetConstructor(Type.EmptyTypes) is not null).ToArray();
                if (factories.Length != 1)
                    throw new InvalidOperationException(
                        $"Site assembly must contain exactly one public, nonabstract ISiteFactory with a public parameterless constructor; found {factories.Length}.");
                var factory = (ISiteFactory)Activator.CreateInstance(factories[0])!;
                definition = await factory.CreateAsync(new SiteFactoryContext(projectDirectory), cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The site factory returned no definition.");
                outputDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                    options.GetValueOrDefault("--output") ?? definition.OutputDirectory, projectDirectory));
                var projectFromOutput = Path.GetRelativePath(outputDirectory, projectDirectory);
                if (Path.GetDirectoryName(outputDirectory) is null || projectFromOutput == "." || !Path.IsPathRooted(projectFromOutput)
                    && projectFromOutput != ".." && !projectFromOutput.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new ArgumentException("The output directory must not contain the project directory or be a file system root.");
                var generator = new SiteGenerator();
                if (command == "clean")
                {
                    var removed = await generator.CleanAsync(outputDirectory, cancellationToken).ConfigureAwait(false);
                    response = new HostResponse
                    {
                        Success = true,
                        OutputDirectory = outputDirectory,
                        RemovedFiles = removed.ToArray(),
                    };
                }
                else
                {
                    var generationOptions = definition.Options
                        ?? throw new InvalidOperationException("The site definition must provide generation options.");
                    if (command == "check")
                    {
                        if (!options.ContainsKey("--output"))
                            throw new ArgumentException("The check host requires an explicit temporary output directory.");
                        var checkRoot = Path.GetDirectoryName(outputDirectory)!;
                        var quality = generationOptions.Quality ?? new SiteQualityOptions();
                        if (quality.ExternalLinks is { } external)
                            quality = new SiteQualityOptions(quality.FailureThreshold, quality.CheckOrphans,
                                new ExternalLinkCheckOptions(Path.Combine(checkRoot, "external-links.json"),
                                    external.CacheDuration, external.MinimumRequestInterval, external.RequestTimeout));
                        generationOptions = generationOptions with
                        {
                            Quality = quality,
                            BuildCacheDirectory = Path.Combine(checkRoot, "build-cache"),
                            AssetCacheDirectory = generationOptions.AssetCacheDirectory is null
                                ? null : Path.Combine(checkRoot, "asset-cache"),
                        };
                    }
                    var result = await generator.GenerateWithOptionsAsync(definition.Site, definition.Posts,
                        outputDirectory, command == "check" || options.ContainsKey("--clean"), definition.Customization,
                        generationOptions, cancellationToken).ConfigureAwait(false);
                    response = FromResult(result, generationOptions, format);
                }
            }
            finally
            {
                if (definition is not null)
                    foreach (var extension in definition.Options.Extensions.Reverse().Distinct())
                        if (extension is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        else if (extension is IDisposable disposable) disposable.Dispose();
                Directory.SetCurrentDirectory(originalDirectory);
                loadContext.Unload();
            }
        }
        catch (SiteQualityValidationException exception)
        {
            response = new HostResponse
            {
                ExitCode = 1,
                OutputDirectory = outputDirectory,
                Error = exception.Message,
                DiagnosticsText = exception.Report.Format(format),
            };
        }
        catch (OperationCanceledException)
        {
            response = new HostResponse { ExitCode = 130, OutputDirectory = outputDirectory, Error = "The operation was canceled." };
        }
        catch (Exception exception)
        {
            IReadOnlyList<SiteDiagnostic> diagnostics = exception switch
            {
                SiteRouteValidationException routes => routes.Diagnostics,
                SiteBuildPlanValidationException plan => plan.Diagnostics,
                SiteBuildExtensionException extension => extension.Diagnostics,
                AssetRegistryException asset => [asset.Diagnostic],
                _ => [new SiteDiagnostic("LSC7001", SiteDiagnosticSeverity.Error, Describe(exception))]
            };
            response = new HostResponse
            {
                ExitCode = 1, OutputDirectory = outputDirectory, Error = Describe(exception),
                DiagnosticsText = new SiteQualityReport(diagnostics).Format(format)
            };
        }

        if (responsePath is null)
        {
            await Console.Error.WriteLineAsync(response.Error ?? "A host response path is required.").ConfigureAwait(false);
            return response.ExitCode == 0 ? 1 : response.ExitCode;
        }
        try
        {
            await WriteResponseAsync(responsePath, response).ConfigureAwait(false);
            return response.ExitCode;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Could not write the host response: {exception.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static HostResponse FromResult(
        SiteGenerationResult result, SiteGenerationOptions options, SiteDiagnosticFormat format)
    {
        var routes = result.Routes.ToDictionary(route => route.RelativeOutputPath, StringComparer.Ordinal);
        return new HostResponse
        {
            Success = true,
            OutputDirectory = result.OutputDirectory,
            Extensions = options.Extensions.Select(extension => extension.GetInspection()).Where(value => value.HasValue).Select(value => value!.Value).ToArray(),
            DiagnosticsText = result.QualityReport.Format(format),
            GeneratedFiles = result.GeneratedFiles.Select(path => Path.GetRelativePath(result.OutputDirectory, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal).ToArray(),
            IgnoredPaths = new[]
                {
                    result.OutputDirectory,
                    options.BuildCacheDirectory ?? Path.Combine(Path.GetDirectoryName(result.OutputDirectory)!, ".lithosharp"),
                    options.AssetCacheDirectory,
                    options.Quality?.ExternalLinks?.CacheFilePath,
                }
                .Where(static path => path is not null).Select(static path => Path.GetFullPath(path!))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray(),
            BuildReport = new HostBuildReport(result.BuildReport.CacheHitCount, result.BuildReport.CacheMissCount,
                result.BuildReport.GeneratedArtifacts.ToArray(), result.BuildReport.SkippedArtifacts.ToArray(),
                result.BuildReport.StaleRemovedArtifacts.ToArray(), result.BuildReport.TransactionOutcome,
                result.BuildReport.RetainedRecoveryState,
                result.BuildReport.Nodes.Select(node => new HostReportNode(node.NodeId, node.OwnedArtifacts.ToArray(), node.CacheHit, node.CacheMissReason)).ToArray()),
            BuildPlan = result.BuildPlan.Nodes.Select(node => new HostBuildNode(node.Id.Value,
                node.Inputs.Select(input => new HostBuildInput(input.Kind.ToString(), input.Key, input.Value)).ToArray(),
                node.Dependencies.Select(id => id.Value).ToArray(),
                node.Artifacts.Select(artifact => new HostBuildArtifact(artifact.Id.Value, artifact.RelativeOutputPath,
                    routes[artifact.RelativeOutputPath].PublicPath)).ToArray())).ToArray(),
        };
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = args.FirstOrDefault() == "__host" ? 1 : 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name is not ("--assembly" or "--project" or "--command" or "--output" or "--response" or "--clean" or "--format"))
                throw new ArgumentException($"Unknown host option '{name}'.");
            var value = name == "--clean" ? "true" : ++index < args.Length
                ? args[index] : throw new ArgumentException($"Host option '{name}' requires a value.");
            if (!options.TryAdd(name, value)) throw new ArgumentException($"Host option '{name}' was specified more than once.");
        }
        return options;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"Host option '{name}' is required.");

    private static string Describe(Exception exception) => exception is ReflectionTypeLoadException types
        ? string.Join(Environment.NewLine, types.LoaderExceptions.Where(item => item is not null).Select(item => item!.Message))
        : exception.GetBaseException().Message;

    private static async Task WriteResponseAsync(string path, HostResponse response)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, response, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FactoryLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var contract = typeof(ISiteFactory).Assembly;
            if (string.Equals(assemblyName.Name, contract.GetName().Name, StringComparison.Ordinal)) return contract;
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

internal sealed record HostResponse
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string? OutputDirectory { get; init; }
    public string? Error { get; init; }
    public string? DiagnosticsText { get; init; }
    public string[] GeneratedFiles { get; init; } = [];
    public string[] RemovedFiles { get; init; } = [];
    public string[] IgnoredPaths { get; init; } = [];
    public HostBuildReport? BuildReport { get; init; }
    public HostBuildNode[] BuildPlan { get; init; } = [];
    public JsonElement[] Extensions { get; init; } = [];
}

internal sealed record HostBuildReport(int CacheHitCount, int CacheMissCount, string[] GeneratedArtifacts,
    string[] SkippedArtifacts, string[] StaleRemovedArtifacts, string TransactionOutcome, bool RetainedRecoveryState,
    HostReportNode[] Nodes);
internal sealed record HostReportNode(string Id, string[] Artifacts, bool CacheHit, string? CacheMissReason);
internal sealed record HostBuildNode(string Id, HostBuildInput[] Inputs, string[] Dependencies, HostBuildArtifact[] Artifacts);
internal sealed record HostBuildInput(string Kind, string Key, string? Value);
internal sealed record HostBuildArtifact(string Id, string Path, string PublicPath);
