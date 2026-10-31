// Requires pinned Tool/package inputs; use tests/LithoSharp.Tool.Tests to run it.
// Ordinary solution tests must not create another intentional consumer compile.
#if COMPILER_DIAGNOSTIC_ACCEPTANCE
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LithoSharp.Diagnostics;

namespace LithoSharp.Tests;

[NotInParallel]
public sealed class CompilerDiagnosticPreflightTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Lazy<Assembly> Tool = new(() =>
    {
        var path = Required("COMPILER_DIAGNOSTIC_TOOL");
        var directory = Path.GetDirectoryName(path)!;
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var dependency = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
        };
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    });

    [Test]
    public void CompilerSarifAdapterPreservesUsableLocationsAndSuppression()
    {
        var root = Path.Combine(Required("COMPILER_DIAGNOSTIC_EVIDENCE"), "adapter");
        Directory.CreateDirectory(root);
        var cwd = Path.Combine(root, "actual-launch");
        var invoked = Path.Combine(root, "compiler-invocation");
        var sourceRoot = Path.Combine(root, "sources");
        var input = Path.Combine(root, "unrelated-log-directory", "compiler.sarif");
        Directory.CreateDirectory(Path.GetDirectoryName(input)!);
        object Location(object artifact, object? region) => new { physicalLocation = new { artifactLocation = artifact, region } };
        var run = new
        {
            columnKind = "utf16CodeUnits",
            originalUriBaseIds = new Dictionary<string, object>
            {
                ["ROOT"] = new { uri = DirectoryAddress(sourceRoot) },
                ["NESTED"] = new { uri = "nested/", uriBaseId = "ROOT" },
                ["CYCLE"] = new { uri = "loop/", uriBaseId = "CYCLE" },
            },
            invocations = new[] { new { workingDirectory = new { uri = DirectoryAddress(invoked) } } },
            artifacts = new[] { new { location = new { uri = "decl%20日本.cs", uriBaseId = "NESTED" } } },
            tool = new { driver = new { rules = new[] { new { id = "LSA1203", properties = new { category = "LithoSharp" } } } } },
            results = new object[]
            {
                new{ruleId="LSA1203",ruleIndex=0,level="error",message=new{text="Missing declared asset"},
                    locations=new[]{Location(new{uri="call%20😀.cs"},new{startLine=3,startColumn=17,endLine=3,endColumn=24})},
                    relatedLocations=new[]{
                        Location(new{index=0},new{startLine=8,startColumn=9,endLine=8,endColumn=17}),
                        Location(new{uri="other.cs",uriBaseId="ROOT"},new{startLine=12}),
                        Location(new{uri="unknown.cs",uriBaseId="ROOT"},null),
                        Location(new{uri="bad.cs",uriBaseId="ROOT"},new{startLine=0,startColumn=1}),
                        Location(new{uri="reversed.cs",uriBaseId="ROOT"},new{startLine=5,startColumn=9,endLine=4,endColumn=1}),
                        Location(new{uri="wrong-type.cs",uriBaseId="ROOT"},new{startLine="x"}),
                        Location(new{uri="missing.cs",uriBaseId="MISSING"},new{startLine=1}),
                        Location(new{uri="cycle.cs",uriBaseId="CYCLE"},new{startLine=1}),
                    }},
                new{ruleId="SUPPRESSED",level="error",message=new{text="Do not revive"},suppressions=new[]{new{kind="inSource"}}},
                new{ruleId="ACCEPTED",level="error",message=new{text="Do not revive"},suppressions=new[]{new{status="accepted"}}},
                new{ruleId="REVIEW",level="warning",message=new{text="Still visible"},suppressions=new[]{new{kind="inSource",status="underReview"}}},
            },
        };
        File.WriteAllText(input, JsonSerializer.Serialize(new { version = "2.1.0", runs = new[] { run } }, Web));
        var diagnostics = Read(input, cwd);
        Check(diagnostics.Count == 2, "Usable error and unaccepted warning remain");
        var error = diagnostics.Single(d => d.Id == "LSA1203");
        Check(error.Category == "LithoSharp" && error.Location?.FilePath == Path.Combine(invoked, "call 😀.cs") && error.Location.Column == 17, "Invocation directory and UTF16 primary");
        Check(error.RelatedLocations.Count == 3, "Exclude each bad related independently");
        Check(error.RelatedLocations[0].FilePath == Path.Combine(sourceRoot, "nested", "decl 日本.cs") && error.RelatedLocations[0].Column == 9, "Indexed/nested SARIF base");
        Check(error.RelatedLocations[1].Line == 12 && error.RelatedLocations[1].Column is null && error.RelatedLocations[2].Line is null, "Missing positions never become1:1");
        var response = Response(diagnostics);
        var json = Format(response, "json");
        var sarif = Format(response, "sarif");
        File.WriteAllText(Path.Combine(root, "preflight.json"), json);
        File.WriteAllText(Path.Combine(root, "preflight.sarif"), sarif);
        using (var parsed = JsonDocument.Parse(sarif))
        {
            var result = parsed.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().Single(r => r.GetProperty("ruleId").GetString() == "LSA1203");
            Check(result.GetProperty("properties").GetProperty("category").GetString() == "LithoSharp" && result.GetProperty("relatedLocations").GetArrayLength() == 3, "Preflight SARIF branch keeps category/related");
            Check(!result.GetProperty("relatedLocations")[2].GetProperty("physicalLocation").TryGetProperty("region", out _), "File-only related has no fabricated region");
        }
        var fallbackInput = Path.Combine(root, "fallback.sarif");
        File.WriteAllText(fallbackInput, "{\"runs\":[{\"results\":[{\"ruleId\":\"SUPPRESSED\",\"level\":\"error\",\"message\":{\"text\":\"hidden\"},\"suppressions\":[{\"kind\":\"external\"}]}]}]}");
        Check(Read(fallbackInput, cwd).Single().Id == "LSC7002" && Read(null, cwd).Single().Id == "LSC7002", "Suppressed/missing snapshot keeps compilation fallback");
        var launchInput = Path.Combine(root, "launch-relative.sarif");
        File.WriteAllText(launchInput, JsonSerializer.Serialize(new { runs = new[] { new { results = new[] { new { ruleId = "E", level = "error", message = new { text = "Error" }, locations = new[] { Location(new { uri = "relative.cs" }, new { startLine = 2 }) } } } } } }, Web));
        Check(Read(launchInput, cwd).Single().Location?.FilePath == Path.Combine(cwd, "relative.cs"), "No project or SARIF-directory fallback");
        File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new { status = "PASS", diagnostics = diagnostics.Select(DiagnosticValue), badRelatedExcluded = 5, preflightFormats = true, sourceFilesRead = false }, Web));
    }

    [Test]
    public async Task ActualCompileFailureKeepsRelatedPositionsThroughPreflightFormats()
    {
        var root = Required("COMPILER_DIAGNOSTIC_CONSUMER");
        var evidence = Path.Combine(Required("COMPILER_DIAGNOSTIC_EVIDENCE"), "consumer");
        Directory.CreateDirectory(evidence);
        var once = Path.Combine(evidence, "preflight-started.json");
        Check(!File.Exists(once), "One actual consumer compile attempt only");
        var project = Path.Combine(root, "site", "Consumer.csproj");
        var cwd = Path.Combine(root, "launch");
        Directory.CreateDirectory(cwd);
        var output = Path.Combine(root, "site", "dist");
        Directory.CreateDirectory(output);
        var retained = Path.Combine(output, "keep.txt");
        File.WriteAllText(retained, "retained output");
        var marker = Path.Combine(root, "factory-executed.txt");
        var created = Path.Combine(root, "factory-created.txt");
        string Literal(string value) => JsonSerializer.Serialize(value);
        var source = """
            // 😀 original compiler diagnostic fixture
            using System; using System.Threading; using System.Threading.Tasks; using LithoSharp; using LithoSharp.Configuration;
            public sealed class Factory : ISiteFactory {
            """ + "\npublic Factory(){System.IO.File.WriteAllText(" + Literal(created) + ",\"created\");}\n" + """
              public Task<SiteDefinition> CreateAsync(SiteFactoryContext context,CancellationToken cancellationToken=default){
            """ + "\nSystem.IO.File.WriteAllText(" + Literal(marker) + ",\"executed\");\n" + """
                var source=new SiteAsset("source","D:/site","a.txt","a.txt");
                var missingInput=new SiteAsset("missing","D:/site","b.txt","b.txt");
                var output=new SiteAssetOutput("result","result.txt");
                var missingOutput=new SiteAssetOutput("missing","missing.txt");
                var transform=new SiteAssetTransform("t","v1",
                  inputs:[source],
                  outputs:[output],
                  handler:(transformContext,token)=>{
            #pragma warning disable LSA1203
                    transformContext.WriteAsync(missingOutput,default,token);
            #pragma warning restore LSA1203
                    /*😀*/ transformContext.GetUrl(missingInput);
                    /*😀*/ transformContext.WriteAsync(missingOutput,default,token);
                    return Task.CompletedTask;
                  });
                return Task.FromResult(new SiteDefinition(new SiteSettings{Title="Fixture",BaseUrl="https://example.test/"},[]));
              }
            }
            """;
        source = source.ReplaceLineEndings("\r\n");
        var sourcePath = Path.Combine(root, "site", "Site.cs");
        File.WriteAllText(sourcePath, source, new UTF8Encoding(true));
        File.WriteAllText(once, JsonSerializer.Serialize(new { project, cwd, consumerCompileLimit = 1, tool = Required("COMPILER_DIAGNOSTIC_TOOL") }, Web));
        var start = new ProcessStartInfo(Required("COMPILER_DIAGNOSTIC_DOTNET")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd };
        foreach (var argument in new[] { Required("COMPILER_DIAGNOSTIC_TOOL"), "preflight", project, "--mode", "trusted", "--format", "json" })
            start.ArgumentList.Add(argument);
        var temp = Path.Combine(root, "tmp");
        Directory.CreateDirectory(temp);
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        var text = await stdout;
        var errors = await stderr;
        File.WriteAllText(Path.Combine(evidence, "stdout.json"), text);
        File.WriteAllText(Path.Combine(evidence, "stderr.log"), errors);
        File.WriteAllText(Path.Combine(evidence, "process.json"), JsonSerializer.Serialize(new { pid = process.Id, exitCode = process.ExitCode, arguments = start.ArgumentList.ToArray(), cwd }, Web));
        Check(process.ExitCode == 1, "Preflight compile failure exit1");
        using var document = JsonDocument.Parse(text);
        var response = document.RootElement;
        Check(response.GetProperty("preflight").GetProperty("stage").GetString() == "compiler", "Stop before factory/render");
        var diagnostics = response.GetProperty("diagnostics").EnumerateArray().Where(d => d.GetProperty("id").GetString() == "LSA1203").ToArray();
        Check(diagnostics.Length == 2 && !response.GetProperty("diagnostics").EnumerateArray().Any(d => d.GetProperty("id").GetString() == "LSC7002"), "Actual unsuppressed input/output errors, no false fallback");
        foreach (var (call, argument, declaration) in new[] { ("transformContext.GetUrl(", "missingInput", "[source]"), ("/*😀*/ transformContext.WriteAsync(", "missingOutput", "[output]") })
        {
            var at = source.IndexOf(call, StringComparison.Ordinal) + call.Length;
            var expected = Position(source, at);
            var diagnostic = diagnostics.Single(d => d.GetProperty("line").GetInt32() == expected.Line && d.GetProperty("column").GetInt32() == expected.Column);
            Check(diagnostic.GetProperty("file").GetString() == sourcePath && diagnostic.GetProperty("category").GetString() == "LithoSharp", "Actual source and rule category");
            Check(diagnostic.GetProperty("endColumn").GetInt32() == expected.Column + argument.Length, "Original UTF16 primary extent");
            var related = diagnostic.GetProperty("relatedLocations");
            var declared = Position(source, source.IndexOf(declaration, StringComparison.Ordinal));
            Check(related.GetArrayLength() == 1 && related[0].GetProperty("file").GetString() == sourcePath && related[0].GetProperty("line").GetInt32() == declared.Line && related[0].GetProperty("column").GetInt32() == declared.Column, "Original declaration reaches preflight JSON");
        }
        var snapshot = JsonSerializer.Deserialize(text, Tool.Value.GetType("LithoSharp.Tool.HostResponse")!, Web)!;
        var json = Format(snapshot, "json");
        var sarif = Format(snapshot, "sarif");
        Check(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(text)), "Same snapshot JSON through preflight formatter");
        File.WriteAllText(Path.Combine(evidence, "same-snapshot-preflight.sarif"), sarif);
        using (var formatted = JsonDocument.Parse(sarif))
        {
            var results = formatted.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray().Where(d => d.GetProperty("ruleId").GetString() == "LSA1203").ToArray();
            Check(results.Length == 2 && results.All(d => d.GetProperty("properties").GetProperty("category").GetString() == "LithoSharp" && d.GetProperty("relatedLocations").GetArrayLength() == 1), "Actual preflight SARIF conversion retains category/related");
        }
        Check(!File.Exists(marker) && !File.Exists(created) && File.ReadAllText(retained) == "retained output" && Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length == 1, "No factory/SSR and old output retained");
        File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new { status = "PASS", consumerCompileInvocations = 1, exitCode = process.ExitCode, factoryConstructed = false, factoryInvoked = false, oldOutputUnchanged = true, sameSnapshotFormats = true, sourcePath, sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sourcePath))) }, Web));
    }

    [Test]
    public async Task ReferencedMultiTargetCompilersKeepSeparateSnapshotsAndUserHooks()
    {
        var root = Path.Combine(Required("COMPILER_DIAGNOSTIC_EVIDENCE"), "graph");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
            <Project><PropertyGroup><CustomAfterMicrosoftCommonTargets>$(MSBuildThisFileDirectory)custom.targets</CustomAfterMicrosoftCommonTargets></PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(root, "custom.targets"), """
            <Project><Target Name="ExistingUserHook" BeforeTargets="CoreCompile"><WriteLinesToFile File="$(MSBuildProjectDirectory)/custom-$(TargetFramework).txt" Lines="retained" Overwrite="true" /></Target></Project>
            """);
        foreach (var leaf in new[] { "LeafA", "LeafB" })
        {
            Directory.CreateDirectory(Path.Combine(root, leaf));
            File.WriteAllText(Path.Combine(root, leaf, leaf + ".csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks></PropertyGroup></Project>
                """);
            File.WriteAllText(Path.Combine(root, leaf, "Input.cs"), "#error " + leaf);
        }
        var project = Path.Combine(root, "Root.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><ProjectReference Include="LeafA/LeafA.csproj" /><ProjectReference Include="LeafB/LeafB.csproj" /></ItemGroup></Project>
            """);
        var build = (Task<string>)Tool.Value.GetType("LithoSharp.Tool.ProjectCompiler")!
            .GetMethod("BuildAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [project, "Release", CancellationToken.None, true, Path.Combine(root, "compiler.sarif")])!;
        Exception? failure = null;
        try
        {
            await build;
        }
        catch (Exception error)
        {
            failure = error;
        }
        Check(failure?.GetType().Name == "ProjectCompilationException", "Build retains a compiler failure");
        var diagnostics = (IReadOnlyList<SiteDiagnostic>)failure!.GetType()
            .GetProperty("Diagnostics", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(failure)!;
        foreach (var leaf in new[] { "LeafA", "LeafB" })
        {
            Check(diagnostics.Count(d => d.Id == "CS1029" && d.Location?.FilePath == Path.Combine(root, leaf, "Input.cs")) == 2,
                "Both target frameworks retain the referenced compiler diagnostic: " + leaf);
            foreach (var framework in new[] { "net10.0", "netstandard2.0" })
                Check(File.ReadAllText(Path.Combine(root, leaf, "custom-" + framework + ".txt")).Trim() == "retained", "User hook remains active");
        }
        Check(!diagnostics.Any(d => d.Id == "LSC7002"), "Complete snapshots need no generic fallback");
        Check(!Directory.EnumerateFiles(root, "*.lithosharp-*.targets", SearchOption.AllDirectories).Any(), "Owned compiler hooks are removed");
    }

    private static IReadOnlyList<SiteDiagnostic> Read(string? path, string cwd) => (IReadOnlyList<SiteDiagnostic>)Tool.Value.GetType("LithoSharp.Tool.CompilerDiagnosticReader")!.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [path, 1, cwd])!;
    private static object Response(IReadOnlyList<SiteDiagnostic> diagnostics)
    {
        var assembly = Tool.Value;
        var response = Activator.CreateInstance(assembly.GetType("LithoSharp.Tool.HostResponse")!)!;
        var mapped = assembly.GetType("LithoSharp.Tool.FactoryHost")!.GetMethod("ToHostDiagnostics", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [diagnostics]);
        response.GetType().GetProperty("Diagnostics")!.SetValue(response, mapped);
        response.GetType().GetProperty("ExitCode")!.SetValue(response, 1);
        var preflight = Activator.CreateInstance(assembly.GetType("LithoSharp.Tool.HostPreflightReport")!, ["trusted-catalog", "compiler", Array.Empty<string>()]);
        response.GetType().GetProperty("Preflight")!.SetValue(response, preflight);
        return response;
    }
    private static string Format(object response, string format) => (string)Tool.Value.GetType("LithoSharp.Tool.PreflightCommand")!.GetMethod("FormatMachineOutput", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [response, format])!;
    private static object DiagnosticValue(SiteDiagnostic d) => new { d.Id, severity = d.Severity.ToString(), d.Category, d.Location, d.RelatedLocations };
    private static (int Line, int Column) Position(string source, int offset) => (source[..offset].Count(c => c == '\n') + 1, offset - source.LastIndexOf('\n', offset - 1));
    private static string DirectoryAddress(string path) => new Uri(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri;
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException(name + " is required");
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
#endif
