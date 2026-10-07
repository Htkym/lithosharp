using System.Text.Json;
using LithoSharp.Inspection;

namespace LithoSharp.Tool;

/// <summary>
/// Explicit Markdown compatibility check. It never builds a site; it inspects one
/// file with the same analysis as <see cref="DocumentInspection"/> and reports
/// unsupported-syntax advisories. New advisories are opt-in via --advisory, and an
/// explicitly acquired project snapshot can be supplied via --project.
/// </summary>
internal static class MarkdownCompatCommand
{
    private const string Usage =
        "Usage: lithosharp markdown-compat <markdown-file> [--advisory on|off] [--format text|json] [--project <snapshot.json>]";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static int Run(string[] args)
    {
        string? file = null;
        var advisory = "off";
        var format = "text";
        string? projectFile = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--advisory")
            {
                if (++index >= args.Length) throw new CliUsageException(Usage);
                advisory = args[index];
            }
            else if (argument is "-f" or "--format")
            {
                if (++index >= args.Length) throw new CliUsageException(Usage);
                format = args[index];
            }
            else if (argument is "--project")
            {
                if (++index >= args.Length) throw new CliUsageException(Usage);
                projectFile = args[index];
            }
            else if (argument.StartsWith('-'))
            {
                throw new CliUsageException(Usage + $" Unknown option '{argument}'.");
            }
            else
            {
                if (file is not null) throw new CliUsageException(Usage);
                file = argument;
            }
        }

        if (file is null) throw new CliUsageException(Usage);
        if (!CompatibilityAdvisoryOption.TryParse(advisory, out var enabled))
            throw new CliUsageException(Usage + $" Unknown advisory '{advisory}'. Supported values: on, off.");
        if (format is not ("text" or "json"))
            throw new CliUsageException(Usage + $" Unknown format '{format}'. Supported formats: text, json.");

        ProjectInspectionSnapshot? project = null;
        if (projectFile is not null)
        {
            var projectPath = Path.GetFullPath(projectFile);
            if (!File.Exists(projectPath)) throw new CliUsageException($"Project snapshot '{projectFile}' does not exist.");
            try
            {
                project = ProjectInspectionSnapshot.ParseJson(File.ReadAllText(projectPath));
            }
            catch (ArgumentException exception)
            {
                throw new CliUsageException(Usage + $" {exception.Message}");
            }
        }

        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) throw new CliUsageException($"Markdown file '{file}' does not exist.");
        var text = File.ReadAllText(path);
        var info = DocumentInspection.Inspect(path, text, new DocumentInspectionOptions
        {
            EnableCompatibilityAdvisory = enabled,
            Project = project,
        });

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = MachineOutput.SchemaVersion,
                success = true,
                exitCode = 0,
                file = path,
                advisory,
                projectStatus = info.ProjectStatus.ToString(),
                schema = info.SchemaName,
                route = info.Route,
                routes = info.RouteCandidates.Select(route => new
                {
                    sourcePath = route.SourcePath,
                    publicPath = route.PublicPath,
                    projectId = route.ProjectId,
                    collection = route.Collection,
                    version = route.Version,
                    locale = route.Locale,
                    publication = route.Publication.ToString(),
                }).ToArray(),
                diagnostics = info.Diagnostics.Select(diagnostic => new
                {
                    id = diagnostic.Id,
                    severity = diagnostic.Severity.ToString(),
                    message = diagnostic.Message,
                    line = diagnostic.Location?.Line,
                    column = diagnostic.Location?.Column,
                }).ToArray(),
            }, JsonOptions));
            return 0;
        }

        foreach (var diagnostic in info.Diagnostics)
        {
            var location = diagnostic.Location is null
                ? string.Empty
                : $" {diagnostic.Location.Line}:{diagnostic.Location.Column}";
            Console.WriteLine($"{diagnostic.Id}{location} {diagnostic.Severity}: {diagnostic.Message}");
        }

        if (project is null)
        {
            if (info.Diagnostics.Count == 0)
            {
                Console.WriteLine("No compatibility diagnostics.");
            }

            return 0;
        }

        Console.WriteLine($"Project: {info.ProjectStatus} schema={info.SchemaName ?? "null"} route={info.Route ?? "null"}");
        foreach (var route in info.RouteCandidates)
        {
            Console.WriteLine($"  {route.SourcePath} -> {route.PublicPath} [{route.ProjectId}/{route.Collection}/{route.Version}/{route.Locale}/{route.Publication}]");
        }

        return 0;
    }
}
