[CmdletBinding()]
param(
    [string] $Output,
    [switch] $SkipPack,
    [Alias('CandidateVersion')] [string] $CandidatePackageVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Output) { $Output = Join-Path $repo '.tmp/package-distribution' }
$Output = [IO.Path]::GetFullPath($Output)

$packages = @(
    @{ id = 'LithoSharp'; project = 'src/LithoSharp/LithoSharp.csproj' },
    @{ id = 'LithoSharp.Generators'; project = 'src/LithoSharp.Generators/LithoSharp.Generators.csproj' },
    @{ id = 'LithoSharp.Images'; project = 'src/LithoSharp.Images/LithoSharp.Images.csproj' },
    @{ id = 'LithoSharp.Tool'; project = 'src/LithoSharp.Tool/LithoSharp.Tool.csproj' },
    @{ id = 'LithoSharp.Testing'; project = 'src/LithoSharp.Testing/LithoSharp.Testing.csproj' },
    @{ id = 'LithoSharp.Mdx'; project = 'src/LithoSharp.Mdx/LithoSharp.Mdx.csproj' },
    @{ id = 'LithoSharp.ProjectTemplates'; project = 'templates/LithoSharp.ProjectTemplates/LithoSharp.ProjectTemplates.csproj' }
)

function Invoke-Dotnet([string[]] $Arguments, [string] $WorkingDirectory, [hashtable] $Environment) {
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    foreach ($entry in $Environment.GetEnumerator()) { $info.Environment[$entry.Key] = $entry.Value }
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        $process.WaitForExit()
        return @{ exitCode = $process.ExitCode; stdout = $stdout.GetAwaiter().GetResult(); stderr = $stderr.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

function Fail([string] $Message) { throw "Package distribution check failed: $Message" }

# One version across the published set: a mismatch fails before packing.
$versions = @{}
foreach ($package in $packages) {
    [xml] $xml = Get-Content -LiteralPath (Join-Path $repo $package.project) -Raw
    $declared = @($xml.SelectNodes('/Project/PropertyGroup/Version') | ForEach-Object { $_.InnerText })
    if ($declared.Count -ne 1 -or !$declared[0]) { Fail "$($package.project) must declare exactly one Version." }
    $versions[$package.id] = [string]$declared[0]
}
$distinct = @($versions.Values | Sort-Object -Unique)
if ($distinct.Count -ne 1) {
    Fail ("published package versions are not aligned: " + (($versions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))
}
$declaredVersion = $distinct[0]
$version = if ($CandidatePackageVersion) { $CandidatePackageVersion } else { $declaredVersion }
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') { Fail "invalid candidate package version: $version." }
Write-Host "Declared set aligns at $declaredVersion; validating candidate $version."

$feed = Join-Path $Output 'feed'
if ($SkipPack) {
    if (!(Test-Path -LiteralPath $feed -PathType Container)) { Fail '-SkipPack requires an existing feed directory.' }
}
else {
    if ((Test-Path -LiteralPath $feed) -and @(Get-ChildItem -LiteralPath $feed -Force).Count -gt 0) {
        Fail 'feed is not empty; use a fresh Output or -SkipPack to preserve existing evidence.'
    }
    $null = New-Item -ItemType Directory -Force -Path $feed
}
$work = Join-Path $Output ('work-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
$artifacts = Join-Path $work 'artifacts'

if (!$SkipPack) {
    foreach ($package in $packages) {
        $pack = Invoke-Dotnet @('pack', (Join-Path $repo $package.project), '--configuration', 'Release', '--artifacts-path', $artifacts, '--output', $feed, "-p:Version=$version", '-p:RestoreLockedMode=true', '-p:EnablePackageValidation=true') $repo @{}
        if ($pack.exitCode -ne 0) { Fail "pack failed for $($package.id):`n$($pack.stdout)`n$($pack.stderr)" }
    }
}

# Exactly seven candidate artifacts: no missing package, other version, or fixture extension.
$expected = @($packages | ForEach-Object { "$($_.id).$version.nupkg" })
$actual = @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' | ForEach-Object Name)
if ($actual.Count -ne $expected.Count -or @(Compare-Object $expected $actual).Count -ne 0) {
    Fail "feed must contain exactly the seven candidate packages at $version; found: $($actual -join ', ')."
}
foreach ($package in $packages) {
    # Validate both newly packed and -SkipPack inputs before installation.
    & (Join-Path $repo 'eng/Validate-Package.ps1') -PackageDirectory $feed -PackageId $package.id -ExpectedVersion $version
}
# Isolated consumer: fresh caches, hive and tool path; the user profile is untouched.
$isolatedEnv = [ordered]@{
    DOTNET_CLI_HOME = (Join-Path $work 'cli-home')
    NUGET_PACKAGES = (Join-Path $work 'packages')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    LithoSharpVersion = $version
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
}
foreach ($dir in @($isolatedEnv.DOTNET_CLI_HOME, $isolatedEnv.NUGET_PACKAGES)) {
    $null = New-Item -ItemType Directory -Path $dir
}
$nugetConfig = Join-Path $work 'NuGet.Config'
[IO.File]::WriteAllText($nugetConfig, @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$([Security.SecurityElement]::Escape($feed))" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping><clear/><packageSource key="local"><package pattern="LithoSharp*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
"@)

$toolPath = Join-Path $work 'tools'
$hive = Join-Path $work 'template-hive'
$install = Invoke-Dotnet @('tool', 'install', 'LithoSharp.Tool', '--version', $version, '--tool-path', $toolPath, '--configfile', $nugetConfig) $repo $isolatedEnv
if ($install.exitCode -ne 0) { Fail "tool install from feed failed:`n$($install.stdout)`n$($install.stderr)" }

$templates = Invoke-Dotnet @('new', 'install', (Join-Path $feed "LithoSharp.ProjectTemplates.$version.nupkg"), '--debug:custom-hive', $hive) $repo $isolatedEnv
if ($templates.exitCode -ne 0) { Fail "template install from feed failed:`n$($templates.stdout)`n$($templates.stderr)" }

# New site from packages only: no ProjectReference escape hatch.
$site = Join-Path $work 'site'
$fromFeed = Invoke-Dotnet @('new', 'lithosharp-docs', '-n', 'FeedSite', '-o', $site, '--debug:custom-hive', $hive) $repo $isolatedEnv
if ($fromFeed.exitCode -ne 0) { Fail "template instantiation failed:`n$($fromFeed.stdout)`n$($fromFeed.stderr)" }
Push-Location $site
try {
    $restore = Invoke-Dotnet @('restore', '--configfile', $nugetConfig) $site $isolatedEnv
    if ($restore.exitCode -ne 0) { Fail "feed-only restore failed:`n$($restore.stdout)`n$($restore.stderr)" }
    # Restore already pinned the feed in the assets file; no config is needed to build.
    $build = Invoke-Dotnet @('build', '-c', 'Release', '--no-restore') $site $isolatedEnv
    if ($build.exitCode -ne 0) { Fail "feed-only build failed (exit $($build.exitCode)):`nSTDOUT:`n$($build.stdout)`nSTDERR:`n$($build.stderr)" }
    $assets = Get-Content -LiteralPath (Join-Path $site 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $resolved = @($assets.libraries.Keys | Where-Object { $_ -match '^LithoSharp/' })
    if ($resolved.Count -ne 1 -or $resolved[0] -ne "LithoSharp/$version") {
        Fail "built site did not resolve the exact candidate LithoSharp/${version}: $($resolved -join ', ')."
    }
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $feed "LithoSharp.$version.nupkg"))
    try {
        $entry = $archive.GetEntry('lib/net10.0/LithoSharp.dll')
        if ($null -eq $entry) { Fail 'candidate package has no core assembly.' }
        $candidateAssembly = Join-Path $work 'candidate-LithoSharp.dll'
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $candidateAssembly, $true)
    }
    finally { $archive.Dispose() }
    $installedAssembly = Join-Path $site 'bin/Release/net10.0/LithoSharp.dll'
    if ((Get-FileHash -LiteralPath $installedAssembly -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $candidateAssembly -Algorithm SHA256).Hash) {
        Fail 'built site did not use the assembly from the candidate package.'
    }
}
finally { Pop-Location }

$report = [ordered]@{
    schemaVersion = '1.0'
    version = $version
    declaredVersion = $declaredVersion
    packageCount = $actual.Count
    packages = @($packages | ForEach-Object {
        $file = Join-Path $feed "$($_.id).$version.nupkg"
        [ordered]@{ id = $_.id; version = $version; file = [IO.Path]::GetFileName($file); sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
    })
    feed = $feed
    tool = Join-Path $toolPath $(if ($IsWindows) { 'lithosharp.exe' } else { 'lithosharp' })
    site = $site
    isolatedCaches = $work
    validationScope = 'seven package contents; isolated tool/template install and docs build'
}
[IO.File]::WriteAllText((Join-Path $Output 'package-distribution.json'), (($report | ConvertTo-Json -Depth 6)))
Write-Host "Package distribution passed: 7 packages at $version validated; tool/template installed and docs built from the isolated feed."
