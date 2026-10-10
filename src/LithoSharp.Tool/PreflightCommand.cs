using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.Inspection;
using LithoSharp.Mdx;
using LithoSharp.Quality;

namespace LithoSharp.Tool;

internal static class PreflightCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        string? project = null;
        var inputs = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith('-'))
            {
                if (project is not null) throw new CliUsageException("Specify one project.");
                project = args[i];
                continue;
            }
            var name = args[i];
            if (name is not ("--mode" or "--input" or "--format" or "--configuration" or "--worker-directory" or "--node-executable") || ++i >= args.Length)
                throw new CliUsageException($"Unknown or incomplete preflight option '{name}'.");
            if (name == "--input") inputs.Add(Path.GetFullPath(args[i]));
            else if (!options.TryAdd(name, args[i])) throw new CliUsageException($"Option '{name}' was specified more than once.");
        }
        var mode = options.GetValueOrDefault("--mode") switch
        {
            "static" or "static-inputs" => "static-inputs",
            "trusted" or "trusted-catalog" => "trusted-catalog",
            _ => throw new CliUsageException("Preflight requires --mode static or --mode trusted."),
        };
        var format = options.GetValueOrDefault("--format", "text");
        if (format is not ("text" or "json" or "sarif")) throw new CliUsageException("Preflight format must be text, json or sarif.");
        if (mode == "static-inputs" && (inputs.Count == 0 || project is not null))
            throw new CliUsageException("Static preflight requires explicit --input files and accepts no project.");
        if (mode == "trusted-catalog" && project is null) throw new CliUsageException("Trusted preflight requires an explicit project.");

        var nodeExecutable = options.GetValueOrDefault("--node-executable", "node");
        if (string.IsNullOrWhiteSpace(nodeExecutable)) throw new CliUsageException("Preflight --node-executable must not be empty.");
        var source = await InspectInputsAsync(inputs, options.GetValueOrDefault("--worker-directory"), nodeExecutable, cancellationToken);
        if (mode == "static-inputs" || !source.Succeeded) return Print(SourceResponse(source), format);
        project = ProjectCompiler.ResolveProject(project);
        var root = SiteGenerator.CreateTemporaryDirectory("lithosharp-preflight-compiler-");
        try
        {
            string assembly;
            try
            {
                assembly = await ProjectCompiler.BuildAsync(project, options.GetValueOrDefault("--configuration", "Debug"),
                    cancellationToken, logToStandardError: true, compilerErrorLog: Path.Combine(root, "compiler.sarif"));
            }
            catch (ProjectCompilationException exception)
            {
                return Print(new HostResponse { ExitCode = 1, Diagnostics = FactoryHost.ToHostDiagnostics(exception.Diagnostics), Error = exception.Message,
                    Preflight = new("trusted-catalog", "compiler", ["Factory and render were not invoked."]) }, format);
            }
            // Re-read selected files after compilation; an earlier source snapshot is not
            // authorization to invoke a factory against newly invalid input.
            source = await InspectInputsAsync(inputs, options.GetValueOrDefault("--worker-directory"), nodeExecutable, cancellationToken);
            if (!source.Succeeded) return Print(SourceResponse(source), format);
            var hostOptions = CommandOptions.Parse(["--format", "json"]);
            var response = await Cli.RunHostAsync(assembly, project, "preflight", hostOptions, null, cancellationToken);
            response = response with { Diagnostics = FactoryHost.ToHostDiagnostics(source.Diagnostics).Concat(response.Diagnostics).ToArray() };
            return Print(response, format);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static async Task<SitePreflightReport> InspectInputsAsync(IReadOnlyList<string> inputs, string? workerDirectory, string nodeExecutable, CancellationToken cancellationToken)
    {
        var diagnostics = new List<SiteDiagnostic>();
        await using var mdx = new MdxInspectionSession(new MdxOptions(Directory.GetCurrentDirectory(), workerDirectory) { NodeExecutable = nodeExecutable });
        foreach (var input in inputs.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var text = new UTF8Encoding(false, true).GetString(await File.ReadAllBytesAsync(input, cancellationToken));
                // UTF-8 BOM is transport metadata, not a front matter character.
                if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
                if (Path.GetExtension(input).Equals(".mdx", StringComparison.OrdinalIgnoreCase))
                    diagnostics.AddRange((await mdx.AnalyzeAsync(input, text, cancellationToken: cancellationToken)).Diagnostics);
                else if (Path.GetExtension(input).Equals(".md", StringComparison.OrdinalIgnoreCase))
                    diagnostics.AddRange(SitePreflight.InspectMarkdown(input, text, cancellationToken).Diagnostics);
                else diagnostics.Add(new("LSC7001", SiteDiagnosticSeverity.Error, "Preflight accepts explicit .md or .mdx inputs.", new SiteSourceLocation(input)));
            }
            catch (SiteBuildExtensionException exception) { diagnostics.AddRange(exception.Diagnostics); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            { diagnostics.Add(new("LSC7001", SiteDiagnosticSeverity.Error, exception.Message, new SiteSourceLocation(input))); }
        }
        return SitePreflight.FromDiagnostics(diagnostics);
    }
    private static HostResponse SourceResponse(SitePreflightReport report) => new()
    {
        Success = report.Succeeded, ExitCode = report.Succeeded ? 0 : 1,
        Diagnostics = FactoryHost.ToHostDiagnostics(report.Diagnostics), Preflight = new(report.Mode, report.Stage, report.DeferredReasons.ToArray()),
    };
    private static int Print(HostResponse response, string format)
    {
        if (format == "json") Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else if (format == "sarif")
        {
            var diagnostics = response.Diagnostics.Select(d => new SiteDiagnostic(d.Id, Enum.Parse<SiteDiagnosticSeverity>(d.Severity), d.Message,
                d.File is null ? null : new SiteSourceLocation(d.File, d.Line, d.Column, d.EndLine, d.EndColumn)));
            var sarif = JsonNode.Parse(new SiteQualityReport(diagnostics).Format(SiteDiagnosticFormat.Sarif))!;
            sarif["runs"]![0]!["properties"] = JsonSerializer.SerializeToNode(response.Preflight, JsonOptions);
            Console.WriteLine(sarif.ToJsonString(JsonOptions));
        }
        else
        {
            Console.WriteLine($"Preflight {response.Preflight?.Mode}/{response.Preflight?.Stage}: {(response.Success ? "no Error" : "failed")}");
            foreach (var d in response.Diagnostics) Console.WriteLine($"{d.Id} {d.Severity}: {d.Message}");
            foreach (var deferred in response.Preflight?.DeferredReasons ?? []) Console.WriteLine($"Deferred: {deferred}");
            if (response.Error is not null) Console.Error.WriteLine(response.Error);
        }
        return response.ExitCode;
    }
}

internal sealed record HostPreflightReport(string Mode, string Stage, string[] DeferredReasons);
