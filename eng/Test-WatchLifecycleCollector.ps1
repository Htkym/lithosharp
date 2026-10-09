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
$cases = [Collections.Generic.List[string]]::new()
[IO.File]::WriteAllText($lifecyclePath, $valid + "`n" + '{"event":"PRIVATE_PARTIAL_BODY', $utf8)
$partial = Read-WatchLifecycleSidecar $lifecyclePath
Check ($partial.events.Count -eq 1 -and $partial.discardedLines -eq 1 -and $partial.incomplete) 'Partial final line must preserve prior complete record.'
$cases.Add('partial-final-line')

$bad = [Collections.Generic.List[string]]::new()
foreach ($mutation in @(@{event='PRIVATE_EVENT_BODY'}, @{reason='PRIVATE_REASON_BODY'}, @{hostId='PRIVATE_PATH_BODY'},
    @{schemaVersion=2}, @{generation=-1}, @{generation=1.5}, @{restartReasons=512}, @{exitCode=2147483648L},
    @{droppedBefore='PRIVATE_BODY'}, @{source='PRIVATE_UNKNOWN_BODY'}, @{workerId=@{body='PRIVATE_NESTED_BODY'}})) {
    $copy = [ordered]@{}; foreach ($key in $row.Keys) { $copy[$key]=$row[$key] }
    foreach ($key in $mutation.Keys) { $copy[$key]=$mutation[$key] }
    $bad.Add(($copy | ConvertTo-Json -Compress -Depth 5))
}
$bad.Add('{broken-json'); $bad.Add(('PRIVATE_OVERSIZED_BODY'*80))
$bytes = [Collections.Generic.List[byte]]::new()
$bytes.AddRange($utf8.GetBytes(($bad -join "`n") + "`n"))
$bytes.AddRange([byte[]]@(0xc3,0x0a)) # Invalid complete UTF-8 line.
$bytes.AddRange($utf8.GetBytes($valid + "`n"))
[IO.File]::WriteAllBytes($lifecyclePath, $bytes.ToArray())
$filtered = Read-WatchLifecycleSidecar $lifecyclePath
Check ($filtered.events.Count -eq 1 -and $filtered.discardedLines -eq $bad.Count+1 -and $filtered.incomplete) 'Malformed/unlisted rows must be discarded independently.'
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
