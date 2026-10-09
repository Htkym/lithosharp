using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LithoSharp.Diagnostics;

namespace LithoSharp.Tool;

internal static class ProjectCompiler
{
    internal static string ResolveProject(string? value)
    {
        var path = Path.GetFullPath(value ?? Directory.GetCurrentDirectory());
        if (File.Exists(path))
        {
            if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new CliUsageException($"Project must be a .csproj file: {path}");
            return path;
        }
        if (!Directory.Exists(path)) throw new CliUsageException($"Project was not found: {path}");
        var projects = Directory.EnumerateFiles(path, "*.csproj", SearchOption.TopDirectoryOnly).ToArray();
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new CliUsageException($"No .csproj file was found in {path}"),
            _ => throw new CliUsageException($"More than one .csproj file was found in {path}; specify one explicitly."),
        };
    }

    internal static async Task<string> BuildAsync(
        string project, string configuration, CancellationToken cancellationToken, bool logToStandardError = false, string? compilerErrorLog = null)
    {
        var arguments = new List<string> { "build", project, "--nologo", "-c", configuration };
        if (compilerErrorLog is not null) arguments.Add("-p:ErrorLog=" + compilerErrorLog.Replace("%", "%25").Replace(",", "%2C")
            .Replace(";", "%3B").Replace("$", "%24") + "%2Cversion=2.1");
        var build = await RunDotNetAsync(arguments, cancellationToken);
        if (build.ExitCode != 0)
        {
            if (compilerErrorLog is not null) WriteProcessOutput(build, allToStandardError: true);
            throw new ProjectCompilationException(
                $"LSC7002: Project build failed with exit code {build.ExitCode}."
                + (compilerErrorLog is null ? Environment.NewLine + build.StandardOutput + build.StandardError : ""),
                ReadCompilerDiagnostics(compilerErrorLog, build.ExitCode));
        }
        WriteProcessOutput(build, logToStandardError);
        var query = await RunDotNetAsync([
            "msbuild", project, "-nologo", $"-p:Configuration={configuration}", "-getProperty:TargetPath"
        ], cancellationToken);
        if (query.ExitCode != 0) throw new InvalidOperationException(query.StandardError.Trim());
        var target = query.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim()).LastOrDefault(static line => line.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (target is null) throw new InvalidOperationException("MSBuild did not report the project's TargetPath.");
        target = Path.GetFullPath(target, Path.GetDirectoryName(project)!);
        if (!File.Exists(target)) throw new InvalidOperationException($"Built site assembly was not found: {target}");
        return target;
    }

    private static IReadOnlyList<SiteDiagnostic> ReadCompilerDiagnostics(string? path, int exitCode)
    {
        var diagnostics = new List<SiteDiagnostic>();
        if (path is not null && File.Exists(path))
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var run in json.RootElement.GetProperty("runs").EnumerateArray())
            foreach (var result in run.GetProperty("results").EnumerateArray())
            {
                if (result.TryGetProperty("suppressions", out var suppressions) && suppressions.EnumerateArray()
                    .Any(s => s.TryGetProperty("status", out var status) && status.GetString() == "accepted")) continue;
                var severity = result.GetProperty("level").GetString() switch
                { "error" => SiteDiagnosticSeverity.Error, "warning" => SiteDiagnosticSeverity.Warning, _ => SiteDiagnosticSeverity.Info };
                SiteSourceLocation? location = null;
                if (result.TryGetProperty("locations", out var locations) && locations.GetArrayLength() > 0)
                {
                    var physical = locations[0].GetProperty("physicalLocation");
                    var file = physical.GetProperty("artifactLocation").GetProperty("uri").GetString()!;
                    if (Uri.TryCreate(file, UriKind.Absolute, out var uri) && uri.IsFile) file = uri.LocalPath;
                    var region = physical.GetProperty("region");
                    int? Position(string key) => region.TryGetProperty(key, out var value) ? value.GetInt32() : null;
                    location = new(file, Position("startLine"), Position("startColumn"), Position("endLine"), Position("endColumn"));
                }
                diagnostics.Add(new(result.GetProperty("ruleId").GetString()!, severity, result.GetProperty("message").GetProperty("text").GetString()!, location));
            }
        }
        if (!diagnostics.Any(d => d.Severity == SiteDiagnosticSeverity.Error))
            diagnostics.Add(new("LSC7002", SiteDiagnosticSeverity.Error, $"Project compilation failed with exit code {exitCode}; no complete compiler Error snapshot is available."));
        return diagnostics;
    }

    internal static async Task<ProcessResult> RunDotNetAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start dotnet.");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    internal static ProcessStartInfo SelfStartInfo()
    {
        var entry = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrEmpty(entry))
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            start.ArgumentList.Add(entry);
            return start;
        }
        return new ProcessStartInfo(Environment.ProcessPath
            ?? throw new InvalidOperationException("The tool executable path is unavailable.")) { UseShellExecute = false };
    }

    internal static void WriteProcessOutput(ProcessResult result, bool allToStandardError = false)
    {
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            (allToStandardError ? Console.Error : Console.Out).Write(result.StandardOutput);
        if (!string.IsNullOrWhiteSpace(result.StandardError)) Console.Error.Write(result.StandardError);
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

internal sealed class ProjectCompilationException(string message, IReadOnlyList<SiteDiagnostic>? diagnostics = null) : Exception(message)
{
    internal IReadOnlyList<SiteDiagnostic> Diagnostics { get; } = diagnostics ?? [];
}
