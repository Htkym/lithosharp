[CmdletBinding()]
param(
    [string] $ToolPath,
    [ValidateRange(30, 600)] [int] $TimeoutSeconds = 180,
    [switch] $KeepFixture
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ToolPath) { $ToolPath = Join-Path $repo 'src/LithoSharp.Tool/bin/Release/net10.0/LithoSharp.Tool.dll' }
$ToolPath = [IO.Path]::GetFullPath($ToolPath)
if (!(Test-Path -LiteralPath $ToolPath -PathType Leaf)) { throw "Build the Release tool first: $ToolPath" }
$temporaryParent = [IO.Path]::GetFullPath((Join-Path $repo '.tmp'))
$fixture = Join-Path $temporaryParent ('tool-test-' + [Guid]::NewGuid().ToString('N'))
$site = Join-Path $fixture 'site'
$logs = Join-Path $fixture 'logs'
$null = New-Item -ItemType Directory -Path $site, $logs
$processes = [Collections.Generic.List[object]]::new()
$passed = $false
$http = $null
$sseResponse = $null
$sseReader = $null
$sequence = 0

function Assert-True([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
}

function Start-TestProcess([string] $Executable, [string[]] $Arguments, [string] $Name) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $site
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $running = [pscustomobject]@{
        Process = $process
        Output = $process.StandardOutput.ReadToEndAsync()
        Error = $process.StandardError.ReadToEndAsync()
        Name = $Name
    }
    $processes.Add($running)
    return $running
}

function Stop-TestProcess($Running) {
    if (!$Running.Process.HasExited) {
        $Running.Process.Kill($true)
        if (!$Running.Process.WaitForExit(10000)) { throw "Process tree did not exit: $($Running.Name)" }
    }
}

function Complete-TestProcess($Running, [int[]] $ExpectedExit = @(0)) {
    if (!$Running.Process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-TestProcess $Running
        throw "Timed out: $($Running.Name)"
    }
    $stdout = $Running.Output.GetAwaiter().GetResult()
    $stderr = $Running.Error.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $logs ($Running.Name + '.stdout.txt')), $stdout)
    [IO.File]::WriteAllText((Join-Path $logs ($Running.Name + '.stderr.txt')), $stderr)
    if ($ExpectedExit -notcontains $Running.Process.ExitCode) {
        throw "$($Running.Name) exited $($Running.Process.ExitCode), expected $ExpectedExit.`n$stderr`n$stdout"
    }
    return $stdout
}

function Invoke-Dotnet([string[]] $Arguments, [int] $ExpectedExit = 0) {
    $script:sequence++
    $running = Start-TestProcess 'dotnet' $Arguments ('command-' + $script:sequence)
    return Complete-TestProcess $running $ExpectedExit
}

function Get-Snapshot([string] $Root) {
    $snapshot = [ordered]@{}
    if (Test-Path -LiteralPath $Root) {
        foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse -Force | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
            $snapshot[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    return ConvertTo-Json -InputObject $snapshot -Compress -Depth 10
}

function Wait-Until([scriptblock] $Condition, [string] $Description) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out waiting for $Description. Logs: $logs"
}

function Get-Http([string] $Url) {
    return Invoke-WebRequest -Uri $Url -NoProxy -SkipHttpErrorCheck -TimeoutSec 5
}

function Wait-Sse([string] $Event) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $read = $sseReader.ReadLineAsync()
        $remaining = [Math]::Max(1, [int](($TimeoutSeconds - $watch.Elapsed.TotalSeconds) * 1000))
        if (!$read.Wait($remaining)) { throw "Timed out waiting for SSE '$Event'." }
        $line = $read.GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'The SSE connection ended unexpectedly.' }
        if ($line -eq "data: $Event") { return }
    }
    throw "Did not receive SSE '$Event'."
}

