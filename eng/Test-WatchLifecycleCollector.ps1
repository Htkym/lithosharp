[CmdletBinding()]
param([Parameter(Mandatory)][string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Fresh owned evidence directory required.' }
$null = [IO.Directory]::CreateDirectory($EvidenceDirectory)
$source = Join-Path $PSScriptRoot 'Test-MdxWatch.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Watch source parse failed.' }
# Exercise the actual collector and save integration without starting a site.
foreach ($name in @('Read-WatchLifecycleSidecar','Save-WatchDiagnostics','Summarize-MachineOutput','Known-Count','Known-Bool')) {
    $functions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name}, $true))
    if ($functions.Count -ne 1) { throw 'Unique actual watch function required.' }
    . ([ScriptBlock]::Create($functions[0].Extent.Text))
}
function Check([bool]$Condition, [string]$Name) { if (!$Condition) { throw $Name } }
$utf8 = [Text.UTF8Encoding]::new($false)
$lifecyclePath = Join-Path $EvidenceDirectory 'capture.jsonl'
$row = [ordered]@{ schemaVersion=1;utcUnixMilliseconds=1791573849654L;writerId=('a'*32);hostId=('b'*32);workerId=$null
    event='HostExited';reason='GracefulClose';generation=3;restartReasons=0;exitCode=-1;droppedBefore=0 }
$valid = $row | ConvertTo-Json -Compress
$errorRow = [ordered]@{}; foreach ($key in $row.Keys) { $errorRow[$key]=$row[$key] }
$errorRow.schemaVersion=2; $errorRow.event='RestartRequested'; $errorRow.reason='None'; $errorRow.restartReasons=144; $errorRow.exitCode=$null
$errorRow.watcherErrorType='Win32Exception'; $errorRow.watcherHResult=-2147467259; $errorRow.watcherNativeErrorCode=5
$errorValid = $errorRow | ConvertTo-Json -Compress
$consumedRow = [ordered]@{}; foreach ($key in $errorRow.Keys) { $consumedRow[$key]=$errorRow[$key] }
$consumedRow.event='RestartConsumed'; $consumedRow.utcUnixMilliseconds++
$consumedRow.watcherErrorType=$null; $consumedRow.watcherHResult=$null; $consumedRow.watcherNativeErrorCode=$null
$consumedValid = $consumedRow | ConvertTo-Json -Compress
$ioRow = [ordered]@{}; foreach ($key in $errorRow.Keys) { $ioRow[$key]=$errorRow[$key] }
$ioRow.watcherErrorType='IOException'; $ioRow.watcherHResult=[int]::MinValue; $ioRow.watcherNativeErrorCode=$null
$ioValid = $ioRow | ConvertTo-Json -Compress
$otherRow = [ordered]@{}; foreach ($key in $ioRow.Keys) { $otherRow[$key]=$ioRow[$key] }
$otherRow.watcherErrorType='Other'; $otherRow.watcherHResult=[int]::MaxValue
$otherValid = $otherRow | ConvertTo-Json -Compress
$mixedValid = $valid + "`n" + $errorValid + "`n" + $consumedValid + "`n" + $ioValid + "`n" + $otherValid
$cases = [Collections.Generic.List[string]]::new()
[IO.File]::WriteAllText($lifecyclePath, $mixedValid + "`n" + '{"event":"PRIVATE_PARTIAL_BODY', $utf8)
$partial = Read-WatchLifecycleSidecar $lifecyclePath
Check ($partial.events.Count -eq 5 -and $partial.discardedLines -eq 1 -and $partial.incomplete) 'Partial final line must preserve mixed legacy and v2 complete records.'
Check ($partial.events[0].schemaVersion -eq 1 -and $partial.events[0].Count -eq 11 -and $partial.events[1].Count -eq 14 -and $partial.events[2].Count -eq 14) 'Collector retains each strict version schema.'
Check ($partial.events[1].watcherErrorType -ceq 'Win32Exception' -and $partial.events[1].watcherHResult -eq -2147467259 -and $partial.events[1].watcherNativeErrorCode -eq 5) 'Approved watcher error scalars survive ingress.'
Check ($partial.events[2].event -ceq 'RestartConsumed' -and $partial.events[2].restartReasons -eq 144 -and
    $partial.events[1].writerId -ceq $partial.events[2].writerId -and $partial.events[1].hostId -ceq $partial.events[2].hostId -and
    $null -eq $partial.events[2].watcherErrorType -and $null -eq $partial.events[2].watcherHResult -and $null -eq $partial.events[2].watcherNativeErrorCode) 'Restart consumption links opaque IDs and reasons without copying error data.'
