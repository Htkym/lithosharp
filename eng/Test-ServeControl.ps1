[CmdletBinding()]
param(
    [string] $ToolPath,
    [ValidateRange(60, 900)] [int] $TimeoutSeconds = 240,
    [switch] $KeepFixture
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ToolPath) { $ToolPath = Join-Path $repo 'src/LithoSharp.Tool/bin/Release/net10.0/LithoSharp.Tool.dll' }
$ToolPath = [IO.Path]::GetFullPath($ToolPath)
if (!(Test-Path -LiteralPath $ToolPath -PathType Leaf)) { throw "Build the Release tool first: $ToolPath" }
$temporaryParent = [IO.Path]::GetFullPath((Join-Path $repo '.tmp'))
$fixture = Join-Path $temporaryParent ('serve-control-' + [Guid]::NewGuid().ToString('N'))
$site = Join-Path $fixture 'site'
$logs = Join-Path $fixture 'logs'
$null = New-Item -ItemType Directory -Path $site, $logs
$sequence = 0

function Assert-True([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
}

function Start-Serve([string] $Name, [string[]] $Arguments) {
    $script:sequence++
    $stdoutPath = Join-Path $logs ($Name + '.stdout.log')
    $stderrPath = Join-Path $logs ($Name + '.stderr.log')
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $site
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $process.StandardInput.AutoFlush = $true
    $stdoutWriter = [IO.StreamWriter]::new($stdoutPath, $false, [Text.UTF8Encoding]::new($false))
    $stderrWriter = [IO.StreamWriter]::new($stderrPath, $false, [Text.UTF8Encoding]::new($false))
    $copy = {
        param($Reader, $Writer)
        try {
            $buffer = New-Object char[] 4096
            while (($count = $Reader.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $Writer.Write($buffer, 0, $count)
                $Writer.Flush()
            }
        } catch { } finally { $Writer.Dispose() }
    }
    $stdoutJob = [PowerShell]::Create().AddScript($copy).AddArgument($process.StandardOutput).AddArgument($stdoutWriter)
    $stderrJob = [PowerShell]::Create().AddScript($copy).AddArgument($process.StandardError).AddArgument($stderrWriter)
    $stdoutHandle = $stdoutJob.BeginInvoke()
    $stderrHandle = $stderrJob.BeginInvoke()
    return [pscustomobject]@{
        Name = $Name
        Process = $process
        Stdin = $process.StandardInput
        StdoutPath = $stdoutPath
        StderrPath = $stderrPath
        Jobs = @($stdoutJob, $stderrJob)
        Handles = @($stdoutHandle, $stderrHandle)
    }
}

function Get-Events([string] $Name) {
    $events = @()
    $path = Join-Path $logs ($Name + '.stdout.log')
    if (!(Test-Path -LiteralPath $path)) { return $events }
    foreach ($line in (Get-Content -LiteralPath $path)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parsed = $line | ConvertFrom-Json
        if ($parsed -is [System.Management.Automation.PSCustomObject]) { $events += $parsed }
    }
    return $events
}

function Assert-StdoutPure([string] $Name) {
    foreach ($line in (Get-Content -LiteralPath (Join-Path $logs ($Name + '.stdout.log')))) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $null = $line | ConvertFrom-Json } catch { throw "Serve stdout was not pure JSON Lines: $line" }
    }
}

function Wait-Event($Serve, [string] $EventName, [scriptblock] $Extra = { $true }) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ($Serve.Process.HasExited) {
            # The process may exit before the redirected log flushes; take one
            # final look before reporting the event as missing.
            Start-Sleep -Milliseconds 2000
            foreach ($event in (Get-Events $Serve.Name)) {
                if ($event.event -eq $EventName -and (& $Extra $event)) { return $event }
            }
            throw "$($Serve.Name) exited before '$EventName'. Stderr: $(Get-Content -LiteralPath $Serve.StderrPath -Raw -ErrorAction SilentlyContinue)"
        }
        foreach ($event in (Get-Events $Serve.Name)) {
            if ($event.event -eq $EventName -and (& $Extra $event)) { return $event }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for '$EventName' in $($Serve.Name)."
}

function Wait-Exit($Serve) {
    if (!$Serve.Process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $Serve.Process.Kill($true) } catch { }
        throw "$($Serve.Name) did not exit in time."
    }
    foreach ($job in $Serve.Jobs) { try { $job.Stop() } catch { }; $job.Dispose() }
    return $Serve.Process.ExitCode
}

