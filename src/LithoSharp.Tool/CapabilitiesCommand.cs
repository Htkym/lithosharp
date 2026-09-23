using System.Text.Json;

namespace LithoSharp.Tool;

/// <summary>
/// Prints the tooling capability report. The command never evaluates a project, so it
/// runs outside a project directory and does not start MSBuild, C#, Node or a restore.
/// </summary>
internal static class CapabilitiesCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static int Run(string[] args)
    {
        var format = "text";
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is not ("-f" or "--format"))
                throw new CliUsageException($"Unknown option '{argument}'.");
            if (++index >= args.Length) throw new CliUsageException($"Option '{argument}' requires a value.");
            format = args[index];
        }

        var toolAssembly = typeof(CapabilitiesCommand).Assembly.GetName();
        var coreAssembly = typeof(ToolingContracts).Assembly.GetName();
        var report = ToolingCapabilities.CreateReport(
            new ToolingIdentityInfo(toolAssembly.Name!, Version(toolAssembly.Version)),
            new ToolingIdentityInfo(coreAssembly.Name!, Version(coreAssembly.Version)));

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return 0;
        }
        if (format != "text")
            throw new CliUsageException($"Unknown format '{format}'. Supported formats: text, json.");

        Console.WriteLine($"LithoSharp tooling capabilities (schema {report.SchemaVersion})");
        Console.WriteLine($"  tool: {report.Tool.Name} {report.Tool.Version}");
        Console.WriteLine($"  core: {report.Core.Name} {report.Core.Version}");
        Console.WriteLine($"  project: not evaluated; {report.Project.Reason}");
        Console.WriteLine("  contracts:");
        foreach (var contract in report.Contracts)
            Console.WriteLine($"    {contract.Name} ({contract.Maturity}, schema {contract.SchemaVersion})");
        Console.WriteLine("  capabilities:");
        foreach (var capability in report.Capabilities)
            Console.WriteLine($"    {capability.Name} ({capability.Maturity}, schema {capability.SchemaVersion})"
                + (capability.Scope.Count == 0 ? string.Empty : $" [{string.Join(", ", capability.Scope)}]"));
        return 0;
    }

    private static string Version(Version? version) => version?.ToString(3) ?? "unknown";
}
