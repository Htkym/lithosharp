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
foreach ($argument in @((Join-Path $repo 'src/LithoSharp.Tool/bin/Release/net10.0/LithoSharp.Tool.dll'), 'serve', (Join-Path $project 'Watch.csproj'), '-c', 'Release', '--port', "$port", '--format', 'json')) { $start.ArgumentList.Add($argument) }
$process = $null; $stdout = $null; $stderr = $null
# Only known scalar observations leave the owned fixture. Raw output stays local.
$watchEvidence = [ordered]@{ schemaVersion = 1; outcome = 'RUNNING'; failureCategory = $null; warmupBaselineGeneration = $null; warmupCompletedGeneration = $null; importedEditBaselineGeneration = $null; errorRecovery = [Collections.Generic.List[object]]::new(); droppedPolls = 0; stdoutCaptured = $false; stderrCaptured = $false; cleanupCompleted = $false; cleanupFailures = [Collections.Generic.List[string]]::new(); polls = [Collections.Generic.List[object]]::new() }
$pollPage = $null; $pollState = $null; $activeFailure = $null; $cleanupFailure = $null
function Record-WatchCleanupFailure([string] $Stage, [Management.Automation.ErrorRecord] $Failure) {
    if ($null -eq $script:cleanupFailure) { $script:cleanupFailure = $Failure }
    if ($script:watchEvidence.cleanupFailures.Count -lt 16) { $script:watchEvidence.cleanupFailures.Add($Stage) }
    $script:watchEvidence.outcome = 'FAIL'
    if ($null -eq $script:activeFailure) { $script:watchEvidence.failureCategory = 'CLEANUP_FAILURE' }
}
function Known-Count($Value) {
    if (($Value -is [int] -or $Value -is [long]) -and $Value -ge 0) { return $Value }
    return $null
}
function Known-Bool($Value) { if ($Value -is [bool]) { return $Value }; return $null }
function Known-Phase([string] $Value) {
    if ($Value -cmatch '^edit [0-9]+ convergence$') { return 'edit-convergence' }
    if ($Value -cin @('initial MDX build', 'completed MDX warmup', 'completed imported component rebuild', 'MDX diagnostic', 'MDX recovery', 'C# diagnostic', 'factory restart', 'watch add', 'watch delete', 'watch re-add', 'watch rename', 'watch rename cleanup', 'continuous edits convergence', 'stale coalescing', 'long-use MDX diagnostic', 'long-use recovery')) { return $Value }
    return 'unknown'
}
function Add-WatchPoll([string] $Description, [long] $ElapsedMilliseconds, [bool] $Passed) {
    if ($script:watchEvidence.polls.Count -eq 1024) { $script:watchEvidence.polls.RemoveAt(0); $script:watchEvidence.droppedPolls++ }
    $generation = if ($null -ne $script:pollState) { $script:pollState.generation } else { $null }
    $baseline = $script:watchEvidence.importedEditBaselineGeneration
    $script:watchEvidence.polls.Add([ordered]@{
        phase = Known-Phase $Description; elapsedMilliseconds = $ElapsedMilliseconds; conditionPassed = $Passed
        page = $script:pollPage; state = $script:pollState
        generationAdvanced = if ($null -ne $generation -and $null -ne $baseline) { $generation -gt $baseline } else { $null }
        contentConverged = if ($null -ne $script:pollPage) { $script:pollPage.count2 } else { $null }
        strictReuseSatisfied = if ($null -ne $script:pollPage -and $null -ne $script:pollState -and $null -ne $script:pollState.workerStarts) { $script:pollPage.count2 -and $script:pollState.workerStarts -eq 0 } else { $null }
    })
}
function Summarize-MachineOutput([string] $Text) {
    $events = [Collections.Generic.List[object]]::new(); $invalid = 0; $dropped = 0
    # Inspect at most the last 1 MiB of characters, then at most 4096 bounded lines.
    $truncated = $Text.Length -gt 1048576
    $tail = if ($truncated) { $Text.Substring($Text.Length - 1048576) } else { $Text }
    $lines = $tail.Split([char]10)
    $startLine = [Math]::Max(0, $lines.Length - 4096)
    for ($lineIndex = $startLine; $lineIndex -lt $lines.Length; $lineIndex++) {
        $line = $lines[$lineIndex]
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line.Length -gt 65536) { $invalid++; continue }
        try { $record = ConvertFrom-Json -InputObject $line -ErrorAction Stop } catch { $invalid++; continue }
        if ($record.event -isnot [string] -or $record.event -cnotin @('startup', 'rebuild-started', 'rebuild-succeeded', 'rebuild-failed', 'shutdown')) { $invalid++; continue }
        if ($events.Count -eq 256) { $events.RemoveAt(0); $dropped++ }
        $events.Add([ordered]@{ event = $record.event; generation = Known-Count $record.generation; success = Known-Bool $record.success; exitCode = Known-Count $record.exitCode })
    }
    return [ordered]@{ characters = $Text.Length; tailTruncated = $truncated; lineWindowTruncated = $startLine -gt 0; invalidOrUnrecognizedLines = $invalid; droppedEvents = $dropped; events = @($events.ToArray()) }
}
function Save-WatchDiagnostics([string] $StdoutText, [string] $StderrText) {
    $document = [ordered]@{
        schemaVersion = 1; outcome = $script:watchEvidence.outcome; failureCategory = $script:watchEvidence.failureCategory
        timeoutSeconds = $TimeoutSeconds; strictImportedReuse = 'Count2_AND_workerStarts0'
        warmupBaselineGeneration = $script:watchEvidence.warmupBaselineGeneration; warmupCompletedGeneration = $script:watchEvidence.warmupCompletedGeneration
        importedEditBaselineGeneration = $script:watchEvidence.importedEditBaselineGeneration
        errorRecovery = @($script:watchEvidence.errorRecovery.ToArray())
        droppedPolls = $script:watchEvidence.droppedPolls; polls = @($script:watchEvidence.polls.ToArray())
        stdoutCaptured = $script:watchEvidence.stdoutCaptured; stderrCaptured = $script:watchEvidence.stderrCaptured; cleanupCompleted = $script:watchEvidence.cleanupCompleted; cleanupFailures = @($script:watchEvidence.cleanupFailures.ToArray()); cleanupBudgetMilliseconds = 10000
        machine = Summarize-MachineOutput $StdoutText
        # No raw stderr, error messages, paths, source, environment or command lines.
        stderr = [ordered]@{ characters = $StderrText.Length; present = $StderrText.Length -gt 0 }
    }
    $json = $document | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText((Join-Path $fixture 'watch-diagnostics.json'), $json)
    # Unique fixture filename; workflow uploads only this explicit sanitized file class.
    $destination = Join-Path $repo 'artifacts/test-diagnostics/mdx-watch'
    $null = [IO.Directory]::CreateDirectory($destination)
    $name = [IO.Path]::GetFileName($fixture) + '.json'
    $stream = [IO.FileStream]::new((Join-Path $destination $name), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes($json); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}
function Wait-For([scriptblock] $Condition, [string] $Description) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ($process.HasExited) { throw "Server stopped while waiting for $Description" }
        $script:pollPage = $null; $script:pollState = $null; $passed = $false
        try { $passed = [bool](& $Condition); if ($passed) { return } } catch [Net.Http.HttpRequestException] { }
        finally { Add-WatchPoll $Description $timer.ElapsedMilliseconds $passed }
        Start-Sleep -Milliseconds 150
    }
    $script:watchEvidence.failureCategory = 'WAIT_TIMEOUT'
    throw "Timed out waiting for $Description"
}
function State {
    $value = Invoke-RestMethod "$origin/_lithosharp/diagnostics" -NoProxy -TimeoutSec 5
    $mdx = $value.extensions[0].mdx
    $script:pollState = [ordered]@{
        generation = Known-Count $value.generation; success = Known-Bool $value.success
        diagnosticCount = if ($value.diagnostics -is [array]) { $value.diagnostics.Count } else { $null }
        pageCount = if ($mdx.pages -is [array]) { $mdx.pages.Count } else { $null }
        workerStarts = Known-Count $mdx.metrics.workerStarts
        renderedPages = Known-Count $mdx.metrics.renderedPages; compiledModules = Known-Count $mdx.metrics.compiledModules
        cacheHit = Known-Bool $mdx.metrics.cacheHit
    }
    return $value
}
function Page {
    $response = Invoke-WebRequest "$origin/guide/index/" -NoProxy -TimeoutSec 5
    $script:pollPage = [ordered]@{ statusCode = Known-Count ([int]$response.StatusCode); count1 = $response.Content -match 'Count (<!-- -->)?1'; count2 = $response.Content -match 'Count (<!-- -->)?2'; warmupMarker = $response.Content -match 'Watch warmup marker' }
    return $response.Content
}
function Start-WatchFailureEvidence([string] $Phase) {
    if ($Phase -cnotin @('MDX diagnostic', 'C# diagnostic', 'long-use MDX diagnostic')) { throw 'Unknown failure phase.' }
    $observedState = State
    $generation = Known-Count $observedState.generation; $success = Known-Bool $observedState.success
    if ($null -eq $generation -or $success -ne $true) { throw 'A known successful baseline is required before an invalid edit.' }
    if ($script:watchEvidence.errorRecovery.Count -ge 3) { throw 'Unexpected additional failure phase.' }
    $evidence = [ordered]@{ phase = $Phase; baselineGeneration = $generation; failureGeneration = $null; recoveryGeneration = $null; recoveryBeforeGeneration = $null; recoveryAfterGeneration = $null; recoveryStateMatched = $null }
    $script:watchEvidence.errorRecovery.Add($evidence)
    return $evidence
}
function Test-WatchFailedGeneration($Evidence) {
    $observedState = State
    $generation = Known-Count $observedState.generation; $success = Known-Bool $observedState.success
    if ($null -eq $generation -or $success -ne $false -or $generation -le $Evidence.baselineGeneration) { return $false }
    $Evidence.failureGeneration = $generation
    return $true
}
function Test-WatchRecoveryGeneration($Evidence, [string] $Marker) {
    $failedGeneration = Known-Count $Evidence.failureGeneration
    if ($null -eq $failedGeneration) { return $false }
    # These HTTP reads are sequential, not an atomic page/state snapshot.
    # Require stable successful State observations around this unique marker.
    $beforeState = State
    $generation = Known-Count $beforeState.generation; $success = Known-Bool $beforeState.success
    if ($null -eq $generation -or $success -ne $true -or $generation -le $failedGeneration) { return $false }
    $observedPage = Page; $afterState = State
    $afterGeneration = Known-Count $afterState.generation; $afterSuccess = Known-Bool $afterState.success
    if ($observedPage -notmatch $Marker -or $null -eq $afterGeneration -or $afterGeneration -ne $generation -or $afterSuccess -ne $true) { return $false }
    $Evidence.recoveryGeneration = $generation
    $Evidence.recoveryBeforeGeneration = $generation; $Evidence.recoveryAfterGeneration = $afterGeneration; $Evidence.recoveryStateMatched = $true
    return $true
}
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
    $treeProcesses = @()
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
                $treeProcesses += [pscustomobject]@{ id = $child.Id; name = $child.ProcessName }
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
        processTree = $treeProcesses
    }
}
try {
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    Wait-For { (Page) -match 'Count (<!-- -->)?1' } 'initial MDX build'
    $initialState = State
    if ($initialState.extensions[0].mdx.metrics.workerStarts -ne 1) { throw 'Initial build did not start one worker.' }
    $warmupBaseline = Known-Count $initialState.generation
    if ($null -eq $warmupBaseline) { throw 'Initial build generation is unavailable.' }
    $script:watchEvidence.warmupBaselineGeneration = $warmupBaseline
    [IO.File]::WriteAllText($pagePath, $source + "`nWatch warmup marker.`n")
    Wait-For {
        $observedPage = Page; $observedState = State
        $generation = Known-Count $observedState.generation; $success = Known-Bool $observedState.success
        $completed = $observedPage -match 'Watch warmup marker' -and $observedPage -match 'Count (<!-- -->)?1' -and $success -eq $true -and $null -ne $generation -and $generation -gt $warmupBaseline
        if ($completed) { $script:watchEvidence.warmupCompletedGeneration = $generation }
        return $completed
    } 'completed MDX warmup'
    $script:watchEvidence.importedEditBaselineGeneration = $script:watchEvidence.warmupCompletedGeneration
    [IO.File]::WriteAllText($componentPath, $component.Replace('useState(1)', 'useState(2)'))
    Wait-For {
        # Page and State are separate HTTP observations, not an atomic snapshot.
        # Require completed non-noop work; a later cache-only generation cannot mask it.
        $observedPage = Page; $observedState = State
        $generation = Known-Count $observedState.generation; $success = Known-Bool $observedState.success
        $compiled = Known-Count $observedState.extensions[0].mdx.metrics.compiledModules
        $rendered = Known-Count $observedState.extensions[0].mdx.metrics.renderedPages
        $workers = Known-Count $observedState.extensions[0].mdx.metrics.workerStarts
        $completed = $observedPage -match 'Count (<!-- -->)?2' -and $success -eq $true -and $null -ne $generation -and $generation -gt $script:watchEvidence.importedEditBaselineGeneration -and $null -ne $compiled -and $compiled -ge 0 -and $null -ne $rendered -and $rendered -ge 1
        if (!$completed) { return $false }
        if ($null -eq $workers -or $workers -ne 0) {
            $script:watchEvidence.failureCategory = 'IMPORTED_EDIT_REUSE_FAILED'
            throw 'A completed non-noop component edit did not reuse the worker.'
        }
        return $true
    } 'completed imported component rebuild'
    if ((State).extensions[0].mdx.metrics.workerStarts -ne 0) { throw 'A component edit restarted the worker.' }
    $mdxFailure = Start-WatchFailureEvidence 'MDX diagnostic'
    [IO.File]::WriteAllText($pagePath, $source + "`n<Unclosed")
    Wait-For { Test-WatchFailedGeneration $mdxFailure } 'MDX diagnostic'
    if ((Page) -notmatch 'Count (<!-- -->)?2') { throw 'Failed MDX replaced the published page.' }
    [IO.File]::WriteAllText($pagePath, $source + "`nRecovered content.`n")
    Wait-For { Test-WatchRecoveryGeneration $mdxFailure 'Recovered content' } 'MDX recovery'
    $csharpFailure = Start-WatchFailureEvidence 'C# diagnostic'
    [IO.File]::WriteAllText($factoryPath, $factory + "`ninvalid C# syntax")
    Wait-For { Test-WatchFailedGeneration $csharpFailure } 'C# diagnostic'
    if ((Page) -notmatch 'Recovered content') { throw 'Failed C# replaced the published page.' }
    [IO.File]::WriteAllText($factoryPath, $factory.Replace('new SiteSettings()', 'new SiteSettings { Title="Factory recovered" }'))
    Wait-For { Test-WatchRecoveryGeneration $csharpFailure 'Factory recovered' } 'factory restart'
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
            if ($edit % 25 -eq 0) {
                Wait-For { (Page) -match "Revision $edit\." -and (State).success } "edit $edit convergence"
            }
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
    $longFailure = Start-WatchFailureEvidence 'long-use MDX diagnostic'
    [IO.File]::WriteAllText($pagePath, $baseSource + "`n<Unclosed")
    Wait-For { Test-WatchFailedGeneration $longFailure } 'long-use MDX diagnostic'
    if ((Page) -notmatch 'Revision final-10000') { throw 'Long-use failure replaced the published page.' }
    [IO.File]::WriteAllText($pagePath, $baseSource + "`nRevision final-10000.`nRecovered after soak.`n")
    Wait-For { Test-WatchRecoveryGeneration $longFailure 'Recovered after soak' } 'long-use recovery'
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
    [IO.File]::WriteAllText((Join-Path $fixture 'watch-resources.json'), ($resources | ConvertTo-Json -Depth 5))
    # Compare warmed continuous-edit samples. Build-server startup and the deliberate
    # worker restart after the final failure are different lifecycle phases.
    $steady = @($resources | Where-Object { $_.label -match '^edit-\d+$' })
    if ($IsWindows -and $steady.Count -gt 1 -and $steady[-1].processTreeCount -gt $steady[0].processTreeCount + 2) {
        throw 'Child process count grew during continuous edits; inspect watch-resources.json.'
    }
    Write-Host 'MDX watch passed: imported component reuse, MDX/C# errors, output preservation and recovery.'
    $script:watchEvidence.outcome = 'PASS'
} catch {
    $activeFailure = $_
    $script:watchEvidence.outcome = 'FAIL'
    if ($null -eq $script:watchEvidence.failureCategory) { $script:watchEvidence.failureCategory = 'EXCEPTION' }
    throw
} finally {
    $stdoutText = ''; $stderrText = ''
    $cleanupTimer = [Diagnostics.Stopwatch]::StartNew()
    if ($null -ne $process) {
        # Every owned stage gets an attempt; no secondary replaces the body ErrorRecord.
        try { if (!$process.HasExited) { $process.Kill($true) } }
        catch { Record-WatchCleanupFailure 'kill' $_ }
        try {
            $remaining = [int][Math]::Max(0, 10000 - $cleanupTimer.ElapsedMilliseconds)
            if (!$process.WaitForExit($remaining)) { throw 'Owned watch exit unavailable within cleanup window.' }
        } catch { Record-WatchCleanupFailure 'wait' $_ }
        try {
            $remaining = [int][Math]::Max(0, 10000 - $cleanupTimer.ElapsedMilliseconds)
            if ($null -eq $stdout -or !$stdout.Wait($remaining)) { throw 'Owned watch stdout unavailable within cleanup window.' }
            $stdoutText = $stdout.GetAwaiter().GetResult(); $script:watchEvidence.stdoutCaptured = $true
        } catch { Record-WatchCleanupFailure 'stdout-capture' $_ }
        try {
            $remaining = [int][Math]::Max(0, 10000 - $cleanupTimer.ElapsedMilliseconds)
            if ($null -eq $stderr -or !$stderr.Wait($remaining)) { throw 'Owned watch stderr unavailable within cleanup window.' }
            $stderrText = $stderr.GetAwaiter().GetResult(); $script:watchEvidence.stderrCaptured = $true
        } catch { Record-WatchCleanupFailure 'stderr-capture' $_ }
        try { if ($script:watchEvidence.stdoutCaptured) { [IO.File]::WriteAllText((Join-Path $fixture 'stdout.txt'), $stdoutText) } }
        catch { Record-WatchCleanupFailure 'stdout-save' $_ }
        try { if ($script:watchEvidence.stderrCaptured) { [IO.File]::WriteAllText((Join-Path $fixture 'stderr.txt'), $stderrText) } }
        catch { Record-WatchCleanupFailure 'stderr-save' $_ }
        try { $process.Dispose() } catch { Record-WatchCleanupFailure 'dispose' $_ }
    }
    $script:watchEvidence.cleanupCompleted = $null -eq $cleanupFailure
    try { Save-WatchDiagnostics $stdoutText $stderrText }
    catch {
        Record-WatchCleanupFailure 'diagnostic-save' $_
        # A fixed, bounded marker exposes missing evidence without serializing errors.
        try { Write-Warning 'MDX_WATCH_DIAGNOSTIC_SAVE_FAILED: sanitized evidence is unavailable.' -WarningAction Continue }
        catch { Record-WatchCleanupFailure 'diagnostic-save-marker' $_ }
    }
    if ($null -eq $activeFailure -and $null -ne $cleanupFailure) {
        $PSCmdlet.ThrowTerminatingError($cleanupFailure)
    }
}
