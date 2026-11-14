[CmdletBinding()]
param([string] $ToolPath, [string] $EvidenceDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$ToolPath) { $ToolPath = Join-Path $PSScriptRoot '../src/LithoSharp.Tool/bin/Release/net10.0/LithoSharp.Tool.dll' }
$ToolPath = [IO.Path]::GetFullPath($ToolPath)
if (!(Test-Path -LiteralPath $ToolPath -PathType Leaf)) { throw "Build the Release tool first: $ToolPath" }
if (!$EvidenceDirectory) { $EvidenceDirectory = Join-Path ([IO.Path]::GetTempPath()) ('publication-' + [Guid]::NewGuid().ToString('N')) }
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if ((Test-Path -LiteralPath $EvidenceDirectory) -and @(Get-ChildItem -LiteralPath $EvidenceDirectory -Force).Count -ne 0) {
    throw 'Publication acceptance requires a new or empty evidence directory.'
}
$output = Join-Path $EvidenceDirectory 'output'
$null = New-Item -ItemType Directory -Path $output -Force
$baseline = Join-Path $EvidenceDirectory 'published.json'
$baseUrl = 'https://example.test/docs/'
$sequence = 0

function Assert-True([bool] $Condition, [string] $Message) {
    if (!$Condition) { throw $Message }
}

function Write-Page([string] $File, [string] $Body) {
    [IO.File]::WriteAllText((Join-Path $output $File), "<!doctype html><html><head><title>$File</title><link rel='canonical' href='$baseUrl$File'></head><body>$Body</body></html>")
}

function Invoke-Check([string[]] $Options, [int] $ExpectedExit, [string] $Format = 'json') {
    $script:sequence++
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($ToolPath, 'check-output', '--output', $output, '--base-url', $baseUrl, '--format', $Format) + $Options) {
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

function Output-Identity {
    return (Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {
        [IO.Path]::GetRelativePath($output, $_.FullName) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n"
}

Write-Page 'index.html' "<a href='guide.html#old'>Guide</a>"
Write-Page 'guide.html' '<h2 id=old>Old heading</h2>'
$before = Output-Identity
$ordinary = Invoke-Check @() 0
Assert-True (!($ordinary.PSObject.Properties.Name -contains 'publicationCompatibility')) 'Ordinary output inspection unexpectedly required a baseline.'
$indexPage = Join-Path $output 'index.html'
$indexBytes = [IO.File]::ReadAllBytes($indexPage)
[IO.File]::WriteAllBytes($indexPage, [byte[]]@(0xc3, 0x28))
$unavailable = Invoke-Check @('--site-id', 'docs', '--require-publication-compatibility') 1
Assert-True ($unavailable.publicationCompatibility.state -eq 'NotCompared' -and $unavailable.publicationCompatibility.required) 'Failed output capture lost its required NotCompared state.'
[IO.File]::WriteAllBytes($indexPage, $indexBytes)
$required = Invoke-Check @('--site-id', 'docs', '--require-publication-compatibility') 1
Assert-True ($required.publicationCompatibility.state -eq 'NotCompared' -and $required.publicationCompatibility.required) 'Required missing baseline did not fail as NotCompared.'
Assert-True (!(Test-Path -LiteralPath $baseline)) 'Comparison created a published baseline.'
$initialized = Invoke-Check @('--site-id', 'docs', '--initialize-baseline', $baseline) 0
Assert-True ($initialized.publicationCompatibility.state -eq 'NotCompared' -and !$initialized.publicationCompatibility.required) 'Explicit initialization pretended to compare.'
$baselineHash = (Get-FileHash -LiteralPath $baseline -Algorithm SHA256).Hash
$null = Invoke-Check @('--site-id', 'docs', '--initialize-baseline', $baseline) 1
Assert-True ((Get-FileHash -LiteralPath $baseline -Algorithm SHA256).Hash -eq $baselineHash) 'Initialization overwrote an existing baseline.'
$alias = Join-Path $EvidenceDirectory 'output-alias'
if ($IsWindows) { $null = New-Item -ItemType Junction -Path $alias -Target $output }
else { $null = New-Item -ItemType SymbolicLink -Path $alias -Target $output }
$null = Invoke-Check @('--site-id', 'docs', '--initialize-baseline', (Join-Path $alias 'published.json')) 1
Assert-True (!(Test-Path -LiteralPath (Join-Path $output 'published.json'))) 'Initialization followed a directory alias into inspected output.'
$compatible = Invoke-Check @('--site-id', 'docs', '--baseline', $baseline) 0
Assert-True ($compatible.publicationCompatibility.state -eq 'Compatible') 'Unchanged publication was not compatible.'
$unavailableAllowances = Invoke-Check @('--site-id', 'docs', '--baseline', $baseline, '--allow-removals', (Join-Path $EvidenceDirectory 'missing-allowances.json')) 1
Assert-True ($unavailableAllowances.publicationCompatibility.state -eq 'NotCompared' -and $unavailableAllowances.publicationCompatibility.required) 'Unreadable removal allowances lost their required NotCompared state.'
$identity = Invoke-Check @('--site-id', 'other-site', '--baseline', $baseline) 1
Assert-True ($identity.publicationCompatibility.state -eq 'NotCompared') 'Wrong publication identity did not stay NotCompared.'
$missing = Invoke-Check @('--site-id', 'docs', '--baseline', (Join-Path $EvidenceDirectory 'missing.json')) 1 'sarif'
Assert-True ($missing.runs[0].properties.publicationCompatibility.state -eq 'NotCompared') 'SARIF state disagreed with the failure exit.'
Assert-True ((Output-Identity) -eq $before) 'Inspection or baseline operations modified output.'

Write-Page 'index.html' "<a href='guide.html#new'>Guide</a>"
Write-Page 'guide.html' '<h2 id=new>New heading</h2>'
$before = Output-Identity
$incompatible = Invoke-Check @('--site-id', 'docs', '--baseline', $baseline) 1
Assert-True ($incompatible.publicationCompatibility.state -eq 'Incompatible' -and @($incompatible.diagnostics | Where-Object id -eq 'LSPUB003').Count -eq 1) 'Fixed internal links hid the lost published anchor.'
$allowances = Join-Path $EvidenceDirectory 'allowances.json'
[IO.File]::WriteAllText($allowances, (@{ ($baseUrl + 'guide.html#old') = 'Explicitly retired heading.' } | ConvertTo-Json))
$allowed = Invoke-Check @('--site-id', 'docs', '--baseline', $baseline, '--allow-removals', $allowances) 0
Assert-True ($allowed.publicationCompatibility.state -eq 'ExplicitlyAllowed') 'Explicit allowance did not agree with the success exit.'
Assert-True ((Output-Identity) -eq $before) 'Comparison modified current output.'
Assert-True ((Get-FileHash -LiteralPath $baseline -Algorithm SHA256).Hash -eq $baselineHash) 'Comparison promoted current output to the published baseline.'
Write-Host "Publication compatibility CLI acceptance passed ($sequence commands). Evidence: $EvidenceDirectory"
