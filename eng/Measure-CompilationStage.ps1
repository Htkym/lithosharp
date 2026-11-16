<# Measures one explicitly prepared restore or C# build state. Does not mutate source files or prepare caches. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Project,
    [Parameter(Mandatory)][string] $WorkingDirectory,
    [Parameter(Mandatory)][string] $EvidenceRoot,
    [Parameter(Mandatory)][string] $ExpectedSdk,
    [Parameter(Mandatory)][ValidateSet('restore','first','no-change','body','shared-schema','input-added','input-deleted')][string] $Stage,
    [Parameter(Mandatory)][string] $PreparedState,
    [string[]] $AdditionalArgument = @(),
    [string[]] $InputPath = @(),
    [string[]] $OutputArtifact = @()
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$working = [IO.Path]::GetFullPath($WorkingDirectory)
$projectPath = [IO.Path]::GetFullPath($Project, $working)
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
if (!(Test-Path -LiteralPath $projectPath -PathType Leaf)) { throw 'Project does not exist.' }
if (Test-Path -LiteralPath $evidence) { throw 'Use a fresh evidence directory.' }
$null = New-Item -ItemType Directory -Path $evidence
Push-Location $working
try {
    $sdk = (& dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdk -cne $ExpectedSdk) { throw 'The selected SDK differs from ExpectedSdk.' }
    $commit = (& git rev-parse HEAD | Out-String).Trim()
} finally { Pop-Location }
$inputs = @($InputPath | ForEach-Object {
    $path = [IO.Path]::GetFullPath($_, $working)
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing declared stage input: $path" }
    [ordered]@{ path = $path; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$arguments = if ($Stage -eq 'restore') { @('restore', $projectPath) }
    else { @('build', $projectPath, '--no-restore', '--configuration', 'Release', ('-bl:' + (Join-Path $evidence 'build.binlog'))) }
$arguments += $AdditionalArgument
$record = [ordered]@{
    schemaVersion = 1; status = 'prepared'; stage = $Stage; preparedState = $PreparedState
    sdk = $sdk; commit = $commit; workingDirectory = $working; project = $projectPath
    arguments = $arguments; additionalArguments = $AdditionalArgument; inputs = $inputs; artifacts = @()
    interval = if ($Stage -eq 'restore') { 'restore process' } else { 'C# build process including Generator; not isolated Generator time' }
    allocatedBytes = $null; allocationStatus = 'unavailable-cross-process'
    memoryScope = 'Peak working set of the direct dotnet process only; build servers and descendant processes are excluded.'
    elapsedMilliseconds = $null; exitCode = $null
}
$recordPath = Join-Path $evidence 'stage.json'
[IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json -Depth 8))
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false; $start.CreateNoWindow = $true
$start.WorkingDirectory = $working
$start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
$clock = [Diagnostics.Stopwatch]::StartNew()
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
try {
    $process.WaitForExit(); $clock.Stop()
    $record.elapsedMilliseconds = $clock.Elapsed.TotalMilliseconds
    $record.exitCode = $process.ExitCode
    $record['directProcessPeakWorkingSetBytes'] = $process.PeakWorkingSet64
    $record.status = if ($process.ExitCode -eq 0) { 'completed' } else { 'failed' }
    $record.artifacts = @($OutputArtifact | ForEach-Object {
        $path = [IO.Path]::GetFullPath($_, $working)
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Expected build artifact is missing: $path" }
        [ordered]@{ path = $path; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = (Get-Item -LiteralPath $path).Length }
    })
} catch { $record.status = 'failed'; $record['error'] = $_.Exception.Message; throw }
finally {
    $clock.Stop()
    [IO.File]::WriteAllText((Join-Path $evidence 'stdout.txt'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $evidence 'stderr.txt'), $stderr.GetAwaiter().GetResult())
    [IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json -Depth 8))
    $process.Dispose()
}
if ($record.status -ne 'completed') { throw 'Compilation stage failed; raw evidence was retained.' }
