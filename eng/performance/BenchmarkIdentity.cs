using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LithoSharp.Performance;

// Shared by the site and MDX harnesses. Versions below describe loaded code,
// never a package declaration substituted for an observed dependency.
internal static class BenchmarkIdentity
{
    internal static object Capture(string profile, string? workerDirectory = null) => new
    {
        product = AssemblyIdentity(typeof(SiteGenerator).Assembly),
        harness = AssemblyIdentity(Assembly.GetExecutingAssembly()),
        buildKind = "source-build",
        runtime = RuntimeInformation.FrameworkDescription,
        processId = Environment.ProcessId,
        markdown = MarkdownRuntime(),
        workloadProfile = profile,
        workerLockSha256 = workerDirectory is null ? null : Hash(Path.Combine(workerDirectory, "package-lock.json")),
        node = workerDirectory is null ? null : Command("node", "--version"),
        timingNote = "Verification intervals overlap execution; stage durations must not be summed as total elapsed time."
    };

    internal static object MarkdownRuntime()
    {
        // Read the actual options instantiated by Core, avoiding a benchmark's
        // own optional embedded Source types shadowing the runtime assembly.
        var adapter = typeof(SiteGenerator).Assembly.GetType("LithoSharp.Content.Compilation.MarkdownAnalysisAdapter");
        var options = adapter?.GetField("BodyOptions", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        var assembly = options?.GetType().Assembly;
        return new
        {
            status = assembly is null ? "unavailable" : "measured",
            runtime = assembly is null ? null : AssemblyIdentity(assembly),
            parserVersion = assembly?.GetType("Syntamark.Compilation.MdParserVersion")?.GetField("Value", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetRawConstantValue(),
            profile = options?.GetType().GetProperty("ProfileId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(options),
            source = new { status = "not-executed", reason = "Core uses its runtime assembly; the embedded Source host is measured separately by GeneratorWorkload." }
        };
    }

    internal static object AssemblyIdentity(Assembly assembly) => new
    {
        assembly = assembly.FullName,
        informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        sha256 = Hash(assembly.Location)
    };

    internal static string? Hash(string path) => File.Exists(path)
        ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : null;

    internal static string? Command(string command, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(true);
                process.WaitForExit();
                return null;
            }
            stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? stdout.GetAwaiter().GetResult().Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
