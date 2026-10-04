<#
.SYNOPSIS
Checks evidence preservation, completed-run skipping and failure propagation.
#>
[CmdletBinding()]
param([string] $Output, [string] $HarnessDll)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Output) { $Output = Join-Path $repo ('.tmp/performance-runner-' + [Guid]::NewGuid().ToString('N')) }
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Use a new output directory so prior test evidence is retained.' }
if (!$HarnessDll) { $HarnessDll = Join-Path $repo 'benchmarks/LithoSharp.Performance/bin/Release/net10.0/LithoSharp.Performance.dll' }
$null = New-Item -ItemType Directory -Path $Output
$runner = Join-Path $repo 'eng/Measure-IncrementalScenarios.ps1'
function Invoke-Runner([string] $Root, [string] $Dll, [string] $Name) {
    $start = [Diagnostics.ProcessStartInfo]::new('pwsh')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = $repo
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', $runner, '-Harness', 'markdown', '-Label', 'candidate',
        '-RunId', 'runner-regression', '-Sizes', '100', '-Scenarios', 'clean', '-Runs', '1',
        '-HarnessDll', $Dll, '-StateRoot', (Join-Path $Root 'runs'), '-Resume')) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        $timedOut = !$process.WaitForExit(120000)
        if ($timedOut) {
            $process.Kill($true)
            $process.WaitForExit()
        }
        [IO.File]::WriteAllText((Join-Path $Root "$Name.stdout.txt"), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $Root "$Name.stderr.txt"), $stderr.GetAwaiter().GetResult())
        if ($timedOut) { throw 'Runner test exceeded its bounded timeout; stdout/stderr were retained.' }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
function Assert([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw "Performance runner regression: $Message" }
}
$abandoned = Join-Path $Output 'abandoned'
$oldRun = Join-Path $abandoned 'runs/markdown-100-clean-run-1'
$null = New-Item -ItemType Directory -Path $oldRun -Force
foreach ($name in @('stdout.txt', 'stderr.txt', 'result.json')) {
    [IO.File]::WriteAllText((Join-Path $oldRun $name), "interrupted $name")
}
$before = @(Get-ChildItem $oldRun -File | Get-FileHash)
Assert ((Invoke-Runner $abandoned $HarnessDll 'retry') -eq 0) 'abandoned run did not retry successfully'
$entries = @(Get-Content (Join-Path $abandoned 'ledger.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })

foreach ($hash in $before) {
    Assert ((Get-FileHash -LiteralPath $hash.Path).Hash -ceq $hash.Hash) 'interrupted evidence was overwritten'
}
Assert ($entries.Count -eq 1 -and $entries[0].status -eq 'completed' -and $entries[0].attempt -eq 2) 'retry record must identify attempt 2'
Assert ($entries[0].resultFile -like '*-attempt-2*') 'retry must use a fresh result directory'
$ledgerHash = (Get-FileHash (Join-Path $abandoned 'ledger.jsonl')).Hash
Assert ((Invoke-Runner $abandoned $HarnessDll 'skip') -eq 0) 'completed run resume failed'
Assert ((Get-FileHash (Join-Path $abandoned 'ledger.jsonl')).Hash -ceq $ledgerHash) 'completed run was not skipped'
Assert (!(Test-Path (Join-Path $abandoned 'runs/markdown-100-clean-run-1-attempt-3'))) 'skipped run allocated an attempt directory'

$failed = Join-Path $Output 'failed'
$null = New-Item -ItemType Directory -Path $failed
$badDll = Join-Path $Output 'invalid.dll'
[IO.File]::WriteAllText($badDll, 'intentional invalid assembly')
Assert ((Invoke-Runner $failed $badDll 'failure') -ne 0) 'harness failure did not propagate'
$failedLedger = [IO.File]::ReadAllText((Join-Path $failed 'ledger.jsonl'))
$summary = Get-Content (Join-Path $failed 'benchmark-baseline.json') -Raw | ConvertFrom-Json
Assert ($summary.cells[0].status -eq 'blocked') 'failed summary was not retained'
$failedRun = Join-Path $failed 'runs/markdown-100-clean-run-1'
$failedHashes = @(Get-ChildItem $failedRun -File | Get-FileHash)
Assert ((Invoke-Runner $failed $HarnessDll 'recovered') -eq 0) 'historical failure blocked a successful retry'
Assert ([IO.File]::ReadAllText((Join-Path $failed 'ledger.jsonl')).StartsWith($failedLedger, [StringComparison]::Ordinal)) 'historical ledger changed'
$recovered = @(Get-Content (Join-Path $failed 'ledger.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
Assert ($recovered.Count -eq 2 -and $recovered[0].status -eq 'failed' -and $recovered[1].status -eq 'completed') 'failure/recovery records were lost'
foreach ($hash in $failedHashes) {
    Assert ((Get-FileHash -LiteralPath $hash.Path).Hash -ceq $hash.Hash) 'failed evidence was overwritten'
}
Write-Host 'Performance runner regression checks passed: interrupted/failed evidence preserved, fresh retry directories, completed runs skipped, failures propagated, retry recovered.'
