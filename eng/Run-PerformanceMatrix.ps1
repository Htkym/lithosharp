# V110-25 performance matrix driver: runs the §5 Site API matrix for B0, B1 and
# the candidate on one machine with per-round A/B rotation and no overlapping
# runs. Every invocation uses -Resume, so an interrupted campaign continues
# without discarding completed runs. Raw evidence lands under EvidenceRoot.
[CmdletBinding()]
param(
    [string] $EvidenceRoot,
    [ValidateSet('All', 'Markdown', 'Mdx100', 'Mdx1000', 'Mdx10000Major', 'Mdx10000Rest')]
    [string] $Scope = 'All',
    [ValidateRange(1, 5)]
    [int] $StartRound = 1,
    [ValidateRange(1, 5)]
    [int] $EndRound = 5,
    # Baseline worktrees. Defaults match the reference machine in V110-25.
    [string] $B0Root = (Join-Path (Split-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) -Parent) 'lithosharp-b0'),
    [string] $B1Root = (Join-Path (Split-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) -Parent) 'lithosharp-b1')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$EvidenceRoot) { $EvidenceRoot = Join-Path $repo '.local/verification/1.1.0/v110-25-20260930' }
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if ($StartRound -gt $EndRound) { throw 'StartRound must not be greater than EndRound.' }
$candidateCommit = (& git -C $repo rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or !$candidateCommit) { throw 'Could not identify the candidate commit.' }
$candidateChanges = @(& git -C $repo status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Could not verify the candidate working tree.' }
$unexpectedCandidateChanges = @($candidateChanges | Where-Object { $_ -cne '?? eng/Run-PerformanceMatrix.ps1' })
if ($unexpectedCandidateChanges.Count -gt 0) {
    throw 'The candidate has uncommitted changes other than this matrix driver. Commit and rebuild it before measuring V110-25.'
}
$driverHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
$workScheduled = $false

$trees = @(
    # B0 and B1 harnesses predate scenario selection: they run "all" in one
    # process (V110-04 methodology). Only the candidate uses per-scenario runs.
    [ordered]@{ label = 'B0'; root = $B0Root; scenarios = 'all' },
    [ordered]@{ label = 'B1'; root = $B1Root; scenarios = 'all' },
    [ordered]@{ label = 'candidate'; root = $repo; scenarios = 'selected' }
)
$baselineCommits = @{
    B0 = '11f7e74494e9646033dce1937385c5aadd58d602'
    B1 = '324842d64ce8505957a1e84b227db6177cdd64bb'
}
foreach ($tree in $trees | Where-Object { $_.label -in @('B0', 'B1') }) {
    if (!(Test-Path -LiteralPath $tree.root -PathType Container)) {
        throw "$($tree.label) worktree is missing: $($tree.root)"
    }
    $actualCommit = (& git -C $tree.root rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -cne $baselineCommits[$tree.label]) {
        throw "$($tree.label) worktree must be $($baselineCommits[$tree.label]); found '$actualCommit'."
    }
}
$mdScenarios = 'clean,no-op,single-page-change,layout-change'
$mdxScenarios = 'cold,warm-cache,no-op,one-page,shared-component,shared-css,shared-image,layout,route,lockfile'
$mdxMajor = 'cold,no-op,one-page'
$mdxRest = 'warm-cache,shared-component,shared-css,shared-image,layout,route,lockfile'

function Get-TreeScenarios([string] $Mode, [string] $Selected) {
    if ($Mode -ceq 'all') { return 'all' }
    return $Selected
}

function Invoke-Matrix(
    [string] $Harness, [string] $Sizes, [string] $SelectedScenarios,
    [int] $Rounds, [int] $TimeoutSeconds, [string] $Tag, [switch] $CandidateOnly) {
    $lastRound = [Math]::Min($EndRound, $Rounds)
    if ($StartRound -gt $lastRound) {
        Write-Host "[$Tag] no rounds remain in the requested range ($StartRound..$lastRound)."
        return
    }
    for ($round = $StartRound; $round -le $lastRound; $round++) {
        # Rotate tree order per round so no tree always runs first or last.
        $order = @(0, 1, 2 | ForEach-Object { ($_ + $round) % 3 })
        foreach ($index in $order) {
            $tree = $trees[$index]
            # B0/B1 "all" runs of the major scope already cover the remaining
            # scenarios; rerunning them here would only duplicate hour-long jobs.
            if ($CandidateOnly -and $tree.label -cne 'candidate') { continue }
            $scenarios = Get-TreeScenarios $tree.scenarios $SelectedScenarios
            $harnessDll = if ($Harness -ceq 'markdown') {
                Join-Path $tree.root 'benchmarks/LithoSharp.Performance/bin/Release/net10.0/LithoSharp.Performance.dll'
            } else {
                Join-Path $tree.root 'benchmarks/LithoSharp.MdxPerformance/bin/Release/net10.0/LithoSharp.MdxPerformance.dll'
            }
            $harnessHash = if ($tree.label -ceq 'candidate') {
                (Get-FileHash -LiteralPath $harnessDll -Algorithm SHA256).Hash.ToLowerInvariant()
            } else { '' }
            $arguments = @{
                Harness = $Harness; Label = $tree.label
                TaskId = 'V110-25'
                RunId = if ($tree.label -ceq 'candidate') {
                    "v110-25-$Tag-candidate-$($candidateCommit.Substring(0, 12))-$($harnessHash.Substring(0, 8))-$($driverHash.Substring(0, 8))"
                } else { "v110-25-$Tag-$($tree.label)" }
                Sizes = $Sizes; Scenarios = $scenarios; Runs = $round; Resume = $true
                TimeoutSeconds = $TimeoutSeconds; EvidenceRoot = $EvidenceRoot
                HarnessRoot = $tree.root; HarnessDll = $harnessDll
            }
            if ($Harness -ceq 'mdx') {
                $arguments.WorkerDirectory = Join-Path $tree.root 'src/LithoSharp.Mdx/worker'
            }
            Write-Host "[$Tag] round $round tree $($tree.label) scenarios $scenarios"
            & (Join-Path $repo 'eng/Measure-IncrementalScenarios.ps1') @arguments
            $script:workScheduled = $true
        }
    }
}

if ($Scope -in @('All', 'Markdown')) {
    Invoke-Matrix 'markdown' '100,1000,10000' $mdScenarios 5 1800 'md'
}
if ($Scope -in @('All', 'Mdx100')) {
    Invoke-Matrix 'mdx' '100' $mdxScenarios 5 1800 'mdx100'
}
if ($Scope -in @('All', 'Mdx1000')) {
    Invoke-Matrix 'mdx' '1000' $mdxScenarios 5 1800 'mdx1000'
}
if ($Scope -in @('All', 'Mdx10000Major')) {
    # 10k all-runs take ~55 min; allow 90 min to absorb loaded-box variance.
    Invoke-Matrix 'mdx' '10000' $mdxMajor 5 5400 'mdx10000major'
}
if ($Scope -in @('All', 'Mdx10000Rest')) {
    Invoke-Matrix 'mdx' '10000' $mdxRest 3 5400 'mdx10000rest' -CandidateOnly
}
if ($workScheduled) {
    Write-Host "Matrix scope $Scope finished; inspect the ledger for completed, failed, and timed-out runs."
}
else {
    Write-Host "Matrix scope $Scope scheduled no rounds."
}
