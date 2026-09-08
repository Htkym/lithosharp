[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Executable,
    [Parameter(Mandatory)] [string[]] $Arguments,
    [Parameter(Mandatory)] [string] $OutputPath
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This RSS sampler uses Windows process discovery. Use an equivalent native sampler on other systems.' }
$destination = [IO.Path]::GetFullPath($OutputPath)
$null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination))
$start = [Diagnostics.ProcessStartInfo]::new($Executable)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
$ids = [Collections.Generic.HashSet[int]]::new()
$null = $ids.Add($process.Id)
$peak = 0L
$samples = 0
$clock = [Diagnostics.Stopwatch]::StartNew()
$discovery = -1000L
try {
    while (!$process.HasExited) {
        if ($clock.ElapsedMilliseconds - $discovery -ge 500) {
            $all = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)
            do {
                $added = $false
                foreach ($child in $all) {
                    if ($ids.Contains([int]$child.ParentProcessId)) { if ($ids.Add([int]$child.ProcessId)) { $added = $true } }
                }
            } while ($added)
            $discovery = $clock.ElapsedMilliseconds
        }
        $rss = 0L
        foreach ($id in $ids) {
            try { $child = [Diagnostics.Process]::GetProcessById($id); $rss += $child.WorkingSet64; $child.Dispose() }
            catch [ArgumentException] { }
            catch [InvalidOperationException] { }
        }
        $peak = [Math]::Max($peak, $rss)
        $samples++
        Start-Sleep -Milliseconds 50
    }
    $process.WaitForExit()
    [IO.File]::WriteAllText($destination + '.stdout.txt', $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText($destination + '.stderr.txt', $stderr.GetAwaiter().GetResult())
    $result = [ordered]@{ schema = 1; exitCode = $process.ExitCode; peakSimultaneousWorkingSetBytes = $peak; samples = $samples;
        nominalSampleMilliseconds = 50; childDiscoveryMilliseconds = 500; elapsedMilliseconds = $clock.ElapsedMilliseconds;
        scope = 'Root .NET process and discovered descendants, including Node and esbuild; shared memory may be counted more than once; short-lived children and between-sample peaks can be missed.' }
    [IO.File]::WriteAllText($destination, ($result | ConvertTo-Json))
    $result | ConvertTo-Json -Compress
    if ($process.ExitCode -ne 0) { throw "Measured command failed; see $destination.stderr.txt" }
} finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
}