function Get-Http([string] $Url) {
    return Invoke-WebRequest -Uri $Url -NoProxy -SkipHttpErrorCheck -TimeoutSec 10
}

function Stop-Serve($Serve) {
    try { $Serve.Stdin.Close() } catch { }
    try {
        if (!$Serve.Process.HasExited) {
            $Serve.Process.Kill($true)
            $Serve.Process.WaitForExit(10000) | Out-Null
        }
    } catch { }
    foreach ($job in $Serve.Jobs) { try { $job.Stop() } catch { }; $job.Dispose() }
    $Serve.Process.Dispose()
}

try {
    $project = Join-Path $site 'ControlFixture.csproj'
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
    [IO.File]::WriteAllText((Join-Path $site 'Program.cs'), @'
using System.Security.Cryptography;
using System.Text;
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;

public static class Program
{
    public static int Main() => 0;
}

public sealed class ControlFactory : ISiteFactory
{
    public async Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default)
    {
        var root = context.ProjectDirectory;
        var entries = new List<ContentEntry<string, string>>();
        foreach (var id in new[] { "one", "two" })
        {
            var body = await File.ReadAllTextAsync(Path.Combine(root, id + ".txt"), cancellationToken);
            entries.Add(new(new ContentEntryId(id), id + ".txt",
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), "Control page", body));
        }
        var collection = new ContentCollection<string, string>(new ContentCollectionId("control"), root, entries,
            entry => entry.Id.Value == "two" ? SiteRoute.ForFile("typed/two/index.html") : SiteRoute.ForDirectoryIndex("typed/one"),
            entry => new PageMetadata(entry.FrontMatter, "Control description", publishFrom: DateTimeOffset.UnixEpoch),
            transformationId: new ContentTransformationId("control:1"), isCacheable: true);
        return new SiteDefinition(new SiteSettings { Title = "Control fixture", Description = "Serve control", BaseUrl = "https://example.test/" }, [])
        {
            OutputDirectory = "../control-output",
            Customization = new SiteCustomization { Template = new BlogSiteTemplate(), FaviconSourceDirectory = Path.Combine(root, "no-icons") },
            Options = new SiteGenerationOptions
            {
                BuildTimestamp = DateTimeOffset.FromUnixTimeSeconds(1700000000),
                BuildCacheDirectory = Path.Combine(root, ".lithosharp"),
                Quality = new SiteQualityOptions(),
                ContentCollections = [new SiteContentCollection<string, string>(collection, new ControlLayout()) { RendererFingerprint = "control-layout:1", IsThreadSafe = true }],
            },
        };
    }
}

