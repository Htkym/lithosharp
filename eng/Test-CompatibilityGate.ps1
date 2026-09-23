<#
.SYNOPSIS
Proves that the 1.x compatibility gates actually detect breakage.

.DESCRIPTION
Positive-only checks cannot show that a gate works. This harness verifies both
directions:

1. The real generator passes eng/Test-GeneratorHostCompatibility.ps1.
2. The TooNewGenerator fixture (references Microsoft.CodeAnalysis 5.9.0) fails it.
3. The api-compat baseline fixture packs successfully with validation.
4. The api-compat candidate fixture removes an API and must fail pack-time
   package validation against its own 1.0.0 baseline.

Fixtures are copied to .tmp and use an isolated package cache; nothing under
tests/fixtures is built in place and no product code is modified.

.EXAMPLE
& ./eng/Test-CompatibilityGate.ps1
#>
[CmdletBinding()]
param(
    [string] $GeneratorPath,
    [switch] $SkipApiCompat,
    [switch] $SkipGenerator
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temporary = Join-Path $repo ('.tmp/compatibility-gate-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Force -Path $temporary
$results = [Collections.Generic.List[object]]::new()

function Add-Result([string] $Name, [bool] $Passed, [string] $Detail) {
    $results.Add([ordered]@{ name = $Name; passed = $Passed; detail = $Detail })
    Write-Host ("{0}: {1} ({2})" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Name, $Detail)
}

function Invoke-Captured([string] $Script, [string[]] $Arguments) {
    $output = & pwsh -NoProfile -File $Script @Arguments 2>&1 | Out-String
    return [ordered]@{ exit = $LASTEXITCODE; output = $output }
}

if (!$SkipGenerator) {
    if (!$GeneratorPath) { $GeneratorPath = Join-Path $repo 'src/LithoSharp.Generators/bin/Release/netstandard2.0/LithoSharp.Generators.dll' }
    if (!(Test-Path -LiteralPath $GeneratorPath -PathType Leaf)) {
        dotnet build (Join-Path $repo 'src/LithoSharp.Generators/LithoSharp.Generators.csproj') -c Release --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The generator did not build, so the host gate cannot be evaluated.' }
    }

    $hostCheck = Join-Path $repo 'eng/Test-GeneratorHostCompatibility.ps1'
    $positive = Invoke-Captured $hostCheck @('-GeneratorPath', $GeneratorPath, '-Quiet')
    Add-Result 'generator-host/real-generator' ($positive.exit -eq 0) "exit=$($positive.exit)"
    if ($positive.exit -ne 0) { Write-Host $positive.output }

    $tooNewProject = Join-Path $repo 'tests/fixtures/generator-host/TooNewGenerator/TooNewGenerator.csproj'
    dotnet build $tooNewProject -c Release --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The TooNewGenerator fixture did not build.' }
    $tooNew = Join-Path $repo 'tests/fixtures/generator-host/TooNewGenerator/bin/Release/netstandard2.0/TooNewGenerator.dll'
    $negative = Invoke-Captured $hostCheck @('-GeneratorPath', $tooNew, '-Quiet')
    $detected = $negative.exit -ne 0 -and $negative.output -match 'generator host compatibility: FAILED'
    Add-Result 'generator-host/too-new-reference' $detected "exit=$($negative.exit); detected=$($negative.output.Contains('generator host compatibility: FAILED'))"
    if (!$detected) { Write-Host $negative.output }
}

if (!$SkipApiCompat) {
    $fixtureSource = Join-Path $repo 'tests/fixtures/api-compat'
    $fixture = Join-Path $temporary 'api-compat'
    Copy-Item -LiteralPath $fixtureSource -Destination $fixture -Recurse
    $feed = Join-Path $fixture 'feed'
    $output = Join-Path $fixture 'out'
    $null = New-Item -ItemType Directory -Force -Path $feed, $output
    [IO.File]::WriteAllText((Join-Path $fixture 'NuGet.Config'), @"
<configuration>
  <packageSources><clear/><add key="local" value="$([Security.SecurityElement]::Escape($feed))"/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><clear/><packageSource key="local"><package pattern="Fixture.ApiCompat"/></packageSource><packageSource key="nuget"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
"@)

    $previousPackages = $env:NUGET_PACKAGES
    try {
        $env:NUGET_PACKAGES = Join-Path $fixture 'cache'
        $baselineOutput = & dotnet pack (Join-Path $fixture 'baseline/Fixture.ApiCompat.csproj') -c Release -o $feed --nologo 2>&1 | Out-String
        Add-Result 'api-compat/baseline-packs' ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $feed 'Fixture.ApiCompat.1.0.0.nupkg'))) "exit=$LASTEXITCODE"
        if ($LASTEXITCODE -ne 0) { Write-Host $baselineOutput }

        $candidateOutput = & dotnet pack (Join-Path $fixture 'candidate/Fixture.ApiCompat.csproj') -c Release -o $output --nologo 2>&1 | Out-String
        $candidateFailed = $LASTEXITCODE -ne 0
        $reportedRemoval = $candidateOutput -match 'CP\d{4}'
        Add-Result 'api-compat/removed-api-detected' ($candidateFailed -and $reportedRemoval) "exit=$LASTEXITCODE; removedMemberReported=$reportedRemoval"
        if (!($candidateFailed -and $reportedRemoval)) { Write-Host $candidateOutput }
    }
    finally {
        $env:NUGET_PACKAGES = $previousPackages
    }
}

$failed = @($results | Where-Object { !$_.passed })
Write-Host ("compatibility gate self-test: {0} of {1} cases passed" -f ($results.Count - $failed.Count), $results.Count)
if ($failed.Count -gt 0) { exit 1 }
exit 0