Check ($partial.events[3].watcherErrorType -ceq 'IOException' -and $partial.events[3].watcherHResult -eq [int]::MinValue -and $null -eq $partial.events[3].watcherNativeErrorCode -and
    $partial.events[4].watcherErrorType -ceq 'Other' -and $partial.events[4].watcherHResult -eq [int]::MaxValue -and $null -eq $partial.events[4].watcherNativeErrorCode) 'Non-native error kinds retain signed int32 endpoints.'
$cases.Add('partial-final-line')

$bad = [Collections.Generic.List[string]]::new()
foreach ($mutation in @(@{event='PRIVATE_EVENT_BODY'}, @{reason='PRIVATE_REASON_BODY'}, @{hostId='PRIVATE_PATH_BODY'},
    @{schemaVersion=2}, @{generation=-1}, @{generation=1.5}, @{restartReasons=512}, @{exitCode=2147483648L},
    @{droppedBefore='PRIVATE_BODY'}, @{source='PRIVATE_UNKNOWN_BODY'}, @{workerId=@{body='PRIVATE_NESTED_BODY'}})) {
    $copy = [ordered]@{}; foreach ($key in $row.Keys) { $copy[$key]=$row[$key] }
    foreach ($key in $mutation.Keys) { $copy[$key]=$mutation[$key] }
    $bad.Add(($copy | ConvertTo-Json -Compress -Depth 5))
}
foreach ($mutation in @(@{source='PRIVATE_UNKNOWN_BODY'}, @{watcherErrorType='System.IO.IOException'}, @{watcherErrorType='win32exception'},
    @{watcherErrorType='PRIVATE_TYPE_BODY'}, @{watcherErrorType=$null}, @{watcherHResult=$null}, @{watcherHResult='PRIVATE_BODY'},
    @{watcherHResult=2147483648L}, @{watcherHResult=-2147483649L}, @{watcherHResult=1.5}, @{watcherNativeErrorCode=$null},
    @{watcherNativeErrorCode=2147483648L}, @{watcherNativeErrorCode=-2147483649L}, @{watcherNativeErrorCode=1.5},
    @{watcherNativeErrorCode='PRIVATE_BODY'}, @{watcherErrorType='IOException'}, @{event='RestartConsumed'},
    @{event='HostExited'}, @{restartReasons=128})) {
    $copy = [ordered]@{}; foreach ($key in $errorRow.Keys) { $copy[$key]=$errorRow[$key] }
    foreach ($key in $mutation.Keys) { $copy[$key]=$mutation[$key] }
    $bad.Add(($copy | ConvertTo-Json -Compress -Depth 5))
}
# Error type/HResult must be present together even when no native code is involved.
$incoherent = [ordered]@{}; foreach ($key in $consumedRow.Keys) { $incoherent[$key]=$consumedRow[$key] }
$incoherent.event='RestartRequested'; $incoherent.watcherHResult=-2146232800
$bad.Add(($incoherent | ConvertTo-Json -Compress))
$incoherent.watcherHResult=$null; $incoherent.watcherErrorType='Other'
$bad.Add(($incoherent | ConvertTo-Json -Compress))
$bad.Add('{broken-json'); $bad.Add(('PRIVATE_OVERSIZED_BODY'*80))
$bytes = [Collections.Generic.List[byte]]::new()
$bytes.AddRange($utf8.GetBytes(($bad -join "`n") + "`n"))
$bytes.AddRange([byte[]]@(0xc3,0x0a)) # Invalid complete UTF-8 line.
$bytes.AddRange($utf8.GetBytes($mixedValid + "`n"))
[IO.File]::WriteAllBytes($lifecyclePath, $bytes.ToArray())
$filtered = Read-WatchLifecycleSidecar $lifecyclePath
Check ($filtered.events.Count -eq 5 -and $filtered.discardedLines -eq $bad.Count+1 -and $filtered.incomplete) 'Malformed/unlisted and incoherent watcher rows must be discarded independently.'
Check (($filtered | ConvertTo-Json -Depth 8) -notmatch 'PRIVATE') 'Ingress must not export unknown JSON or bodies.'
$cases.Add('schema-enum-guid-scalar-allowlist-and-invalid-utf8')

