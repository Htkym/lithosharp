using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using System.Xml.Linq;

namespace LithoSharp.Tests;

/// <summary>Real serve/compiler/host regressions, gated by explicit fixture barriers.</summary>
[NotInParallel]
public sealed class DevServerLifecycleTests
{
    [Test]
    public Task StdinShutdownCancelsBlockedInitialProjectCompilation() => AssertStartupCancellation("compile");

    [Test]
    public Task StdinShutdownCancelsBlockedInitialHostBuild() => AssertStartupCancellation("render");

    private static async Task AssertStartupCancellation(string phase)
    {
        using var fixture = await Fixture.CreateAsync(phase);
        await using var serve = ServeProcess.Start(fixture);
        await WaitForFileAsync(Path.Combine(fixture.Barriers, phase + "-entered"));
        await serve.ShutdownAsync("shutdown-blocked-" + phase);
        var acknowledgement = await serve.WaitForEventAsync(value => IsEvent(value, "control-ack"));
        await Assert.That(acknowledgement.GetProperty("requestId").GetString()).IsEqualTo("shutdown-blocked-" + phase);
        // The release file stays absent: exiting must actually cancel the
        // blocked stage, rather than wait for this test to release startup.
        await serve.WaitForExitAsync();
        await Assert.That(serve.ExitCode).IsEqualTo(0);
        await Assert.That(File.Exists(Path.Combine(fixture.Barriers, phase + "-release"))).IsFalse();
        await Assert.That(serve.Events.Count(value => IsEvent(value, "startup"))).IsEqualTo(0);
        await Assert.That(serve.Events.Count(value => IsEvent(value, "shutdown"))).IsEqualTo(1);
    }

