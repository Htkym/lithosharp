using System.Text.Json;
using System.Xml.Linq;

namespace LithoSharp.Tool;

internal sealed class CompilerDiagnosticCapture : IDisposable
{
    private readonly List<(string Hook, string Logs, string WorkingDirectory)> projects = [];
    internal string Session { get; } = Guid.NewGuid().ToString("N");

    internal static async Task<CompilerDiagnosticCapture> CreateAsync(
        string project, string configuration, string output, string workingDirectory, CancellationToken token)
    {
        var capture = new CompilerDiagnosticCapture();
        try
        {
            var graphPath = output + ".graph.json";
            var graph = await ProjectCompiler.RunDotNetAsync([
                "msbuild", project, "-nologo", "-t:GenerateRestoreGraphFile",
                "-p:Configuration=" + configuration, "-p:RestoreGraphOutputPath=" + Escape(graphPath)
            ], token, workingDirectory);
            if (graph.ExitCode != 0 || !File.Exists(graphPath)) return capture;
            using var json = JsonDocument.Parse(File.ReadAllBytes(graphPath));
            foreach (var item in json.RootElement.GetProperty("projects").EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var restore = item.Value.GetProperty("restore");
                var projectPath = restore.GetProperty("projectPath").GetString()!;
                var extensions = restore.GetProperty("outputPath").GetString()!;
                var directory = Path.GetDirectoryName(projectPath)!;
                var logs = Path.Combine(output + ".logs", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(logs);
                Directory.CreateDirectory(extensions);
                var hook = Path.Combine(extensions, Path.GetFileName(projectPath) + ".lithosharp-" + capture.Session + ".targets");
                using var stream = new FileStream(hook, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                capture.projects.Add((hook, logs, directory));
                // Project-extension imports preserve user hooks; the session isolates concurrent builds.
                new XDocument(new XElement("Project", new XElement("Target",
                    new XAttribute("Name", "LithoSharpCompilerDiagnostics_" + capture.Session),
                    new XAttribute("BeforeTargets", "CoreCompile"),
                    new XAttribute("Condition", "'$(LithoSharpCompilerDiagnosticSession)' == '" + capture.Session + "'"),
                    new XElement("PropertyGroup", new XElement("ErrorLog",
                        Escape(logs + Path.DirectorySeparatorChar) + "$(TargetFramework)-$([System.Guid]::NewGuid().ToString('N')).sarif,version=2.1")))))
                    .Save(stream);
            }
            return capture;
        }
        catch { capture.Dispose(); throw; }
    }

    internal IEnumerable<(string Path, string WorkingDirectory)> Reports()
        => projects.SelectMany(project => Directory.EnumerateFiles(project.Logs, "*.sarif")
            .OrderBy(path => path, StringComparer.Ordinal).Select(path => (path, project.WorkingDirectory)));

    private static string Escape(string value)
    {
        foreach (var character in new[] { '%', '$', '@', ';', '\'', '(', ')', '*', '?' })
            value = value.Replace(character.ToString(), "%" + ((int)character).ToString("X2"), StringComparison.Ordinal);
        return value;
    }

    public void Dispose()
    {
        foreach (var project in projects) File.Delete(project.Hook);
    }
}