try {
    $project = Join-Path $site 'ToolFixture.csproj'
    $reference = [Security.SecurityElement]::Escape((Join-Path $repo 'src/LithoSharp/LithoSharp.csproj'))
    [IO.File]::WriteAllText($project, @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$reference" /></ItemGroup>
</Project>
"@)
    $sourcePath = Join-Path $site 'Program.cs'
    $source = @'
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;

public sealed class FixtureFactory : ISiteFactory
{
    public async Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        var root = context.ProjectDirectory;
        if (File.Exists(Path.Combine(root, "wait.flag")))
        {
            await File.WriteAllTextAsync(Path.Combine(Directory.GetParent(root)!.FullName, "active-host.pid"),
                Environment.ProcessId.ToString(), cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        var entries = new List<ContentEntry<string, string>>();
        foreach (var id in new[] { "one", "two" })
        {
            var file = id + ".txt";
            var body = await File.ReadAllTextAsync(Path.Combine(root, file), cancellationToken);
            entries.Add(new(new ContentEntryId(id), file,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), "Fixture page", body));
        }
        var collection = new ContentCollection<string, string>(new ContentCollectionId("fixture"), root, entries,
            entry => File.Exists(Path.Combine(root, "route-error.flag")) ? SiteRoute.ForDirectoryIndex("typed/one")
                : entry.Id.Value == "two" ? SiteRoute.ForFile("typed/two/index.html") : SiteRoute.ForDirectoryIndex("typed/one"),
            entry => new PageMetadata(entry.FrontMatter, "Fixture description", publishFrom: DateTimeOffset.UnixEpoch),
            transformationId: new ContentTransformationId("fixture:1"), isCacheable: true);
        return new SiteDefinition(new SiteSettings { Title = "Tool fixture", Description = "CLI integration", BaseUrl = "https://example.test/" }, [])
        {
            OutputDirectory = "../cli-output",
            Customization = new SiteCustomization { Template = new BlogSiteTemplate(), FaviconSourceDirectory = Path.Combine(root, "no-icons") },
            Options = new SiteGenerationOptions
            {
                BuildTimestamp = DateTimeOffset.FromUnixTimeSeconds(1700000000),
                BuildCacheDirectory = Path.Combine(root, ".lithosharp"),
                Quality = new SiteQualityOptions(),
                ContentCollections = [new SiteContentCollection<string, string>(collection, new FixtureLayout())
                    { RendererFingerprint = "fixture-layout:1", IsThreadSafe = true }],
                MaxDegreeOfParallelism = 2,
            },
        };
    }
}

public sealed class FixtureLayout : IPageLayout<ContentEntry<string, string>>
{
    public IHtmlContent Render(SitePage<ContentEntry<string, string>> page, PageRenderingContext context) =>
        new BlogPageLayout().Render(new SitePage<PageLayoutContent>(page.Id, page.Route,
            new PageLayoutContent(Html.UnsafeRaw("<h1>Fixture page</h1><p>layout-v1</p><p>"
                + Html.Encode(page.Content.Body) + "</p>")), page.Metadata), context);
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var definition = await new FixtureFactory().CreateAsync(new SiteFactoryContext(args[0]));
        try
        {
        var result = await new SiteGenerator().GenerateWithOptionsAsync(definition.Site, definition.Posts, args[1], true,
            definition.Customization, definition.Options, CancellationToken.None);
        await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(new
        {
            Diagnostics = result.QualityReport.Format(SiteDiagnosticFormat.Json),
            Paths = result.BuildPlan.Artifacts.Select(artifact => artifact.RelativeOutputPath).Order(StringComparer.Ordinal).ToArray(),
            Routes = result.Routes.Select(route => new { Output = route.RelativeOutputPath, Public = route.PublicPath })
                .OrderBy(route => route.Output, StringComparer.Ordinal).ToArray(),
        }));
        return 0;
        }
        catch (SiteRouteValidationException exception)
        {
            await File.WriteAllTextAsync(args[2], new SiteQualityReport(exception.Diagnostics).Format(SiteDiagnosticFormat.Json));
            return 1;
        }
        catch (SiteBuildPlanValidationException exception)
        {
            await File.WriteAllTextAsync(args[2], new SiteQualityReport(exception.Diagnostics).Format(SiteDiagnosticFormat.Json));
            return 1;
        }
    }
}
'@
    [IO.File]::WriteAllText($sourcePath, $source)
    [IO.File]::WriteAllText((Join-Path $site 'one.txt'), 'content-v1')
    [IO.File]::WriteAllText((Join-Path $site 'two.txt'), 'stable-content')
    $cliOutput = Join-Path $fixture 'cli-output'
    $directOutput = Join-Path $fixture 'direct-output'
    $directReportPath = Join-Path $fixture 'direct.json'
    $common = @($project, '-c', 'Release')

    Write-Host 'Checking build, direct-library equivalence, and inspection...'
    $build = Invoke-Dotnet -Arguments (@($ToolPath, 'build') + $common + @('--format', 'json')) | ConvertFrom-Json
    Assert-True $build.success 'CLI build did not succeed.'
    $fixtureDll = Join-Path $site 'bin/Release/net10.0/ToolFixture.dll'
    $null = Invoke-Dotnet -Arguments @($fixtureDll, $site, $directOutput, $directReportPath)
    Assert-True ((Get-Snapshot $cliOutput) -ceq (Get-Snapshot $directOutput)) 'CLI and direct-library artifact SHA-256 snapshots differ.'
    $direct = Get-Content -LiteralPath $directReportPath -Raw | ConvertFrom-Json
    $paths = @($build.buildPlan | ForEach-Object artifacts | ForEach-Object path | Sort-Object)
    Assert-True (($paths -join "`n") -ceq ($direct.Paths -join "`n")) 'CLI and library route/artifact paths differ.'
    $cliRoutes = @($build.buildPlan | ForEach-Object artifacts | ForEach-Object { "$($_.path)`t$($_.publicPath)" } | Sort-Object)
    $directRoutes = @($direct.Routes | ForEach-Object { "$($_.Output)`t$($_.Public)" } | Sort-Object)
    Assert-True (($cliRoutes -join "`n") -ceq ($directRoutes -join "`n")) 'CLI and direct-library public routes differ.'
    Assert-True ($cliRoutes -contains "typed/one/index.html`t/typed/one/" -and $cliRoutes -contains "typed/two/index.html`t/typed/two/index.html") 'Directory and file index routes were conflated.'
    Assert-True ($build.diagnosticsText -ceq $direct.Diagnostics) 'CLI and direct-library diagnostics differ.'
    Assert-True (($direct.Diagnostics | ConvertFrom-Json).diagnostics.Count -gt 0) 'Fixture did not exercise diagnostic output.'
    $inspect = Invoke-Dotnet -Arguments (@($ToolPath, 'inspect') + $common + @('--format', 'json')) | ConvertFrom-Json
    Assert-True ($inspect.buildReport.cacheHitCount -gt 0 -and $inspect.buildReport.cacheMissCount -eq 0) 'No-op inspect did not report actual cache hits.'
    Assert-True (@($inspect.buildPlan | ForEach-Object artifacts | Where-Object publicPath).Count -gt 0) 'Inspect omitted public routes.'

    Write-Host 'Checking diagnostic formats and nonpublishing check...'
    $beforeCheck = Get-Snapshot $cliOutput
    $cacheBeforeCheck = Get-Snapshot (Join-Path $site '.lithosharp')
    $text = Invoke-Dotnet -Arguments (@($ToolPath, 'check') + $common + @('--format', 'text'))
    Assert-True ($text -match 'LSQ009') 'Text check omitted fixture diagnostics.'
    $json = Invoke-Dotnet -Arguments (@($ToolPath, 'check') + $common + @('--format', 'json')) | ConvertFrom-Json
    Assert-True ($json.diagnostics.Count -gt 0) 'JSON check did not produce a diagnostic report.'
    $sarif = Invoke-Dotnet -Arguments (@($ToolPath, 'check') + $common + @('--format', 'sarif')) | ConvertFrom-Json
    Assert-True ($sarif.version -eq '2.1.0' -and $sarif.runs[0].results.Count -gt 0) 'SARIF check did not produce valid diagnostic results.'
    Assert-True ($beforeCheck -ceq (Get-Snapshot $cliOutput)) 'Check changed published output.'
    Assert-True ($cacheBeforeCheck -ceq (Get-Snapshot (Join-Path $site '.lithosharp'))) 'Check changed the normal build cache.'

    Write-Host 'Checking original route diagnostics on failure...'
    $routeFlag = Join-Path $site 'route-error.flag'
    [IO.File]::WriteAllText($routeFlag, 'duplicate route')
    try {
        $routeDiagnostics = Invoke-Dotnet -Arguments (@($ToolPath, 'check') + $common + @('--format', 'json')) -ExpectedExit 1
        $null = Invoke-Dotnet -Arguments @($fixtureDll, $site, $directOutput, $directReportPath) -ExpectedExit 1
        Assert-True ($routeDiagnostics.Trim() -ceq (Get-Content -LiteralPath $directReportPath -Raw)) 'CLI dropped or changed library route diagnostics.'
        Assert-True (($routeDiagnostics | ConvertFrom-Json).diagnostics.Count -gt 0) 'Route collision returned no diagnostics.'
        Assert-True ($beforeCheck -ceq (Get-Snapshot $cliOutput)) 'Failed route validation changed published output.'
    }
    finally { Remove-Item -LiteralPath $routeFlag }

    Write-Host 'Checking ownership-aware clean...'
    $modified = Join-Path $cliOutput 'typed/one/index.html'
    [IO.File]::AppendAllText($modified, '<!-- user modification -->')
    $modifiedHash = (Get-FileHash -LiteralPath $modified).Hash
    $unowned = Join-Path $cliOutput 'user.txt'
    [IO.File]::WriteAllText($unowned, 'preserve user file')
    $clean = Invoke-Dotnet -Arguments (@($ToolPath, 'clean') + $common + @('--format', 'json')) | ConvertFrom-Json
    Assert-True ($clean.success -and $clean.removedFiles.Count -gt 0) 'Clean did not remove unchanged owned artifacts.'
    Assert-True ((Test-Path -LiteralPath $modified) -and (Get-FileHash -LiteralPath $modified).Hash -eq $modifiedHash) 'Clean removed or changed a modified owned file.'
    Assert-True ((Get-Content -LiteralPath $unowned -Raw) -eq 'preserve user file') 'Clean removed an unowned file.'
    Assert-True (!(Test-Path -LiteralPath (Join-Path $cliOutput 'index.html'))) 'Clean kept an unchanged owned page.'

    # A separate hidden console permits a real Windows Ctrl+C without interrupting this test runner.
    $controllerPath = Join-Path $fixture 'serve-controller.ps1'
    [IO.File]::WriteAllText($controllerPath, @'
param([string] $Mode, [string] $Tool, [string] $Project, [string] $Output, [string] $PidFile, [int] $Port, [int] $SignalPid)
$ErrorActionPreference = 'Stop'
if ($IsWindows) {
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class ToolTestConsole {
    private delegate bool Handler(uint signal);
    private static readonly Handler Ignore = signal => true;
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetConsoleCtrlHandler(Handler handler, bool add);
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    public static void Create() {
        FreeConsole();
        if (!AllocConsole()) throw new Win32Exception(Marshal.GetLastWin32Error());
        ShowWindow(GetConsoleWindow(), 0);
        SetConsoleCtrlHandler(Ignore, true);
    }
    public static void Signal(int processId) {
        FreeConsole();
        if (!AttachConsole((uint)processId)) throw new Win32Exception(Marshal.GetLastWin32Error());
        SetConsoleCtrlHandler(Ignore, true);
        if (!GenerateConsoleCtrlEvent(0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        System.Threading.Thread.Sleep(300);
        FreeConsole();
    }
}
"@
}
if ($Mode -eq 'signal') {
    if ($IsWindows) { [ToolTestConsole]::Signal($SignalPid) }
    else {
        $signalStart = [Diagnostics.ProcessStartInfo]::new('kill')
        $signalStart.UseShellExecute = $false
        foreach ($argument in @('-INT', $SignalPid.ToString())) { $signalStart.ArgumentList.Add($argument) }
        $signal = [Diagnostics.Process]::Start($signalStart)
        $signal.WaitForExit()
        exit $signal.ExitCode
    }
    exit 0
}
if ($IsWindows) { [ToolTestConsole]::Create() }
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false
$start.CreateNoWindow = !$IsWindows
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @($Tool, 'serve', $Project, '-c', 'Release', '-o', $Output, '--port', $Port.ToString())) { $start.ArgumentList.Add($argument) }
$server = [Diagnostics.Process]::Start($start)
[IO.File]::WriteAllText($PidFile, $server.Id.ToString())
$stdout = $server.StandardOutput.ReadToEndAsync()
$stderr = $server.StandardError.ReadToEndAsync()
try {
    $server.WaitForExit()
    [Console]::Out.Write($stdout.GetAwaiter().GetResult())
    [Console]::Error.Write($stderr.GetAwaiter().GetResult())
    exit $server.ExitCode
}
finally {
    if (!$server.HasExited) { $server.Kill($true); $server.WaitForExit() }
    $server.Dispose()
}
'@)
    Write-Host 'Checking serve, rebuilds, diagnostics, traversal, and cancellation...'
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $url = "http://127.0.0.1:$port"
    $serverOutput = Join-Path $site 'server-output'
    $serverPidPath = Join-Path $fixture 'server.pid'
    $pwsh = (Get-Process -Id $PID).Path
    $server = Start-TestProcess $pwsh @('-NoProfile', '-File', $controllerPath, '-Mode', 'serve', '-Tool', $ToolPath,
        '-Project', $project, '-Output', $serverOutput, '-PidFile', $serverPidPath, '-Port', $port.ToString()) 'serve'
    Wait-Until {
        if ($server.Process.HasExited) { throw "Serve exited before listening: $($server.Error.GetAwaiter().GetResult())" }
        try { return (Get-Http "$url/typed/one/").StatusCode -eq 200 } catch { return $false }
    } 'serve startup'
    $html = (Get-Http "$url/typed/one/").Content
    Assert-True ($html.Contains("EventSource('/_lithosharp/reload')") -and $html.Contains('layout-v1')) 'Serve omitted HTML reload injection or custom layout.'
    Assert-True (!(Get-Content -LiteralPath (Join-Path $serverOutput 'typed/one/index.html') -Raw).Contains('EventSource(')) 'Serve persisted its injected client into production output.'

    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $http = [Net.Http.HttpClient]::new($handler)
    $http.Timeout = [Threading.Timeout]::InfiniteTimeSpan
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, "$url/_lithosharp/reload")
    $connect = $http.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead)
    Assert-True ($connect.Wait(10000)) 'SSE endpoint did not send headers.'
    $sseResponse = $connect.GetAwaiter().GetResult()
    Assert-True ([int]$sseResponse.StatusCode -eq 200) 'SSE connection failed.'
    $sseReader = [IO.StreamReader]::new($sseResponse.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $site 'one.txt'), 'content-v2')
    Wait-Sse 'reload'
    Wait-Until { (Get-Http "$url/typed/one/").Content.Contains('content-v2') } 'content rebuild'
    $state = (Get-Http "$url/_lithosharp/diagnostics").Content | ConvertFrom-Json
    Assert-True ($state.success -and $state.buildReport.cacheHitCount -gt 0 -and $state.buildReport.cacheMissCount -gt 0) 'Content rebuild did not expose incremental hit/miss counts.'

    $source = $source.Replace('layout-v1', 'layout-v2')
    [IO.File]::WriteAllText($sourcePath, $source)
    Wait-Sse 'reload'
    Wait-Until { (Get-Http "$url/typed/one/").Content.Contains('layout-v2') } 'C# layout rebuild'
    $beforeError = Get-Snapshot $serverOutput
    $compileFailureWatch = [Diagnostics.Stopwatch]::StartNew()
    [IO.File]::WriteAllText($sourcePath, $source + "`nthis is invalid C#;`n")
    Wait-Sse 'error'
    $compileFailureWatch.Stop()
    $state = (Get-Http "$url/_lithosharp/diagnostics").Content | ConvertFrom-Json
    Assert-True (!$state.success -and ![string]::IsNullOrWhiteSpace($state.error)) 'Compile error did not reach diagnostics endpoint.'
    Assert-True ($beforeError -ceq (Get-Snapshot $serverOutput)) 'Compile failure changed the previous output.'
    Assert-True ((Get-Http "$url/typed/one/").Content.Contains('layout-v2')) 'Serve lost the last successful page after compile failure.'

    # Keep this output inside the watched project: a failure must retain its exclusion.
    # Observe the event stream for twice the measured failed-build latency, rather than sleeping blindly.
    $probeResponse = $null
    $probeReader = $null
    $probeRead = $null
    try {
        $probeRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, "$url/_lithosharp/reload")
        $probeConnect = $http.SendAsync($probeRequest, [Net.Http.HttpCompletionOption]::ResponseHeadersRead)
        Assert-True ($probeConnect.Wait(10000)) 'Ignore-path probe could not connect to SSE.'
        $probeResponse = $probeConnect.GetAwaiter().GetResult()
        $probeReader = [IO.StreamReader]::new($probeResponse.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
        foreach ($expected in @(': connected', '')) {
            $line = $probeReader.ReadLineAsync()
            Assert-True ($line.Wait(10000) -and $line.GetAwaiter().GetResult() -ceq $expected) 'Unexpected SSE connection preamble.'
        }
        $probeRead = $probeReader.ReadLineAsync()
        [IO.File]::WriteAllText((Join-Path $serverOutput 'unowned-watch-probe.txt'), 'Output changes remain ignored after a failed build.')
        $quietMilliseconds = [int][Math]::Min($TimeoutSeconds * 1000, [Math]::Max(2000, $compileFailureWatch.Elapsed.TotalMilliseconds * 2 + 500))
        Assert-True (!$probeRead.Wait($quietMilliseconds)) 'Writing ignored output after a compile failure caused another rebuild/SSE event.'
    }
    finally {
        if ($probeReader) { $probeReader.Dispose() }
        if ($probeResponse) { $probeResponse.Dispose() }
        if ($probeRead -and $probeRead.IsFaulted) { $null = $probeRead.Exception }
    }
    [IO.File]::WriteAllText($sourcePath, $source)
    Wait-Sse 'reload'

    $outside = Join-Path $fixture 'outside'
    $null = New-Item -ItemType Directory -Path $outside
    [IO.File]::WriteAllText((Join-Path $outside 'secret.txt'), 'OUTSIDE_TOOL_TEST_SECRET')
    foreach ($path in @('/%2e%2e%2foutside%2fsecret.txt', '/..%5coutside%5csecret.txt')) {
        $response = Get-Http ($url + $path)
        Assert-True ($response.StatusCode -ne 200 -and !$response.Content.Contains('OUTSIDE_TOOL_TEST_SECRET')) "Traversal was served: $path"
    }
    $link = Join-Path $serverOutput 'outside-link'
    $null = New-Item -ItemType $(if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }) -Path $link -Target $outside
    try {
        $response = Get-Http "$url/outside-link/secret.txt"
        Assert-True ($response.StatusCode -eq 404 -and !$response.Content.Contains('OUTSIDE_TOOL_TEST_SECRET')) 'Serve followed an outside directory link.'
    }
    finally { Remove-Item -LiteralPath $link -Force }

    # Hold an actual factory subprocess open so cancellation must terminate a child as well.
    [IO.File]::WriteAllText((Join-Path $site 'wait.flag'), 'wait')
    $childPidPath = Join-Path $fixture 'active-host.pid'
    Wait-Until { Test-Path -LiteralPath $childPidPath } 'waiting factory child'
    $hostPid = [int](Get-Content -LiteralPath $childPidPath -Raw)
    Assert-True ($null -ne (Get-Process -Id $hostPid -ErrorAction SilentlyContinue)) 'The cancellation fixture child was not running.'
    $toolPid = [int](Get-Content -LiteralPath $serverPidPath -Raw)
    $signal = Start-TestProcess $pwsh @('-NoProfile', '-File', $controllerPath, '-Mode', 'signal', '-SignalPid', $toolPid.ToString()) 'cancel'
    $null = Complete-TestProcess $signal
    $null = Complete-TestProcess $server @(0, 130)
    Wait-Until { $null -eq (Get-Process -Id $hostPid -ErrorAction SilentlyContinue) } 'factory child termination'
    Assert-True ($null -eq (Get-Process -Id $toolPid -ErrorAction SilentlyContinue)) 'Serve process remained alive after cancellation.'
    $passed = $true
    Write-Host 'Tool integration passed: build/library equivalence, cache inspection, check formats, clean ownership, serve rebuilds, path safety, and cancellation.'
}
finally {
    if ($sseReader) { $sseReader.Dispose() }
    if ($sseResponse) { $sseResponse.Dispose() }
    if ($http) { $http.Dispose() }
    foreach ($running in $processes) {
        try { Stop-TestProcess $running }
        finally { $running.Process.Dispose() }
    }
    if ($passed -and !$KeepFixture) {
        $resolved = [IO.Path]::GetFullPath($fixture)
        $relative = [IO.Path]::GetRelativePath($temporaryParent, $resolved)
        if ([IO.Path]::IsPathRooted($relative) -or $relative.StartsWith('..') -or !$relative.StartsWith('tool-test-')) {
            throw "Refusing to remove unexpected fixture path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    else { Write-Host "Fixture and logs retained: $fixture" }
}