public sealed class ControlLayout : IPageLayout<ContentEntry<string, string>>
{
    public IHtmlContent Render(SitePage<ContentEntry<string, string>> page, PageRenderingContext context) =>
        new BlogPageLayout().Render(new SitePage<PageLayoutContent>(page.Id, page.Route,
            new PageLayoutContent(Html.UnsafeRaw("<h1>Control page</h1><p>" + Html.Encode(page.Content.Body) + "</p>")), page.Metadata), context);
}
'@)
    [IO.File]::WriteAllText((Join-Path $site 'one.txt'), 'control-v1')
    [IO.File]::WriteAllText((Join-Path $site 'two.txt'), 'stable-content')
    $common = @($project, '-c', 'Release')
    $cliOutput = Join-Path $fixture 'control-output'

    Write-Host 'Checking text-mode control rejection...'
    $usage = & dotnet $ToolPath serve $project -c Release -o (Join-Path $fixture 'unused-output') --port 0 --format text --control-stdin 2>&1
    Assert-True ($LASTEXITCODE -eq 2) 'Text-mode --control-stdin did not fail with exit 2.'

    Write-Host 'Checking startup, routes, and placement...'
    $serve = Start-Serve 'serve' (@($ToolPath, 'serve') + $common + @('-o', $cliOutput, '--port', '0', '--format', 'json', '--control-stdin'))
    try {
        $startup = Wait-Event $serve 'startup'
        Assert-True ($startup.schemaVersion -eq '1.0') 'Startup omitted schemaVersion.'
        Assert-True (![string]::IsNullOrWhiteSpace($startup.outputDirectory)) 'Startup omitted outputDirectory.'
        Assert-True ($startup.actualPort -gt 0 -and $startup.requestedPort -eq 0) 'Port 0 was not allocated dynamically.'
        Assert-True ($startup.url -eq "http://127.0.0.1:$($startup.actualPort)") 'Startup URL/port mismatch without regex parsing.'
        Assert-True ($startup.basePath -eq '/') 'Served base path is not the root-delivery contract.'
        Assert-True ($startup.siteBasePath -eq '/') 'Generated site base path was not reported distinctly.'
        Assert-True ($startup.generation -eq 1) 'Initial generation is not 1.'
        $routes = @($startup.routes | ForEach-Object { "$($_.path)`t$($_.publicPath)" } | Sort-Object)
        Assert-True ($routes -contains "typed/one/index.html`t/typed/one/") 'Directory route mapping is missing or double-prefixed.'
        Assert-True ($routes -contains "typed/two/index.html`t/typed/two/index.html") 'File route mapping is missing.'
        Assert-StdoutPure 'serve'
        $url = $startup.url
        $page = Get-Http "$url/typed/one/"
        Assert-True ($page.StatusCode -eq 200) 'Directory index was not served.'
        Assert-True ($page.Content.Contains('control-v1')) 'Served page does not match the output file.'
        Assert-True ($page.Content.Contains("EventSource('/_lithosharp/reload')")) 'Serve omitted HTML reload injection.'
        Assert-True (![IO.File]::ReadAllText((Join-Path $cliOutput 'typed/one/index.html'), [Text.Encoding]::UTF8).Contains('EventSource(')) 'Serve persisted its injected client into production output.'
        Assert-True ((Get-Http "$url/no-such-page/").StatusCode -eq 404) 'Unknown path did not fall back to 404.'
        $state = (Get-Http "$url/_lithosharp/diagnostics").Content | ConvertFrom-Json
        Assert-True ($state.success -and $state.generation -eq 1 -and $state.schemaVersion -eq '1.0') 'Diagnostics endpoint did not expose structured generation state.'

        Write-Host 'Checking invalid and fragmented control input...'
        $serve.Stdin.WriteLine('not json')
        $errorEvent = Wait-Event $serve 'control-error' { param($e) $null -eq $e.requestId }
        Assert-True ($errorEvent.success -eq $false -and $errorEvent.error -notmatch 'not json') 'Invalid input leaked into the error or missed its shape.'
        Assert-True ((Get-Http "$url/typed/one/").StatusCode -eq 200) 'Server stopped after invalid input.'
        $serve.Stdin.Write('{"schemaVers')
        Start-Sleep -Milliseconds 300
        $serve.Stdin.WriteLine('ion":"1.0","command":"bogus","requestId":"frag-1"}')
        $fragError = Wait-Event $serve 'control-error' { param($e) $e.requestId -eq 'frag-1' }
        Assert-True ($fragError.error -match 'Unknown control command') 'Fragmented control message was not reassembled.'
        Assert-StdoutPure 'serve'

        Write-Host 'Checking rebuild generations and failure retention...'
        [IO.File]::WriteAllText((Join-Path $site 'two.txt'), 'control-v2')
        $rebuilt = Wait-Event $serve 'rebuild-succeeded' { param($e) $e.generation -eq 2 }
        Assert-True ($rebuilt.routeCount -eq @($startup.routes).Count) 'Rebuild did not report the route count.'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            if ((Get-Http "$url/typed/two/index.html").Content.Contains('control-v2')) { break }
            Start-Sleep -Milliseconds 300
        }
        Remove-Item -LiteralPath (Join-Path $site 'two.txt')
        $failed = Wait-Event $serve 'rebuild-failed' { param($e) $e.generation -eq 3 }
        Assert-True (![string]::IsNullOrWhiteSpace($failed.error)) 'Rebuild failure carried no diagnostics.'
        Assert-True ((Get-Http "$url/typed/two/index.html").Content.Contains('control-v2')) 'Failed rebuild did not keep the last successful output.'
        $failState = (Get-Http "$url/_lithosharp/diagnostics").Content | ConvertFrom-Json
        Assert-True (!$failState.success -and ![string]::IsNullOrWhiteSpace($failState.error)) 'Failure diagnostics are not separately retrievable from the kept output.'
        Assert-True ($failState.generation -eq 3) 'Diagnostics endpoint did not expose the failure generation.'
        [IO.File]::WriteAllText((Join-Path $site 'two.txt'), 'control-v3')
        $recovered = Wait-Event $serve 'rebuild-succeeded' { param($e) $e.generation -eq 4 }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            if ((Get-Http "$url/typed/two/index.html").Content.Contains('control-v3')) { break }
            Start-Sleep -Milliseconds 300
        }
        Assert-True ((Get-Http "$url/typed/two/index.html").Content.Contains('control-v3')) 'Recovery event did not publish the latest output.'

        Write-Host 'Checking idempotent structured shutdown...'
        $serve.Stdin.WriteLine('{"schemaVersion":"1.0","command":"shutdown","requestId":"stop-1"}')
        $serve.Stdin.WriteLine('{"schemaVersion":"1.0","command":"shutdown","requestId":"stop-2"}')
        $ack1 = Wait-Event $serve 'control-ack' { param($e) $e.requestId -eq 'stop-1' }
        Assert-True ($ack1.success -eq $true) 'First shutdown was not acked.'
        $ack2 = Wait-Event $serve 'control-ack' { param($e) $e.requestId -eq 'stop-2' }
        $exit = Wait-Exit $serve
        Assert-True ($exit -eq 0) "Structured shutdown exited $exit instead of 0."
        $shutdowns = @(Get-Events 'serve' | Where-Object { $_.event -eq 'shutdown' })
        Assert-True ($shutdowns.Count -eq 1 -and $shutdowns[0].generation -eq 4) 'Shutdown was not exactly-once with the last generation.'
        Assert-StdoutPure 'serve'
        $connected = $false
        try {
            $probe = [Net.Sockets.TcpClient]::new()
            $pending = $probe.BeginConnect('127.0.0.1', $startup.actualPort, $null, $null)
            if ($pending.AsyncWaitHandle.WaitOne(2000)) {
                $probe.EndConnect($pending)
                $connected = $true
            }
        } catch { $connected = $false } finally { $probe.Close() }
        Assert-True (!$connected) 'Port is still occupied after shutdown.'
    } finally { Stop-Serve $serve }

    Write-Host 'Checking EOF shutdown...'
    $eof = Start-Serve 'serve-eof' (@($ToolPath, 'serve') + $common + @('-o', (Join-Path $fixture 'eof-output'), '--port', '0', '--format', 'json', '--control-stdin'))
    try {
        $eofStarted = Wait-Event $eof 'startup'
        $eof.Stdin.Close()
        $eofExit = Wait-Exit $eof
        Assert-True ($eofExit -eq 0) "EOF shutdown exited $eofExit instead of 0."
        $eofShutdowns = @(Get-Events 'serve-eof' | Where-Object { $_.event -eq 'shutdown' })
        Assert-True ($eofShutdowns.Count -eq 1) 'EOF did not produce exactly one shutdown.'
        Assert-StdoutPure 'serve-eof'
    } finally { Stop-Serve $eof }

    Write-Host 'Checking mid-startup shutdown...'
    $early = Start-Serve 'serve-early' (@($ToolPath, 'serve') + $common + @('-o', (Join-Path $fixture 'early-output'), '--port', '0', '--format', 'json', '--control-stdin'))
    try {
        $early.Stdin.WriteLine('{"schemaVersion":"1.0","command":"shutdown","requestId":"early-1"}')
        $earlyExit = Wait-Exit $early
        Assert-True ($earlyExit -eq 0) "Mid-startup shutdown exited $earlyExit instead of 0."
        $earlyShutdowns = @(Get-Events 'serve-early' | Where-Object { $_.event -eq 'shutdown' })
        Assert-True ($earlyShutdowns.Count -eq 1) 'Mid-startup shutdown was not exactly-once.'
        Assert-StdoutPure 'serve-early'
    } finally { Stop-Serve $early }

    Write-Host 'Serve control passed: startup/routes/placement, control validation, generations, failure retention, idempotent shutdown, EOF, and mid-startup stop.'
}
finally {
    if ($KeepFixture) { Write-Host "Fixture and logs retained: $fixture" }
    else {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $relative = [IO.Path]::GetRelativePath($temporaryParent, $resolved)
    if ([IO.Path]::IsPathRooted($relative) -or $relative.StartsWith('..') -or !$relative.StartsWith('serve-control-')) {
        throw "Refusing to remove unexpected fixture path: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
