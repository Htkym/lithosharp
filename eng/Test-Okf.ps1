[CmdletBinding()]
param([string] $ToolPath, [string] $EvidenceDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$ToolPath) { $ToolPath = Join-Path $PSScriptRoot '../src/LithoSharp.Tool/bin/Release/net10.0/LithoSharp.Tool.dll' }
$ToolPath = [IO.Path]::GetFullPath($ToolPath)
if (!(Test-Path -LiteralPath $ToolPath -PathType Leaf)) { throw "Build the Release tool first: $ToolPath" }
if (!$EvidenceDirectory) { $EvidenceDirectory = Join-Path ([IO.Path]::GetTempPath()) ('okf-' + [Guid]::NewGuid().ToString('N')) }
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if ((Test-Path -LiteralPath $EvidenceDirectory) -and @(Get-ChildItem -LiteralPath $EvidenceDirectory -Force).Count -ne 0) {
    throw 'OKF acceptance requires a new or empty evidence directory.'
}
$null = New-Item -ItemType Directory -Path $EvidenceDirectory -Force
$sequence = 0
$asOf = '2026-11-13T12:00:00+00:00'

function Assert-True([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
}

function Write-Bundle([string] $Name, [string] $Metadata) {
    $directory = Join-Path $EvidenceDirectory $Name
    $null = New-Item -ItemType Directory -Path $directory
    [IO.File]::WriteAllText((Join-Path $directory 'concept.md'), "---`n$Metadata`n---`n# Retained original`n")
    return $directory
}

function Bundle-Identity([string] $Directory) {
    return (Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/') + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n"
}

function Invoke-Okf([string] $Command, [string] $InputDirectory, [string[]] $Options, [int] $ExpectedExit, [string] $Format = 'json', [string] $RequestedAsOf = $asOf) {
    $script:sequence++
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($ToolPath, 'okf', $Command, '--input', $InputDirectory, '--as-of', $RequestedAsOf, '--format', $Format) + $Options) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $null = $process.WaitForExitAsync().GetAwaiter().GetResult()
        $text = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $EvidenceDirectory "$script:sequence.stdout.json"), $text)
        [IO.File]::WriteAllText((Join-Path $EvidenceDirectory "$script:sequence.stderr.txt"), $errorText)
        Assert-True ($process.ExitCode -eq $ExpectedExit) "Expected exit $ExpectedExit, got $($process.ExitCode): $errorText $text"
        return $text | ConvertFrom-Json
    }
    finally { $process.Dispose() }
}

$valid = Write-Bundle 'valid' 'type: previously-unknown-family'
$concept = Join-Path $valid 'concept.md'
$body = "---`r`ntype: previously-unknown-family`r`nextension:`r`n  nested: [one, two]`r`n---`r`n# Original body`r`n"
[IO.File]::WriteAllBytes($concept, [byte[]](@(0xef, 0xbb, 0xbf) + [Text.Encoding]::UTF8.GetBytes($body)))
[IO.File]::WriteAllText((Join-Path $valid 'index.md'), "---`nokf_version: '0.2'`n---`n# Bundle`n")
[IO.File]::WriteAllBytes((Join-Path $valid 'data.bin'), [byte[]]@(0, 255, 128, 13, 10))
$executionMarker = Join-Path $EvidenceDirectory 'unexpected-execution.txt'
$escapedMarker = [Security.SecurityElement]::Escape($executionMarker)
[IO.File]::WriteAllText((Join-Path $valid 'must-not-build.csproj'), "<Project Sdk='Microsoft.NET.Sdk'><Target Name='MustNotRun' BeforeTargets='Build'><WriteLinesToFile File='$escapedMarker' Lines='executed'/><Error Text='OKF must not build a project.'/></Target></Project>")
[IO.File]::WriteAllText((Join-Path $valid 'computation.py'), "raise RuntimeError('OKF must not execute computation.')`n")
$original = Bundle-Identity $valid
$inspection = Invoke-Okf 'inspect' $valid @() 0
Assert-True ($inspection.okf.complete -and $inspection.okf.snapshotComplete -and $inspection.okf.conformance -eq 'Conformant') 'A minimal concept or root index was not accepted.'
Assert-True (@($inspection.okf.documents | Where-Object { $_.conceptId -eq 'concept' -and $_.type -eq 'previously-unknown-family' }).Count -eq 1) 'Unknown type was lost from the inspection view.'
$validation = Invoke-Okf 'validate' $valid @() 0
Assert-True ($validation.okf.success -and $validation.okf.asOf -eq $inspection.okf.asOf) 'Fixed as-of did not produce a successful deterministic validation.'
$output = Join-Path $EvidenceDirectory 'export'
$exported = Invoke-Okf 'export' $valid @('--output', $output) 0
Assert-True ($exported.okf.exported -and (Bundle-Identity $output) -eq $original) 'Preservation export changed original bytes or membership.'
$refused = Invoke-Okf 'export' $valid @('--output', $output) 1
Assert-True (!$refused.okf.exported -and (Bundle-Identity $output) -eq $original) 'Export overwrote a pre-existing output directory.'

