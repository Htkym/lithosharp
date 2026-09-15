[CmdletBinding()]
param([ValidateRange(30, 600)] [int] $TimeoutSeconds = 180,
    [ValidateRange(5, 1000)] [int] $StressEdits = 20,
    [ValidateRange(0, 120)] [int] $SoakMinutes = 0)
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
function Get-Url([string] $Path) {
    return Invoke-WebRequest -Uri ($origin + $Path) -NoProxy -SkipHttpErrorCheck -TimeoutSec 5
}
function Sample-ServeResources([string] $Label) {
    $process.Refresh()
    $handles = -1
    try { $handles = $process.HandleCount } catch { $handles = -1 }
    $threads = -1
    try { $threads = $process.Threads.Count } catch { $threads = -1 }
    $dist = Join-Path $project 'dist'
    $distFiles = 0
    if (Test-Path -LiteralPath $dist) { $distFiles = @(Get-ChildItem -LiteralPath $dist -Recurse -File -Force -ErrorAction SilentlyContinue).Count }
    $treeWorkingSet = $null
    $treeProcessCount = $null
    if ($IsWindows) {
        $ids = [Collections.Generic.HashSet[int]]::new()
        $null = $ids.Add($process.Id)
        $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)
        do {
            $added = $false
            foreach ($child in $all) {
                if ($ids.Contains([int]$child.ParentProcessId) -and $ids.Add([int]$child.ProcessId)) { $added = $true }
            }
        } while ($added)
        $treeWorkingSet = 0L
        $treeProcessCount = 0
        foreach ($childId in $ids) {
            try {
                $child = [Diagnostics.Process]::GetProcessById($childId)
                $treeWorkingSet += $child.WorkingSet64
                $treeProcessCount++
                $child.Dispose()
            } catch [ArgumentException] { }
        }
    }
    return [pscustomobject]@{
        label = $Label
        time = [DateTimeOffset]::UtcNow.ToString('o')
        workingSetBytes = $process.WorkingSet64
        handleCount = $handles
        threadCount = $threads
        distFiles = $distFiles
        processTreeWorkingSetBytes = $treeWorkingSet
        processTreeCount = $treeProcessCount
    }
}
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
    $restartWorkerStarts = (State).extensions[0].mdx.metrics.workerStarts
    Write-Host "Factory restart recovered with workerStarts=$restartWorkerStarts."
    $resources = [Collections.Generic.List[object]]::new()
    $resources.Add((Sample-ServeResources 'baseline-after-restart'))

    Write-Host 'Checking watch add/delete/rename...'
    $extraPath = Join-Path $content 'extra.mdx'
    $extraSource = "---`ntitle: Extra`n---`n# Extra`n`nExtra body.`n"
    [IO.File]::WriteAllText($extraPath, $extraSource)
    Wait-For { (Get-Url '/guide/extra/').StatusCode -eq 200 -and (Get-Url '/guide/extra/').Content -match 'Extra body' } 'watch add'
    if (!(State).success) { throw 'Add broke the build.' }
    Remove-Item -LiteralPath $extraPath
    Wait-For { (Get-Url '/guide/extra/').StatusCode -eq 404 } 'watch delete'
    if (!(State).success) { throw 'Delete broke the build.' }
    if ((Page) -notmatch 'Factory recovered') { throw 'Delete replaced the surviving page.' }
    $renamedPath = Join-Path $content 'renamed.mdx'
    [IO.File]::WriteAllText($extraPath, $extraSource)
    Wait-For { (Get-Url '/guide/extra/').StatusCode -eq 200 } 'watch re-add'
    Move-Item -LiteralPath $extraPath -Destination $renamedPath
    Wait-For { (Get-Url '/guide/renamed/').StatusCode -eq 200 -and (Get-Url '/guide/extra/').StatusCode -eq 404 } 'watch rename'
    if (!(State).success) { throw 'Rename broke the build.' }
    Remove-Item -LiteralPath $renamedPath
    Wait-For { (Get-Url '/guide/renamed/').StatusCode -eq 404 -and (State).success } 'watch rename cleanup'
    $resources.Add((Sample-ServeResources 'after-add-delete-rename'))

    Write-Host "Checking $StressEdits continuous watch edits..."
    $baseSource = [IO.File]::ReadAllText($pagePath)
    $soak = [Diagnostics.Stopwatch]::StartNew()
    for ($edit = 1; $edit -le $StressEdits; $edit++) {
        [IO.File]::WriteAllText($pagePath, $baseSource + "`nRevision $edit.`n")
        if ($SoakMinutes -gt 0) {
            Wait-For { (Page) -match "Revision $edit\." -and (State).success } "edit $edit convergence"
            $due = $SoakMinutes * 60 * $edit / $StressEdits
            while ($soak.Elapsed.TotalSeconds -lt $due) { Start-Sleep -Milliseconds 150 }
        }
        if ($edit % 5 -eq 0) { $resources.Add((Sample-ServeResources "edit-$edit")) }
        if ($edit % 25 -eq 0) { Write-Host "Watch edit $edit/$StressEdits; elapsed $([Math]::Round($soak.Elapsed.TotalSeconds)) seconds." }
    }
    Wait-For { (Page) -match "Revision $StressEdits" -and (State).success } 'continuous edits convergence'
    $resources.Add((Sample-ServeResources 'after-continuous-edits'))
    Write-Host "Completed $StressEdits edits over $([Math]::Round($soak.Elapsed.TotalSeconds, 1)) seconds (requested minimum: $SoakMinutes minutes)."

    Write-Host 'Checking stale coalescing converges to the latest edit...'
    [IO.File]::WriteAllText($pagePath, $baseSource + "`nRevision stale-9999.`n")
    [IO.File]::WriteAllText($pagePath, $baseSource + "`nRevision final-10000.`n")
    Wait-For { (Page) -match 'Revision final-10000' -and (State).success } 'stale coalescing'
    if ((Page) -match 'Revision stale-9999' -and (Page) -notmatch 'Revision final-10000') { throw 'A stale edit overwrote the latest result.' }
    $resources.Add((Sample-ServeResources 'after-stale-coalescing'))

    Write-Host 'Checking failed rebuild keeps the previous output after long use...'
    $beforeLongFailure = (Get-Url '/guide/index/').Content
    [IO.File]::WriteAllText($pagePath, $baseSource + "`n<Unclosed")
    Wait-For { !(State).success } 'long-use MDX diagnostic'
    if ((Page) -notmatch 'Revision final-10000') { throw 'Long-use failure replaced the published page.' }
    [IO.File]::WriteAllText($pagePath, $baseSource + "`nRevision final-10000.`nRecovered after soak.`n")
    Wait-For { (Page) -match 'Recovered after soak' -and (State).success } 'long-use recovery'
    $resources.Add((Sample-ServeResources 'after-recovery'))

    $first = $resources[0]
    $last = $resources[$resources.Count - 1]
    $workingGrowth = $last.workingSetBytes - $first.workingSetBytes
    $handleGrowth = ($last.handleCount - $first.handleCount)
    Write-Host ("Watch resources: first workingSet={0}MB handles={1} threads={2} distFiles={3}; last workingSet={4}MB handles={5} threads={6} distFiles={7}; growth workingSet={8}MB handles={9}." -f `
        ([Math]::Round($first.workingSetBytes / 1MB, 1)), $first.handleCount, $first.threadCount, $first.distFiles, `
        ([Math]::Round($last.workingSetBytes / 1MB, 1)), $last.handleCount, $last.threadCount, $last.distFiles, `
        ([Math]::Round($workingGrowth / 1MB, 1)), $handleGrowth)
    if ($workingGrowth -gt 500MB) { throw "Unexplained working-set growth: $([Math]::Round($workingGrowth / 1MB, 1))MB." }
    if ($first.handleCount -ge 0 -and $last.handleCount -ge 0 -and $handleGrowth -gt 300) { throw "Unexplained handle growth: $handleGrowth." }
    if ($IsWindows -and $last.processTreeCount -gt $first.processTreeCount + 2) { throw 'Unexplained child process growth.' }
    [IO.File]::WriteAllText((Join-Path $fixture 'watch-resources.json'), ($resources | ConvertTo-Json -Depth 5))
    Write-Host 'MDX watch passed: imported component reuse, MDX/C# errors, output preservation and recovery.'
} finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    [IO.File]::WriteAllText((Join-Path $fixture 'stdout.txt'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $fixture 'stderr.txt'), $stderr.GetAwaiter().GetResult())
    $process.Dispose()
    Write-Host "Watch evidence: $fixture"
}
