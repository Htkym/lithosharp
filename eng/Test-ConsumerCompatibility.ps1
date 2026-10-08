<#
.SYNOPSIS
Checks that a consumer compiled against the published 1.0.0 package still runs
against the candidate package, and that the consumer recompiles against the
candidate.

.DESCRIPTION
Binary and source compatibility are separate claims, so they are measured
separately:

- binary: build the consumer against the published baseline package, run it,
  replace only the LithoSharp.dll in its output directory with the candidate
  assembly taken from the candidate .nupkg, then run the same binary again.
  Both runs must print identical output.
- source: copy the same consumer, explicitly select the candidate version, restore
  and build with a separate cache, verify the candidate assembly, and require
  the same output.

The package directories and NuGet cache are passed in, so the check works with
the published packages, an isolated feed or a local build.

.EXAMPLE
& ./eng/Test-ConsumerCompatibility.ps1 `
    -BaselinePackageDirectory .local/verification/1.1.0/v110-00-20260923/B0/packages `
    -CandidatePackageDirectory .local/verification/1.1.0/v110-02-20260923/packages
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $BaselinePackageDirectory,
    [Parameter(Mandatory)] [string] $CandidatePackageDirectory,
    [Alias('LithoSharpVersion')] [ValidateNotNullOrEmpty()] [string] $BaselineVersion = '1.0.0',
    [ValidateNotNullOrEmpty()] [string] $CandidateVersion = '1.1.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$baseline = [IO.Path]::GetFullPath($BaselinePackageDirectory)
$candidate = [IO.Path]::GetFullPath($CandidatePackageDirectory)
foreach ($directory in $baseline, $candidate) {
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { throw "Package directory not found: $directory" }
}
$baselinePackage = Join-Path $baseline "LithoSharp.$BaselineVersion.nupkg"
$candidatePackage = Join-Path $candidate "LithoSharp.$CandidateVersion.nupkg"
foreach ($package in $baselinePackage, $candidatePackage) {
    if (!(Test-Path -LiteralPath $package -PathType Leaf)) { throw "Package not found: $package" }
}

