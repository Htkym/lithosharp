<# Candidate-only T06/Y09/I08 raw measurements. Build beforehand; no restore/build is performed here. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $HarnessDll,
    [Parameter(Mandatory)][string] $HarnessRoot,
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [Parameter(Mandatory)][string] $ExpectedSdk,
    [ValidateSet('editor','yaml','images','site','factory')][string[]] $Family = @('editor','yaml','images','site'),
    [ValidateSet(100,1000,10000)][int] $Size = 100,
    [ValidateRange(1,100)][int] $Iterations = 5,
    [ValidateRange(0,100)][int] $Warmup = 1,
    [ValidateRange(0,2147483647)][int] $CancelAfterMilliseconds = 5,
    [string] $GeneratorAssembly,
    [string] $FactoryAssembly,
    [string] $FactoryDirectory,
    [string[]] $DistributionPath = @()
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($HarnessRoot)
$dll = [IO.Path]::GetFullPath($HarnessDll, $root)
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
if (!(Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Build the candidate acceptance harness first.' }
if (Test-Path -LiteralPath $evidence) { throw 'Use a new evidenceRoot; no result or failed attempt is overwritten.' }
if (@($Family | Sort-Object -Unique).Count -ne $Family.Count) { throw 'Duplicate families are not separate repetitions.' }
if ($Family -contains 'yaml') {
    if (!$GeneratorAssembly) { throw 'YAML acceptance needs the actual built Generator assembly.' }
    $GeneratorAssembly = [IO.Path]::GetFullPath($GeneratorAssembly, $root)
    if (!(Test-Path -LiteralPath $GeneratorAssembly -PathType Leaf)) { throw 'Generator assembly does not exist.' }
}
if ($Family -contains 'factory') {
    if (!$FactoryAssembly -or !$FactoryDirectory) { throw 'Factory measurements need an explicitly built official-site assembly and its source directory.' }
    $FactoryAssembly = [IO.Path]::GetFullPath($FactoryAssembly, $root)
    $FactoryDirectory = [IO.Path]::GetFullPath($FactoryDirectory, $root)
    if (!(Test-Path -LiteralPath $FactoryAssembly -PathType Leaf) -or !(Test-Path -LiteralPath $FactoryDirectory -PathType Container)) { throw 'Factory input does not exist.' }
}
Push-Location $root
try {
    $sdk = (& dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -cne $ExpectedSdk) { throw 'Selected SDK differs from ExpectedSdk.' }
    $commit = (& git rev-parse HEAD | Out-String).Trim()
    $changes = @(& git status --porcelain=v1 --untracked-files=all)
} finally { Pop-Location }
$null = New-Item -ItemType Directory -Path $evidence
$longest = Join-Path $evidence 'site/inputs/iteration-100/content/page-99999.md'
Write-Host ("Planned workload path example ({0} characters): {1}" -f $longest.Length, $longest)
$manifest = [ordered]@{
    schemaVersion = 1; status = 'prepared'; acceptance = 'not-evaluated'; comparison = 'candidate-only-feature-cost'
    sdk = $sdk; commit = $commit; changes = $changes; size = $Size; iterations = $Iterations; warmup = $Warmup
    cancellationDelayMilliseconds = $CancelAfterMilliseconds; harness = $dll
    harnessSha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant()
    plannedPath = $longest; runs = @(); distribution = @()
}
foreach ($item in $DistributionPath) {
    $path = [IO.Path]::GetFullPath($item, $root)
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing distribution artifact: $path" }
    $entry = [ordered]@{ path = $path; bytes = (Get-Item -LiteralPath $path).Length
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    if ([IO.Path]::GetExtension($path) -in '.nupkg','.zip','.vsix') {
        $archive = [IO.Compression.ZipFile]::OpenRead($path)
        try {
            $entry['entryCount'] = $archive.Entries.Count
            $entry['uncompressedBytes'] = ($archive.Entries | Measure-Object -Property Length -Sum).Sum
            $entry['longestEntry'] = if ($archive.Entries.Count) { ($archive.Entries | Sort-Object { $_.FullName.Length } -Descending | Select-Object -First 1).FullName } else { $null }
        } finally { $archive.Dispose() }
    }
    $manifest.distribution += $entry
}
$manifest['distributionStatus'] = if ($DistributionPath.Count) { 'recorded-explicit-artifacts' } else { 'not-executed' }
$manifestPath = Join-Path $evidence 'acceptance-campaign.json'
function Save-Manifest { [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 20)) }
Save-Manifest
$failed = $false
foreach ($name in $Family) {
    $run = Join-Path $evidence $name
    $null = New-Item -ItemType Directory -Path $run
    $resultPath = Join-Path $run 'result.json'
    $arguments = @($dll, '--mode', 'acceptance', '--acceptance-family', $name, '--size', $Size.ToString(), '--output', $resultPath,
        '--iterations', $Iterations.ToString(), '--warmup', $Warmup.ToString(), '--cancel-after-ms', $CancelAfterMilliseconds.ToString())
    if ($GeneratorAssembly) { $arguments += @('--generator-assembly', $GeneratorAssembly) }
    if ($FactoryAssembly) { $arguments += @('--factory-assembly', $FactoryAssembly, '--factory-directory', $FactoryDirectory) }
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.WorkingDirectory = $root
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    $record = [ordered]@{ family = $name; status = 'running'; arguments = $arguments; result = $resultPath; exitCode = $null }
    try {
        $process.WaitForExit(); $record.exitCode = $process.ExitCode
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $resultPath)) { throw 'Harness failed or did not produce a result.' }
        if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifest.harnessSha256) { throw 'Harness binary changed during execution.' }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        $expectedNames = switch ($name) {
            editor { @('editor-first','editor-no-change','editor-warm','editor-body','editor-heading','editor-reference-definition','editor-large-paste','editor-after-close','cancel-unclosed','cancel-deep') }
            yaml { @('yaml-small-many','yaml-long-scalar','yaml-deep','yaml-unknown-metadata','yaml-typed-bind-many','yaml-source-small-many') }
            images { @('png-decode','png-encode','image-resize','image-composite','image-site-none','image-site-copy','image-site-png-miss','image-site-png-hit') }
            site { @('site-first','site-warm-cache','site-no-change','site-body','site-layout','site-shared-schema','site-shared-component') }
            factory { @('factory-create','factory-site-first','factory-site-warm-cache','factory-site-no-change','factory-after-dispose') }
        }
        if ($result.status -cne 'completed' -or $result.comparison -cne 'candidate-only-feature-cost' -or $result.family -cne $name -or $null -eq $result.identity) { throw 'Invalid acceptance result identity.' }
        if (@($result.samples).Count -ne $expectedNames.Count * $Iterations) { throw 'Missing or duplicate acceptance sample.' }
        foreach ($expected in $expectedNames) {
            $rows = @($result.samples | Where-Object Name -CEQ $expected)
            if ($rows.Count -ne $Iterations -or @($rows.Iteration | Sort-Object -Unique).Count -ne $Iterations) { throw 'Missing or duplicate scenario iteration.' }
            foreach ($row in $rows) {
                if ($row.Iteration -lt 0 -or $row.Iteration -ge $Iterations -or $row.InputSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid sample identity.' }
                foreach ($field in 'ElapsedMilliseconds','AllocatedBytes','ManagedBytesBefore','ManagedBytesAfterCollection','WorkingSetBefore','WorkingSetAfter','SampledPeakWorkingSetBytes') {
                    $value = $row.PSObject.Properties[$field]
                    if ($null -eq $value -or $null -eq $value.Value) { throw 'Missing raw measurement.' }
                    $type = [Type]::GetTypeCode($value.Value.GetType()).ToString()
                    if ($type -notin 'Byte','SByte','Int16','UInt16','Int32','UInt32','Int64','UInt64','Single','Double','Decimal' -or
                        ![double]::IsFinite([double]$value.Value) -or [double]$value.Value -lt 0) { throw 'Invalid raw measurement.' }
                }
                if ($row.SampledPeakWorkingSetBytes -lt [Math]::Max($row.WorkingSetBefore, $row.WorkingSetAfter)) { throw 'Invalid sampled working set peak.' }
            }
        }
        $record.status = 'measured'
        if ($name -eq 'editor') {
            $record['cancellationEvidence'] = @($result.samples | Where-Object { $_.Name -like 'cancel-*' } | ForEach-Object { $_.Counters })
        }
    } catch { $failed = $true; $record.status = 'failed'; $record['error'] = $_.Exception.Message }
    finally {
        [IO.File]::WriteAllText((Join-Path $run 'stdout.jsonl'), $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $run 'stderr.txt'), $stderr.GetAwaiter().GetResult())
        $process.Dispose(); $manifest.runs += $record; Save-Manifest
    }
}
$manifest.status = if ($failed) { 'partial' } else { 'measured' }
Save-Manifest
if ($failed) { throw 'An acceptance workload failed. Completed samples and raw process evidence were retained.' }
