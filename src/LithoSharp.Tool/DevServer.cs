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

namespace LithoSharp.Tool;

internal static class DevServer
{
    private const string ReloadScript =
        "<script>(()=>{const e=new EventSource('/_lithosharp/reload');e.onmessage=x=>{if(x.data==='reload')location.reload();if(x.data==='error')fetch('/_lithosharp/diagnostics').then(r=>r.json()).then(d=>{let p=document.getElementById('__lithosharp_error');if(!p){p=document.body.appendChild(document.createElement('pre'));p.id='__lithosharp_error';Object.assign(p.style,{position:'fixed',inset:'0',margin:'0',padding:'1rem',overflow:'auto',zIndex:2147483647,color:'#fff',background:'#400'});}p.textContent=d.error||d.diagnosticsText||'Build failed';});};})();</script>";

    internal static async Task<int> RunAsync(
        string project, string configuration, CommandOptions options, CancellationToken cancellationToken)
    {
        var assembly = await ProjectCompiler.BuildAsync(project, configuration, cancellationToken);
        await using var session = new WatchHostSession();
        var latest = await session.BuildAsync(assembly, project, options, cancellationToken);
        if (!latest.Success)
        {
            if (!string.IsNullOrWhiteSpace(latest.Error)) Console.Error.WriteLine(latest.Error);
            return latest.ExitCode;
        }
        var outputRoot = Path.GetFullPath(latest.OutputDirectory
            ?? throw new InvalidOperationException("The site host did not report an output directory."));
        var port = ParsePort(options.Value("port"));
        var host = options.Value("host") ?? "127.0.0.1";
        if (host is "*" or "+") throw new CliUsageException("Use an explicit address for --host.");
        var url = $"http://{host}:{port}";
        var hub = new ReloadHub();
        var state = new ServerState(latest);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
        app.MapGet("/_lithosharp/reload", context => hub.ConnectAsync(context, cancellationToken));
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
        var rebuild = RebuildLoopAsync(changes.Reader, project, configuration, options, state, hub, session, assembly, cancellationToken);
        await app.StartAsync(cancellationToken);
        var actualUrl = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? url;
        Console.WriteLine($"Serving {outputRoot} at {actualUrl}");
        if (options.Has("open")) OpenBrowser(actualUrl);
        try { await app.WaitForShutdownAsync(cancellationToken); }
        finally
        {
            changes.Writer.TryComplete();
            try { await rebuild; } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            await app.StopAsync(CancellationToken.None);
        }
        return 0;
    }

    private static async Task RebuildLoopAsync(
        ChannelReader<bool> changes, string project, string configuration, CommandOptions options,
        ServerState state, ReloadHub hub, WatchHostSession session, string assembly, CancellationToken cancellationToken)
    {
        await foreach (var ignored in changes.ReadAllAsync(cancellationToken))
        {
            await Task.Delay(150, cancellationToken);
            while (changes.TryRead(out _)) { }
            try
            {
                if (Interlocked.Exchange(ref state.Restart, 0) != 0)
                {
                    await session.StopAsync();
                    assembly = await ProjectCompiler.BuildAsync(project, configuration, cancellationToken);
                }
                var response = await session.BuildAsync(assembly, project, options, cancellationToken);
                state.Latest = response;
                if (response.Success && response.OutputDirectory is not null)
                {
                    state.OutputRoot = Path.GetFullPath(response.OutputDirectory);
                    state.IgnoredPaths = response.IgnoredPaths;
                }
                if (!response.Success && !string.IsNullOrWhiteSpace(response.Error)) Console.Error.WriteLine(response.Error);
                else Console.WriteLine($"Rebuilt at {DateTimeOffset.Now:T}");
                hub.Publish(response.Success ? "reload" : "error");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref state.Restart, 1);
                state.Latest = new HostResponse { ExitCode = 1, Error = exception.Message };
                Console.Error.WriteLine(exception.Message);
                hub.Publish("error");
            }
        }
    }

    private static FileSystemWatcher CreateWatcher(
        string root, Func<IReadOnlyList<string>> ignoredPaths, Action<string> changed)
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
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
