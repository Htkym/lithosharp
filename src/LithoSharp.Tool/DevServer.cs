using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LithoSharp.Build;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;

namespace LithoSharp.Tool;

internal static class DevServer
{
    private const string ReloadScript =
        "<script>(()=>{const e=new EventSource('/_lithosharp/reload');e.onmessage=x=>{if(x.data==='reload')location.reload();if(x.data==='error')fetch('/_lithosharp/diagnostics').then(r=>r.json()).then(d=>{let p=document.getElementById('__lithosharp_error');if(!p){p=document.body.appendChild(document.createElement('pre'));p.id='__lithosharp_error';Object.assign(p.style,{position:'fixed',inset:'0',margin:'0',padding:'1rem',overflow:'auto',zIndex:2147483647,color:'#fff',background:'#400'});}p.textContent=d.error||d.diagnosticsText||'Build failed';});};})();</script>";

    internal static async Task<int> RunAsync(
        string project, string configuration, CommandOptions options, CancellationToken cancellationToken)
    {
        var machine = string.Equals(options.Value("format"), "json", StringComparison.Ordinal);
        var control = options.Has("control-stdin");
        if (control && !machine)
            throw new CliUsageException("serve --control-stdin requires --format json.");
        var assembly = await ProjectCompiler.BuildAsync(project, configuration, cancellationToken, machine);
        await using var session = new WatchHostSession(machine);
        using var stopSource = new CancellationTokenSource();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopSource.Token);
        using var controlLifetime = new CancellationTokenSource();
        using var controlToken = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, stopSource.Token, controlLifetime.Token);
        var controller = new ServeController(machine, stopSource);
        _ = controlToken;
        // Console.In reads block the calling thread synchronously, so stdin
        // runs on a worker thread that the main flow never awaits (process
        // exit reclaims it). Awaiting it here hung startup with an open pipe.
        var controlTask = control
            ? Task.Run(() => ControlLoop(controller))
            : Task.CompletedTask;
        var latest = await session.BuildAsync(assembly, project, options, cancellationToken);
        if (!latest.Success)
        {
            if (!string.IsNullOrWhiteSpace(latest.Error)) Console.Error.WriteLine(latest.Error);
            if (machine) WriteMachine(new
            {
                SchemaVersion = MachineOutput.SchemaVersion,
                Event = "startup-failed",
                Success = false,
                latest.ExitCode,
                latest.Error,
                latest.OutputDirectory,
            });
            controlLifetime.Cancel();
            // The stdin reader blocks in a synchronous read that cancellation
            // cannot abort; never wait for it here (process exit reclaims the
            // thread). Only observe it when it already finished (for example EOF).
            if (controlTask.IsCompleted) await controlTask.ConfigureAwait(false);
            if (controller.ShutdownRequested) WriteShutdown(controller.Generation, 0);
            return controller.ShutdownRequested ? 0 : latest.ExitCode;
        }
        if (controller.ShutdownRequested)
        {
            // A shutdown arrived during the initial build: never start serving.
            controlLifetime.Cancel();
            // The stdin reader blocks in a synchronous read that cancellation
            // cannot abort; never wait for it here (process exit reclaims the
            // thread). Only observe it when it already finished (for example EOF).
            if (controlTask.IsCompleted) await controlTask.ConfigureAwait(false);
            if (machine) WriteShutdown(controller.Generation, 0);
            return 0;
        }
        var outputRoot = Path.GetFullPath(latest.OutputDirectory
            ?? throw new InvalidOperationException("The site host did not report an output directory."));
        var port = ParsePort(options.Value("port"));
        var host = options.Value("host") ?? "127.0.0.1";
        if (host is "*" or "+") throw new CliUsageException("Use an explicit address for --host.");
        var url = $"http://{host}:{port}";
        var hub = new ReloadHub();
        var state = new ServerState(latest with { Generation = 1 });
        controller.Generation = 1;
        var builder = WebApplication.CreateSlimBuilder();
        // Machine mode keeps stdout as pure JSON Lines, so ASP.NET logs must not pollute it.
        if (machine) builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
        app.MapGet("/_lithosharp/reload", context => hub.ConnectAsync(context, stopping.Token));
        app.MapGet("/_lithosharp/diagnostics", async context =>
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(context.Response.Body, state.Latest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web), context.RequestAborted);
        });
        app.MapFallback("/{**path}", context => ServeFileAsync(context, state.OutputRoot));

        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        using var watcher = CreateWatcher(Path.GetDirectoryName(project)!, () => state.IgnoredPaths,
            path => { if (Path.GetExtension(path).ToLowerInvariant() is not (".mdx" or ".jsx" or ".tsx" or ".js" or ".ts" or ".css" or ".png" or ".jpg" or ".jpeg" or ".svg" or ".webp" or ".avif")) Interlocked.Exchange(ref state.Restart, 1); changes.Writer.TryWrite(true); });
        // Stop order is watch, then worker, then HTTP server, then host: the
        // rebuild loop (worker) runs on the stopping token so a control shutdown
        // aborts an in-flight rebuild instead of waiting for it.
        var rebuild = RebuildLoopAsync(changes.Reader, project, configuration, options, state, hub, session, assembly, machine, controller, stopping.Token);
        var requestedPort = port;
        var started = false;
        string actualUrl = url;
        try
        {
            await app.StartAsync(stopping.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine(exception.Message);
            if (machine) WriteMachine(new
            {
                SchemaVersion = MachineOutput.SchemaVersion,
                Event = "startup-failed",
                Success = false,
                ExitCode = 1,
                Error = exception.Message,
                OutputDirectory = (string?)null,
            });
            changes.Writer.TryComplete();
            try { await rebuild; } catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            await app.StopAsync(CancellationToken.None);
            controlLifetime.Cancel();
            // The stdin reader blocks in a synchronous read that cancellation
            // cannot abort; never wait for it here (process exit reclaims the
            // thread). Only observe it when it already finished (for example EOF).
            if (controlTask.IsCompleted) await controlTask.ConfigureAwait(false);
            if (controller.ShutdownRequested) WriteShutdown(controller.Generation, 0);
            return controller.ShutdownRequested ? 0 : 1;
        }
        actualUrl = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? url;
        started = true;
        var actualPort = TryGetPort(actualUrl) ?? requestedPort;
        if (machine)
        {
            Console.Error.WriteLine($"Serving {outputRoot} at {actualUrl}");
            WriteMachine(new
            {
                SchemaVersion = MachineOutput.SchemaVersion,
                Event = "startup",
                Host = host,
                RequestedPort = requestedPort,
                ActualPort = actualPort,
                Url = actualUrl,
                OutputDirectory = outputRoot,
                BasePath = "/",
                SiteBasePath = state.Latest.SiteBasePath,
                Generation = controller.Generation,
                Routes = state.Latest.BuildPlan
                    .SelectMany(node => node.Artifacts)
                    .Select(artifact => new { Path = artifact.Path, PublicPath = artifact.PublicPath })
                    .ToArray(),
            });
        }
        else
        {
            Console.WriteLine($"Serving {outputRoot} at {actualUrl}");
        }
        if (options.Has("open")) OpenBrowser(actualUrl);
        try { await app.WaitForShutdownAsync(stopping.Token); }
        finally
        {
            changes.Writer.TryComplete();
            try { await rebuild; } catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            await app.StopAsync(CancellationToken.None);
            controlLifetime.Cancel();
            // The stdin reader blocks in a synchronous read that cancellation
            // cannot abort; never wait for it here (process exit reclaims the
            // thread). Only observe it when it already finished (for example EOF).
            if (controlTask.IsCompleted) await controlTask.ConfigureAwait(false);
            if (machine && (started || controller.ShutdownRequested)) WriteShutdown(controller.Generation, controller.ShutdownRequested ? 0 : 130);
        }
        return controller.ShutdownRequested ? 0 : cancellationToken.IsCancellationRequested ? 130 : 0;
    }

    private static async Task RebuildLoopAsync(
        ChannelReader<bool> changes, string project, string configuration, CommandOptions options,
        ServerState state, ReloadHub hub, WatchHostSession session, string assembly, bool machine,
        ServeController controller, CancellationToken cancellationToken)
    {
        await foreach (var ignored in changes.ReadAllAsync(cancellationToken))
        {
            await Task.Delay(150, cancellationToken);
            while (changes.TryRead(out _)) { }
            if (machine) WriteMachine(new { SchemaVersion = MachineOutput.SchemaVersion, Event = "rebuild-started" });
            try
            {
                if (Interlocked.Exchange(ref state.Restart, 0) != 0)
                {
                    await session.StopAsync();
                    assembly = await ProjectCompiler.BuildAsync(project, configuration, cancellationToken, machine);
                }
                var response = await session.BuildAsync(assembly, project, options, cancellationToken);
                controller.Generation++;
                state.Latest = response with { Generation = controller.Generation };
                if (response.Success && response.OutputDirectory is not null)
                {
                    state.OutputRoot = Path.GetFullPath(response.OutputDirectory);
                    state.IgnoredPaths = response.IgnoredPaths;
                }
                if (!response.Success && !string.IsNullOrWhiteSpace(response.Error)) Console.Error.WriteLine(response.Error);
                else if (machine) Console.Error.WriteLine($"Rebuilt at {DateTimeOffset.Now:T}");
                else Console.WriteLine($"Rebuilt at {DateTimeOffset.Now:T}");
                if (machine)
                {
                    if (response.Success) WriteMachine(new
                    {
                        SchemaVersion = MachineOutput.SchemaVersion,
                        Event = "rebuild-succeeded",
                        Success = true,
                        response.ExitCode,
                        OutputDirectory = state.OutputRoot,
                        Generation = controller.Generation,
                        RouteCount = response.BuildPlan.SelectMany(node => node.Artifacts).Count(),
                    });
                    else WriteMachine(new
                    {
                        SchemaVersion = MachineOutput.SchemaVersion,
                        Event = "rebuild-failed",
                        Success = false,
                        response.ExitCode,
                        response.Error,
                        OutputDirectory = state.OutputRoot,
                        Generation = controller.Generation,
                    });
                }
                hub.Publish(response.Success ? "reload" : "error");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref state.Restart, 1);
                controller.Generation++;
                state.Latest = new HostResponse { ExitCode = 1, Error = exception.Message, Generation = controller.Generation };
                Console.Error.WriteLine(exception.Message);
                if (machine) WriteMachine(new
                {
                    SchemaVersion = MachineOutput.SchemaVersion,
                    Event = "rebuild-failed",
                    Success = false,
                    ExitCode = 1,
                    Error = exception.Message,
                    OutputDirectory = state.OutputRoot,
                });
                hub.Publish("error");
            }
        }
    }

    private static void WriteShutdown(long generation, int exitCode) => WriteMachine(new
    {
        SchemaVersion = MachineOutput.SchemaVersion,
        Event = "shutdown",
        Generation = generation,
        ExitCode = exitCode,
    });

    /// <summary>Structured stdin shutdown ownership: only the control loop requests
    /// stops through this controller, and duplicate requests share one stop.</summary>
    private sealed class ServeController
    {
        private readonly CancellationTokenSource stopSource;
        private int shutdowns;

        public ServeController(bool machine, CancellationTokenSource stopSource)
        {
            Machine = machine;
            this.stopSource = stopSource;
        }

        public bool Machine { get; }

        public bool ShutdownRequested => Volatile.Read(ref shutdowns) != 0;

        public long Generation;

        public void RequestShutdown(string? requestId)
        {
            if (Interlocked.Increment(ref shutdowns) == 1)
            {
                if (requestId is not null)
                {
                    WriteMachine(new
                    {
                        SchemaVersion = MachineOutput.SchemaVersion,
                        Event = "control-ack",
                        RequestId = requestId,
                        Success = true,
                    });
                }

                stopSource.Cancel();
            }
            else if (requestId is not null)
            {
                WriteMachine(new
                {
                    SchemaVersion = MachineOutput.SchemaVersion,
                    Event = "control-ack",
                    RequestId = requestId,
                    Success = true,
                });
            }
        }
    }

    private static void ControlLoop(ServeController controller)
    {
        // Synchronous stdin reads block the calling thread, so this loop owns a
        // worker thread the main flow never awaits. Fragments across writes are
        // reassembled into lines by the reader itself. Duplicate shutdowns are
        // acked idempotently; the loop ends on EOF, a broken pipe, or process
        // exit (which abandons a parked read).
        while (true)
        {
            string? line;
            try
            {
                line = Console.In.ReadLine();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                return;
            }

            if (line is null)
            {
                // Stdin EOF (or the parent controller is gone) is a normal stop request.
                controller.RequestShutdown(null);
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument message;
            try
            {
                message = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                ControlError(null, "Unknown control format. Send one JSON object per line.");
                continue;
            }

            using (message)
            {
                var root = message.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    ControlError(null, "Unknown control format. Send one JSON object per line.");
                    continue;
                }

                var requestId = root.TryGetProperty("requestId", out var id)
                    && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                if (string.IsNullOrEmpty(requestId) || requestId.Length > 256)
                {
                    ControlError(null, "Control requests require a string requestId.");
                    continue;
                }

                var schema = root.TryGetProperty("schemaVersion", out var version)
                    && version.ValueKind == JsonValueKind.String ? version.GetString() ?? "" : "";
                if (!ToolingCompatibility.IsCompatible(schema))
                {
                    ControlError(requestId, "Unsupported control schema version. Use schema version 1.0.");
                    continue;
                }

                if (!root.TryGetProperty("command", out var command)
                    || command.ValueKind != JsonValueKind.String
                    || command.GetString() != "shutdown")
                {
                    ControlError(requestId, "Unknown control command. The only supported command is shutdown.");
                    continue;
                }

                controller.RequestShutdown(requestId);
            }
        }
    }

    private static void ControlError(string? requestId, string error) => WriteMachine(new
    {
        SchemaVersion = MachineOutput.SchemaVersion,
        Event = "control-error",
        RequestId = requestId,
        Success = false,
        Error = error,
    });

    private static readonly JsonSerializerOptions MachineOptions = new(JsonSerializerDefaults.Web);

    private static void WriteMachine(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, MachineOptions));

    private static int? TryGetPort(string url)
    {
        try
        {
            var port = new Uri(url, UriKind.Absolute).Port;
            return port is >= 0 and <= 65535 ? port : null;
        }
        catch (UriFormatException) { return null; }
    }

    private static FileSystemWatcher CreateWatcher(
        string root, Func<IReadOnlyList<string>> ignoredPaths, Action<string> changed)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        FileSystemEventHandler onChange = (_, eventArgs) => { if (ShouldWatch(root, ignoredPaths(), eventArgs.FullPath)) changed(eventArgs.FullPath); };
        RenamedEventHandler onRename = (_, eventArgs) =>
        {
            if (ShouldWatch(root, ignoredPaths(), eventArgs.FullPath)
                || ShouldWatch(root, ignoredPaths(), eventArgs.OldFullPath)) { changed(eventArgs.FullPath); changed(eventArgs.OldFullPath); }
        };
        watcher.Changed += onChange;
        watcher.Created += onChange;
        watcher.Deleted += onChange;
        watcher.Renamed += onRename;
        watcher.Error += (_, eventArgs) =>
        {
            Console.Error.WriteLine($"File watching lost changes: {eventArgs.GetException().Message} Rebuilding the site.");
            changed(root);
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static bool ShouldWatch(string root, IReadOnlyList<string> ignoredPaths, string path)
    {
        var full = Path.GetFullPath(path);
        if (ignoredPaths.Any(ignored => IsWithin(ignored, full))) return false;
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        return !relative.Split('/').Any(segment => segment is ".git" or "bin" or "obj" or ".lithosharp"
            || segment.StartsWith(".lithosharp-", StringComparison.Ordinal));
    }

    private static async Task ServeFileAsync(HttpContext context, string root)
    {
        string relative;
        try { relative = Uri.UnescapeDataString(context.Request.Path.Value ?? "/").TrimStart('/'); }
        catch (UriFormatException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        try
        {
            if (relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
                throw new InvalidOperationException("The requested path is not contained in the output directory.");
            var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(root, path)) throw new InvalidOperationException("The requested path is not contained in the output directory.");
            if (Directory.Exists(path)) path = Path.Combine(path, "index.html");
            if (!File.Exists(path) && Path.GetExtension(path).Length == 0) path = Path.Combine(path, "index.html");
            await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(root, path, asynchronous: true);
            if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                using var reader = new StreamReader(stream);
                var html = await reader.ReadToEndAsync(context.RequestAborted);
                var index = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                await context.Response.WriteAsync(index < 0 ? html + ReloadScript : html.Insert(index, ReloadScript), context.RequestAborted);
                return;
            }
            context.Response.ContentType = ContentType(path);
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                             or InvalidOperationException or ArgumentException)
        {
            if (context.Response.HasStarted) context.Abort();
            else context.Response.StatusCode = StatusCodes.Status404NotFound;
        }
    }

    private static bool IsWithin(string root, string path) =>
        path.Equals(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".css" => "text/css; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8", ".xml" => "application/xml; charset=utf-8",
        ".svg" => "image/svg+xml", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp", ".avif" => "image/avif", ".ico" => "image/x-icon",
        ".txt" => "text/plain; charset=utf-8", _ => "application/octet-stream",
    };

    private static int ParsePort(string? value) => value is null ? 0
        : int.TryParse(value, out var port) && port is >= 0 and <= 65535 ? port
        : throw new CliUsageException("--port must be between 0 and 65535.");

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        { Console.Error.WriteLine($"Unable to open a browser: {exception.Message}"); }
    }

    private sealed class ServerState(HostResponse latest)
    {
        public int Restart;
        public HostResponse Latest { get; set; } = latest;
        public string OutputRoot { get; set; } = Path.GetFullPath(latest.OutputDirectory!);
        public IReadOnlyList<string> IgnoredPaths { get; set; } = latest.IgnoredPaths;
    }

    private sealed class ReloadHub
    {
        private readonly ConcurrentDictionary<Guid, Channel<string>> clients = new();
        public void Publish(string message)
        {
            foreach (var client in clients.Values) client.Writer.TryWrite(message);
        }
        public async Task ConnectAsync(HttpContext context, CancellationToken stoppingToken)
        {
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentType = "text/event-stream";
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<string>(1);
            clients[id] = channel;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stoppingToken);
            try
            {
                await context.Response.WriteAsync(": connected\n\n", linked.Token);
                await context.Response.Body.FlushAsync(linked.Token);
                await foreach (var message in channel.Reader.ReadAllAsync(linked.Token))
                {
                    await context.Response.WriteAsync($"data: {message}\n\n", linked.Token);
                    await context.Response.Body.FlushAsync(linked.Token);
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally { clients.TryRemove(id, out _); }
        }
    }
}
