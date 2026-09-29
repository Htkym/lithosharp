[CmdletBinding()]
param(
    [string] $Output,
    [switch] $SkipPack
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return @{ exitCode = $process.ExitCode; stdout = $stdout; stderr = $stderr }
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
$version = $distinct[0]
Write-Host "Published set aligns at $version."

$feed = Join-Path $Output 'feed'
$work = Join-Path $Output 'work'
foreach ($dir in @($feed, $work)) {
    if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    $null = New-Item -ItemType Directory -Path $dir
}

if (!$SkipPack) {
    foreach ($package in $packages) {
        $pack = Invoke-Dotnet @('pack', (Join-Path $repo $package.project), '--configuration', 'Release', '--output', $feed, '-p:EnablePackageValidation=true') $repo @{}
        if ($pack.exitCode -ne 0) { Fail "pack failed for $($package.id):`n$($pack.stderr)" }
        # Validate-Package.ps1 throws on any violation; a return means pass.
        & (Join-Path $repo 'eng/Validate-Package.ps1') -PackageDirectory $feed -PackageId $package.id -ExpectedVersion $version
    }
}

# Exactly the published set: the test-only fixture extension must not be here.
$shipped = @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' | ForEach-Object {
    $_.Name -replace '\.\d+\.\d+\.\d+.*\.nupkg$', ''
} | Sort-Object -Unique)
foreach ($id in $shipped) {
    if ($packages.id -notcontains $id) { Fail "unexpected package in feed: $id." }
}
if ($shipped -contains 'LithoSharp.FixtureExtension') { Fail 'test-only FixtureExtension leaked into the feed.' }

# Isolated consumer: fresh caches, hive and tool path; the user profile is untouched.
$isolatedEnv = [ordered]@{
    DOTNET_CLI_HOME = (Join-Path $work 'cli-home')
    NUGET_PACKAGES = (Join-Path $work 'packages')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
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
    <add key="local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@)

$toolPath = Join-Path $work 'tools'
$install = Invoke-Dotnet @('tool', 'install', 'LithoSharp.Tool', '--version', $version, '--tool-path', $toolPath, '--add-source', $feed, '--configfile', $nugetConfig) $repo $isolatedEnv
if ($install.exitCode -ne 0) { Fail "tool install from feed failed:`n$($install.stderr)" }

$templates = Invoke-Dotnet @('new', 'install', "LithoSharp.ProjectTemplates::$version", '--add-source', $feed) $repo $isolatedEnv
if ($templates.exitCode -ne 0) { Fail "template install from feed failed:`n$($templates.stderr)" }

# New site from packages only: no ProjectReference escape hatch.
$site = Join-Path $work 'site'
$fromFeed = Invoke-Dotnet @('new', 'lithosharp-docs', '-n', 'FeedSite', '-o', $site) $repo $isolatedEnv
if ($fromFeed.exitCode -ne 0) { Fail "template instantiation failed:`n$($fromFeed.stderr)" }
Push-Location $site
try {
    $restore = Invoke-Dotnet @('restore', '--configfile', $nugetConfig) $site $isolatedEnv
    if ($restore.exitCode -ne 0) { Fail "feed-only restore failed:`n$($restore.stderr)" }
    # Restore already pinned the feed in the assets file; no config is needed to build.
    $build = Invoke-Dotnet @('build', '-c', 'Release', '--no-restore') $site $isolatedEnv
    if ($build.exitCode -ne 0) { Fail "feed-only build failed (exit $($build.exitCode)):`nSTDOUT:`n$($build.stdout)`nSTDERR:`n$($build.stderr)" }
    $litho = Invoke-Dotnet @('list', 'package') $site $isolatedEnv
    foreach ($id in @('LithoSharp')) {
        if ($litho.stdout -notmatch [regex]::Escape($id)) { Fail "built site does not reference $id." }
    }
}
finally { Pop-Location }

$report = [ordered]@{
    schemaVersion = '1.0'
    version = $version
    packageCount = $packages.Count
    feed = $feed
    tool = Join-Path $toolPath 'lithosharp.exe'
    site = $site
    isolatedCaches = $work
}
[IO.File]::WriteAllText((Join-Path $Output 'package-distribution.json'), (($report | ConvertTo-Json -Depth 6)))
Write-Host "Package distribution passed: 7 packages at $version installed and built from an isolated feed."