[IO.File]::WriteAllText($lifecyclePath, $valid + "`n" + ('x'*150000), $utf8)
$bounded = Read-WatchLifecycleSidecar $lifecyclePath
Check ($bounded.byteLimitReached -and $bounded.incomplete -and $bounded.events.Count -eq 1) 'Oversized capture must remain bounded and preserve earlier complete records.'
$cases.Add('capture-byte-bound')
[IO.File]::WriteAllText($lifecyclePath, ("`n"*600), $utf8)
$lines = Read-WatchLifecycleSidecar $lifecyclePath
Check ($lines.lineLimitReached -and $lines.discardedLines -eq 512 -and $lines.incomplete) 'Line processing must stop at its fixed limit.'
$cases.Add('capture-line-bound')
$missing = Read-WatchLifecycleSidecar (Join-Path $EvidenceDirectory 'missing.jsonl')
Check ($missing.readFailed -and $missing.incomplete -and $missing.events.Count -eq 0) 'Unavailable capture must fail open.'
$cases.Add('unavailable-capture')

# Save-WatchDiagnostics must preserve a successful strict watch and cleanup even
# when abrupt termination leaves the optional sidecar with a partial UTF-8 line.
$fixture = Join-Path $EvidenceDirectory 'fixture'; $null = [IO.Directory]::CreateDirectory($fixture)
$repo = Join-Path $EvidenceDirectory 'owned-repo'
$TimeoutSeconds = 180
$watchEvidence = [ordered]@{ outcome='PASS';failureCategory=$null;warmupBaselineGeneration=1;warmupCompletedGeneration=2
    importedEditBaselineGeneration=2;importedEditWitness=$null;errorRecovery=[Collections.Generic.List[object]]::new();droppedPolls=0;polls=[Collections.Generic.List[object]]::new()
    stdoutCaptured=$true;stderrCaptured=$true;cleanupCompleted=$true;cleanupFailures=[Collections.Generic.List[string]]::new() }
$liveTerminalEvents = [Collections.Generic.List[object]]::new()
$liveDroppedTerminals=0; $livePendingBuild=$false; $liveShutdownSeen=$true; $liveLastGeneration=3
[IO.File]::WriteAllBytes($lifecyclePath, $utf8.GetBytes($valid + "`n") + [byte[]]@(0xc3))
Save-WatchDiagnostics '' ''
$saved = Get-Content -LiteralPath (Join-Path $fixture 'watch-diagnostics.json') -Raw | ConvertFrom-Json
Check ($saved.outcome -ceq 'PASS' -and $saved.cleanupCompleted -and $saved.strictImportedReuse -ceq 'Count2_AND_workerStarts0') 'Collector must not replace strict watch result with a cleanup failure.'
Check ($saved.lifecycle.discardedLines -eq 1 -and $saved.lifecycle.incomplete -and $saved.lifecycle.events.Count -eq 1) 'Saved diagnostics must record bounded discarded/incomplete scalars.'
$cases.Add('actual-save-after-partial-utf8-preserves-success')
$proof = [ordered]@{outcome='PASS';cases=@($cases.ToArray());sourceSha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant();strictReuseUnchanged=$true;partialSave=$saved.lifecycle}
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'collector-result.json'), ($proof | ConvertTo-Json -Depth 8), $utf8)
Write-Output ('Watch lifecycle collector passed: ' + $cases.Count + ' targeted cases.')
