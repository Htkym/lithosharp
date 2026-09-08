using System.Diagnostics;
using System.Reflection;

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
        string project, string configuration, CancellationToken cancellationToken, bool logToStandardError = false)
    {
        var build = await RunDotNetAsync(["build", project, "--nologo", "-c", configuration], cancellationToken);
        if (build.ExitCode != 0)
            throw new ProjectCompilationException(
                $"LSC7002: Project build failed with exit code {build.ExitCode}."
                + Environment.NewLine + build.StandardOutput + build.StandardError);
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

internal sealed class ProjectCompilationException(string message) : Exception(message);
