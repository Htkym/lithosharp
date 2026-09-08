[CmdletBinding()]
param([ValidateRange(30, 600)] [int] $TimeoutSeconds = 180)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $repo ('.tmp/mdx-watch-' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $fixture 'site'
$content = Join-Path $project 'content'
$null = New-Item -ItemType Directory -Path $content
$mdxProject = [Security.SecurityElement]::Escape((Join-Path $repo 'src/LithoSharp.Mdx/LithoSharp.Mdx.csproj'))
[IO.File]::WriteAllText((Join-Path $project 'Watch.csproj'), "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><ProjectReference Include=`"$mdxProject`" /></ItemGroup></Project>")
$worker = (Join-Path $repo 'src/LithoSharp.Mdx/worker').Replace('\', '/')
$factory = @"
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;
public sealed class WatchFactory : ISiteFactory {
 public Task<SiteDefinition> CreateAsync(SiteFactoryContext context, CancellationToken cancellationToken = default) {
  var docs = new DocumentationSite(new(context.ProjectDirectory, "$worker") {Cacheable=true});
  docs.AddCollection(new("guide", [new("current", "en", Path.Combine(context.ProjectDirectory,"content"), "guide")]) {UseMdx=true});
  return Task.FromResult(new SiteDefinition(new SiteSettings(), []) {OutputDirectory="dist", Customization=new(){Template=new DocsSiteTemplate()}, Options=new(){Extensions=[docs],BuildTimestamp=DateTimeOffset.UnixEpoch}});
 }
}
"@
$factoryPath = Join-Path $project 'Factory.cs'
[IO.File]::WriteAllText($factoryPath, $factory)
$pagePath = Join-Path $content 'index.mdx'
$source = "---`ntitle: Watch`n---`nimport Counter from './Counter.jsx';`n`n# Watch`n`n<Counter />`n"
[IO.File]::WriteAllText($pagePath, $source)
$componentPath = Join-Path $content 'Counter.jsx'
$component = "import {useState} from 'react';export default function Counter(){const [n,set]=useState(1);return <button onClick={()=>set(n+1)}>Count {n}</button>}"
[IO.File]::WriteAllText($componentPath, $component)
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
$origin = "http://127.0.0.1:$port"
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
foreach ($argument in @((Join-Path $repo 'src/LithoSharp.Tool/bin/Release/net10.0/LithoSharp.Tool.dll'), 'serve', (Join-Path $project 'Watch.csproj'), '-c', 'Release', '--port', "$port")) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
function Wait-For([scriptblock] $Condition, [string] $Description) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ($process.HasExited) { throw "Server stopped while waiting for $Description" }
        try { if (& $Condition) { return } } catch [Net.Http.HttpRequestException] { }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out waiting for $Description"
}
function State { Invoke-RestMethod "$origin/_lithosharp/diagnostics" -NoProxy -TimeoutSec 5 }
function Page { (Invoke-WebRequest "$origin/guide/index/" -NoProxy -TimeoutSec 5).Content }
try {
    Wait-For { (Page) -match 'Count (<!-- -->)?1' } 'initial MDX build'
    if ((State).extensions[0].mdx.metrics.workerStarts -ne 1) { throw 'Initial build did not start one worker.' }
    [IO.File]::WriteAllText($componentPath, $component.Replace('useState(1)', 'useState(2)'))
    Wait-For { (Page) -match 'Count (<!-- -->)?2' -and (State).extensions[0].mdx.metrics.workerStarts -eq 0 } 'completed imported component rebuild'
    if ((State).extensions[0].mdx.metrics.workerStarts -ne 0) { throw 'A component edit restarted the worker.' }
    [IO.File]::WriteAllText($pagePath, $source + "`n<Unclosed")
    Wait-For { !(State).success } 'MDX diagnostic'
    if ((Page) -notmatch 'Count (<!-- -->)?2') { throw 'Failed MDX replaced the published page.' }
    [IO.File]::WriteAllText($pagePath, $source + "`nRecovered content.`n")
    Wait-For { (Page) -match 'Recovered content' } 'MDX recovery'
    [IO.File]::WriteAllText($factoryPath, $factory + "`ninvalid C# syntax")
    Wait-For { !(State).success } 'C# diagnostic'
    if ((Page) -notmatch 'Recovered content') { throw 'Failed C# replaced the published page.' }
    [IO.File]::WriteAllText($factoryPath, $factory.Replace('new SiteSettings()', 'new SiteSettings { Title="Factory recovered" }'))
    Wait-For { (State).success -and (Page) -match 'Factory recovered' } 'factory restart'
    Write-Host 'MDX watch passed: imported component reuse, MDX/C# errors, output preservation and recovery.'
} finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    [IO.File]::WriteAllText((Join-Path $fixture 'stdout.txt'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $fixture 'stderr.txt'), $stderr.GetAwaiter().GetResult())
    $process.Dispose()
    Write-Host "Watch evidence: $fixture"
}