$temporary = Join-Path $repo ('.tmp/consumer-compat-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Force -Path $temporary
$failures = [Collections.Generic.List[string]]::new()

function Write-NuGetConfig([string] $Directory, [string] $LocalFeed) {
    [IO.File]::WriteAllText((Join-Path $Directory 'NuGet.Config'), @"
<configuration>
  <packageSources><clear/><add key="local" value="$([Security.SecurityElement]::Escape($LocalFeed))"/><add key="markdown-fixed" value="$([Security.SecurityElement]::Escape((Join-Path $repo 'eng/markdown/feed')))"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/><packageSource key="local"><package pattern="LithoSharp*"/></packageSource><packageSource key="markdown-fixed"><package pattern="LithoSharp.Markdown"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
"@)
}

function Invoke-DotNet([string[]] $Arguments, [string] $WorkingDirectory) {
    Push-Location $WorkingDirectory
    try {
        $output = & dotnet @Arguments 2>&1 | Out-String
        return [ordered]@{ exit = $LASTEXITCODE; output = $output }
    }
    finally { Pop-Location }
}

function Assert-ResolvedVersion([string] $Directory, [string] $Expected) {
    $assets = Get-Content -LiteralPath (Join-Path $Directory 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $keys = @($assets.libraries.Keys | Where-Object { $_ -match '^LithoSharp/' })
    if ($keys.Count -ne 1 -or $keys[0] -ne "LithoSharp/$Expected" -or $assets.libraries[$keys[0]].type -ne 'package') {
        throw "Expected LithoSharp/$Expected, but resolved $($keys -join ', ') in $Directory."
    }
    Write-Host "Resolved consumer package: $($keys[0])"
}

$previousPackages = $env:NUGET_PACKAGES
$previousHome = $env:DOTNET_CLI_HOME
try {
    $source = Join-Path $repo 'tests/fixtures/consumer-compat'
    $binaryDirectory = Join-Path $temporary 'binary'
    $sourceDirectory = Join-Path $temporary 'source'
    foreach ($directory in $binaryDirectory, $sourceDirectory) {
        $null = New-Item -ItemType Directory -Path $directory
        # Copy source files only; existing fixture bin/obj must not influence restore or build.
        Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $directory
    }
    $env:NUGET_PACKAGES = Join-Path $temporary 'baseline-cache'
    $env:DOTNET_CLI_HOME = Join-Path $temporary 'cli-home'
    Write-NuGetConfig $binaryDirectory $baseline
    Write-NuGetConfig $sourceDirectory $candidate

    $build = Invoke-DotNet @('build', '-c', 'Release', '--nologo', "-p:LithoSharpVersion=$BaselineVersion") $binaryDirectory
    if ($build.exit -ne 0) { throw "The consumer did not build against the baseline package.`n$($build.output)" }
    Assert-ResolvedVersion $binaryDirectory $BaselineVersion

    $consumerDll = Join-Path $binaryDirectory 'bin/Release/net10.0/ConsumerCompat.dll'
    $firstRun = Invoke-DotNet @($consumerDll) $binaryDirectory
    if ($firstRun.exit -ne 0) { throw "The consumer did not run against the baseline package.`n$($firstRun.output)" }

    $candidateAssembly = Join-Path $temporary 'LithoSharp.dll'
    $archive = [System.IO.Compression.ZipFile]::OpenRead($candidatePackage)
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq 'lib/net10.0/LithoSharp.dll' } | Select-Object -First 1
        if ($null -eq $entry) { throw "lib/net10.0/LithoSharp.dll was not found in $candidatePackage" }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $candidateAssembly, $true)
    }
    finally { $archive.Dispose() }

    $installed = Join-Path $binaryDirectory 'bin/Release/net10.0/LithoSharp.dll'
    $baselineHash = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
    $candidateHash = (Get-FileHash -LiteralPath $candidateAssembly -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $candidateAssembly -Destination $installed -Force

    $secondRun = Invoke-DotNet @($consumerDll) $binaryDirectory
    $identical = $secondRun.exit -eq 0 -and $secondRun.output -eq $firstRun.output
    if (!$identical) { $failures.Add('the 1.0-compiled consumer did not behave identically against the candidate assembly') }
    Write-Host ("binary compatibility: exit={0} outputIdentical={1} baselineHash={2} candidateHash={3}" -f $secondRun.exit, $identical, $baselineHash.Substring(0, 12), $candidateHash.Substring(0, 12))
    if (!$identical) { Write-Host $secondRun.output }

    Write-Host ("consumer output: {0}" -f (($firstRun.output -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join ' | '))

    # A separate cache also supports deliberate same-version artifact comparisons.
    $env:NUGET_PACKAGES = Join-Path $temporary 'candidate-cache'
    $recompile = Invoke-DotNet @('build', '-c', 'Release', '--nologo', "-p:LithoSharpVersion=$CandidateVersion") $sourceDirectory
    $sourceCompatible = $recompile.exit -eq 0
    if (!$sourceCompatible) { $failures.Add('the consumer did not recompile against the candidate package') }
    Write-Host ("source compatibility: exit={0}" -f $recompile.exit)
    if (!$sourceCompatible) { Write-Host $recompile.output }
    else {
        Assert-ResolvedVersion $sourceDirectory $CandidateVersion
        & (Join-Path $repo 'eng/markdown/Test-FixedRuntimeAssets.ps1') -AssetsPath (Join-Path $sourceDirectory 'obj/project.assets.json')
        $sourceAssembly = Join-Path $sourceDirectory 'bin/Release/net10.0/LithoSharp.dll'
        if ((Get-FileHash -LiteralPath $sourceAssembly -Algorithm SHA256).Hash -ne $candidateHash) {
            throw 'The recompiled consumer did not use the assembly from the candidate package.'
        }
        $sourceRun = Invoke-DotNet @((Join-Path $sourceDirectory 'bin/Release/net10.0/ConsumerCompat.dll')) $sourceDirectory
        if ($sourceRun.exit -ne 0 -or $sourceRun.output -ne $firstRun.output) {
            $failures.Add('the recompiled candidate consumer did not produce the baseline output')
            Write-Host $sourceRun.output
        }
    }
}
finally {
    $env:NUGET_PACKAGES = $previousPackages
    $env:DOTNET_CLI_HOME = $previousHome
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host "FAILED: $failure" }
    exit 1
}
Write-Host 'consumer compatibility: OK'
exit 0