$warning = Write-Bundle 'quality' "type: custom`nstatus: unknown-status"
$accepted = Invoke-Okf 'validate' $warning @() 0
Assert-True ($accepted.okf.conformance -eq 'Conformant' -and @($accepted.diagnostics | Where-Object category -eq 'Quality').Count -gt 0) 'Optional quality findings became structural failure or disappeared.'
$strict = Invoke-Okf 'validate' $warning @('--strict-lint') 1
Assert-True (!$strict.okf.success -and $strict.okf.conformance -eq 'Conformant') 'Strict lint did not preserve the distinction between quality and structural conformance.'

$invalid = Write-Bundle 'invalid' 'type: 42'
$rejected = Invoke-Okf 'validate' $invalid @() 1
Assert-True ($rejected.okf.conformance -eq 'NonConformant' -and @($rejected.diagnostics | Where-Object category -eq 'Structure').Count -gt 0) 'A numeric type was silently accepted as a string.'
$sarif = Invoke-Okf 'validate' $invalid @() 1 'sarif'
Assert-True ($sarif.runs[0].properties.okf.conformance -eq 'NonConformant' -and @($sarif.runs[0].results).Count -gt 0) 'SARIF disagreed with JSON conformance or lost diagnostics.'

$unsupported = Write-Bundle 'unsupported' "type: custom`nextension: &anchor retained"
$partial = Invoke-Okf 'inspect' $unsupported @() 1
Assert-True ($partial.okf.conformance -eq 'NotAssessed' -and @($partial.diagnostics | Where-Object category -eq 'Unsupported').Count -gt 0) 'Unsupported YAML was reported as validated conformance.'
$rawOutput = Join-Path $EvidenceDirectory 'unsupported-export'
$preserved = Invoke-Okf 'export' $unsupported @('--output', $rawOutput) 1
Assert-True ($preserved.okf.exported -and (Bundle-Identity $rawOutput) -eq (Bundle-Identity $unsupported)) 'Unsupported parsing prevented byte-preserving output of a captured bundle.'
$limited = Invoke-Okf 'inspect' $valid @('--max-files', '1') 1
Assert-True (!$limited.okf.snapshotComplete -and $limited.okf.conformance -eq 'NotAssessed' -and @($limited.diagnostics | Where-Object category -eq 'ResourceLimit').Count -gt 0) 'A file limit was confused with structural invalidity or complete capture.'
$walkLimited = Invoke-Okf 'inspect' $valid @('--max-discovery-entries', '1') 1
Assert-True (!$walkLimited.okf.snapshotComplete -and @($walkLimited.diagnostics | Where-Object category -eq 'ResourceLimit').Count -gt 0) 'Discovery enumeration ignored its explicit entry limit.'
$missing = Invoke-Okf 'inspect' (Join-Path $EvidenceDirectory 'missing') @() 1
Assert-True (!$missing.okf.snapshotComplete -and @($missing.diagnostics | Where-Object category -eq 'Io').Count -gt 0) 'I/O failure was not classified.'
$invalidTime = Invoke-Okf 'validate' $valid @() 2 'json' '2026-11-13'
Assert-True ($invalidTime.exitCode -eq 2) 'Date-only as-of was not rejected as a usage error.'
$newlineTime = Invoke-Okf 'validate' $valid @() 2 'json' "2026-11-13T00:00:00Z`n"
Assert-True ($newlineTime.exitCode -eq 2) 'A trailing newline in as-of was accepted.'
Assert-True ((Bundle-Identity $valid) -eq $original) 'Read-only inspection or export changed the input bundle.'
Assert-True (!(Test-Path -LiteralPath $executionMarker)) 'An OKF command executed a project target.'
Write-Host "OKF CLI acceptance passed ($sequence commands). Evidence: $EvidenceDirectory"
