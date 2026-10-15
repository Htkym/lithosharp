[CmdletBinding()]
param([string[]] $HistoricalArtifacts = @(), [string] $ResultPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'WatchImportedWindow.ps1')
$checks = [Collections.Generic.List[object]]::new()
function Assert-Status($Value, [string] $Expected, [string] $Name) {
    if ($Value.status -cne $Expected) { throw "$Name expected $Expected, got $($Value.status): $($Value.reason)" }
    $checks.Add([ordered]@{ name = $Name; status = $Value.status; normalReuseProven = $Value.normalReuseProven })
}
$owner = 'a' * 32; $hostId = 'b' * 32; $nextHost = 'c' * 32
$barrier = [ordered]@{ schemaVersion = '1.0'; success = $true; generation = 2L; scope = 'admitted-inputs-only'
    watch = [ordered]@{ ownerId = $owner; hostId = $hostId; previousHostId = $hostId; inputRevision = 2L; restartReasons = 144 } }
function Terminal([long] $Generation, [int] $Reasons, [string] $Before, [string] $After, [int] $Workers, [bool] $Hit) {
    return [ordered]@{ event = 'rebuild-succeeded'; generation = $Generation; success = $true; exitCode = 0
        mdxWork = [ordered]@{ workerStarts = $Workers; renderedPages = $(if ($Hit) { 0 } else { 1 }); compiledModules = $(if ($Hit) { 0 } else { 1 }); cacheHit = $Hit }
        watch = [ordered]@{ ownerId = $owner; previousHostId = $Before; hostId = $After; inputRevision = $Generation; restartReasons = $Reasons } }
}
$normal = @(Terminal 3 0 $hostId $hostId 0 $false; Terminal 4 0 $hostId $hostId 0 $true)
Assert-Status (Get-WatchImportedWindow $barrier $normal 4 $true $true) 'REUSE_PROVEN' 'all normal terminals preserve host and zero starts'
$recovery = @(Terminal 3 144 $hostId $nextHost 1 $false; Terminal 4 0 $nextHost $nextHost 0 $true)
Assert-Status (Get-WatchImportedWindow $barrier $recovery 4 $true $true) 'RECOVERY_OBSERVED_REUSE_UNPROVEN' 'legitimate recovery never proves ordinary reuse despite final cache hit'
Assert-Status (Get-WatchImportedWindow $barrier $recovery 4 $false $true) 'PENDING' 'recovery requires current output content'
Assert-Status (Get-WatchImportedWindow $barrier $recovery 4 $true $false) 'PENDING' 'recovery requires matching publication generation'
Assert-Status (Get-WatchImportedWindow $barrier @((Terminal 3 0 $hostId $nextHost 1 $false)) 3 $true $true) 'INVALID' 'unexplained host and worker restart cannot pass'
Assert-Status (Get-WatchImportedWindow $barrier @((Terminal 3 0 $hostId $nextHost 0 $false)) 3 $true $true) 'INVALID' 'zero worker metric cannot hide an unexplained host replacement'
Assert-Status (Get-WatchImportedWindow $barrier @((Terminal 3 0 $hostId $hostId 1 $false)) 3 $true $true) 'INVALID' 'same host cannot hide an unexplained worker restart'
Assert-Status (Get-WatchImportedWindow $barrier @((Terminal 3 129 $hostId $nextHost 1 $false)) 3 $true $true) 'INVALID' 'unrelated restart reason cannot be excused as recovery'
Assert-Status (Get-WatchImportedWindow $barrier @($normal[1]) 4 $true $true) 'INVALID' 'missing intermediate terminal cannot erase restart'
$moved = [ordered]@{}; foreach ($key in $barrier.Keys) { $moved[$key] = $barrier[$key] }
$moved.generation = 3L; $moved.watch = $recovery[0].watch
Assert-Status (Get-WatchImportedWindow $moved @($recovery[1]) 4 $true $true) 'PENDING' 'post-edit baseline plus cache hit cannot supply the required edit render'
foreach ($path in $HistoricalArtifacts) {
    $d = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    # Both historical windows contain a real restart and a final cache hit, but no reliable owner barrier.
    Assert-Status (Get-WatchImportedWindow $null @($d.liveMachine.events) $d.liveMachine.lastGeneration $true $true) 'INVALID' ('historical sidecar cannot manufacture a green owner witness: ' + [IO.Path]::GetFileName($path))
}
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-MdxWatch.ps1'), [ref] $tokens, [ref] $parseErrors)
if (@($parseErrors).Count) { throw 'Watch script parse failed.' }
$requests = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Invoke-RestMethod' -and $n.Extent.Text.Contains('watch-barrier') }, $true))
$edits = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $n.Member.Extent.Text -ceq 'WriteAllText' -and $n.Extent.Text.Contains('useState(2)') }, $true))
$baselines = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$script:watchEvidence.importedEditBaselineGeneration' }, $true))
if ($requests.Count -ne 1 -or $edits.Count -ne 1 -or $baselines.Count -ne 1 -or
    $requests[0].Extent.EndOffset -ge $baselines[0].Extent.StartOffset -or $baselines[0].Extent.EndOffset -ge $edits[0].Extent.StartOffset) { throw 'Owner barrier and unique fixed baseline must precede the component edit.' }
$checks.Add([ordered]@{ name = 'actual script fixes the owner baseline once before the component write'; status = 'PASS'; normalReuseProven = $false })
$result = [ordered]@{ passed = $true; assertions = $checks.Count; historicalArtifacts = $HistoricalArtifacts.Count; checks = @($checks.ToArray()); liveWatchReproductions = 0; CI = 0 }
if ($ResultPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($ResultPath), ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false)) }
$result | ConvertTo-Json -Depth 8
