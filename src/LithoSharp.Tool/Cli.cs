using System.Diagnostics;
using System.Text.Json;

namespace LithoSharp.Tool;

internal static class Cli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }
        if (args[0] == "--version")
        {
            Console.WriteLine(typeof(Cli).Assembly.GetName().Version?.ToString(3));
            return 0;
        }
        if (args[0] == "new") return await NewAsync(args[1..], cancellationToken);
        if (args[0] is "snapshot" or "extract-translations" or "restore-mdx" or "migrate-docusaurus")
            return await ContentCommands.RunAsync(args, cancellationToken);

        var options = CommandOptions.Parse(args[1..]);
        var project = ProjectCompiler.ResolveProject(options.Project);
        var configuration = options.Value("configuration") ?? "Debug";
        if (args[0] == "serve")
            return await DevServer.RunAsync(project, configuration, options, cancellationToken);
        if (args[0] is not ("build" or "check" or "clean" or "inspect"))
            throw new CliUsageException($"Unknown command '{args[0]}'.");

        var format = options.Value("format") ?? "text";
        var machineOutput = format == "json" || args[0] == "check" && format == "sarif";
        var assembly = await ProjectCompiler.BuildAsync(project, configuration, cancellationToken, machineOutput);
        var temporaryRoot = args[0] == "check"
            ? Path.Combine(Path.GetTempPath(), $"lithosharp-check-{Guid.NewGuid():N}")
            : null;
        var temporaryOutput = temporaryRoot is null ? null : Path.Combine(temporaryRoot, "output");
        try
        {
            var response = await RunHostAsync(assembly, project, args[0], options, temporaryOutput, cancellationToken);
            PrintResponse(args[0], format, response);
            return response.ExitCode;
        }
        finally
        {
            if (temporaryRoot is not null && Directory.Exists(temporaryRoot))
                Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    internal static async Task<HostResponse> RunHostAsync(
        string assembly, string project, string command, CommandOptions options,
        string? forcedOutput, CancellationToken cancellationToken)
    {
        var responsePath = Path.Combine(Path.GetTempPath(), $"lithosharp-response-{Guid.NewGuid():N}.json");
        var start = ProjectCompiler.SelfStartInfo();
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.ArgumentList.Add("__host");
        Add("--assembly", assembly);
        Add("--project", Path.GetDirectoryName(project)!);
        Add("--command", command);
        Add("--response", responsePath);
        Add("--format", options.Value("format") ?? "text");
        var output = forcedOutput ?? options.Value("output");
        if (output is not null) Add("--output", Path.GetFullPath(output, Directory.GetCurrentDirectory()));
        if (options.Has("clean")) start.ArgumentList.Add("--clean");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the LithoSharp site host.");
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            var capturedOutput = await stdout;
            var capturedError = await stderr;
            var selectedFormat = options.Value("format") ?? "text";
            var machineOutput = selectedFormat == "json" || command == "check" && selectedFormat == "sarif";
            if (!string.IsNullOrWhiteSpace(capturedOutput))
                (machineOutput ? Console.Error : Console.Out).Write(capturedOutput);
            if (!string.IsNullOrWhiteSpace(capturedError)) Console.Error.Write(capturedError);
            if (!File.Exists(responsePath))
                throw new InvalidOperationException($"The site host exited with code {process.ExitCode} without a response.");
            return JsonSerializer.Deserialize<HostResponse>(await File.ReadAllTextAsync(responsePath, cancellationToken), JsonOptions)
                ?? throw new InvalidOperationException("The site host returned an empty response.");
        }
        finally
        {
            _ = await stdout;
            _ = await stderr;
            File.Delete(responsePath);
        }

        void Add(string name, string value)
        {
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(value);
        }
    }

    private static void PrintResponse(string command, string format, HostResponse response)
    {
        if (command == "check" && format is "json" or "sarif")
        {
            Console.WriteLine(response.DiagnosticsText);
            if (!response.Success && !string.IsNullOrWhiteSpace(response.Error)) Console.Error.WriteLine(response.Error);
            return;
        }
        if (format == "json" || command == "inspect" && format != "text")
        {
            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            return;
        }
        if (!string.IsNullOrWhiteSpace(response.DiagnosticsText)) Console.WriteLine(response.DiagnosticsText);
        if (!response.Success)
        {
            if (!string.IsNullOrWhiteSpace(response.Error)) Console.Error.WriteLine(response.Error);
            return;
        }
        if (command == "clean")
        {
            Console.WriteLine($"Removed {response.RemovedFiles.Length} owned artifact(s).");
            return;
        }
        if (response.BuildReport is { } report)
            Console.WriteLine($"{command} succeeded: {report.CacheHitCount} cache hit(s), {report.CacheMissCount} cache miss(es). Output: {response.OutputDirectory}");
        if (command == "inspect") PrintInspection(response);
    }

    private static void PrintInspection(HostResponse response)
    {
        foreach (var extension in response.Extensions) Console.WriteLine(JsonSerializer.Serialize(extension, JsonOptions));
        foreach (var node in response.BuildPlan)
        {
            var report = response.BuildReport?.Nodes.FirstOrDefault(item => item.Id == node.Id);
            Console.WriteLine($"{node.Id} [{(report?.CacheHit == true ? "hit" : "miss")}]"
                + (report?.CacheMissReason is null ? "" : $" {report.CacheMissReason}"));
            foreach (var artifact in node.Artifacts) Console.WriteLine($"  {artifact.PublicPath} -> {artifact.Path} ({artifact.Id})");
            foreach (var dependency in node.Dependencies) Console.WriteLine($"  depends on {dependency}");
        }
    }

    private static async Task<int> NewAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is not ("docs" or "blog" or "empty" or "mdx"))
            throw new CliUsageException("Usage: lithosharp new <docs|blog|empty|mdx> [name] [-o directory]");
        var forwarded = new List<string> { "new", $"lithosharp-{args[0]}" };
        var remainder = args[1..];
        if (remainder.Length > 0 && !remainder[0].StartsWith('-'))
        {
            forwarded.Add("--name");
            forwarded.Add(remainder[0]);
            remainder = remainder[1..];
        }
        forwarded.AddRange(remainder);
        var result = await ProjectCompiler.RunDotNetAsync(forwarded, cancellationToken);
        ProjectCompiler.WriteProcessOutput(result);
        return result.ExitCode;
    }

    private static void PrintHelp() => Console.WriteLine(
        """
        LithoSharp static site tool

        Commands:
          lithosharp new <docs|blog|empty|mdx> [name] [-o directory]
          lithosharp snapshot <source> <destination> <version>
          lithosharp extract-translations <source>
          lithosharp restore-mdx <worker-directory> [--allow-scripts]
          lithosharp migrate-docusaurus <source> (read-only JSON report; never executes config)
          lithosharp build [project] [-o directory] [--clean] [-c configuration]
          lithosharp serve [project] [-o directory] [--port number] [-c configuration]
          lithosharp check [project] [--format text|json|sarif] [-c configuration]
          lithosharp clean [project] [-o directory] [-c configuration]
          lithosharp inspect [project] [--format text|json] [-c configuration]
        """);
}

internal sealed class CommandOptions
{
    private readonly Dictionary<string, string?> values;
    private CommandOptions(string? project, Dictionary<string, string?> values) { Project = project; this.values = values; }
    internal string? Project { get; }
    internal bool Has(string key) => values.ContainsKey(key);
    internal string? Value(string key) => values.GetValueOrDefault(key);

    internal static CommandOptions Parse(IReadOnlyList<string> args)
    {
        string? project = null;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            var key = argument switch
            {
                "-o" => "output", "-c" => "configuration", "-f" => "format",
                "--output" => "output", "--configuration" => "configuration", "--format" => "format",
                "--port" => "port", "--host" => "host", "--clean" => "clean", "--open" => "open",
                _ => null,
            };
            if (key is null)
            {
                if (argument.StartsWith('-')) throw new CliUsageException($"Unknown option '{argument}'.");
                if (project is not null) throw new CliUsageException("Specify only one site project.");
                project = argument;
            }
            else if (key is "clean" or "open") values[key] = null;
            else if (++index >= args.Count) throw new CliUsageException($"Option '{argument}' requires a value.");
            else values[key] = args[index];
        }
        return new CommandOptions(project, values);
    }
}