    [Test]
    public async Task EditAfterInitialReadIsPublishedWithoutAnotherEdit()
    {
        using var fixture = await Fixture.CreateAsync("render");
        await using var serve = ServeProcess.Start(fixture);
        var entered = Path.Combine(fixture.Barriers, "render-entered");
        await WaitForFileAsync(entered);
        await Assert.That(await File.ReadAllTextAsync(entered)).IsEqualTo("before-initial-read");
        // This is the only project edit. The barrier release is outside the
        // watched project, so it cannot manufacture a subsequent change.
        await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDirectory, "content.txt"), "after-initial-read");
        await File.WriteAllTextAsync(Path.Combine(fixture.Barriers, "render-release"), "release");
        var startup = await serve.WaitForEventAsync(value => IsEvent(value, "startup"));
        var rebuilt = await serve.WaitForEventAsync(value => IsEvent(value, "rebuild-succeeded"));
        await Assert.That(rebuilt.GetProperty("generation").GetInt64()).IsGreaterThan(startup.GetProperty("generation").GetInt64());
        var uri = new Uri(startup.GetProperty("url").GetString()!);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var published = await http.GetStringAsync(new Uri(uri, "index.html"));
        await Assert.That(published).Contains("after-initial-read");
        await Assert.That(published.Contains("before-initial-read", StringComparison.Ordinal)).IsFalse();
        await serve.ShutdownAsync("shutdown-latest-initial-content");
        await serve.WaitForExitAsync();
        await Assert.That(serve.ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task ThrowingRebuildReportsCurrentGenerationAndKeepsLastPublication()
    {
        using var fixture = await Fixture.CreateAsync(null);
        await using var serve = ServeProcess.Start(fixture);
        var startup = await serve.WaitForEventAsync(value => IsEvent(value, "startup"));
        var source = Path.Combine(fixture.ProjectDirectory, "Factory.cs");
        var original = await File.ReadAllTextAsync(source);
        // A failed C# recompilation throws ProjectCompilationException in the
        // parent rebuild loop, rather than returning a host failure response.
        await File.WriteAllTextAsync(source, "This is intentionally invalid C# source.");
        var failure = await serve.WaitForEventAsync(value => IsEvent(value, "rebuild-failed"));
        await Assert.That(failure.TryGetProperty("generation", out _)).IsTrue();
        var generation = failure.GetProperty("generation").GetInt64();
        await Assert.That(generation).IsGreaterThan(startup.GetProperty("generation").GetInt64());
        var uri = new Uri(startup.GetProperty("url").GetString()!);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var diagnostics = JsonDocument.Parse(await http.GetStringAsync(new Uri(uri, "_lithosharp/diagnostics")));
        await Assert.That(diagnostics.RootElement.GetProperty("generation").GetInt64()).IsEqualTo(generation);
        await Assert.That(diagnostics.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(await http.GetStringAsync(new Uri(uri, "index.html"))).Contains("before-initial-read");
        await File.WriteAllTextAsync(source, original);
        var recovered = await serve.WaitForEventAsync(value => IsEvent(value, "rebuild-succeeded")
            && value.GetProperty("generation").GetInt64() > generation);
        await Assert.That(recovered.GetProperty("generation").GetInt64()).IsGreaterThan(generation);
        await serve.ShutdownAsync("shutdown-rebuild-generation");
        await serve.WaitForExitAsync();
        await Assert.That(serve.ExitCode).IsEqualTo(0);
    }

    private static bool IsEvent(JsonElement value, string name) => value.TryGetProperty("event", out var field)
        && field.GetString() == name;

    private static async Task WaitForFileAsync(string file)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!File.Exists(file)) await Task.Delay(10, timeout.Token);
        // Polling only waits for an explicit positive barrier; no fixed delay
        // is used to assume that a compile or source read has happened.
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryWorkspace workspace = new();
        public string ProjectDirectory => Path.Combine(workspace.Root, "project");
        public string Barriers => Path.Combine(workspace.Root, "barriers");
        public string Output => Path.Combine(workspace.Root, "publication");
        public string Project => Path.Combine(ProjectDirectory, "site.csproj");

        public static async Task<Fixture> CreateAsync(string? blockedPhase)
        {
            var fixture = new Fixture();
            try
            {
                Directory.CreateDirectory(fixture.ProjectDirectory);
                Directory.CreateDirectory(fixture.Barriers);
                if (blockedPhase is not null) await File.WriteAllTextAsync(Path.Combine(fixture.Barriers, blockedPhase + "-arm"), "arm");
                await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDirectory, "content.txt"), "before-initial-read");
                await File.WriteAllTextAsync(Path.Combine(fixture.ProjectDirectory, "Factory.cs"), FactorySource);
                var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                    new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"),
                        new XElement("ImplicitUsings", "enable"), new XElement("Nullable", "enable"),
                        new XElement("UseSharedCompilation", "false")),
                    new XElement("ItemGroup", new XElement("Reference", new XAttribute("Include", "LithoSharp"),
                        new XElement("HintPath", typeof(SiteGenerator).Assembly.Location))),
                    new XElement("UsingTask", new XAttribute("TaskName", "WaitForOwnedCompileRelease"),
                        new XAttribute("TaskFactory", "RoslynCodeTaskFactory"),
                        new XAttribute("AssemblyFile", "$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll"),
                        new XElement("ParameterGroup", new XElement("BarrierDirectory", new XAttribute("ParameterType", "System.String"), new XAttribute("Required", "true"))),
                        new XElement("Task", new XElement("Using", new XAttribute("Namespace", "System.IO")),
                            new XElement("Using", new XAttribute("Namespace", "System.Threading")),
                            new XElement("Code", new XAttribute("Type", "Fragment"), new XAttribute("Language", "cs"),
                                new XCData("File.WriteAllText(Path.Combine(BarrierDirectory, \"compile-entered\"), \"entered\"); while (!File.Exists(Path.Combine(BarrierDirectory, \"compile-release\"))) Thread.Sleep(10);")))),
                    new XElement("Target", new XAttribute("Name", "HoldInitialOwnedCompile"),
                        new XAttribute("BeforeTargets", "CoreCompile"),
                        new XAttribute("Condition", "Exists('$(MSBuildProjectDirectory)/../barriers/compile-arm')"),
                        new XElement("WaitForOwnedCompileRelease", new XAttribute("BarrierDirectory", "$(MSBuildProjectDirectory)/../barriers"))));
                await File.WriteAllTextAsync(fixture.Project, project.ToString());
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }

        public void ReleaseForCleanup()
        {
            File.WriteAllText(Path.Combine(Barriers, "compile-release"), "cleanup-only");
            File.WriteAllText(Path.Combine(Barriers, "render-release"), "cleanup-only");
        }

        public void Dispose() => workspace.Dispose();

        private const string FactorySource = """
            using LithoSharp;
            using LithoSharp.Configuration;
            public sealed class LifecycleFactory : ISiteFactory
            {
                public Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default) =>
                    Task.FromResult(new SiteDefinition(new SiteSettings { BaseUrl = "https://example.test/" }, [])
                    { Customization = new() { Template = new ControlledTemplate(context.ProjectDirectory) } });
                private sealed class ControlledTemplate(string project) : ISiteTemplate
                {
                    public async Task<SiteTemplateResult> RenderAsync(SiteTemplateContext context, CancellationToken cancellationToken = default)
                    {
                        var text = await File.ReadAllTextAsync(Path.Combine(project, "content.txt"), cancellationToken);
                        var barriers = Path.GetFullPath(Path.Combine(project, "../barriers"));
                        var entered = Path.Combine(barriers, "render-entered");
                        if (File.Exists(Path.Combine(barriers, "render-arm")) && !File.Exists(entered))
                        {
                            var entering = entered + ".pending";
                            await File.WriteAllTextAsync(entering, text, cancellationToken);
                            File.Move(entering, entered);
                            while (!File.Exists(Path.Combine(barriers, "render-release"))) await Task.Delay(10, cancellationToken);
                        }
                        var html = "<html><head><title>Lifecycle</title><meta name='description' content='Lifecycle'><link rel='canonical' href='https://example.test/index.html'></head><body><h1>" + text + "</h1></body></html>";
                        return new SiteTemplateResult([new SiteTemplateFile { RelativePath = "index.html", Content = html }]);
                    }
                }
            }
            """;
    }

    private sealed class ServeProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Fixture fixture;
        private readonly Task output;
        private readonly Task<string> error;
        private readonly Channel<JsonElement> events = Channel.CreateUnbounded<JsonElement>();
        private readonly List<JsonElement> observed = [];
        private Exception? parseFailure;
        public int ExitCode => process.ExitCode;
        public IReadOnlyList<JsonElement> Events { get { lock (observed) return observed.ToArray(); } }

        private ServeProcess(Process process, Fixture fixture)
        {
            this.process = process; this.fixture = fixture;
            error = process.StandardError.ReadToEndAsync();
            output = ReadOutputAsync();
        }

        public static ServeProcess Start(Fixture fixture)
        {
            var tool = FindExactTool();
            var start = new ProcessStartInfo("dotnet")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = fixture.ProjectDirectory };
            foreach (var arg in new[] { tool, "serve", fixture.Project, "--configuration", "Release", "--format", "json",
                "--control-stdin", "--host", "127.0.0.1", "--port", "0", "--output", fixture.Output }) start.ArgumentList.Add(arg);
            start.Environment["UseSharedCompilation"] = "false";
            start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            return new ServeProcess(Process.Start(start) ?? throw new InvalidOperationException("Cannot start owned serve fixture."), fixture);
        }

        private async Task ReadOutputAsync()
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    using var json = JsonDocument.Parse(line);
                    var value = json.RootElement.Clone();
                    lock (observed) observed.Add(value);
                    await events.Writer.WriteAsync(value);
                }
                events.Writer.TryComplete();
            }
            catch (Exception failure) { parseFailure = failure; events.Writer.TryComplete(failure); }
        }

        public async Task<JsonElement> WaitForEventAsync(Func<JsonElement, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (await events.Reader.WaitToReadAsync(timeout.Token))
                while (events.Reader.TryRead(out var value)) if (predicate(value)) return value;
            throw new InvalidOperationException("Serve event missing. "
                + (error.IsCompletedSuccessfully ? error.Result : "stderr remains open; owned cleanup follows"), parseFailure);
        }

        public async Task ShutdownAsync(string id)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = "1.0", command = "shutdown", requestId = id }));
            await process.StandardInput.FlushAsync();
        }

        public async Task WaitForExitAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            await output;
            if (parseFailure is not null) throw new InvalidOperationException("Serve stdout was not JSON Lines.", parseFailure);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                fixture.ReleaseForCleanup();
                if (!process.HasExited)
                {
                    Exception? shutdownFailure = null;
                    try { await ShutdownAsync("cleanup-owned-fixture"); }
                    catch (Exception exception) { shutdownFailure = exception; }
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                    await output; await error;
                    if (shutdownFailure is not null)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(shutdownFailure).Throw();
                }
                await output; await error;
            }
            finally { process.Dispose(); }
        }

        private static string FindExactTool()
        {
            var expectedCore = SHA256.HashData(File.ReadAllBytes(typeof(SiteGenerator).Assembly.Location));
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                foreach (var candidate in new[]
                {
                    Path.Combine(directory.FullName, "LithoSharp.Tool", "release", "LithoSharp.Tool.dll"),
                    Path.Combine(directory.FullName, "src", "LithoSharp.Tool", "bin", "Release", "net10.0", "LithoSharp.Tool.dll")
                })
                {
                    var core = Path.Combine(Path.GetDirectoryName(candidate)!, "LithoSharp.dll");
                    if (File.Exists(candidate) && File.Exists(core)
                        && File.Exists(Path.ChangeExtension(candidate, ".deps.json"))
                        && File.Exists(Path.ChangeExtension(candidate, ".runtimeconfig.json"))
                        && SHA256.HashData(File.ReadAllBytes(core)).AsSpan().SequenceEqual(expectedCore)) return candidate;
                }
            }
            throw new InvalidOperationException("Build the Release Tool against exactly the test Core bytes before running DevServer lifecycle tests.");
        }
    }
}
