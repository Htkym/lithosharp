<#
.SYNOPSIS
Runs the incremental performance scenarios as independent jobs and records raw evidence.

.DESCRIPTION
Each run gets its own corpus, cache and output directory under -StateRoot, its own process,
and its own timeout. The script never deletes a run directory: a timeout is recorded as a
timeout, and a retry is added as a new run index. Completed runs are skipped with -Resume.

Scenario selection:
- a single scenario runs after a successful baseline build; the harness reports that baseline
  as "prepare" separately, so a cold-process cache hit is never confused with a warm worker.
- "all" runs the harness sequence in one process (the published 1.0.0 harness has no scenario
  selection) and the runner records one entry per measured scenario.

The measured numbers come from the harness result file, not from the process wall time:

- markdown: <runDir>/result.json            (clean, no-op, single-page-change, layout-change)
- mdx:      <runDir>/harness/measurements.json (cold, warm-cache, no-op, one-page,
            shared-component, shared-css, shared-image, layout, route, lockfile)

.EXAMPLE
& ./eng/Measure-IncrementalScenarios.ps1 -Harness markdown -Label B1r -RunId v110-04-B1r `
    -Sizes 1000,10000 -Scenarios all -Runs 5
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('markdown', 'mdx')] [string] $Harness,
    [Parameter(Mandatory)] [string] $RunId,
    [string] $Label = 'unlabeled',
    [string] $TaskId = 'V110-04',
    [string] $Sizes = '1000',
    [string] $Scenarios,
    [ValidateRange(1, 20)] [int] $Runs = 5,
    [ValidateRange(30, 86400)] [int] $TimeoutSeconds = 1800,
    [string] $EvidenceRoot,
    [string] $StateRoot,
    [string] $HarnessDll,
    [string] $WorkerDirectory,
    [string] $HarnessRoot,
    [switch] $Resume
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$EvidenceRoot) { $EvidenceRoot = Join-Path $repo '.local/verification/1.1.0' }
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (!$StateRoot) { $StateRoot = Join-Path $EvidenceRoot "$RunId/runs" }
$StateRoot = [IO.Path]::GetFullPath($StateRoot)
if (!$HarnessRoot) { $HarnessRoot = $repo }
$HarnessRoot = [IO.Path]::GetFullPath($HarnessRoot)

$knownScenarios = if ($Harness -eq 'markdown') {
    @('all', 'clean', 'no-op', 'single-page-change', 'layout-change')
}
else {
    @('all', 'cold', 'warm-cache', 'no-op', 'one-page', 'shared-component', 'shared-css', 'shared-image', 'layout', 'route', 'lockfile')
}
# Each named scenario already has its own measurement. The combined sequence
# remains available explicitly; running both by default repeats every workload.
if (!$Scenarios) { $scenarioList = @($knownScenarios | Where-Object { $_ -ne 'all' }) }
else { $scenarioList = @($Scenarios -split '[,\s]+' | Where-Object { $_ }) }
$sizeList = @($Sizes -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { [int]$_ })
foreach ($scenario in $scenarioList) {
    if ($knownScenarios -notcontains $scenario) { throw "Unknown $Harness scenario '$scenario'. Known: $($knownScenarios -join ', ')." }
}
foreach ($size in $sizeList) {
    if ($size -notin 100, 1000, 10000) { throw "The size must be 100, 1000 or 10000 (got $size)." }
}

if (!$HarnessDll) {
    $HarnessDll = if ($Harness -eq 'markdown') {
        Join-Path $repo 'benchmarks/LithoSharp.Performance/bin/Release/net10.0/LithoSharp.Performance.dll'
    }
    else {
        Join-Path $repo 'benchmarks/LithoSharp.MdxPerformance/bin/Release/net10.0/LithoSharp.MdxPerformance.dll'
    }
}
$HarnessDll = [IO.Path]::GetFullPath($HarnessDll)
if (!(Test-Path -LiteralPath $HarnessDll -PathType Leaf)) { throw "Harness assembly not found: $HarnessDll (build it in Release first)." }
if ($Harness -eq 'mdx') {
    if (!$WorkerDirectory) { throw '-WorkerDirectory is required for the mdx harness (restored worker directory).' }
    $WorkerDirectory = [IO.Path]::GetFullPath($WorkerDirectory)
    if (!(Test-Path -LiteralPath $WorkerDirectory -PathType Container)) { throw "Worker directory not found: $WorkerDirectory" }
}

$null = New-Item -ItemType Directory -Force -Path $StateRoot
$ledgerPath = Join-Path (Split-Path $StateRoot -Parent) 'ledger.jsonl'
$summaryPath = Join-Path (Split-Path $StateRoot -Parent) 'benchmark-baseline.json'

function Get-Property($Object, [string] $Name, $Default = $null) {
    if ($null -eq $Object) { return $Default }
    if ($Object -is [Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $Default
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Test-NonnegativeFiniteNumber($Value) {
    if ($null -eq $Value -or !($Value -is [byte] -or $Value -is [sbyte] -or $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or $Value -is [int64] -or $Value -is [uint64] -or
        $Value -is [single] -or $Value -is [double] -or $Value -is [decimal])) { return $false }
    return [double]::IsFinite([double]$Value) -and $Value -ge 0
}

function Test-MeasurementProperty($Object, [string] $Name) {
    if ($null -eq $Object) { return $false }
    if ($Object -is [Collections.IDictionary]) { return $Object.Contains($Name) }
    return $null -ne $Object.PSObject.Properties[$Name]
}

function Test-WorkCount($Value, [decimal] $Maximum) {
    if (!(Test-NonnegativeFiniteNumber $Value)) { return $false }
    # Floating counts outside their exact-safe integer range are UNPROVEN;
    # decimal and integral CLR values retain their exact representation.
    if ($Value -is [single] -and $Value -gt 16777215) { return $false }
    if ($Value -is [double] -and $Value -gt 9007199254740991) { return $false }
    $exact = [decimal]$Value
    if ($Value -is [single] -or $Value -is [double]) {
        if ([double]$Value -ne [Math]::Floor([double]$Value)) { return $false }
    }
    elseif ($exact -ne [decimal]::Truncate($exact)) { return $false }
    return $exact -le $Maximum
}

function Assert-MdxWorkMeasurement($Metrics) {
    if (!(Test-MeasurementProperty $Metrics 'work')) { return }
    $work = Get-Property $Metrics 'work'
    if ($null -eq $work -or (Get-Property $work 'hasCompleteReports') -isnot [bool]) { throw 'Invalid reported MDX Work metrics.' }
    $counts = @('requestAttempts', 'workerStarts', 'compiledModules', 'renderedPages', 'mdxCompileInvocations',
        'renderInvocations', 'esbuildInvocations', 'browserBuildInvocations', 'browserEntryBuildAttempts')
    foreach ($field in @($counts + @('requestMilliseconds', 'workerMilliseconds', 'serverBundleMilliseconds',
        'browserBundleMilliseconds', 'liveRuntimeMilliseconds', 'renderMilliseconds', 'pluginBundleMilliseconds'))) {
        $value = Get-Property $work $field
        if (!(Test-NonnegativeFiniteNumber $value)) { throw "Invalid reported MDX Work measurement '$field'." }
        if ($counts -ccontains $field) {
            $maximum = if ($field -cin @('requestAttempts', 'workerStarts')) { [decimal]([int]::MaxValue) } else { [decimal]([long]::MaxValue) }
            if (!(Test-WorkCount $value $maximum)) { throw "Invalid or unproven reported MDX Work count '$field'." }
        }
    }
}

function Get-UnknownMeasurementFields($Measured, [string] $MeasurementHarness = $Harness) {
    $unknown = [Collections.Generic.List[string]]::new()
    if ($MeasurementHarness -ceq 'mdx') {
        $metrics = Get-Property $Measured 'mdx'
        if ($null -eq (Get-Property $metrics 'rebundledPages')) { $unknown.Add('mdx.rebundledPages') }
        if (!(Test-MeasurementProperty $metrics 'work')) { $unknown.Add('mdx.work') }
    }
    return ,$unknown.ToArray()
}

function Assert-PrepareMeasurement($Prepare) {
    if ($null -eq $Prepare) { return }
    foreach ($field in 'elapsedMilliseconds', 'cacheHitCount', 'cacheMissCount') {
        if (!(Test-NonnegativeFiniteNumber (Get-Property $Prepare $field))) { throw "Invalid preparation measurement '$field'." }
    }
}

function Assert-MeasurementSet($Entries, [string] $RequestedScenario, [string] $MeasurementHarness = $Harness) {
    if ($MeasurementHarness -cnotin 'markdown', 'mdx') { throw 'Unknown measurement harness.' }
    $scenarioNames = if ($MeasurementHarness -ceq 'markdown') { @('clean', 'no-op', 'single-page-change', 'layout-change') }
        else { @('cold', 'warm-cache', 'no-op', 'one-page', 'shared-component', 'shared-css', 'shared-image', 'layout', 'route', 'lockfile') }
    if ($RequestedScenario -cne 'all' -and $scenarioNames -cnotcontains $RequestedScenario) { throw 'Unknown requested scenario.' }
    $expected = @(if ($RequestedScenario -ceq 'all') { $scenarioNames } else { $RequestedScenario })
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in @($Entries)) {
        $name = Get-Property $entry 'name'
        if ($name -isnot [string] -or $expected -cnotcontains $name -or !$seen.Add($name)) {
            throw 'Measurements contain an unexpected or duplicate scenario name.'
        }
        $measured = Get-Property $entry 'measured'
        $required = if ($MeasurementHarness -ceq 'markdown') {
            @('elapsedMilliseconds', 'allocatedBytes', 'generatedNodeCount', 'invalidatedNodeCount', 'artifactCount', 'peakWorkingSetBytes')
        } else { @('elapsedMilliseconds', 'allocatedBytes', 'cacheHits', 'cacheMisses', 'generatedFiles', 'outputBytes') }
        foreach ($field in $required) {
            if (!(Test-NonnegativeFiniteNumber (Get-Property $measured $field))) { throw "Invalid mandatory measurement '$field'." }
        }
        if ($MeasurementHarness -ceq 'mdx') {
            $metrics = Get-Property $measured 'mdx'
            foreach ($field in 'workerStarts', 'workerMilliseconds', 'serverBundleMilliseconds', 'renderMilliseconds',
                'browserBundleMilliseconds', 'nodeHeapUsedBytes', 'compiledModules', 'renderedPages', 'bundledPages') {
                if (!(Test-NonnegativeFiniteNumber (Get-Property $metrics $field))) { throw "Invalid mandatory MDX measurement '$field'." }
            }
            # Additive work metrics are UNKNOWN when absent, never inferred zero.
            $rebundled = Get-Property $metrics 'rebundledPages'
            if ($null -ne $rebundled -and !(Test-NonnegativeFiniteNumber $rebundled)) { throw 'Invalid optional MDX measurement rebundledPages.' }
            Assert-MdxWorkMeasurement $metrics
        }
    }
    if ($seen.Count -ne $expected.Count) { throw 'Measurements do not contain the complete requested scenario set.' }
}

function Test-CompletedRecordSet($Rows, [string] $RequestedScenario) {
    if (@($Rows).Count -eq 0) { return $false }
    $first = @($Rows)[0]
    if ((Get-Property $first 'requestedScenario') -cne $RequestedScenario) { return $false }
    $entries = [Collections.Generic.List[object]]::new()
    foreach ($row in @($Rows)) {
        if ((Get-Property $row 'status') -cne 'completed' -or
            !(Test-NonnegativeFiniteNumber (Get-Property $row 'exitCode')) -or (Get-Property $row 'exitCode') -ne 0) { return $false }
        foreach ($field in 'harness', 'label', 'size', 'requestedScenario', 'run', 'attempt', 'startedAt', 'resultFile', 'stdoutFile', 'stderrFile') {
            $value = Get-Property $row $field
            if ($null -eq $value -or [string]$value -cne [string](Get-Property $first $field)) { return $false }
        }
        foreach ($field in 'resultFile', 'stdoutFile', 'stderrFile') {
            $value = Get-Property $row $field
            if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) { return $false }
        }
        foreach ($field in 'size', 'run', 'attempt') {
            $value = Get-Property $row $field
            if (!(Test-NonnegativeFiniteNumber $value) -or $value -lt 1 -or [double]$value -ne [Math]::Floor([double]$value)) { return $false }
        }
        try { Assert-PrepareMeasurement (Get-Property $row 'prepare') } catch { return $false }
        $entries.Add([ordered]@{ name = Get-Property $row 'scenario'; measured = Get-Property $row 'measured' })
    }
    try { Assert-MeasurementSet $entries.ToArray() $RequestedScenario (Get-Property $first 'harness'); return $true } catch { return $false }
}

function Test-CompletedRun($Rows, [int] $Size, [string] $RequestedScenario, [int] $Run) {
    $matching = @($Rows | Where-Object {
        (Get-Property $_ 'harness') -ceq $Harness -and (Get-Property $_ 'label') -ceq $Label -and
        (Test-NonnegativeFiniteNumber (Get-Property $_ 'size')) -and (Get-Property $_ 'size') -eq $Size -and
        (Get-Property $_ 'requestedScenario') -ceq $RequestedScenario -and
        (Test-NonnegativeFiniteNumber (Get-Property $_ 'run')) -and (Get-Property $_ 'run') -eq $Run
    })
    foreach ($group in ($matching | Group-Object { Get-Property $_ 'attempt' })) {
        if (Test-CompletedRecordSet @($group.Group) $RequestedScenario) { return $true }
    }
    return $false
}

function Get-Statistics([double[]] $Values) {
    if ($null -eq $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $median = if ($sorted.Count % 2 -eq 1) { $sorted[[int](($sorted.Count - 1) / 2)] }
        else { ($sorted[$sorted.Count / 2 - 1] + $sorted[$sorted.Count / 2]) / 2 }
    $rank = [Math]::Min($sorted.Count - 1, [Math]::Ceiling(0.95 * $sorted.Count) - 1)
    return [ordered]@{
        count  = $sorted.Count
        min    = $sorted[0]
        max    = $sorted[-1]
        median = $median
        p50    = $median
        p95    = $sorted[$rank]
        raw    = @($Values)
    }
}

function ConvertTo-Record($Entry, [string] $Scenario, [string] $RequestedScenario, [string] $Status, $ExitCode,
    [string] $StartedAt, [long] $WallMs, $Prepare, [string[]] $Command, [string] $ResultPath,
    [string] $StdoutPath, [string] $StderrPath, [int] $Size) {
    return [ordered]@{
        harness           = $Harness
        label             = $Label
        size              = $Size
        scenario          = $Scenario
        requestedScenario = $RequestedScenario
        status            = $Status
        exitCode          = $ExitCode
        startedAt         = $StartedAt
        finishedAt        = (Get-Date).ToUniversalTime().ToString('o')
        wallMs            = $WallMs
        measured          = if ($null -ne $Entry) { $Entry.measured } else { $null }
        prepare           = $Prepare
        counters          = if ($null -ne $Entry) { $Entry.counters } else { $null }
        command           = @('dotnet') + $Command
        cwd               = $HarnessRoot
        resultFile        = $ResultPath
        stdoutFile        = $StdoutPath
        stderrFile        = $StderrPath
    }
}

function Invoke-HarnessRun([string] $RunDirectory, [int] $Size, [string] $Scenario) {
    $null = New-Item -ItemType Directory -Force -Path $RunDirectory
    $stdoutPath = Join-Path $RunDirectory 'stdout.txt'
    $stderrPath = Join-Path $RunDirectory 'stderr.txt'
    if ($Harness -eq 'markdown') {
        $resultPath = Join-Path $RunDirectory 'result.json'
        $arguments = @($HarnessDll, '--size', $Size.ToString(), '--output', $resultPath)
        if ($Scenario -ne 'all') { $arguments += @('--scenario', $Scenario) }
    }
    else {
        $resultPath = Join-Path $RunDirectory 'harness/measurements.json'
        $arguments = @($HarnessDll, $Size.ToString(), $WorkerDirectory, (Join-Path $RunDirectory 'harness'))
        if ($Scenario -ne 'all') { $arguments += @('--scenario', $Scenario) }
    }

    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = $HarnessRoot
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $startedAt = (Get-Date).ToUniversalTime().ToString('o')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $status = 'completed'
    $exitCode = $null
    try {
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) {
            $status = 'timeout'
            $process.Kill($true)
            $process.WaitForExit()
        }
        else { $exitCode = $process.ExitCode }
    }
    finally {
        $clock.Stop()
        [IO.File]::WriteAllText($stdoutPath, $stdoutTask.GetAwaiter().GetResult())
        [IO.File]::WriteAllText($stderrPath, $stderrTask.GetAwaiter().GetResult())
        $process.Dispose()
    }
    if ($status -eq 'completed' -and $exitCode -ne 0) { $status = 'failed' }

    $entries = [Collections.Generic.List[object]]::new()
    $prepare = $null
    $measurementError = $null
    if ($status -eq 'completed' -and (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        try {
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        if ($Harness -eq 'markdown') {
            $prepareValue = Get-Property $result 'prepare'
            if ($null -ne $prepareValue) {
                $prepare = [ordered]@{
                    elapsedMilliseconds = Get-Property $prepareValue 'elapsedMilliseconds'
                    cacheHitCount       = Get-Property $prepareValue 'cacheHitCount'
                    cacheMissCount      = Get-Property $prepareValue 'cacheMissCount'
                }
            }
            foreach ($workload in @(Get-Property $result 'workloads' @())) {
                $name = [string](Get-Property $workload 'name')
                $entries.Add([ordered]@{
                    name     = $name
                    measured = [ordered]@{
                        elapsedMilliseconds  = Get-Property $workload 'elapsedMilliseconds'
                        allocatedBytes       = Get-Property $workload 'allocatedBytes'
                        generatedNodeCount   = Get-Property $workload 'generatedNodeCount'
                        invalidatedNodeCount = Get-Property $workload 'invalidatedNodeCount'
                        artifactCount        = Get-Property $workload 'artifactCount'
                        peakWorkingSetBytes  = Get-Property $workload 'peakWorkingSetBytes'
                    }
                    counters = $null
                })
            }
        }
        else {
            foreach ($entry in @(Get-Property $result 'measurements' @())) {
                $name = [string](Get-Property $entry 'name')
                if ($name -ceq 'prepare') {
                    if ($Scenario -ceq 'all' -or $Scenario -ceq 'cold' -or $null -ne $prepare) { throw 'Unexpected or duplicate preparation measurement.' }
                    $prepare = [ordered]@{
                        elapsedMilliseconds = Get-Property $entry 'elapsedMilliseconds'
                        cacheHitCount       = Get-Property $entry 'cacheHits'
                        cacheMissCount      = Get-Property $entry 'cacheMisses'
                    }
                    continue
                }
                $metrics = Get-Property $entry 'mdx'
                $entries.Add([ordered]@{
                    name     = $name
                    measured = [ordered]@{
                        elapsedMilliseconds = Get-Property $entry 'elapsedMilliseconds'
                        allocatedBytes      = Get-Property $entry 'dotnetAllocatedBytes'
                        cacheHits           = Get-Property $entry 'cacheHits'
                        cacheMisses         = Get-Property $entry 'cacheMisses'
                        generatedFiles      = Get-Property $entry 'generatedFiles'
                        outputBytes         = Get-Property $entry 'outputBytes'
                        mdx                 = $metrics
                    }
                    counters = [ordered]@{
                        compiledModules = Get-Property $metrics 'compiledModules'
                        renderedPages   = Get-Property $metrics 'renderedPages'
                        bundledPages    = Get-Property $metrics 'bundledPages'
                        rebundledPages  = Get-Property $metrics 'rebundledPages'
                        workerStarts    = Get-Property $metrics 'workerStarts'
                        work            = Get-Property $metrics 'work'
                    }
                })
            }
        }
        Assert-PrepareMeasurement $prepare
        Assert-MeasurementSet $entries.ToArray() $Scenario
        }
        catch {
            $status = 'invalid-measurement'
            $measurementError = $_.Exception.Message
        }
    }
    if ($status -eq 'completed' -and $entries.Count -eq 0) { $status = 'no-measurement' }

    $records = [Collections.Generic.List[object]]::new()
    if ($entries.Count -eq 0) {
        $records.Add((ConvertTo-Record $null $Scenario $Scenario $status $exitCode $startedAt $clock.ElapsedMilliseconds $prepare $arguments $resultPath $stdoutPath $stderrPath $Size))
    }
    else {
        foreach ($entry in $entries) {
            $records.Add((ConvertTo-Record $entry $entry.name $Scenario $status $exitCode $startedAt $clock.ElapsedMilliseconds $prepare $arguments $resultPath $stdoutPath $stderrPath $Size))
        }
    }
    foreach ($record in $records) {
        $record.measurementError = $measurementError
        $record.unknownMetrics = Get-UnknownMeasurementFields $record.measured
    }
    return $records.ToArray()
}

$existing = @()
if ($Resume -and (Test-Path -LiteralPath $ledgerPath -PathType Leaf)) {
    $existing = @(Get-Content -LiteralPath $ledgerPath | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
}

$records = [Collections.Generic.List[object]]::new()
foreach ($size in $sizeList) {
    foreach ($scenario in $scenarioList) {
        for ($run = 1; $run -le $Runs; $run++) {
            $runDirectory = Join-Path $StateRoot "$Harness-$size-$scenario-run-$run"
            if ($Resume) {
                if (Test-CompletedRun $existing $size $scenario $run) {
                    Write-Host ("resume: skipping $Harness $size $scenario run $run")
                    continue
                }
            }
            # Preserve failed or interrupted attempts, including those with no ledger record.
            $attempt = 1
            $baseRunDirectory = $runDirectory
            while (Test-Path -LiteralPath $runDirectory) {
                $attempt++
                $runDirectory = "$baseRunDirectory-attempt-$attempt"
            }
            Write-Host ("run: $Harness $size $scenario run $run attempt $attempt")
            foreach ($record in (Invoke-HarnessRun $runDirectory $size $scenario)) {
                $record.run = $run
                $record.attempt = $attempt
                $records.Add($record)
                [IO.File]::AppendAllText($ledgerPath, (($record | ConvertTo-Json -Depth 10 -Compress) + [Environment]::NewLine))
                Write-Host ("  {0} {1} exit={2} wall={3}ms measured={4}ms" -f $record.status, $record.scenario, $record.exitCode, $record.wallMs,
                    (Get-Property (Get-Property $record 'measured') 'elapsedMilliseconds'))
            }
        }
    }
}

$all = [Collections.Generic.List[object]]::new()
foreach ($item in $existing) { $all.Add($item) }
foreach ($item in $records) { $all.Add($item) }
# Reclassify old false-completed cohorts in the derived summary only. Raw ledger
# bytes and historical attempts remain untouched; separate retries cannot fill gaps.
foreach ($item in $all) {
    $unknown = Get-UnknownMeasurementFields (Get-Property $item 'measured') (Get-Property $item 'harness')
    if ($item -is [Collections.IDictionary]) { $item['unknownMetrics'] = $unknown }
    else { $item | Add-Member -NotePropertyName unknownMetrics -NotePropertyValue $unknown -Force }
    if ((Get-Property $item 'status') -cne 'completed') { continue }
    $cohort = @($all | Where-Object {
        $same = $true
        foreach ($field in 'harness', 'label', 'size', 'requestedScenario', 'run', 'attempt') {
            if ([string](Get-Property $_ $field) -cne [string](Get-Property $item $field)) { $same = $false; break }
        }
        $same
    })
    if (!(Test-CompletedRecordSet $cohort (Get-Property $item 'requestedScenario'))) {
        if ($item -is [Collections.IDictionary]) { $item['originalStatus'] = 'completed' }
        else { $item | Add-Member -NotePropertyName originalStatus -NotePropertyValue 'completed' -Force }
        $item.status = 'invalid-measurement'
    }
}

# The B0/B1 harnesses predate --scenario. Keep those failed invocations in the
# raw ledger, but do not count their argument errors as workload attempts.
$excluded = @($all | Where-Object {
    $label = [string](Get-Property $_ 'label')
    $status = [string](Get-Property $_ 'status')
    $command = @(Get-Property $_ 'command' @())
    $label -in @('B0', 'B1') -and $status -notin @('completed', 'invalid-measurement') -and $command -contains '--scenario'
})
$included = @($all | Where-Object {
    $label = [string](Get-Property $_ 'label')
    $status = [string](Get-Property $_ 'status')
    $command = @(Get-Property $_ 'command' @())
    !($label -in @('B0', 'B1') -and $status -notin @('completed', 'invalid-measurement') -and $command -contains '--scenario')
})

$cells = [Collections.Generic.List[object]]::new()
foreach ($group in ($included | Group-Object { "$(Get-Property $_ 'harness')|$(Get-Property $_ 'size')|$(Get-Property $_ 'scenario')" })) {
    $items = @($group.Group)
    $completed = @($items | Where-Object { (Get-Property $_ 'status') -eq 'completed' })
    $values = [Collections.Generic.List[double]]::new()
    $prepareValues = [Collections.Generic.List[double]]::new()
    foreach ($item in $completed) {
        $elapsed = Get-Property (Get-Property $item 'measured') 'elapsedMilliseconds'
        if ($null -ne $elapsed) { $values.Add([double]$elapsed) }
        $prepareElapsed = Get-Property (Get-Property $item 'prepare') 'elapsedMilliseconds'
        if ($null -ne $prepareElapsed) { $prepareValues.Add([double]$prepareElapsed) }
    }
    $first = $items[0]
    $cells.Add([ordered]@{
        harness   = Get-Property $first 'harness'
        label     = Get-Property $first 'label'
        size      = Get-Property $first 'size'
        scenario  = Get-Property $first 'scenario'
        status    = if ($completed.Count -eq $items.Count) { 'measured' } elseif ($completed.Count -gt 0) { 'partial' } else { 'blocked' }
        runs      = $items.Count
        completed = $completed.Count
        failures  = @($items | Where-Object { (Get-Property $_ 'status') -ne 'completed' } | ForEach-Object { Get-Property $_ 'status' })
        elapsedMs = Get-Statistics $values.ToArray()
        prepareMs = Get-Statistics $prepareValues.ToArray()
        counters  = @($completed | ForEach-Object { Get-Property $_ 'counters' } | Where-Object { $null -ne $_ })
        unknownMetrics = @($completed | ForEach-Object { Get-Property $_ 'unknownMetrics' @() } | Sort-Object -Unique)
    })
}

$summary = [ordered]@{
    schemaVersion = '1.0'
    planVersion   = '1.1.0'
    taskId        = $TaskId
    runId         = $RunId
    label         = $Label
    harnessRoot   = $HarnessRoot
    harnessDll    = $HarnessDll
    harnessSha256 = (Get-FileHash -LiteralPath $HarnessDll -Algorithm SHA256).Hash
    workerDirectory = $WorkerDirectory
    workerPackageLockSha256 = if ($Harness -eq 'mdx' -and (Test-Path -LiteralPath (Join-Path $WorkerDirectory 'package-lock.json'))) {
        (Get-FileHash -LiteralPath (Join-Path $WorkerDirectory 'package-lock.json') -Algorithm SHA256).Hash
    } else { $null }
    stateRoot     = $StateRoot
    ledger        = $ledgerPath
    capturedAt    = (Get-Date).ToUniversalTime().ToString('o')
    recordCount   = $all.Count
    comparedRecordCount = $included.Count
    excludedRecords = @($excluded | ForEach-Object { [ordered]@{
        harness = Get-Property $_ 'harness'
        label = Get-Property $_ 'label'
        size = Get-Property $_ 'size'
        scenario = Get-Property $_ 'scenario'
        requestedScenario = Get-Property $_ 'requestedScenario'
        run = Get-Property $_ 'run'
        status = Get-Property $_ 'status'
        reason = 'The baseline harness does not support --scenario; the failed invocation remains in the raw ledger.'
    } })
    environment   = [ordered]@{
        os           = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        dotnetSdk    = (& dotnet --version).Trim()
        node         = (& node --version).Trim()
        machine      = $env:COMPUTERNAME
    }
    cells = @($cells)
}

[IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 12))
Write-Host ("wrote {0} ({1} cells, {2} records)" -f $summaryPath, $cells.Count, $all.Count)
if ($excluded.Count -gt 0) {
    Write-Host ("Excluded {0} unsupported B0/B1 --scenario invocations from statistics; raw records remain in the ledger." -f $excluded.Count)
}
if (@($cells | Where-Object { $_.status -ne 'measured' }).Count -gt 0) {
    Write-Host 'Some cells are partial or blocked; see the summary.'
}

# Historical failures remain in the summary; only new failures fail this invocation.
if (@($records | Where-Object { $_.status -ne 'completed' }).Count -gt 0) {
    throw 'One or more harness attempts failed, timed out, or produced no measurement; raw evidence and summary were retained.'
}
