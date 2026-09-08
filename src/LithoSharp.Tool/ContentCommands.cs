using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LithoSharp.Documentation;

namespace LithoSharp.Tool;

internal static class ContentCommands
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args[0] == "snapshot")
        {
            if (args.Length != 4) throw new CliUsageException("Usage: snapshot <source> <destination> <version>");
            await DocumentSnapshot.CreateAsync(args[1], args[2], args[3], cancellationToken);
            Console.WriteLine(Path.GetFullPath(args[2]));
            return 0;
        }
        if (args.Length < 2 || args.Length > 3) throw new CliUsageException("Specify an input directory.");
        var root = Path.GetFullPath(args[1]);
        if (args[0] == "restore-mdx")
        {
            if (args.Length == 3 && args[2] != "--allow-scripts") throw new CliUsageException("Only --allow-scripts is supported.");
            if (!File.Exists(Path.Combine(root, "package-lock.json")) || !File.Exists(Path.Combine(root, "worker.mjs")))
                throw new CliUsageException("Specify the packaged worker directory with its lockfile.");
            // npm.cmd is a Windows launcher; fixed arguments never contain user paths or shell syntax.
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "npm") { WorkingDirectory = root, UseShellExecute = false };
            if (OperatingSystem.IsWindows()) { start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("npm.cmd"); }
            start.ArgumentList.Add("ci"); start.ArgumentList.Add("--no-audit"); start.ArgumentList.Add("--no-fund");
            if (args.Length == 2) start.ArgumentList.Add("--ignore-scripts");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start npm.");
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); throw; }
            return process.ExitCode;
        }
        if (args.Length != 2) throw new CliUsageException("This command accepts only an input directory.");
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar).Any(segment => segment is "node_modules" or ".git" or "bin" or "obj"))
            .Where(file => Path.GetExtension(file) is ".md" or ".mdx" or ".js" or ".jsx" or ".ts" or ".tsx" or ".json").Order(StringComparer.Ordinal).ToArray();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files) texts.Add(Path.GetRelativePath(root, file).Replace('\\', '/'), await File.ReadAllTextAsync(file, cancellationToken));
        object report;
        if (args[0] == "extract-translations") report = texts.Values.SelectMany(TranslationCatalog.ExtractKeys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToDictionary(key => key, _ => "");
        else
        {
            var diagnostics = new List<object>();
            foreach (var (file, text) in texts)
            {
                if (Path.GetFileName(file).StartsWith("docusaurus.config", StringComparison.Ordinal) || Path.GetFileName(file).StartsWith("sidebars.", StringComparison.Ordinal))
                    diagnostics.Add(new { file, line = 1, id = "LSMIG001", message = "JavaScript configuration was not executed. Map routes, variants, sidebars and plugins explicitly to the C# preset." });
                foreach (Match match in Regex.Matches(text, "(?:from\\s*|import\\s*)[\"'](?<name>@(?:docusaurus|theme)/[^\"']+)[\"']"))
                {
                    var name = match.Groups["name"].Value;
                    if (name is "@docusaurus/BrowserOnly" || name.StartsWith("@theme/", StringComparison.Ordinal) && new[] { "Tabs", "TabItem", "Admonition", "Details", "CodeBlock", "TOCInline", "Card", "MDXComponents", "BrowserOnly" }.Contains(name[7..])) continue;
                    diagnostics.Add(new { file, line = text.AsSpan(0, match.Index).Count('\n') + 1, id = "LSMIG002", message = "Unsupported import requires an explicit replacement: " + name });
                }
                if (text.Contains("@docusaurus/plugin-", StringComparison.Ordinal) || text.Contains("@docusaurus/theme-", StringComparison.Ordinal))
                    diagnostics.Add(new { file, line = 1, id = "LSMIG003", message = "Docusaurus plugins and themes are not loaded by LithoSharp. Select a supported C# or Remark/Rehype extension." });
            }
            report = new { dryRun = true, executedConfiguration = false, files = texts.Keys, diagnostics,
                nextSteps = new[] { "Create a lithosharp-mdx project.", "Copy trusted content, components and assets to the project.", "Configure explicit collections, variants, author profiles and sidebars.", "Restore pinned dependencies explicitly, then run check to validate strict front matter and MDX." } };
        }
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return 0;
    }
}
